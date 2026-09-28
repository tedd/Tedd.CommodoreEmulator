using System;

namespace Tedd.Commodore.Embedding;

/// <summary>
/// The pixel layouts a <see cref="RawFrame"/> can be written in. The names give the byte order in memory, which is
/// how graphics APIs and UI toolkits describe their formats; alpha is always 255 (opaque, so premultiplied and
/// straight alpha are the same bytes).
/// </summary>
public enum FramePixelFormat
{
    /// <summary>
    /// 4 bytes per pixel: B, G, R, A. Vulkan <c>VK_FORMAT_B8G8R8A8_UNORM</c> (or <c>_SRGB</c>), Direct3D
    /// <c>DXGI_FORMAT_B8G8R8A8_UNORM</c>, Metal <c>BGRA8Unorm</c>, Avalonia <c>PixelFormat.Bgra8888</c>, SDL
    /// <c>SDL_PIXELFORMAT_ARGB8888</c>. It is the emulator's own 0xAARRGGBB pixel on a little-endian CPU, so writing
    /// it is a plain memory copy.
    /// </summary>
    Bgra8888,

    /// <summary>
    /// 4 bytes per pixel: R, G, B, A. Vulkan <c>VK_FORMAT_R8G8B8A8_UNORM</c> (or <c>_SRGB</c>), OpenGL
    /// <c>GL_RGBA</c> + <c>GL_UNSIGNED_BYTE</c>, WebGPU <c>rgba8unorm</c>, Avalonia <c>PixelFormat.Rgba8888</c>.
    /// </summary>
    Rgba8888,

    /// <summary>
    /// 1 byte per pixel: the colour number (0-15) the video chip drew, an index into <see cref="RawFrame.Palette"/>.
    /// Upload as Vulkan <c>VK_FORMAT_R8_UINT</c> (or <c>R8_UNORM</c>) and look the colour up in a shader, which lets
    /// the host swap palettes or run a CRT/PAL filter on the real colour numbers.
    /// </summary>
    Indexed8,
}

/// <summary>Size helpers for <see cref="FramePixelFormat"/>.</summary>
public static class FramePixelFormatExtensions
{
    /// <summary>Bytes one pixel takes in <paramref name="format"/> (4, or 1 for <see cref="FramePixelFormat.Indexed8"/>).</summary>
    public static int BytesPerPixel(this FramePixelFormat format) => format switch
    {
        FramePixelFormat.Bgra8888 => 4,
        FramePixelFormat.Rgba8888 => 4,
        FramePixelFormat.Indexed8 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format"),
    };
}
