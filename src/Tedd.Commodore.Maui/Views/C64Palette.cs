using Tedd.Maui;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Maui.Views;

/// <summary>The 16 C64 colors as MAUI colors, plus their names, in <see cref="VicII.Palette"/> order.</summary>
internal static class C64Palette
{
    /// <summary>Palette entries 0-15.</summary>
    public static readonly Color[] Colors = Build();

    /// <summary>The same colors at half opacity, for the sprite boxes of the layout view.</summary>
    public static readonly Color[] Translucent = Build(0.5f);

    /// <summary>
    /// The same colors packed into the platform's native, premultiplied pixel layout, which is what a
    /// <see cref="PixelSurface"/> is written in. Asked of the package rather than assumed, because the layout
    /// differs between Windows and the mobile heads.
    /// </summary>
    public static readonly uint[] Native = BuildNative();

    public static readonly string[] Names =
    {
        "black", "white", "red", "cyan", "purple", "green", "blue", "yellow",
        "orange", "brown", "light red", "dark grey", "grey", "light green", "light blue", "light grey",
    };

    /// <summary>The same names abbreviated, for the narrow color pickers.</summary>
    public static readonly string[] ShortNames =
    {
        "black", "white", "red", "cyan", "purple", "green", "blue", "yellow",
        "orange", "brown", "lt red", "dk grey", "grey", "lt green", "lt blue", "lt grey",
    };

    public static Color Of(int index) => Colors[index & 15];

    /// <summary>Palette entry <paramref name="index"/> as a <see cref="PixelSurface"/> pixel.</summary>
    public static uint NativeOf(int index) => Native[index & 15];

    /// <summary>Black or white, whichever stays readable on <paramref name="index"/>.</summary>
    public static Color ContrastOf(int index)
    {
        uint argb = VicII.Palette[index & 15];
        int luma = ((int)((argb >> 16) & 0xFF) * 299 + (int)((argb >> 8) & 0xFF) * 587 + (int)(argb & 0xFF) * 114) / 1000;
        return luma > 110 ? Microsoft.Maui.Graphics.Colors.Black : Microsoft.Maui.Graphics.Colors.White;
    }

    private static uint[] BuildNative()
    {
        var native = new uint[Colors.Length];
        for (int i = 0; i < native.Length; i++)
            native[i] = WriteableBitmap.FromColor(Colors[i]);
        return native;
    }

    private static Color[] Build(float alpha = 1f)
    {
        var colors = new Color[VicII.Palette.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            uint argb = VicII.Palette[i];
            colors[i] = Color.FromRgba((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(alpha * 255));
        }
        return colors;
    }
}
