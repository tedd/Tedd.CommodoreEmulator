using System.Numerics;

namespace Tedd.Commodore.Maui.Views;

/// <summary>
/// Which bytes of a memory dump were just read or written, and how long ago: the state behind the hex grid's
/// fading highlights. One byte of heat per address: bit 7 = the last access was a write (orange, otherwise
/// green), bits 0-6 = the intensity left, which <see cref="Decay"/> counts down from <see cref="MaxHeat"/> to
/// zero over <see cref="FadeMilliseconds"/>.
/// <para>
/// The work per frame is proportional to what is lit, not to the size of the dump. <c>_active</c> is a bitmap
/// (8 KB for a 64K dump) with a bit per address whose heat is non-zero; it is scanned a <see cref="Vector{T}"/>
/// at a time, so an idle map costs a few hundred loads and only the words that hold something are walked. The
/// rows whose look changed are collected in a second bitmap for the view to repaint. Nothing here is per row:
/// a row that is off screen is never touched, and one that scrolls into view reads the heat when it paints.
/// </para>
/// </summary>
public sealed class HexHeatMap
{
    /// <summary>How long a highlight takes to fade out completely.</summary>
    public const int FadeMilliseconds = 500;

    /// <summary>Intensity of a byte that was touched this instant.</summary>
    public const int MaxHeat = 127;

    private const byte WriteBit = 0x80;

    private readonly byte[] _heat;
    private readonly ulong[] _active;
    private readonly ulong[] _dirtyRows;

    /// <param name="size">Bytes in the dump: a multiple of 16.</param>
    public HexHeatMap(int size)
    {
        Size = size;
        _heat = new byte[size];
        _active = new ulong[(size + 63) >> 6];
        _dirtyRows = new ulong[((size >> 4) + 63) >> 6];
    }

    public int Size { get; }

    /// <summary>Heat of one byte: 0 = nothing, otherwise bit 7 = written (else read) and bits 0-6 the intensity.</summary>
    public byte this[int offset] => _heat[offset];

    public static bool IsWrite(byte heat) => (heat & WriteBit) != 0;

    public static int Level(byte heat) => heat & MaxHeat;

    /// <summary>The byte was just accessed: full intensity, and its row is due for a repaint.</summary>
    public void Touch(int offset, bool write)
    {
        _heat[offset] = (byte)((write ? WriteBit : 0) | MaxHeat);
        _active[offset >> 6] |= 1UL << (offset & 63);
        MarkRowDirty(offset >> 4);
    }

    /// <summary>Flags a 16-byte row for repainting for a reason other than heat (its bytes changed).</summary>
    public void MarkRowDirty(int row) => _dirtyRows[row >> 6] |= 1UL << (row & 63);

    /// <summary>Fades every lit byte by the share of the fade time that has passed.</summary>
    public void Decay(int elapsedMs)
    {
        if (elapsedMs <= 0) return;
        int step = Math.Max(1, MaxHeat * elapsedMs / FadeMilliseconds);
        for (int w = NextNonZeroWord(_active, 0); w >= 0; w = NextNonZeroWord(_active, w + 1))
        {
            ulong remaining = _active[w];
            for (ulong bits = remaining; bits != 0; bits &= bits - 1)
            {
                int offset = (w << 6) | BitOperations.TrailingZeroCount(bits);
                byte h = _heat[offset];
                int level = (h & MaxHeat) - step;
                if (level > 0)
                {
                    _heat[offset] = (byte)((h & WriteBit) | level);
                }
                else
                {
                    _heat[offset] = 0;
                    remaining &= ~(1UL << (offset & 63));
                }
                MarkRowDirty(offset >> 4);
            }
            _active[w] = remaining;
        }
    }

    /// <summary>Hands out (and forgets) the rows that need repainting, lowest first.</summary>
    public void DrainDirtyRows(Action<int> onRow)
    {
        for (int w = NextNonZeroWord(_dirtyRows, 0); w >= 0; w = NextNonZeroWord(_dirtyRows, w + 1))
        {
            ulong bits = _dirtyRows[w];
            _dirtyRows[w] = 0;
            for (; bits != 0; bits &= bits - 1)
                onRow((w << 6) | BitOperations.TrailingZeroCount(bits));
        }
    }

    public void Clear()
    {
        Array.Clear(_heat);
        Array.Clear(_active);
        Array.Clear(_dirtyRows);
    }

    /// <summary>
    /// Index of the first non-zero word at or after <paramref name="from"/>, or -1. The empty stretches, which
    /// is nearly all of a bitmap, go by a vector at a time; only a chunk that holds something is looked at
    /// word by word.
    /// </summary>
    public static int NextNonZeroWord(ReadOnlySpan<ulong> words, int from)
    {
        int i = from;
        int width = Vector<ulong>.Count;
        while (i + width <= words.Length)
        {
            if (new Vector<ulong>(words.Slice(i, width)) != Vector<ulong>.Zero)
                break;
            i += width;
        }
        for (; i < words.Length; i++)
            if (words[i] != 0)
                return i;
        return -1;
    }
}
