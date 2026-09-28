using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Embedding;

/// <summary>Settings for an <see cref="EmbeddedEmulator"/>; everything has a usable default.</summary>
public sealed class EmbeddedEmulatorOptions
{
    /// <summary>
    /// Rate of the audio the host plays, in Hz; the SID is resampled to exactly this rate. Use the rate the audio
    /// device was opened with. Default 48000.
    /// </summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>Attach a 1541 drive as device 8 when the ROM set includes the 1541 DOS ROM. Default true.</summary>
    public bool AttachDrive { get; set; } = true;

    /// <summary>Physical key (W3C <c>KeyboardEvent.code</c>) to C64 key/joystick table; null = the default table for the machine.</summary>
    public KeyBindings? Bindings { get; set; }

    /// <summary>C128 only: start with the 40/80 DISPLAY key down, so it boots on the 80 column VDC screen.</summary>
    public bool Columns80 { get; set; }

    /// <summary>Queued audio beyond which incoming blocks are dropped (<see cref="AudioOutput.MaxLatencySeconds"/>). Default 0.15 s.</summary>
    public double AudioMaxLatencySeconds { get; set; } = 0.15;

    /// <summary>Silence put in front of audio that finds the queue empty (<see cref="AudioOutput.PrimeSeconds"/>). Default 0.06 s.</summary>
    public double AudioPrimeSeconds { get; set; } = 0.06;
}
