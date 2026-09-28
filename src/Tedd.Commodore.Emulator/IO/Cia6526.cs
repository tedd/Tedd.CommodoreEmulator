using System;
using Tedd.Commodore.Emulator.Bus;

namespace Tedd.Commodore.Emulator.IO;

/// <summary>
/// MOS 6526 Complex Interface Adapter (two of them in the C64: CIA1 at $DC00, CIA2 at $DD00).
///
/// The timers, the interrupt logic and the port outputs follow the delay-pipeline model from Wolfgang Lorenz,
/// "A Software Model of the CIA6526" (the model used by VICE's <c>ciatimer.c</c>/<c>ciacore.c</c> and Hoxs64's
/// <c>cia6526.cpp</c>; both were consulted for the exact bit choreography). Every internal event travels through
/// a shift register of "delay" bits that is advanced once per <see cref="Clock"/>, so the observable timing
/// emerges from the pipeline rather than from ad-hoc counters.
///
/// <para><b>Cycle convention.</b> One system cycle is: the CPU performs its bus access (<see cref="Read"/> /
/// <see cref="Write"/>), then <see cref="Clock"/> is called. Consequently a value written in cycle N is acted upon
/// starting with the <see cref="Clock"/> of cycle N, and a <see cref="Read"/> in cycle N+k observes everything the
/// first k <see cref="Clock"/> calls after the write have done. The Lorenz model describes the same interleaving
/// ("the chip processes cycle N, then the CPU accesses it in cycle N"), so its cycle tables map 1:1 onto this
/// class: the CPU-visible effects are</para>
/// <list type="bullet">
/// <item>START written in cycle N: the counter is unchanged when read in N+1 and N+2, the first decrement is
/// visible in N+3 (Lorenz: Count2 → Count3 → decrement). Symmetrically, after START is cleared in cycle M the
/// counter still decrements twice (visible in M+1 and M+2), so a timer counts exactly M−N pulses.</item>
/// <item>Underflow happens in the <see cref="Clock"/> in which the counter is 0 after this cycle's decrement and
/// the next count pulse is already in the pipeline (Lorenz: <c>counter == 0 &amp;&amp; Count2</c>, evaluated after
/// the decrement; VICE <c>ciat_update</c> does the same): the ICR flag is set and the counter is reloaded from the
/// latch in that very cycle, and the count pulse in flight is swallowed. A continuous φ2 timer with latch L
/// therefore shows L, L-1, ..., 1, L, L, L-1, ... (0 is never visible, the latch value is seen twice) and
/// underflows every L+1 cycles; with sparse count pulses (CNT or timer A underflows) the 0 is visible.</item>
/// <item>Force load (CRx bit 4) and a high-byte write while the timer is stopped load the counter through
/// Load0 → Load1: the latch value is visible from cycle N+2.</item>
/// <item>Old 6526 (<see cref="Model6526A"/> = false, the C64 default): /IRQ and ICR bit 7 follow the flag one
/// cycle later (Interrupt0 → Interrupt1). 6526A: same cycle. Reading the ICR releases /IRQ immediately.</item>
/// </list>
/// </summary>
public sealed class Cia6526 : IClockable
{
    // Register indexes (6526 data sheet, "Register Map").
    public const int RegPra = 0x0;
    public const int RegPrb = 0x1;
    public const int RegDdra = 0x2;
    public const int RegDdrb = 0x3;
    public const int RegTaLo = 0x4;
    public const int RegTaHi = 0x5;
    public const int RegTbLo = 0x6;
    public const int RegTbHi = 0x7;
    public const int RegTod10ths = 0x8;
    public const int RegTodSec = 0x9;
    public const int RegTodMin = 0xA;
    public const int RegTodHr = 0xB;
    public const int RegSdr = 0xC;
    public const int RegIcr = 0xD;
    public const int RegCra = 0xE;
    public const int RegCrb = 0xF;

    // ICR bits (6526 data sheet, "Interrupt Control Register").
    public const byte IcrTimerA = 0x01;
    public const byte IcrTimerB = 0x02;
    public const byte IcrAlarm = 0x04;
    public const byte IcrSerial = 0x08;
    public const byte IcrFlag = 0x10;
    public const byte IcrIr = 0x80;

    // ---------------------------------------------------------------------------------------------------------
    // Delay pipeline (Lorenz). Every Clock() shifts all bits one position to the left: stage 0 -> 1 -> 2 -> 3.
    // Stage-0 bits are cleared by the shift and only re-appear if they are in the "feed" word (levels) or are
    // set again by an event; that is how one-cycle pulses (CNT edge, force load, ICR read...) enter the pipe.
    // The shift is masked so that the last stage of one signal never spills into the first stage of the next.
    // ---------------------------------------------------------------------------------------------------------
    private const ulong CountA0 = 1UL << 0;
    private const ulong CountA1 = 1UL << 1;
    private const ulong CountA2 = 1UL << 2;
    private const ulong CountA3 = 1UL << 3;
    private const ulong CountB0 = 1UL << 4;
    private const ulong CountB1 = 1UL << 5;
    private const ulong CountB2 = 1UL << 6;
    private const ulong CountB3 = 1UL << 7;
    private const ulong LoadA0 = 1UL << 8;
    private const ulong LoadA1 = 1UL << 9;
    private const ulong LoadB0 = 1UL << 10;
    private const ulong LoadB1 = 1UL << 11;
    private const ulong Pb6Low0 = 1UL << 12;
    private const ulong Pb6Low1 = 1UL << 13;
    private const ulong Pb7Low0 = 1UL << 14;
    private const ulong Pb7Low1 = 1UL << 15;
    private const ulong Interrupt0 = 1UL << 16;
    private const ulong Interrupt1 = 1UL << 17;
    private const ulong OneShotA0 = 1UL << 18; // feed only
    private const ulong OneShotB0 = 1UL << 19; // feed only
    private const ulong ClearIcr0 = 1UL << 20;
    private const ulong ClearIcr1 = 1UL << 21;
    private const ulong SetIcr0 = 1UL << 22;
    private const ulong SetIcr1 = 1UL << 23;
    private const ulong ReadIcr0 = 1UL << 24;
    private const ulong ReadIcr1 = 1UL << 25;

    private const ulong Stage0Bits = CountA0 | CountB0 | LoadA0 | LoadB0 | Pb6Low0 | Pb7Low0 | Interrupt0
                                     | OneShotA0 | OneShotB0 | ClearIcr0 | SetIcr0 | ReadIcr0;
    private const ulong DelayMask = ((1UL << 26) - 1) & ~Stage0Bits;

    private ulong _delay;
    private ulong _feed;

    // Ports
    private byte _pra, _prb, _ddra, _ddrb;

    // Timers
    private ushort _ta, _tb;
    private ushort _latchA, _latchB;
    private byte _cra, _crb;
    private bool _pb6Toggle, _pb7Toggle;   // underflow toggle flip-flops (set to 1 when the timer is started)
    // RUNMODE (one-shot) as seen by the underflow logic is the bit as written now OR as it stood at the end of the
    // previous cycle (Hoxs64: "(delay | feed) & OneShotA0"): setting it in the underflow cycle still stops the
    // timer, and clearing it in the underflow cycle does not prevent the stop (Lorenz tests "flipos", "cia1ta" #19).
    private bool _oneShotA1, _oneShotB1;
    // True during the cycle after a reload (Hoxs64 "LoadA2"): a latch write in that cycle updates the counter at
    // once instead of waiting for the next underflow (Lorenz "cia1ta" test #08).
    private bool _loadA2, _loadB2;
    private bool _pb6Out, _pb7Out;         // level currently driven on PB6/PB7 when the timer output is enabled

    // Interrupts
    private byte _icr;   // pending flags (bits 0-4) and IR (bit 7)
    private byte _imr;   // interrupt mask
    private bool _flagPending;
    private bool _alarmPending;
    private bool _sdrPending;

    // External pins
    private bool _cnt = true;             // CNT pin idles high (Lorenz test "cntdef")
    private bool _flag;

    // Serial data register
    private byte _sdr;
    private int _sdrBitsLeft;     // output mode: timer A underflows left for the byte in the shifter (16 per byte)
    private bool _sdrBuffered;    // output mode: another byte is waiting in the SDR
    private byte _sdrIn;          // input mode: bits shifted in so far
    private int _sdrInCount;

    // Time of day
    private byte _todTenths, _todSec, _todMin, _todHr;
    private byte _almTenths, _almSec, _almMin, _almHr;
    private byte _latTenths, _latSec, _latMin, _latHr;
    private bool _todLatched;
    private bool _todHalted;
    private int _todDivider;

    /// <summary>Creates a CIA in reset state.</summary>
    /// <param name="name">Name used for debugging ("CIA1", "CIA2", ...).</param>
    public Cia6526(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Reset();
    }

    /// <summary>Name given at construction (for debugging).</summary>
    public string Name { get; }

    /// <summary>
    /// External levels on PA0-PA7 (1 = high). Called when PRA is read; the result is ANDed with what the chip drives.
    /// When null all pins float high.
    /// </summary>
    public Func<byte>? PortAInput;

    /// <summary>External levels on PB0-PB7 (1 = high), see <see cref="PortAInput"/>.</summary>
    public Func<byte>? PortBInput;

    /// <summary>What the chip drives on port A: PRA for output pins, 1 (pull-up) for input pins.</summary>
    public byte PortAOutput => (byte)(_pra | ~_ddra);

    /// <summary>
    /// What the chip drives on port B: PRB | ~DDRB, with PB6/PB7 replaced by the timer outputs when CRA/CRB bit 1
    /// (PBON) is set. PBON forces the pin to be an output regardless of DDRB (6526 data sheet, "Port B").
    /// </summary>
    public byte PortBOutput
    {
        get
        {
            int o = _prb | ~_ddrb;
            if ((_cra & 0x02) != 0)
                o = (o & ~0x40) | (_pb6Out ? 0x40 : 0);
            if ((_crb & 0x02) != 0)
                o = (o & ~0x80) | (_pb7Out ? 0x80 : 0);
            return (byte)o;
        }
    }

    /// <summary>Raised after a write (or reset) changed <see cref="PortAOutput"/>.</summary>
    public event Action? PortAChanged;

    /// <summary>Raised after a write, reset or clock cycle changed <see cref="PortBOutput"/>.</summary>
    public event Action? PortBChanged;

    /// <summary>
    /// FLAG input pin level. A true → false transition (negative edge) sets ICR bit 4 in the next
    /// <see cref="Clock"/> (Lorenz: the edge is recognised in the following cycle).
    /// </summary>
    public bool Flag
    {
        set
        {
            if (_flag && !value)
                _flagPending = true;
            _flag = value;
        }
    }

    /// <summary>
    /// CNT input pin level. A positive edge is a count pulse for timer A (CRA bit 5 = 1) and timer B (CRB bits 5-6 =
    /// 01); the pulse enters the pipeline at stage 0, so the decrement is visible four cycles after the edge (Lorenz:
    /// Count0 → Count1 → Count2 → Count3). In serial input mode (CRA bit 6 = 0) a positive edge also shifts
    /// <see cref="Sp"/> into the shift register. The level is also used by timer B mode 11 (timer A underflow while
    /// CNT is high).
    /// </summary>
    public bool Cnt
    {
        set
        {
            if (value && !_cnt)
            {
                _delay |= CountA0 | CountB0;
                if ((_cra & 0x40) == 0)
                {
                    _sdrIn = (byte)((_sdrIn << 1) | (Sp ? 1 : 0));
                    if (++_sdrInCount == 8)
                    {
                        _sdrInCount = 0;
                        _sdr = _sdrIn;
                        _sdrPending = true;
                    }
                }
            }
            _cnt = value;
        }
    }

    /// <summary>SP (serial port) input pin level, sampled on CNT positive edges in serial input mode. Default high.</summary>
    public bool Sp { get; set; } = true;

    /// <summary>True while /IRQ is asserted.</summary>
    public bool IrqLine { get; private set; }

    /// <summary>
    /// false = original 6526 (C64 default): /IRQ and ICR bit 7 are set one cycle after the interrupt flag.
    /// true = 6526A: /IRQ is asserted in the same cycle as the flag (Lorenz, "Interrupt"; VICE CIA_MODEL_6526A).
    /// </summary>
    public bool Model6526A { get; set; }

    /// <summary>
    /// Reset (/RES): all registers 0, ports are inputs, timer latches and counters $FFFF, interrupt mask 0, TOD
    /// 00:00:00.0 running, no interrupt pending (6526 data sheet, "Reset").
    /// </summary>
    public void Reset()
    {
        _oneShotA1 = _oneShotB1 = false;
        byte oldA = PortAOutput, oldB = PortBOutput;

        _delay = 0;
        _feed = 0;
        _pra = _prb = _ddra = _ddrb = 0;
        _ta = _tb = 0xFFFF;
        _latchA = _latchB = 0xFFFF;
        _cra = _crb = 0;
        _pb6Toggle = _pb7Toggle = false;
        _pb6Out = _pb7Out = false;
        _icr = 0;
        _imr = 0;
        _flagPending = _alarmPending = _sdrPending = false;
        IrqLine = false;
        _sdr = 0;
        _sdrBitsLeft = 0;
        _sdrBuffered = false;
        _sdrIn = 0;
        _sdrInCount = 0;
        _todTenths = _todSec = _todMin = _todHr = 0;
        _almTenths = _almSec = _almMin = _almHr = 0;
        _latTenths = _latSec = _latMin = _latHr = 0;
        _todLatched = false;
        _todHalted = false;
        _todDivider = 0;

        if (oldA != PortAOutput) PortAChanged?.Invoke();
        if (oldB != PortBOutput) PortBChanged?.Invoke();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Register access
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Reads register <paramref name="reg"/> (0..15) with all side effects (ICR clear, TOD latch/unlatch).</summary>
    public byte Read(int reg)
    {
        switch (reg & 0x0F)
        {
            case RegPra: return (byte)(PortAOutput & (PortAInput?.Invoke() ?? 0xFF));
            case RegPrb: return (byte)(PortBOutput & (PortBInput?.Invoke() ?? 0xFF));
            case RegDdra: return _ddra;
            case RegDdrb: return _ddrb;
            case RegTaLo: return (byte)_ta;
            case RegTaHi: return (byte)(_ta >> 8);
            case RegTbLo: return (byte)_tb;
            case RegTbHi: return (byte)(_tb >> 8);

            case RegTod10ths:
                // Reading tenths releases the latch set by reading hours (data sheet, "Time of Day Clock").
                if (_todLatched)
                {
                    _todLatched = false;
                    return _latTenths;
                }
                return _todTenths;
            case RegTodSec: return _todLatched ? _latSec : _todSec;
            case RegTodMin: return _todLatched ? _latMin : _todMin;
            case RegTodHr:
                // Reading hours latches all four registers until tenths are read.
                if (!_todLatched)
                {
                    _todLatched = true;
                    _latTenths = _todTenths;
                    _latSec = _todSec;
                    _latMin = _todMin;
                    _latHr = _todHr;
                }
                return _latHr;

            case RegSdr: return _sdr;

            case RegIcr:
            {
                byte result = _icr;
                // 6526A: an interrupt that is about to be raised (delayed because the ICR was read in the previous
                // cycle) already shows IR in the read value (Lorenz model as implemented by Hoxs64).
                if (Model6526A && (_delay & Interrupt1) != 0 && (_icr & 0x1F) != 0)
                    result |= IcrIr;
                // The flags are cleared at once; IR (bit 7) stays set for two more cycles (ClearIcr0 → ClearIcr1),
                // which is invisible to a 6502 (two reads of the ICR are at least four cycles apart) but is what the
                // model (VICE: "irqflags &= CIA_IM_SET", Hoxs64: "icr &= 0x80") does.
                _icr &= IcrIr;
                _delay |= ClearIcr0 | ReadIcr0;
                // A pending assertion is cancelled: on the old 6526 an interrupt whose flag was set in the same cycle
                // as the ICR read is lost (neither IR nor /IRQ is ever set) - the well-known 6526 quirk.
                _delay &= ~(Interrupt1 | SetIcr1);
                // /IRQ is released immediately by the read.
                IrqLine = false;
                return result;
            }

            case RegCra: return _cra;
            case RegCrb: return _crb;
        }
        return 0xFF;
    }

    /// <summary>Reads register <paramref name="reg"/> without side effects (for debuggers).</summary>
    public byte Peek(int reg)
    {
        switch (reg & 0x0F)
        {
            case RegTod10ths: return _todLatched ? _latTenths : _todTenths;
            case RegTodHr: return _todLatched ? _latHr : _todHr;
            case RegIcr:
            {
                byte result = _icr;
                if (Model6526A && (_delay & Interrupt1) != 0 && (_icr & 0x1F) != 0)
                    result |= IcrIr;
                return result;
            }
            default: return Read(reg);
        }
    }

    /// <summary>Writes register <paramref name="reg"/> (0..15).</summary>
    public void Write(int reg, byte value)
    {
        switch (reg & 0x0F)
        {
            case RegPra:
            {
                byte old = PortAOutput;
                _pra = value;
                if (old != PortAOutput) PortAChanged?.Invoke();
                break;
            }
            case RegPrb:
            {
                byte old = PortBOutput;
                _prb = value;
                if (old != PortBOutput) PortBChanged?.Invoke();
                break;
            }
            case RegDdra:
            {
                byte old = PortAOutput;
                _ddra = value;
                if (old != PortAOutput) PortAChanged?.Invoke();
                break;
            }
            case RegDdrb:
            {
                byte old = PortBOutput;
                _ddrb = value;
                if (old != PortBOutput) PortBChanged?.Invoke();
                break;
            }

            case RegTaLo:
                _latchA = (ushort)((_latchA & 0xFF00) | value);
                if (_loadA2) _ta = _latchA;
                break;
            case RegTaHi:
                _latchA = (ushort)((_latchA & 0x00FF) | (value << 8));
                // Writing the high byte while the timer is stopped loads the counter (data sheet); the load goes
                // through Load0 → Load1 like a force load (Lorenz).
                if ((_cra & 0x01) == 0)
                    _delay |= LoadA0;
                if (_loadA2) _ta = _latchA;
                break;
            case RegTbLo:
                _latchB = (ushort)((_latchB & 0xFF00) | value);
                if (_loadB2) _tb = _latchB;
                break;
            case RegTbHi:
                _latchB = (ushort)((_latchB & 0x00FF) | (value << 8));
                if ((_crb & 0x01) == 0)
                    _delay |= LoadB0;
                if (_loadB2) _tb = _latchB;
                break;

            case RegTod10ths:
                if ((_crb & 0x80) != 0)
                {
                    SetAlarm(ref _almTenths, (byte)(value & 0x0F));
                }
                else
                {
                    // A write to tenths restarts a clock halted by a write to hours (data sheet).
                    if (_todHalted)
                    {
                        _todHalted = false;
                        _todDivider = 0;
                    }
                    SetTod(ref _todTenths, (byte)(value & 0x0F));
                }
                break;
            case RegTodSec:
                if ((_crb & 0x80) != 0) SetAlarm(ref _almSec, (byte)(value & 0x7F));
                else SetTod(ref _todSec, (byte)(value & 0x7F));
                break;
            case RegTodMin:
                if ((_crb & 0x80) != 0) SetAlarm(ref _almMin, (byte)(value & 0x7F));
                else SetTod(ref _todMin, (byte)(value & 0x7F));
                break;
            case RegTodHr:
            {
                byte hr = (byte)(value & 0x9F);
                if ((_crb & 0x80) != 0)
                {
                    SetAlarm(ref _almHr, hr);
                }
                else
                {
                    // Writing hours halts the clock until tenths are written (data sheet). Writing hour 12 inverts
                    // the AM/PM bit - a hardware quirk reproduced by VICE ("Flip AM/PM on hour 12") and Hoxs64.
                    _todHalted = true;
                    if ((hr & 0x1F) == 0x12)
                        hr ^= 0x80;
                    SetTod(ref _todHr, hr);
                }
                break;
            }

            case RegSdr:
                _sdr = value;
                if ((_cra & 0x40) != 0)
                {
                    // Output mode: the byte is shifted out at half the timer A underflow rate, i.e. 16 underflows per
                    // byte (data sheet: "the shift clock is timer A underflow / 2"). A second write while a byte is
                    // in flight is buffered and starts when the current byte completes.
                    if (_sdrBitsLeft == 0)
                        _sdrBitsLeft = 16;
                    else
                        _sdrBuffered = true;
                }
                break;

            case RegIcr:
            {
                // Bit 7 selects set (1) or clear (0) of the mask bits given in bits 0-4 (data sheet).
                if ((value & 0x80) != 0)
                    _imr |= (byte)(value & 0x1F);
                else
                    _imr &= (byte)~value;
                // Enabling the mask of an already pending flag raises the interrupt: with the usual delay on the old
                // 6526 (Interrupt0 → Interrupt1 → /IRQ, two cycles after the write), one cycle earlier on the 6526A.
                if ((_icr & _imr & 0x1F) != 0 && !IrqLine)
                {
                    if (Model6526A)
                    {
                        if ((_delay & ReadIcr1) == 0)
                            _delay |= Interrupt1 | SetIcr1;
                    }
                    else
                    {
                        _delay |= Interrupt0 | SetIcr0;
                    }
                }
                break;
            }

            case RegCra:
            {
                byte oldB = PortBOutput;
                // Force load (bit 4): Load0 → Load1 → counter = latch, two cycles after the write. Reads back as 0.
                if ((value & 0x10) != 0)
                    _delay |= LoadA0;
                if ((value & 0x08) != 0) _feed |= OneShotA0; else _feed &= ~OneShotA0;
                // Count source: phi2 feeds Count2 directly (Lorenz); CNT pulses arrive through Count0/Count1.
                if ((value & 0x20) != 0) _feed &= ~CountA2;
                else if ((value & 0x01) != 0) _feed |= CountA2;
                else _feed &= ~CountA2;
                // Starting the timer sets the PB6 toggle flip-flop (data sheet: "toggle output is set high whenever
                // the timer is started").
                if ((value & 0x01) != 0 && (_cra & 0x01) == 0)
                    _pb6Toggle = true;
                if ((value & 0x02) != 0)
                    _pb6Out = (value & 0x04) != 0 ? _pb6Toggle : (_delay & Pb6Low1) != 0;
                _cra = (byte)(value & 0xEF);
                if (oldB != PortBOutput) PortBChanged?.Invoke();
                break;
            }

            case RegCrb:
            {
                byte oldB = PortBOutput;
                if ((value & 0x10) != 0)
                    _delay |= LoadB0;
                if ((value & 0x08) != 0) _feed |= OneShotB0; else _feed &= ~OneShotB0;
                // Only mode 00 (phi2) counts through the feed; CNT / timer A underflow pulses enter at Count0/Count1.
                if ((value & 0x60) != 0) _feed &= ~CountB2;
                else if ((value & 0x01) != 0) _feed |= CountB2;
                else _feed &= ~CountB2;
                if ((value & 0x01) != 0 && (_crb & 0x01) == 0)
                    _pb7Toggle = true;
                if ((value & 0x02) != 0)
                    _pb7Out = (value & 0x04) != 0 ? _pb7Toggle : (_delay & Pb7Low1) != 0;
                _crb = (byte)(value & 0xEF);
                if (oldB != PortBOutput) PortBChanged?.Invoke();
                break;
            }
        }
    }

    private void SetTod(ref byte field, byte value)
    {
        if (field == value) return;
        field = value;
        CheckAlarm();
    }

    private void SetAlarm(ref byte field, byte value)
    {
        if (field == value) return;
        field = value;
        CheckAlarm();
    }

    private void CheckAlarm()
    {
        // The alarm flag is set when the clock reaches (or is written to) the alarm time (data sheet). The compare
        // runs whenever the clock or the alarm changes; the flag reaches the ICR in the next Clock().
        if (_todTenths == _almTenths && _todSec == _almSec && _todMin == _almMin && _todHr == _almHr)
            _alarmPending = true;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Time of day
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One pulse of the TOD input (mains frequency). CRA bit 7 selects the divider: 1 = 50 Hz input (divide by 5),
    /// 0 = 60 Hz input (divide by 6), so tenths advance ten times per second either way (data sheet).
    /// Ignored while the clock is halted by a write to the hours register.
    /// </summary>
    public void TodTick()
    {
        if (_todHalted)
            return;
        int divider = (_cra & 0x80) != 0 ? 5 : 6;
        if (++_todDivider < divider)
            return;
        _todDivider = 0;
        IncrementTod();
        CheckAlarm();
    }

    /// <summary>
    /// BCD increment with the 6526 rules: tenths 0-9, seconds and minutes 00-59, hours 1-12 with AM/PM in bit 7.
    /// 11:59:59.9 → 12:00:00.0 with AM/PM flipped, 12:59:59.9 → 01:00:00.0 (data sheet; sequence as in Hoxs64).
    /// </summary>
    private void IncrementTod()
    {
        _todTenths = (byte)((_todTenths + 1) & 0x0F);
        if (_todTenths != 0x0A)
            return;
        _todTenths = 0;

        if (!IncrementBcd60(ref _todSec))
            return;
        if (!IncrementBcd60(ref _todMin))
            return;

        int pm = _todHr & 0x80;
        int hr = _todHr & 0x1F;
        if (hr == 0x12)
            hr = 0;                 // 12 behaves as 0 for the increment: 12 → 1
        int tens = hr & 0x10;
        hr = (hr + 1) & 0x1F;
        if (hr == 0x12)
            pm ^= 0x80;             // 11 → 12 flips AM/PM
        if (hr == 0x0A)
            _todHr = (byte)(pm | 0x10);   // 9 → 10
        else
            _todHr = (byte)(pm | tens | (hr & 0x0F));
    }

    /// <summary>Increments a BCD 00-59 field; returns true on wrap to 00.</summary>
    private static bool IncrementBcd60(ref byte field)
    {
        int lo = (field & 0x0F) + 1;
        int hi = field & 0x70;
        if (lo == 0x0A)
        {
            lo = 0;
            hi += 0x10;
        }
        if (hi == 0x60)
        {
            field = 0;
            return true;
        }
        field = (byte)(hi | lo);
        return false;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Clock
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Advances the chip by one system cycle (phi2). Runs the timers, the interrupt logic and the port output pulses
    /// through the Lorenz delay pipeline; see the class remarks for the resulting CPU-visible timing.
    /// </summary>
    public void Clock()
    {
        ulong delay = _delay;
        byte newIcr = 0;
        bool pbChanged = false;
        bool oneShotA = _oneShotA1 || (_feed & OneShotA0) != 0;
        bool oneShotB = _oneShotB1 || (_feed & OneShotB0) != 0;
        _oneShotA1 = (_feed & OneShotA0) != 0;
        _oneShotB1 = (_feed & OneShotB0) != 0;

        // --- End of the one-cycle PB6/PB7 pulses started by an underflow in the previous cycle (Lorenz PB6Low1) ---
        if ((delay & Pb6Low1) != 0 && _pb6Out)
        {
            _pb6Out = false;
            pbChanged = true;
        }
        if ((delay & Pb7Low1) != 0 && _pb7Out)
        {
            _pb7Out = false;
            pbChanged = true;
        }

        // --- Timer A ---
        // Decrement on Count3. (The counter can only be 0 here if it was reloaded with a latch of 0, in which case
        // the underflow below fires instead of wrapping; the guard mirrors VICE's "if (cnt && COUNT3)".)
        if ((delay & CountA3) != 0 && _ta != 0)
            _ta--;
        // Underflow: counter is 0 and the next count pulse is in the pipeline (Lorenz: "TA == 0 && Count2").
        bool taOut = _ta == 0 && (delay & CountA2) != 0;
        if (taOut)
        {
            newIcr |= IcrTimerA;
            delay |= LoadA1;                          // reload in this cycle
            if (oneShotA)
            {
                _cra &= 0xFE;                         // one-shot: START is cleared
                _feed &= ~CountA2;
            }
            _pb6Toggle = !_pb6Toggle;
            if ((_cra & 0x02) != 0)
            {
                if ((_cra & 0x04) != 0)
                {
                    _pb6Out = _pb6Toggle;             // toggle mode
                }
                else
                {
                    _pb6Out = true;                   // pulse mode: high for exactly one cycle
                    delay |= Pb6Low0;
                    delay &= ~Pb6Low1;
                }
                pbChanged = true;
            }
            // Serial output: one byte takes 16 underflows (shift clock = underflow / 2).
            if ((_cra & 0x40) != 0 && _sdrBitsLeft > 0 && --_sdrBitsLeft == 0)
            {
                newIcr |= IcrSerial;
                if (_sdrBuffered)
                {
                    _sdrBuffered = false;
                    _sdrBitsLeft = 16;
                }
            }
        }
        // The "just loaded" window (Hoxs64 LoadA2) is open for writes made between this Clock() and the next one.
        _loadA2 = false;
        if ((delay & LoadA1) != 0)
        {
            _ta = _latchA;
            delay &= ~CountA2;                        // a load swallows the count pulse in flight (Lorenz)
            _loadA2 = true;
        }
        // CNT pulses (Count0 → Count1) only pass in CNT mode and while the timer is started.
        if ((_cra & 0x20) == 0 || (_cra & 0x01) == 0)
            delay &= ~CountA1;

        // --- Timer B ---
        if ((delay & CountB3) != 0 && _tb != 0)
            _tb--;
        bool tbOut = _tb == 0 && (delay & CountB2) != 0;
        if (tbOut)
        {
            newIcr |= IcrTimerB;
            delay |= LoadB1;
            if (oneShotB)
            {
                _crb &= 0xFE;
                _feed &= ~CountB2;
            }
            _pb7Toggle = !_pb7Toggle;
            if ((_crb & 0x02) != 0)
            {
                if ((_crb & 0x04) != 0)
                {
                    _pb7Out = _pb7Toggle;
                }
                else
                {
                    _pb7Out = true;
                    delay |= Pb7Low0;
                    delay &= ~Pb7Low1;
                }
                pbChanged = true;
            }
        }
        _loadB2 = false;
        if ((delay & LoadB1) != 0)
        {
            _tb = _latchB;
            _loadB2 = true;
            delay &= ~CountB2;
        }
        // Timer B count source (CRB bits 5-6). Timer A underflow pulses enter at Count1 (Lorenz), so timer B
        // decrements two cycles after the timer A underflow cycle.
        switch (_crb & 0x60)
        {
            case 0x00: // phi2 (through the feed)
                delay &= ~CountB1;
                break;
            case 0x20: // CNT positive edges
                if ((_crb & 0x01) == 0)
                    delay &= ~CountB1;
                break;
            case 0x40: // timer A underflows
                if (taOut && (_crb & 0x01) != 0)
                    delay |= CountB1;
                else
                    delay &= ~CountB1;
                break;
            default:   // timer A underflows while CNT is high
                if (taOut && _cnt && (_crb & 0x01) != 0)
                    delay |= CountB1;
                else
                    delay &= ~CountB1;
                break;
        }

        // --- Other interrupt sources, recognised one cycle after the event (Lorenz) ---
        if (_flagPending)
        {
            newIcr |= IcrFlag;
            _flagPending = false;
        }
        if (_alarmPending)
        {
            newIcr |= IcrAlarm;
            _alarmPending = false;
        }
        if (_sdrPending)
        {
            newIcr |= IcrSerial;
            _sdrPending = false;
        }

        // --- Interrupt logic ---
        // IR (bit 7) is cleared two cycles after an ICR read (ClearIcr0 → ClearIcr1), the flags were cleared by the
        // read itself.
        if ((delay & ClearIcr1) != 0)
            _icr &= 0x7F;
        _icr |= newIcr;
        if ((newIcr & _imr & 0x1F) != 0)
        {
            if (Model6526A && (delay & ReadIcr0) == 0)
            {
                // 6526A: /IRQ and IR in the same cycle as the flag - unless the ICR was read in the previous cycle,
                // in which case the old-6526 delay applies (Lorenz model; Hoxs64 "bEarlyIRQ").
                delay |= Interrupt1 | SetIcr1;
            }
            else
            {
                // Old 6526: Interrupt0 → Interrupt1: /IRQ and IR one cycle after the flag.
                delay |= Interrupt0 | SetIcr0;
            }
        }
        if ((delay & SetIcr1) != 0)
            _icr |= IcrIr;
        if ((delay & Interrupt1) != 0)
            IrqLine = true;

        // --- Advance the pipeline ---
        _delay = ((delay << 1) & DelayMask) | _feed;

        if (pbChanged)
            PortBChanged?.Invoke();
    }
}
