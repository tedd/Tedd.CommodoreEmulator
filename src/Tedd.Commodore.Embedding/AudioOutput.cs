using System;
using System.Threading;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Embedding;

/// <summary>
/// The sound side of an embedded emulator: an <see cref="IAudioSink"/> that queues the SID's 16-bit mono samples
/// and hands them to the host's audio code as 32-bit float or 16-bit integer PCM with any number of interleaved
/// channels (the mono signal goes to every channel).
/// </summary>
/// <remarks>
/// <para>
/// Two ways to take the samples: <see cref="Fill(Span{float}, int)"/> for pull APIs whose callback must hand back
/// a full buffer (miniaudio, SDL's callback mode, PortAudio, NAudio, Unity's <c>OnAudioFilterRead</c>): a shortfall
/// becomes silence and is counted in <see cref="Underruns"/>. <see cref="Read(Span{float}, int)"/> for push APIs
/// that queue whatever there is (OpenAL buffer queues, XAudio2, <c>SDL_QueueAudio</c>): it copies only what has
/// been produced.
/// </para>
/// <para>
/// Latency is kept in check the way the SDL front-end does it: when a block arrives and the queue is empty (start,
/// after <see cref="Clear"/> or an underrun) <see cref="PrimeSeconds"/> of silence goes in first, so the next
/// frame-to-frame jitter does not starve the device; when more than <see cref="MaxLatencySeconds"/> is queued
/// (the emulator runs ahead of the audio clock) the incoming block is dropped and counted in
/// <see cref="DroppedSamples"/>.
/// </para>
/// <para>
/// Threading: one producer (the thread that runs the emulator, <see cref="Write"/>) and one consumer (the audio
/// thread, <see cref="Read(Span{float}, int)"/>/<see cref="Fill(Span{float}, int)"/>), lock free.
/// <see cref="Clear"/> may be called from any thread.
/// </para>
/// </remarks>
public sealed class AudioOutput : IAudioSink
{
    /// <summary>Largest block <see cref="EmulatorSession.RunFrame"/> writes at once.</summary>
    private const int MaxBlock = 8192;

    private readonly short[] _ring;
    private readonly int _maxQueued;
    private readonly int _prime;
    private long _written;     // producer cursor: samples ever written
    private long _read;        // consumer cursor: samples ever read
    private long _clearedTo;   // everything before this position is discarded (set by Clear)
    private long _dropped;
    private long _underruns;

    /// <param name="sampleRate">Rate of the samples, which is the rate the session's SID resampler was created with.</param>
    /// <param name="maxLatencySeconds">Queued audio above this means the emulator runs ahead; new blocks are dropped.</param>
    /// <param name="primeSeconds">Silence queued in front of a block that finds the queue empty.</param>
    public AudioOutput(int sampleRate, double maxLatencySeconds = 0.15, double primeSeconds = 0.06)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!(maxLatencySeconds > 0) || maxLatencySeconds > 10) throw new ArgumentOutOfRangeException(nameof(maxLatencySeconds));
        if (!(primeSeconds >= 0) || primeSeconds > maxLatencySeconds) throw new ArgumentOutOfRangeException(nameof(primeSeconds));
        SampleRate = sampleRate;
        MaxLatencySeconds = maxLatencySeconds;
        PrimeSeconds = primeSeconds;
        _maxQueued = (int)Math.Round(sampleRate * maxLatencySeconds);
        _prime = (int)Math.Round(sampleRate * primeSeconds);
        _ring = new short[_maxQueued + _prime + MaxBlock];
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>Queued audio beyond which incoming blocks are dropped, in seconds.</summary>
    public double MaxLatencySeconds { get; }

    /// <summary>Silence put in front of a block that finds the queue empty, in seconds.</summary>
    public double PrimeSeconds { get; }

    /// <summary>Capacity of the queue in samples.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Samples (per channel) waiting to be read.</summary>
    public int Available => (int)(Volatile.Read(ref _written) - ReadPosition());

    /// <summary>Audio waiting to be read, in seconds: the latency this queue adds.</summary>
    public double BufferedSeconds => Available / (double)SampleRate;

    /// <summary>Sample frames <see cref="Fill(Span{float}, int)"/> had to fill with silence because nothing was queued.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Samples dropped because more than <see cref="MaxLatencySeconds"/> was queued already.</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return;
        long w = _written;
        int queued = (int)(w - ReadPosition());
        if (queued > _maxQueued)
        {
            Interlocked.Add(ref _dropped, samples.Length);
            return;
        }
        if (queued == 0 && _prime > 0)
        {
            for (int i = 0; i < _prime; i++)
                _ring[(int)((w + i) % _ring.Length)] = 0;
            w += _prime;
            queued = _prime;
        }
        int n = Math.Min(samples.Length, _ring.Length - queued);
        if (n < samples.Length)
            Interlocked.Add(ref _dropped, samples.Length - n);
        int start = (int)(w % _ring.Length);
        int first = Math.Min(n, _ring.Length - start);
        samples.Slice(0, first).CopyTo(_ring.AsSpan(start));
        samples.Slice(first, n - first).CopyTo(_ring);
        Volatile.Write(ref _written, w + n);
    }

    /// <summary>Discards everything queued so far (entering warp, pausing, resetting). Safe from any thread.</summary>
    public void Clear()
    {
        long w = Volatile.Read(ref _written);
        long c;
        do
        {
            c = Volatile.Read(ref _clearedTo);
            if (c >= w) return;
        }
        while (Interlocked.CompareExchange(ref _clearedTo, w, c) != c);
    }

    /// <summary>
    /// Copies up to <c>destination.Length / channels</c> sample frames as floats in -1..1, the same value in each of
    /// the <paramref name="channels"/> interleaved channels. Returns the number of frames copied; the rest of
    /// <paramref name="destination"/> is left alone.
    /// </summary>
    public int Read(Span<float> destination, int channels = 2)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        return Take(destination.Length / channels, destination, default, channels);
    }

    /// <summary>
    /// Copies up to <c>destination.Length / channels</c> sample frames as 16-bit integers, the same value in each of
    /// the <paramref name="channels"/> interleaved channels. Returns the number of frames copied; the rest of
    /// <paramref name="destination"/> is left alone.
    /// </summary>
    public int Read(Span<short> destination, int channels = 2)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        return Take(destination.Length / channels, default, destination, channels);
    }

    /// <summary>
    /// Fills all of <paramref name="destination"/>: the queued audio first, silence for the rest (counted in
    /// <see cref="Underruns"/>). Returns the number of sample frames that carried emulator audio. This is the call
    /// for an audio callback that must always return a full buffer.
    /// </summary>
    public int Fill(Span<float> destination, int channels = 2)
    {
        int n = Read(destination, channels);
        PadWithSilence(destination, n * channels, destination.Length / channels - n);
        return n;
    }

    /// <summary>16-bit version of <see cref="Fill(Span{float}, int)"/>.</summary>
    public int Fill(Span<short> destination, int channels = 2)
    {
        int n = Read(destination, channels);
        PadWithSilence(destination, n * channels, destination.Length / channels - n);
        return n;
    }

    private void PadWithSilence<T>(Span<T> destination, int from, int missingFrames)
    {
        destination.Slice(from).Clear();
        if (missingFrames > 0)
            Interlocked.Add(ref _underruns, missingFrames);
    }

    /// <summary>The consumer's position, moved past anything <see cref="Clear"/> discarded.</summary>
    private long ReadPosition() => Math.Max(Volatile.Read(ref _read), Volatile.Read(ref _clearedTo));

    private int Take(int frames, Span<float> floats, Span<short> shorts, int channels)
    {
        long r = ReadPosition();
        int n = (int)Math.Min(Volatile.Read(ref _written) - r, frames);
        if (n <= 0) return 0;
        int o = 0;
        for (int i = 0; i < n; i++)
        {
            short s = _ring[(int)((r + i) % _ring.Length)];
            if (floats.IsEmpty)
            {
                for (int c = 0; c < channels; c++)
                    shorts[o++] = s;
            }
            else
            {
                float v = s * (1f / 32768f);
                for (int c = 0; c < channels; c++)
                    floats[o++] = v;
            }
        }
        Volatile.Write(ref _read, r + n);
        return n;
    }
}
