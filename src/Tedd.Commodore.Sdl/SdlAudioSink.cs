using System;
using System.Threading;
using Tedd.Commodore.Hosting;
using static SDL2.SDL;

namespace Tedd.Commodore.Sdl;

/// <summary>
/// An <see cref="IAudioSink"/> on top of an SDL audio device in queue mode (<c>SDL_QueueAudio</c>): 16-bit signed
/// mono PCM at whatever rate the device gave us.
/// </summary>
/// <remarks>
/// <para>
/// Create the sink first and build the <see cref="EmulatorSession"/> with <see cref="SampleRate"/>: the device is
/// opened with <c>SDL_AUDIO_ALLOW_FREQUENCY_CHANGE</c>, so it may run at its native rate (48000 Hz is common) instead of
/// the requested one and the emulator's resampler then produces exactly that rate.
/// </para>
/// <para>
/// <see cref="Write"/> is called on the emulation thread; <c>SDL_QueueAudio</c>/<c>SDL_GetQueuedAudioSize</c>/
/// <c>SDL_ClearQueuedAudio</c> are thread-safe, so no marshalling to the SDL main thread is needed.
/// Latency control: when the queue is empty (start-up, after <see cref="Clear"/>, after an underrun) a short block of
/// silence is queued first so the device never starves on frame-to-frame jitter; when the queue grows beyond
/// <c>maxQueuedSeconds</c> (default 150 ms, i.e. the emulator is running ahead of the audio clock) the incoming block is
/// dropped.
/// </para>
/// </remarks>
public sealed class SdlAudioSink : IAudioSink, IDisposable
{
    private readonly uint _device;
    private readonly uint _maxQueuedBytes;
    private readonly byte[] _silence;
    private long _dropped;
    private long _underruns;
    private bool _disposed;

    /// <param name="requestedSampleRate">Preferred sample rate; the device may choose another one (see <see cref="SampleRate"/>).</param>
    /// <param name="bufferSamples">Device buffer size in samples (power of two).</param>
    /// <param name="maxQueuedSeconds">Queued audio above this is considered "running ahead" and new samples are dropped.</param>
    /// <param name="primeSeconds">Silence queued whenever the queue is found empty, to absorb scheduling jitter.</param>
    /// <exception cref="SdlException">No audio device could be opened.</exception>
    public SdlAudioSink(int requestedSampleRate = 44100, int bufferSamples = 1024, double maxQueuedSeconds = 0.15, double primeSeconds = 0.06)
    {
        if (SDL_InitSubSystem(SDL_INIT_AUDIO) != 0)
            throw new SdlException("SDL_InitSubSystem(SDL_INIT_AUDIO)");

        var desired = new SDL_AudioSpec
        {
            freq = requestedSampleRate,
            format = AUDIO_S16SYS,
            channels = 1,
            samples = (ushort)Math.Clamp(bufferSamples, 64, 8192),
            callback = null,
            userdata = IntPtr.Zero,
        };
        _device = SDL_OpenAudioDevice(IntPtr.Zero, 0, ref desired, out var obtained, (int)SDL_AUDIO_ALLOW_FREQUENCY_CHANGE);
        if (_device == 0)
        {
            SDL_QuitSubSystem(SDL_INIT_AUDIO);
            throw new SdlException("SDL_OpenAudioDevice");
        }

        SampleRate = obtained.freq;
        DeviceBufferSamples = obtained.samples;
        _maxQueuedBytes = (uint)(SampleRate * maxQueuedSeconds) * sizeof(short);
        _silence = new byte[(uint)(SampleRate * Math.Max(0, primeSeconds)) * sizeof(short)];
        SDL_PauseAudioDevice(_device, 0);
    }

    /// <inheritdoc />
    public int SampleRate { get; }
    /// <summary>The device buffer size SDL settled on, in samples.</summary>
    public int DeviceBufferSamples { get; }
    /// <summary>Samples discarded because the queue was already more than the allowed amount ahead.</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);
    /// <summary>Times the queue was found empty when new samples arrived (after the initial start).</summary>
    public long Underruns => Interlocked.Read(ref _underruns);
    /// <summary>Audio currently queued for the device, in seconds.</summary>
    public double QueuedSeconds => _disposed ? 0 : SDL_GetQueuedAudioSize(_device) / (double)sizeof(short) / SampleRate;

    /// <inheritdoc />
    public unsafe void Write(ReadOnlySpan<short> samples)
    {
        if (_disposed || samples.IsEmpty) return;

        uint queued = SDL_GetQueuedAudioSize(_device);
        if (queued > _maxQueuedBytes)
        {
            Interlocked.Add(ref _dropped, samples.Length);
            return;
        }
        if (queued == 0 && _silence.Length > 0)
        {
            Interlocked.Increment(ref _underruns);
            fixed (byte* s = _silence)
                SDL_QueueAudio(_device, (IntPtr)s, (uint)_silence.Length);
        }
        fixed (short* p = samples)
            SDL_QueueAudio(_device, (IntPtr)p, (uint)(samples.Length * sizeof(short)));
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (!_disposed)
            SDL_ClearQueuedAudio(_device);
    }

    /// <summary>Pauses or resumes the device (queued audio is kept).</summary>
    public void Pause(bool paused)
    {
        if (!_disposed)
            SDL_PauseAudioDevice(_device, paused ? 1 : 0);
    }

    /// <summary>Closes the device. Stop the emulation thread first so no <see cref="Write"/> is in flight.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SDL_CloseAudioDevice(_device);
        SDL_QuitSubSystem(SDL_INIT_AUDIO);
    }
}
