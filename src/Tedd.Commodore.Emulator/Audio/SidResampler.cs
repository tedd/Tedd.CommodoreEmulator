using System;
using System.Threading;

namespace Tedd.Commodore.Emulator.Audio;

/// <summary>
/// Converts the per-cycle output of a <see cref="Sid6581"/> into 16-bit mono PCM at a fixed sample rate.
///
/// Every output sample is the average of the SID output over exactly <c>clockFrequency / sampleRate</c> cycles
/// (a box filter; the cycle in which a sample boundary falls is split proportionally between the two samples).
/// The sample boundary is tracked with an exact accumulator, so N system cycles always yield
/// <c>floor(N * sampleRate / clockFrequency)</c> samples with no long-term drift.
///
/// Samples are queued in a ring buffer that is safe for one producer thread (calling <see cref="Clock"/>) and
/// one consumer thread (calling <see cref="Read"/>). When the buffer is full new samples are dropped and counted
/// in <see cref="Overruns"/>.
/// </summary>
public sealed class SidResampler
{
    private readonly Sid6581 _sid;
    private readonly int _sampleRate;
    private readonly double _clockFrequency;
    private readonly double _cyclesPerSample;
    private readonly short[] _buffer;

    private long _written;      // total samples written to the ring buffer (producer side)
    private long _read;         // total samples read from the ring buffer (consumer side)
    private double _phase;      // position inside the current sample window, in units of 1/sampleRate cycles: 0 .. clockFrequency
    private double _accumulator;// integrated SID output over the current sample window (cycle units)

    /// <param name="sid">The chip to clock and sample.</param>
    /// <param name="sampleRate">Output sample rate in Hz (must not exceed the clock frequency).</param>
    /// <param name="clockFrequency">System clock in Hz, default PAL 985 248 Hz.</param>
    /// <param name="bufferSize">Ring buffer capacity in samples; default one second of audio.</param>
    public SidResampler(Sid6581 sid, int sampleRate, double clockFrequency = Sid6581.PalClockFrequency, int bufferSize = 0)
    {
        _sid = sid ?? throw new ArgumentNullException(nameof(sid));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!(clockFrequency > 0) || double.IsInfinity(clockFrequency))
            throw new ArgumentOutOfRangeException(nameof(clockFrequency));
        if (sampleRate > clockFrequency)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "The sample rate must not exceed the clock frequency.");
        _sampleRate = sampleRate;
        _clockFrequency = clockFrequency;
        _cyclesPerSample = clockFrequency / sampleRate;
        _buffer = new short[bufferSize > 0 ? bufferSize : sampleRate];
    }

    /// <summary>The chip being sampled.</summary>
    public Sid6581 Sid => _sid;

    /// <summary>Output sample rate in Hz.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>System clock frequency in Hz.</summary>
    public double ClockFrequency => _clockFrequency;

    /// <summary>Ring buffer capacity in samples.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Number of samples waiting in the ring buffer.</summary>
    public int Available => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    /// <summary>Total number of samples produced, including dropped ones.</summary>
    public long SamplesProduced { get; private set; }

    /// <summary>Number of samples dropped because the ring buffer was full.</summary>
    public long Overruns { get; private set; }

    /// <summary>Runs one system cycle of the SID and accumulates its output into the current sample.</summary>
    public void Clock()
    {
        _sid.Clock();
        float v = _sid.Output;

        double next = _phase + _sampleRate;
        if (next < _clockFrequency)
        {
            // Whole cycle belongs to the current sample window.
            _accumulator += v;
            _phase = next;
            return;
        }

        // The sample boundary falls inside this cycle: the first fraction f of the cycle completes the current
        // window, the remainder starts the next one.
        double f = (_clockFrequency - _phase) / _sampleRate;
        _accumulator += v * f;
        Emit(_accumulator / _cyclesPerSample);
        _accumulator = v * (1.0 - f);
        _phase = next - _clockFrequency;
    }

    /// <summary>
    /// Drains up to <paramref name="destination"/>.Length samples from the ring buffer. Returns the number of
    /// samples copied (0 when nothing is available).
    /// </summary>
    public int Read(Span<short> destination)
    {
        long r = _read;
        long w = Volatile.Read(ref _written);
        int n = (int)Math.Min(w - r, destination.Length);
        if (n <= 0)
            return 0;

        int start = (int)(r % _buffer.Length);
        int first = Math.Min(n, _buffer.Length - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        if (n > first)
            _buffer.AsSpan(0, n - first).CopyTo(destination.Slice(first));

        Volatile.Write(ref _read, r + n);
        return n;
    }

    private void Emit(double value)
    {
        SamplesProduced++;
        int s = (int)Math.Round(value * 32767.0);
        if (s > short.MaxValue) s = short.MaxValue;
        else if (s < short.MinValue) s = short.MinValue;

        long w = _written;
        if (w - Volatile.Read(ref _read) >= _buffer.Length)
        {
            Overruns++;
            return;
        }
        _buffer[(int)(w % _buffer.Length)] = (short)s;
        Volatile.Write(ref _written, w + 1);
    }
}
