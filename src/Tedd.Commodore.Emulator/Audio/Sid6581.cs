using System;
using System.Runtime.CompilerServices;
using Tedd.Commodore.Emulator.Bus;

namespace Tedd.Commodore.Emulator.Audio;

/// <summary>
/// MOS 6581 SID (Sound Interface Device): three voices with ADSR envelopes, a state-variable filter and a master
/// volume, stepped one system cycle at a time by <see cref="Clock"/>.
///
/// The model follows reSID 0.16 by Dag Lem (files wave.cc/wave.h, envelope.cc/envelope.h, filter.cc/filter.h,
/// sid.cc): 24-bit phase accumulators, a 23-bit noise LFSR clocked on the rising edge of accumulator bit 19,
/// oscillator hard sync and ring modulation, the test bit, the 8-bit envelope counter driven by a 15-bit rate
/// counter and an exponential counter, the two-integrator-loop filter with the measured 6581 cutoff curve, per
/// voice filter routing, "3 OFF" and the volume register.
///
/// Simplifications relative to reSID: combined waveforms are approximated by ANDing the selected waveforms
/// (reSID uses sampled wave tables), the external RC output filter is not modelled and the 6581 DC offsets
/// (wave_zero / voice_DC / mixer_DC) are omitted so that silence is exactly 0.
///
/// Register reads: $19/$1A return <see cref="PotX"/>/<see cref="PotY"/>, $1B returns the upper 8 bits of voice 3's
/// waveform output and $1C voice 3's envelope counter. All other registers are write-only; the chip does not drive
/// the data bus for them, so <see cref="Read"/> returns 0 and the memory map substitutes the last bus value.
/// </summary>
public sealed class Sid6581 : IClockable
{
    /// <summary>Number of registers. Mirrors inside the $D400-$D7FF window are handled by the memory map.</summary>
    public const int RegisterCount = 32;

    /// <summary>PAL C64 system clock in Hz (17 734 475 Hz / 18).</summary>
    public const double PalClockFrequency = 985248.0;

    /// <summary>NTSC C64 system clock in Hz (14 318 181 Hz / 14).</summary>
    public const double NtscClockFrequency = 1022727.0;

    // Per-voice register offsets (voice n uses registers n * 7 + offset).
    public const int RegFreqLo = 0x00;
    public const int RegFreqHi = 0x01;
    public const int RegPwLo = 0x02;
    public const int RegPwHi = 0x03;
    public const int RegControl = 0x04;
    public const int RegAttackDecay = 0x05;
    public const int RegSustainRelease = 0x06;
    public const int VoiceRegisterStride = 7;

    // Global registers.
    public const int RegFilterCutoffLo = 0x15;
    public const int RegFilterCutoffHi = 0x16;
    public const int RegFilterResonanceRouting = 0x17;
    public const int RegModeVolume = 0x18;
    public const int RegPotX = 0x19;
    public const int RegPotY = 0x1A;
    public const int RegOsc3 = 0x1B;
    public const int RegEnv3 = 0x1C;

    // Control register bits.
    public const byte ControlGate = 0x01;
    public const byte ControlSync = 0x02;
    public const byte ControlRingMod = 0x04;
    public const byte ControlTest = 0x08;
    public const byte ControlTriangle = 0x10;
    public const byte ControlSawtooth = 0x20;
    public const byte ControlPulse = 0x40;
    public const byte ControlNoise = 0x80;

    // Resonance/routing register ($17) bits.
    public const byte FilterVoice1 = 0x01;
    public const byte FilterVoice2 = 0x02;
    public const byte FilterVoice3 = 0x04;
    public const byte FilterExternal = 0x08;

    // Mode/volume register ($18) bits.
    public const byte ModeLowPass = 0x10;
    public const byte ModeBandPass = 0x20;
    public const byte ModeHighPass = 0x40;
    public const byte ModeVoice3Off = 0x80;

    /// <summary>
    /// Envelope rate counter periods indexed by the attack/decay/release nibble (reSID envelope.cc
    /// rate_counter_period[]): the number of cycles between envelope steps, derived from the data sheet ADSR
    /// times (attack 2 ms .. 8 s over 256 steps at 1 MHz).
    /// </summary>
    public static readonly int[] EnvelopeRatePeriods =
    {
        9,      //   2 ms *1.0 MHz/256 =     7.81
        32,     //   8 ms *1.0 MHz/256 =    31.25
        63,     //  16 ms *1.0 MHz/256 =    62.50
        95,     //  24 ms *1.0 MHz/256 =    93.75
        149,    //  38 ms *1.0 MHz/256 =   148.44
        220,    //  56 ms *1.0 MHz/256 =   218.75
        267,    //  68 ms *1.0 MHz/256 =   265.63
        313,    //  80 ms *1.0 MHz/256 =   312.50
        392,    // 100 ms *1.0 MHz/256 =   390.63
        977,    // 250 ms *1.0 MHz/256 =   976.56
        1954,   // 500 ms *1.0 MHz/256 =  1953.13
        3126,   // 800 ms *1.0 MHz/256 =  3125.00
        3907,   //   1 s  *1.0 MHz/256 =  3906.25
        11720,  //   3 s  *1.0 MHz/256 = 11718.75
        19532,  //   5 s  *1.0 MHz/256 = 19531.25
        31251,  //   8 s  *1.0 MHz/256 = 31250.00
    };

    /// <summary>Scale of one voice: 12-bit waveform centred on $800 times the 8-bit envelope maps to -1..1.</summary>
    private const float VoiceScale = 1f / (2048f * 255f);

    /// <summary>
    /// Filter state variables are bounded by the input (at most three full scale voices) times the resonance
    /// gain; anything beyond this is a numerical accident and is clamped so the loop can never run away.
    /// </summary>
    private const float FilterStateLimit = 16f;

    /// <summary>
    /// The filter is stepped once per cycle with a forward Euler integrator; reSID limits the cutoff to 16 kHz
    /// to keep that one-cycle integrator stable (filter.cc set_w0, w0_max_1).
    /// </summary>
    private const double MaxCutoffHz = 16000.0;

    private static readonly float[] CutoffTable = BuildCutoffTable();

    private readonly byte[] _regs = new byte[RegisterCount];
    private readonly WaveformGenerator[] _wave = new WaveformGenerator[3];
    private readonly EnvelopeGenerator[] _env = new EnvelopeGenerator[3];

    // Filter/mixer registers.
    private int _fc;            // 11-bit cutoff
    private int _res;           // 4-bit resonance
    private int _filt;          // routing bits: voice 1..3, external input
    private int _mode;          // bit 0 LP, bit 1 BP, bit 2 HP
    private bool _voice3Off;
    private int _vol;           // 4-bit master volume

    // Filter state (reSID filter.h Vhp/Vbp/Vlp), in voice units (one full scale voice = 1.0).
    private float _vhp, _vbp, _vlp;
    private float _w0;          // 2*pi*f0/clock
    private float _invQ;        // 1/Q
    private float _outputScale; // volume/15 * gain / 3
    private float _output;
    private float _gain = 1f;
    private double _clockFrequency = PalClockFrequency;

    public Sid6581()
    {
        for (int i = 0; i < 3; i++)
        {
            _wave[i] = new WaveformGenerator();
            _env[i] = new EnvelopeGenerator();
        }
        // Sync/ring modulation sources: voice 1 <- voice 3, voice 2 <- voice 1, voice 3 <- voice 2
        // (reSID sid.cc SID::SID: voice[0].set_sync_source(&voice[2]) etc.).
        _wave[0].SyncSource = _wave[2]; _wave[0].SyncDest = _wave[1];
        _wave[1].SyncSource = _wave[0]; _wave[1].SyncDest = _wave[2];
        _wave[2].SyncSource = _wave[1]; _wave[2].SyncDest = _wave[0];
        Reset();
    }

    /// <summary>Mixed, filtered and volume scaled output after the last <see cref="Clock"/>, clamped to -1..1.</summary>
    public float Output => _output;

    /// <summary>Paddle X input as read from register $19 (the real chip's A/D converter result). Default $FF.</summary>
    public byte PotX { get; set; } = 0xFF;

    /// <summary>Paddle Y input as read from register $1A. Default $FF.</summary>
    public byte PotY { get; set; } = 0xFF;

    /// <summary>External audio input (EXT IN pin) in the same -1..1 scale as one voice; routed by bit 3 of $17.</summary>
    public float ExternalInput { get; set; }

    /// <summary>
    /// Extra output gain applied before the -1..1 clamp (default 1). Three full scale voices at volume 15 produce
    /// exactly 1.0 at gain 1, so typical music peaks well below full scale.
    /// </summary>
    public float Gain
    {
        get => _gain;
        set { _gain = value; UpdateOutputScale(); }
    }

    /// <summary>
    /// System clock in Hz used to convert the filter cutoff to a per-cycle coefficient (default PAL 985 248 Hz).
    /// reSID 0.16 assumes exactly 1 MHz here; using the real clock keeps the cutoff frequencies exact.
    /// </summary>
    public double ClockFrequency
    {
        get => _clockFrequency;
        set
        {
            if (!(value > 0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _clockFrequency = value;
            UpdateW0();
        }
    }

    /// <summary>Resets all registers and internal state to power-on values (reSID SID::reset). Pot inputs are kept.</summary>
    public void Reset()
    {
        Array.Clear(_regs, 0, _regs.Length);
        for (int i = 0; i < 3; i++)
        {
            _wave[i].Reset();
            _env[i].Reset();
        }
        _fc = 0;
        _res = 0;
        _filt = 0;
        _mode = 0;
        _voice3Off = false;
        _vol = 0;
        _vhp = _vbp = _vlp = 0f;
        _output = 0f;
        UpdateW0();
        UpdateQ();
        UpdateOutputScale();
    }

    /// <summary>
    /// Reads a register (reg 0..31). Only $19-$1C are readable; write-only and unused registers return 0 because
    /// the chip leaves the data bus floating for them (reSID sid.cc SID::read returns the decaying bus value).
    /// </summary>
    public byte Read(int reg)
    {
        switch (reg & 0x1F)
        {
            case RegPotX: return PotX;
            case RegPotY: return PotY;
            case RegOsc3: return (byte)(_wave[2].Output() >> 4); // reSID wave.h readOSC: output() >> 4
            case RegEnv3: return (byte)_env[2].Level;            // reSID envelope.h readENV
            default: return 0;
        }
    }

    /// <summary>
    /// Reads a register without side effects (SID reads never have any). For the readable registers this equals
    /// <see cref="Read"/>; for the write-only registers it returns the last value written so that debuggers can
    /// display the voice and filter settings.
    /// </summary>
    public byte Peek(int reg)
    {
        reg &= 0x1F;
        return reg >= RegPotX ? Read(reg) : _regs[reg];
    }

    /// <summary>Writes a register (reg 0..31). Writes to $19-$1F are ignored.</summary>
    public void Write(int reg, byte value)
    {
        reg &= 0x1F;
        _regs[reg] = value;
        if (reg < 3 * VoiceRegisterStride)
        {
            int voice = reg / VoiceRegisterStride;
            var w = _wave[voice];
            switch (reg - voice * VoiceRegisterStride)
            {
                case RegFreqLo: w.Freq = (w.Freq & 0xFF00) | value; break;
                case RegFreqHi: w.Freq = (w.Freq & 0x00FF) | (value << 8); break;
                case RegPwLo: w.Pw = (w.Pw & 0xF00) | value; break;
                case RegPwHi: w.Pw = (w.Pw & 0x0FF) | ((value & 0x0F) << 8); break;
                case RegControl:
                    w.WriteControl(value);
                    _env[voice].WriteControl(value);
                    break;
                case RegAttackDecay: _env[voice].WriteAttackDecay(value); break;
                case RegSustainRelease: _env[voice].WriteSustainRelease(value); break;
            }
            return;
        }

        switch (reg)
        {
            case RegFilterCutoffLo:
                // reSID filter.cc writeFC_LO: only bits 0-2 of the low byte are used.
                _fc = (_fc & 0x7F8) | (value & 0x007);
                UpdateW0();
                break;
            case RegFilterCutoffHi:
                // reSID filter.cc writeFC_HI: the high byte forms bits 3-10.
                _fc = ((value << 3) & 0x7F8) | (_fc & 0x007);
                UpdateW0();
                break;
            case RegFilterResonanceRouting:
                _res = (value >> 4) & 0x0F;
                _filt = value & 0x0F;
                UpdateQ();
                break;
            case RegModeVolume:
                _voice3Off = (value & ModeVoice3Off) != 0;
                _mode = (value >> 4) & 0x07;
                _vol = value & 0x0F;
                UpdateOutputScale();
                break;
            // $19-$1F: read-only / unused.
        }
    }

    /// <summary>
    /// Advances the chip by one system cycle in the order used by reSID sid.cc SID::clock(): envelopes, oscillators,
    /// oscillator synchronisation, then the filter and mixer.
    /// </summary>
    public void Clock()
    {
        _env[0].Clock();
        _env[1].Clock();
        _env[2].Clock();

        _wave[0].Clock();
        _wave[1].Clock();
        _wave[2].Clock();

        _wave[0].Synchronize();
        _wave[1].Synchronize();
        _wave[2].Synchronize();

        // Voice output = (waveform - $800) * envelope (reSID voice.h Voice::output, without the DC offset).
        float v1 = (_wave[0].Output() - 0x800) * _env[0].Level * VoiceScale;
        float v2 = (_wave[1].Output() - 0x800) * _env[1].Level * VoiceScale;
        float v3 = (_wave[2].Output() - 0x800) * _env[2].Level * VoiceScale;

        // "3 OFF" silences voice 3 only when it is not routed through the filter (reSID filter.h Filter::clock).
        if (_voice3Off && (_filt & FilterVoice3) == 0)
            v3 = 0f;

        // Route each source into the filter (Vi) or straight to the mixer (Vnf).
        float vi = 0f, vnf = 0f;
        if ((_filt & FilterVoice1) != 0) vi += v1; else vnf += v1;
        if ((_filt & FilterVoice2) != 0) vi += v2; else vnf += v2;
        if ((_filt & FilterVoice3) != 0) vi += v3; else vnf += v3;
        if ((_filt & FilterExternal) != 0) vi += ExternalInput; else vnf += ExternalInput;

        // Two-integrator-loop state variable filter, one Euler step per cycle (reSID filter.h Filter::clock):
        //   Vhp = Vbp/Q - Vlp - Vi;  dVbp = -w0*Vhp*dt;  dVlp = -w0*Vbp*dt
        float dVbp = _w0 * _vhp;
        float dVlp = _w0 * _vbp;
        _vbp -= dVbp;
        _vlp -= dVlp;
        _vhp = _vbp * _invQ - _vlp - vi;
        if (_vbp > FilterStateLimit) _vbp = FilterStateLimit; else if (_vbp < -FilterStateLimit) _vbp = -FilterStateLimit;
        if (_vlp > FilterStateLimit) _vlp = FilterStateLimit; else if (_vlp < -FilterStateLimit) _vlp = -FilterStateLimit;
        if (_vhp > FilterStateLimit) _vhp = FilterStateLimit; else if (_vhp < -FilterStateLimit) _vhp = -FilterStateLimit;

        // The selected filter outputs are summed unweighted (reSID filter.h Filter::output, verified by sampling
        // LP, BP and LP+BP on a real chip), added to the unfiltered voices and multiplied by the volume.
        float vf = 0f;
        if ((_mode & 1) != 0) vf += _vlp;
        if ((_mode & 2) != 0) vf += _vbp;
        if ((_mode & 4) != 0) vf += _vhp;

        float o = (vnf + vf) * _outputScale;
        _output = o > 1f ? 1f : (o < -1f ? -1f : o);
    }

    /// <summary>Current 24-bit phase accumulator of a voice (0..2), for tests and debuggers.</summary>
    public int PeekAccumulator(int voice) => _wave[voice].Accumulator;

    /// <summary>Current 12-bit waveform output of a voice (0..2) before envelope scaling, for tests and debuggers.</summary>
    public int PeekWaveform(int voice) => _wave[voice].Output();

    /// <summary>Current 8-bit envelope counter of a voice (0..2), for tests and debuggers.</summary>
    public byte PeekEnvelope(int voice) => (byte)_env[voice].Level;

    /// <summary>Current 23-bit noise shift register of a voice (0..2), for tests and debuggers.</summary>
    public int PeekNoiseShiftRegister(int voice) => _wave[voice].ShiftRegister;

    /// <summary>
    /// Filter cutoff frequency in Hz for an 11-bit cutoff register value, from the reSID 0.16 6581 curve
    /// (filter.cc f0_points_6581, measured by Dag Lem; cubic spline interpolated by spline.h).
    /// </summary>
    public static float CutoffFrequencyHz(int fc) => CutoffTable[fc & 0x7FF];

    private void UpdateW0()
    {
        // w0 = 2*pi*f0/clock, limited to 16 kHz for stability (reSID filter.cc set_w0). reSID divides by 1 MHz
        // regardless of the real clock; the actual clock frequency is used here instead.
        double f0 = Math.Min(CutoffTable[_fc], MaxCutoffHz);
        _w0 = (float)(2.0 * Math.PI * f0 / _clockFrequency);
    }

    private void UpdateQ()
    {
        // Q is controlled linearly by res, approximate range 0.707..1.7 (reSID filter.cc set_Q: 1024/(0.707 + res/15)).
        _invQ = (float)(1.0 / (0.707 + _res / 15.0));
    }

    private void UpdateOutputScale()
    {
        // Three full scale voices at volume 15 give 1.0 (reSID sid.cc SID::output scales the sum of three voices
        // at full volume to the 16-bit range with 6 dB headroom; Gain can be used to reproduce that).
        _outputScale = (_vol / 15f) * _gain / 3f;
    }

    /// <summary>
    /// Builds the 2048-entry cutoff table from the reSID 6581 spline points using the interpolation rules of
    /// reSID spline.h: repeated points mark segment ends, and the slope at a segment end is either the chord of
    /// the neighbouring points (interior), the straight-line slope (both ends repeated) or chosen so that the
    /// second derivative vanishes (one end repeated).
    /// </summary>
    private static float[] BuildCutoffTable()
    {
        // reSID 0.16 filter.cc f0_points_6581: {FC, f0 Hz}. Points are repeated at the ends and at the
        // discontinuity between FC 1023 and 1024 (the 6581's cutoff DAC is not monotonic at the MSB change).
        int[] x = { 0, 0, 128, 256, 384, 512, 640, 768, 832, 896, 960, 992, 1008, 1016, 1023, 1023, 1024, 1024, 1032, 1056, 1088, 1120, 1152, 1280, 1408, 1536, 1664, 1792, 1920, 2047, 2047 };
        int[] y = { 220, 220, 230, 250, 300, 420, 780, 1600, 2300, 3200, 4300, 5000, 5400, 5700, 6000, 6000, 4600, 4600, 4800, 5300, 6000, 6600, 7200, 9500, 12000, 14500, 16000, 17100, 17700, 18000, 18000 };

        var table = new float[2048];
        // reSID spline.h interpolate(p0, pn, plot, res): segments p1..p2 with neighbours p0 and p3, p2 != pn (last point).
        for (int i = 0; i + 3 < x.Length; i++)
        {
            double x0 = x[i], y0 = y[i], x1 = x[i + 1], y1 = y[i + 1], x2 = x[i + 2], y2 = y[i + 2], x3 = x[i + 3], y3 = y[i + 3];
            if (x1 == x2)
                continue; // p1 and p2 equal; single point

            double k1, k2;
            if (x0 == x1 && x2 == x3)
            {
                // Both end points repeated; straight line.
                k1 = k2 = (y2 - y1) / (x2 - x1);
            }
            else if (x0 == x1)
            {
                // p0 and p1 equal; use f''(x1) = 0.
                k2 = (y3 - y1) / (x3 - x1);
                k1 = (3 * (y2 - y1) / (x2 - x1) - k2) / 2;
            }
            else if (x2 == x3)
            {
                // p2 and p3 equal; use f''(x2) = 0.
                k1 = (y2 - y0) / (x2 - x0);
                k2 = (3 * (y2 - y1) / (x2 - x1) - k1) / 2;
            }
            else
            {
                // Normal curve.
                k1 = (y2 - y0) / (x2 - x0);
                k2 = (y3 - y1) / (x3 - x1);
            }

            // reSID spline.h cubic_coefficients.
            double dx = x2 - x1, dy = y2 - y1;
            double a = ((k1 + k2) - 2 * dy / dx) / (dx * dx);
            double b = ((k2 - k1) / dx - 3 * (x1 + x2) * a) / 2;
            double c = k1 - (3 * x1 * a + 2 * b) * x1;
            double d = y1 - ((x1 * a + b) * x1 + c) * x1;
            for (int px = (int)x1; px <= (int)x2; px++)
                table[px] = (float)(((a * px + b) * px + c) * px + d);
        }
        return table;
    }

    /// <summary>
    /// One oscillator (reSID wave.h/wave.cc WaveformGenerator): 24-bit phase accumulator, 23-bit noise LFSR,
    /// waveform selection, test bit, sync and ring modulation.
    /// </summary>
    private sealed class WaveformGenerator
    {
        public WaveformGenerator SyncSource = null!;
        public WaveformGenerator SyncDest = null!;

        public int Accumulator;     // 24 bits
        public int ShiftRegister;   // 23 bits
        public int Freq;            // 16 bits
        public int Pw;              // 12 bits
        public int Waveform;        // control register bits 4-7
        public bool Test;
        public bool RingMod;
        public bool Sync;
        public bool MsbRising;      // accumulator bit 23 went 0 -> 1 in this cycle

        /// <summary>Value the LFSR takes when the test bit is released (reSID wave.cc writeCONTROL_REG / reset).</summary>
        public const int ShiftRegisterReset = 0x7FFFF8;

        public void Reset()
        {
            Accumulator = 0;
            ShiftRegister = ShiftRegisterReset;
            Freq = 0;
            Pw = 0;
            Waveform = 0;
            Test = false;
            RingMod = false;
            Sync = false;
            MsbRising = false;
        }

        public void WriteControl(byte control)
        {
            Waveform = (control >> 4) & 0x0F;
            RingMod = (control & ControlRingMod) != 0;
            Sync = (control & ControlSync) != 0;

            bool testNext = (control & ControlTest) != 0;
            if (testNext)
            {
                // Test bit set: the accumulator is cleared and held; the shift register is reset (reSID wave.cc
                // writeCONTROL_REG). reSID 0.16 zeroes the register while the test bit is held and loads $7FFFF8
                // when it is released; the released value is loaded immediately here so that the noise output
                // is defined while the bit is held. The sequence after release is identical.
                Accumulator = 0;
                ShiftRegister = ShiftRegisterReset;
                MsbRising = false;
            }
            else if (Test)
            {
                // Test bit cleared: the accumulator starts counting and the shift register is $7FFFF8.
                ShiftRegister = ShiftRegisterReset;
            }
            Test = testNext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clock()
        {
            // No operation while the test bit is set (reSID wave.h WaveformGenerator::clock); a held accumulator
            // has no rising MSB so it must not sync its destination either.
            if (Test)
            {
                MsbRising = false;
                return;
            }

            int prev = Accumulator;
            Accumulator = (Accumulator + Freq) & 0xFFFFFF;

            // MSB rising edge is used for hard sync.
            MsbRising = (prev & 0x800000) == 0 && (Accumulator & 0x800000) != 0;

            // The noise shift register is clocked once every time accumulator bit 19 goes high;
            // feedback is bit 22 XOR bit 17 shifted into bit 0 (reSID wave.h clock_shift_register).
            if ((prev & 0x080000) == 0 && (Accumulator & 0x080000) != 0)
            {
                int bit0 = ((ShiftRegister >> 22) ^ (ShiftRegister >> 17)) & 1;
                ShiftRegister = ((ShiftRegister << 1) & 0x7FFFFF) | bit0;
            }
        }

        /// <summary>
        /// Hard sync (reSID wave.h WaveformGenerator::synchronize): a rising MSB resets the destination's accumulator
        /// if the destination has its sync bit set. When this oscillator is itself synced in the same cycle its MSB
        /// rises, the destination is not synced (verified by Dag Lem by sampling OSC3).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Synchronize()
        {
            if (MsbRising && SyncDest.Sync && !(Sync && SyncSource.MsbRising))
                SyncDest.Accumulator = 0;
        }

        /// <summary>12-bit waveform output. Combined waveforms are the AND of the selected waveforms (approximation).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Output()
        {
            int wf = Waveform;
            switch (wf)
            {
                case 0: return 0;
                case 1: return Triangle();
                case 2: return Sawtooth();
                case 4: return Pulse();
                case 8: return Noise();
            }
            int result = 0xFFF;
            if ((wf & 1) != 0) result &= Triangle();
            if ((wf & 2) != 0) result &= Sawtooth();
            if ((wf & 4) != 0) result &= Pulse();
            if ((wf & 8) != 0) result &= Noise();
            return result;
        }

        /// <summary>
        /// Triangle (reSID wave.h output___T): the accumulator is inverted while its MSB is set, then shifted to
        /// 12 bits. With ring modulation the MSB of the sync source is XORed in (only the triangle is affected).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Triangle()
        {
            int msb = (RingMod ? Accumulator ^ SyncSource.Accumulator : Accumulator) & 0x800000;
            return ((msb != 0 ? ~Accumulator : Accumulator) >> 11) & 0xFFF;
        }

        /// <summary>Sawtooth (reSID wave.h output__S_): the upper 12 bits of the accumulator.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Sawtooth() => Accumulator >> 12;

        /// <summary>
        /// Pulse (reSID wave.h output_P__): high while the upper 12 accumulator bits are >= the pulse width;
        /// the test bit forces the output high.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Pulse() => (Test || (Accumulator >> 12) >= Pw) ? 0xFFF : 0x000;

        /// <summary>
        /// Noise (reSID 1.0 wave.h set_noise_output): shift register bits 20, 18, 14, 11, 9, 5, 2 and 0 form the
        /// upper 8 bits of the 12-bit output.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Noise()
        {
            int s = ShiftRegister;
            return ((s & 0x100000) >> 9) |   // bit 20 -> bit 11
                   ((s & 0x040000) >> 8) |   // bit 18 -> bit 10
                   ((s & 0x004000) >> 5) |   // bit 14 -> bit 9
                   ((s & 0x000800) >> 3) |   // bit 11 -> bit 8
                   ((s & 0x000200) >> 2) |   // bit 9  -> bit 7
                   ((s & 0x000020) << 1) |   // bit 5  -> bit 6
                   ((s & 0x000004) << 3) |   // bit 2  -> bit 5
                   ((s & 0x000001) << 4);    // bit 0  -> bit 4
        }
    }

    /// <summary>
    /// ADSR envelope (reSID envelope.h/envelope.cc EnvelopeGenerator): an 8-bit counter stepped by a 15-bit rate
    /// counter; in decay and release an exponential counter slows the steps as the level falls.
    /// </summary>
    private sealed class EnvelopeGenerator
    {
        private enum EnvState { Attack, DecaySustain, Release }

        public int Level;                   // 8-bit envelope counter
        public int AttackRate, DecayRate, SustainLevel, ReleaseRate;

        private int _rateCounter;           // 15 bits
        private int _ratePeriod;
        private int _exponentialCounter;
        private int _exponentialPeriod;
        private EnvState _state;
        private bool _holdZero;
        private bool _gate;

        public void Reset()
        {
            // reSID envelope.cc EnvelopeGenerator::reset.
            Level = 0;
            AttackRate = DecayRate = SustainLevel = ReleaseRate = 0;
            _gate = false;
            _rateCounter = 0;
            _exponentialCounter = 0;
            _exponentialPeriod = 1;
            _state = EnvState.Release;
            _ratePeriod = EnvelopeRatePeriods[0];
            _holdZero = true;
        }

        public void WriteControl(byte control)
        {
            // The rate counter is never reset, so there is a delay of up to one rate period before the envelope
            // starts moving (reSID envelope.cc writeCONTROL_REG).
            bool gateNext = (control & ControlGate) != 0;
            if (!_gate && gateNext)
            {
                // Gate on: attack from the current level; unlocks the zero freeze.
                _state = EnvState.Attack;
                _ratePeriod = EnvelopeRatePeriods[AttackRate];
                _holdZero = false;
            }
            else if (_gate && !gateNext)
            {
                // Gate off: release from the current level.
                _state = EnvState.Release;
                _ratePeriod = EnvelopeRatePeriods[ReleaseRate];
            }
            _gate = gateNext;
        }

        public void WriteAttackDecay(byte value)
        {
            AttackRate = (value >> 4) & 0x0F;
            DecayRate = value & 0x0F;
            if (_state == EnvState.Attack)
                _ratePeriod = EnvelopeRatePeriods[AttackRate];
            else if (_state == EnvState.DecaySustain)
                _ratePeriod = EnvelopeRatePeriods[DecayRate];
        }

        public void WriteSustainRelease(byte value)
        {
            SustainLevel = (value >> 4) & 0x0F;
            ReleaseRate = value & 0x0F;
            if (_state == EnvState.Release)
                _ratePeriod = EnvelopeRatePeriods[ReleaseRate];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clock()
        {
            // ADSR delay bug (reSID envelope.h EnvelopeGenerator::clock): if the rate period is set below the
            // current rate counter value the counter keeps counting up until it wraps at 2^15 and then counts
            // rate_period - 1 more before the envelope steps. Verified by sampling ENV3.
            if ((++_rateCounter & 0x8000) != 0)
                _rateCounter = (_rateCounter + 1) & 0x7FFF;

            if (_rateCounter != _ratePeriod)
                return;

            _rateCounter = 0;

            // The first envelope step in the attack state also resets the exponential counter.
            if (_state == EnvState.Attack || ++_exponentialCounter == _exponentialPeriod)
            {
                _exponentialCounter = 0;

                // Frozen at zero until the next gate on.
                if (_holdZero)
                    return;

                switch (_state)
                {
                    case EnvState.Attack:
                        // The counter can wrap from $FF to $00 (attack entered at $FF); it is then frozen at zero.
                        Level = (Level + 1) & 0xFF;
                        if (Level == 0xFF)
                        {
                            _state = EnvState.DecaySustain;
                            _ratePeriod = EnvelopeRatePeriods[DecayRate];
                        }
                        break;
                    case EnvState.DecaySustain:
                        // Decays until the sustain level (sustain nibble replicated into both nibbles) is reached;
                        // a sustain level raised above the current level is never reached and the decay continues to 0.
                        if (Level != SustainLevel * 0x11)
                            Level = (Level - 1) & 0xFF;
                        break;
                    case EnvState.Release:
                        // The counter can wrap from $00 to $FF (release entered right after a wrap in attack).
                        Level = (Level - 1) & 0xFF;
                        break;
                }

                // Exponential counter periods change at fixed levels (reSID envelope.h; verified by sampling ENV3).
                switch (Level)
                {
                    case 0xFF: _exponentialPeriod = 1; break;
                    case 0x5D: _exponentialPeriod = 2; break;
                    case 0x36: _exponentialPeriod = 4; break;
                    case 0x1A: _exponentialPeriod = 8; break;
                    case 0x0E: _exponentialPeriod = 16; break;
                    case 0x06: _exponentialPeriod = 30; break;
                    case 0x00:
                        _exponentialPeriod = 1;
                        // When the counter reaches zero it is frozen there.
                        _holdZero = true;
                        break;
                }
            }
        }
    }
}
