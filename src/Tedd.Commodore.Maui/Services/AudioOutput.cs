using System.Diagnostics;
using Tedd.Commodore.Hosting;
#if WINDOWS
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
#endif

namespace Tedd.Commodore.Maui.Services;

/// <summary>
/// <see cref="IAudioSink"/> that plays the 16-bit mono PCM produced by the SID resampler through the host's
/// default output device. Windows uses WASAPI (shared mode, 60 ms latency) through NAudio; a head for another
/// platform fills in the branch below, and until it does the emulator runs silently rather than not at all.
/// </summary>
public sealed class AudioOutput : IAudioSink, IDisposable
{
#if WINDOWS
    private readonly WasapiOut _out;
    private readonly BufferedWaveProvider _buffer;
    private readonly byte[] _bytes = new byte[16384];
#endif

    private AudioOutput(int sampleRate)
    {
        SampleRate = sampleRate;
#if WINDOWS
        _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1), TimeSpan.FromMilliseconds(500))
        {
            DiscardOnBufferOverflow = true,
        };
        _out = new WasapiOut(AudioClientShareMode.Shared, 60);
        _out.Init(_buffer);
        _out.Play();
#endif
    }

    /// <summary>Opens the default output device, or returns null when there is none (the reason is traced).</summary>
    public static AudioOutput? TryCreate(int sampleRate)
    {
#if WINDOWS
        try
        {
            return new AudioOutput(sampleRate);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Audio disabled: " + ex.Message);
            return null;
        }
#else
        Debug.WriteLine("Audio disabled: no output implementation for this platform.");
        return null;
#endif
    }

    public int SampleRate { get; }

    /// <summary>Queues samples for playback (called on the emulator thread).</summary>
    public void Write(ReadOnlySpan<short> samples)
    {
#if WINDOWS
        while (!samples.IsEmpty)
        {
            int n = Math.Min(samples.Length, _bytes.Length / 2);
            MemoryMarshal.AsBytes(samples[..n]).CopyTo(_bytes);
            _buffer.AddSamples(_bytes, 0, n * 2);
            samples = samples[n..];
        }
#endif
    }

    /// <summary>Drops queued audio (warp mode / resume after a pause).</summary>
    public void Clear()
    {
#if WINDOWS
        _buffer.ClearBuffer();
#endif
    }

    public void Dispose()
    {
#if WINDOWS
        _out.Stop();
        _out.Dispose();
#endif
    }
}
