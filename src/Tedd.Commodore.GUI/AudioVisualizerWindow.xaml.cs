using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using Tedd.Commodore.Emulator.Audio;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.GUI;

/// <summary>
/// Debug window showing what the SID produces: an oscilloscope and spectrum of the mixed output (read from the
/// <see cref="AudioTap"/> in front of the sound card) and the voice/filter registers (read through the runner,
/// so it works while the machine runs and while it is frozen). Updated about 30 times per second.
/// </summary>
public partial class AudioVisualizerWindow : Window
{
    private const int FftSize = 1024;
    private const int BarCount = 64;
    private const float FloorDb = -60f;

    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private readonly EmulatorRunner _runner;
    private readonly AudioTap _tap;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Fft _fft = new(FftSize);
    private readonly short[] _samples = new short[FftSize];
    private readonly float[] _magnitudes = new float[FftSize / 2];
    private readonly float[] _bars = new float[BarCount];
    private readonly int[] _barStart = new int[BarCount + 1];
    private readonly byte[] _regs = new byte[Sid6581.RegModeVolume + 1];
    private readonly byte[] _envelopes = new byte[3];
    private readonly int[] _waveforms = new int[3];
    private readonly Action _readSid;
    private readonly StringBuilder _sb = new();
    private readonly TextBlock[] _voiceTexts;
    private readonly Rectangle[] _voiceEnvs;

    public AudioVisualizerWindow(EmulatorRunner runner, AudioTap tap)
    {
        InitializeComponent();
        _runner = runner;
        _tap = tap;
        _readSid = ReadSid;
        _voiceTexts = new[] { Voice1Text, Voice2Text, Voice3Text };
        _voiceEnvs = new[] { Voice1Env, Voice2Env, Voice3Env };
        BuildBands();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Log-spaced bands from ~40 Hz to Nyquist, at least one FFT bin wide each.</summary>
    private void BuildBands()
    {
        int bins = FftSize / 2;
        double binHz = (double)_tap.SampleRate / FftSize;
        double fMin = Math.Max(binHz, 40.0);
        double fMax = _tap.SampleRate / 2.0;
        var labels = new string?[BarCount];
        for (int i = 0; i <= BarCount; i++)
        {
            double f = fMin * Math.Pow(fMax / fMin, (double)i / BarCount);
            int bin = Math.Clamp((int)Math.Round(f / binHz), 1, bins);
            if (i > 0 && bin <= _barStart[i - 1]) bin = Math.Min(bins, _barStart[i - 1] + 1);
            _barStart[i] = bin;
            if (i < BarCount && i % 8 == 0)
            {
                double hz = _barStart[i] * binHz;
                labels[i] = hz >= 1000 ? $"{hz / 1000:0.#}k" : $"{hz:0}";
            }
        }
        Spectrum.SetLabels(labels);
    }

    private void ReadSid()
    {
        var sid = _runner.Session.Machine.Sid;
        for (int r = 0; r < _regs.Length; r++)
            _regs[r] = sid.Peek(r);
        for (int v = 0; v < 3; v++)
        {
            _envelopes[v] = sid.PeekEnvelope(v);
            _waveforms[v] = sid.PeekWaveform(v);
        }
    }

    private void Tick()
    {
        // Mixed output: scope, level and spectrum.
        _tap.CopyLatest(_samples);
        double sumSquares = 0;
        int peak = 0;
        for (int i = 0; i < _samples.Length; i++)
        {
            int s = _samples[i];
            sumSquares += (double)s * s;
            int a = s < 0 ? -s : s;
            if (a > peak) peak = a;
        }
        double rms = Math.Sqrt(sumSquares / _samples.Length) / 32768.0;
        double peakLevel = peak / 32768.0;
        LevelText.Text = $"Level: RMS {ToDb(rms),6:0.0} dB   Peak {ToDb(peakLevel),6:0.0} dB   ({_tap.SampleRate} Hz, {_tap.TotalSamples:N0} samples)";

        _fft.MagnitudeSpectrum(_samples, _magnitudes);
        for (int b = 0; b < BarCount; b++)
        {
            float max = 0f;
            int end = Math.Max(_barStart[b] + 1, _barStart[b + 1]);
            for (int k = _barStart[b]; k < end && k < _magnitudes.Length; k++)
                if (_magnitudes[k] > max) max = _magnitudes[k];
            float db = max <= 1e-6f ? FloorDb : 20f * MathF.Log10(max);
            _bars[b] = Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);
        }
        Scope.SetSamples(_samples);
        Spectrum.SetBars(_bars);

        // SID registers (between frames, or immediately when frozen).
        _runner.Invoke(_readSid);
        for (int v = 0; v < 3; v++)
            UpdateVoice(v);
        UpdateFilter();
        StateText.Text = _runner.Paused ? "Frozen" : _runner.Warp ? "Warp" : "Running";
    }

    private static double ToDb(double level) => level <= 1e-6 ? -120.0 : 20.0 * Math.Log10(level);

    private void UpdateVoice(int voice)
    {
        int b = voice * Sid6581.VoiceRegisterStride;
        int freq = _regs[b + Sid6581.RegFreqLo] | (_regs[b + Sid6581.RegFreqHi] << 8);
        int pw = _regs[b + Sid6581.RegPwLo] | ((_regs[b + Sid6581.RegPwHi] & 0x0F) << 8);
        byte control = _regs[b + Sid6581.RegControl];
        byte ad = _regs[b + Sid6581.RegAttackDecay];
        byte sr = _regs[b + Sid6581.RegSustainRelease];
        double hz = freq * C64.ClockFrequency / 16777216.0;

        var sb = _sb;
        sb.Clear();
        sb.Append("Freq $").Append(freq.ToString("X4")).Append("  ").Append(hz.ToString("0.0")).Append(" Hz");
        AppendNote(sb, hz);
        sb.Append('\n');
        sb.Append("PW   $").Append(pw.ToString("X3")).Append("  ").Append((pw * 100.0 / 4096.0).ToString("0.0")).Append(" %\n");
        sb.Append("Wave ");
        AppendFlag(sb, (control & Sid6581.ControlTriangle) != 0, "TRI");
        AppendFlag(sb, (control & Sid6581.ControlSawtooth) != 0, "SAW");
        AppendFlag(sb, (control & Sid6581.ControlPulse) != 0, "PUL");
        AppendFlag(sb, (control & Sid6581.ControlNoise) != 0, "NOI");
        sb.Append('\n');
        sb.Append("Ctrl ");
        AppendFlag(sb, (control & Sid6581.ControlGate) != 0, "GATE");
        AppendFlag(sb, (control & Sid6581.ControlSync) != 0, "SYNC");
        AppendFlag(sb, (control & Sid6581.ControlRingMod) != 0, "RING");
        AppendFlag(sb, (control & Sid6581.ControlTest) != 0, "TEST");
        sb.Append('\n');
        sb.Append("ADSR A:").Append((ad >> 4).ToString("X")).Append(" D:").Append((ad & 0x0F).ToString("X"))
          .Append(" S:").Append((sr >> 4).ToString("X")).Append(" R:").Append((sr & 0x0F).ToString("X")).Append('\n');
        sb.Append("Env  $").Append(_envelopes[voice].ToString("X2")).Append(" (").Append(_envelopes[voice]).Append(")  Out $")
          .Append(_waveforms[voice].ToString("X3"));
        _voiceTexts[voice].Text = sb.ToString();

        var track = (FrameworkElement?)_voiceEnvs[voice].Parent;
        double trackWidth = track?.ActualWidth ?? 0;
        _voiceEnvs[voice].Width = trackWidth * _envelopes[voice] / 255.0;
    }

    private void UpdateFilter()
    {
        int fc = ((_regs[Sid6581.RegFilterCutoffHi] << 3) | (_regs[Sid6581.RegFilterCutoffLo] & 0x07)) & 0x7FF;
        byte resRoute = _regs[Sid6581.RegFilterResonanceRouting];
        byte modeVol = _regs[Sid6581.RegModeVolume];

        var sb = _sb;
        sb.Clear();
        sb.Append("Cutoff $").Append(fc.ToString("X3")).Append("  ~").Append(Sid6581.CutoffFrequencyHz(fc).ToString("0")).Append(" Hz\n");
        sb.Append("Res    $").Append((resRoute >> 4).ToString("X")).Append('\n');
        sb.Append("Route  ");
        AppendFlag(sb, (resRoute & Sid6581.FilterVoice1) != 0, "V1");
        AppendFlag(sb, (resRoute & Sid6581.FilterVoice2) != 0, "V2");
        AppendFlag(sb, (resRoute & Sid6581.FilterVoice3) != 0, "V3");
        AppendFlag(sb, (resRoute & Sid6581.FilterExternal) != 0, "EXT");
        sb.Append('\n');
        sb.Append("Mode   ");
        AppendFlag(sb, (modeVol & Sid6581.ModeLowPass) != 0, "LP");
        AppendFlag(sb, (modeVol & Sid6581.ModeBandPass) != 0, "BP");
        AppendFlag(sb, (modeVol & Sid6581.ModeHighPass) != 0, "HP");
        AppendFlag(sb, (modeVol & Sid6581.ModeVoice3Off) != 0, "3OFF");
        sb.Append('\n');
        sb.Append("Volume $").Append((modeVol & 0x0F).ToString("X")).Append(" (").Append(modeVol & 0x0F).Append("/15)\n");
        sb.Append("Regs   $D415-$D418: ")
          .Append(_regs[Sid6581.RegFilterCutoffLo].ToString("X2")).Append(' ')
          .Append(_regs[Sid6581.RegFilterCutoffHi].ToString("X2")).Append(' ')
          .Append(resRoute.ToString("X2")).Append(' ')
          .Append(modeVol.ToString("X2"));
        FilterText.Text = sb.ToString();
    }

    private static void AppendFlag(StringBuilder sb, bool set, string name)
    {
        if (set) sb.Append(name);
        else sb.Append('-', name.Length);
        sb.Append(' ');
    }

    /// <summary>Appends the nearest note name and cents ("A-4 +3") for audible frequencies.</summary>
    private static void AppendNote(StringBuilder sb, double hz)
    {
        if (hz < 8.0 || hz > 20000.0) return;
        double midi = 69.0 + 12.0 * Math.Log2(hz / 440.0);
        int nearest = (int)Math.Round(midi);
        int cents = (int)Math.Round((midi - nearest) * 100.0);
        int octave = nearest / 12 - 1;
        var name = NoteNames[((nearest % 12) + 12) % 12];
        sb.Append("  ").Append(name).Append(name.Length == 1 ? "-" : "").Append(octave);
        if (cents != 0) sb.Append(' ').Append(cents > 0 ? "+" : "").Append(cents);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
