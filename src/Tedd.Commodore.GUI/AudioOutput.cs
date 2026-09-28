using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.GUI;

/// <summary>
/// <see cref="IAudioSink"/> that plays the 16-bit mono PCM produced by the SID resampler through the default
/// WASAPI output device (shared mode, 60 ms latency) via a <see cref="BufferedWaveProvider"/>.
/// </summary>
public sealed class AudioOutput : IAudioSink, IDisposable
{
    private readonly WasapiOut _out;
    private readonly BufferedWaveProvider _buffer;
    private readonly byte[] _bytes = new byte[16384];

    public AudioOutput(int sampleRate)
    {
        SampleRate = sampleRate;
        _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1), TimeSpan.FromMilliseconds(500))
        {
            DiscardOnBufferOverflow = true,
        };
        _out = new WasapiOut(AudioClientShareMode.Shared, 60);
        _out.Init(_buffer);
        _out.Play();
    }

    public int SampleRate { get; }

    /// <summary>Queues samples for playback (called on the emulator thread).</summary>
    public void Write(ReadOnlySpan<short> samples)
    {
        while (!samples.IsEmpty)
        {
            int n = Math.Min(samples.Length, _bytes.Length / 2);
            MemoryMarshal.AsBytes(samples[..n]).CopyTo(_bytes);
            _buffer.AddSamples(_bytes, 0, n * 2);
            samples = samples[n..];
        }
    }

    /// <summary>Drops queued audio (warp mode / resume after a pause).</summary>
    public void Clear() => _buffer.ClearBuffer();

    public void Dispose()
    {
        _out.Stop();
        _out.Dispose();
    }
}
