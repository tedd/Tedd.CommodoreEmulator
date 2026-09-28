using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Web.Interop;

/// <summary>
/// <see cref="IAudioSink"/> that feeds the AudioWorklet in <c>c64-audio-worklet.js</c>. Every
/// <see cref="Write"/> (one per emulated frame, about 20 ms of audio) is posted to the worklet as a 16-bit
/// PCM chunk; the worklet converts to float, keeps a ring buffer and plays silence on underrun.
/// The sample rate must be the AudioContext's rate (returned by <see cref="C64Js.AudioStart"/>), and the
/// <c>EmulatorSession</c> must have been created with the same rate.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserAudioSink : IAudioSink
{
    public BrowserAudioSink(int sampleRate) => SampleRate = sampleRate;

    public int SampleRate { get; }

    /// <summary>When true, samples are dropped instead of queued.</summary>
    public bool Muted { get; set; }

    /// <summary>Total samples queued so far.</summary>
    public long SamplesWritten { get; private set; }

    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return;
        SamplesWritten += samples.Length;
        if (Muted) return;
        // The marshaller wants a Span<byte>; reinterpret the PCM in place (no copy until JS slices it).
        var writable = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(samples), samples.Length);
        C64Js.AudioWrite(MemoryMarshal.AsBytes(writable));
    }

    public void Clear() => C64Js.AudioClear();
}
