using System;
using System.Runtime.CompilerServices;

namespace Tedd.Commodore.Emulator.Drive;

/// <summary>
/// One half-track of a 1541 disk as a raw bit stream: the sequence of bit cells that passes under the head
/// during one revolution. The drive mechanics (<c>DiskUnit</c>) read it one bit per bit cell and write to it
/// one bit per bit cell; this class knows nothing about sync marks, GCR or sectors (see <see cref="GcrDisk"/>).
///
/// Bits are stored MSB first in <see cref="Data"/>: bit <c>p</c> of the track is bit <c>7 - (p &amp; 7)</c> of
/// byte <c>p &gt;&gt; 3</c>. Positions wrap around modulo <see cref="BitLength"/> (the track is a loop).
/// The length of a track is fixed at construction: at a constant 300 RPM and a constant bit rate (selected by
/// the speed zone, see <see cref="GcrDisk.SpeedZone"/>) one revolution always contains the same number of bit
/// cells, no matter what is written into them.
/// </summary>
public sealed class GcrTrack
{
    private readonly byte[] _data;
    private readonly int _bitLength;

    /// <summary>Creates a track of <paramref name="bitLength"/> bit cells, all 0 (no flux transitions: unformatted).</summary>
    public GcrTrack(int bitLength)
    {
        if (bitLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitLength), "A track needs at least one bit cell.");
        _bitLength = bitLength;
        _data = new byte[(bitLength + 7) >> 3];
    }

    /// <summary>Wraps an existing bit stream (the array is used directly, not copied).</summary>
    public GcrTrack(byte[] data, int bitLength)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (bitLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitLength), "A track needs at least one bit cell.");
        if ((long)data.Length * 8 < bitLength)
            throw new ArgumentException("The data array is too small for the requested bit length.", nameof(data));
        _data = data;
        _bitLength = bitLength;
    }

    /// <summary>Creates a track from whole bytes (bit length = 8 × byte count); the bytes are copied.</summary>
    public static GcrTrack FromBytes(ReadOnlySpan<byte> bytes) => new(bytes.ToArray(), bytes.Length * 8);

    /// <summary>Creates an unformatted track with the nominal length of a half-track's speed zone (see <see cref="GcrDisk.TrackLength"/>).</summary>
    public static GcrTrack CreateUnformatted(int halfTrack) => new(GcrDisk.TrackLength(halfTrack) * 8);

    /// <summary>Speed zone (0..3) of a half-track; same as <see cref="GcrDisk.SpeedZone"/>.</summary>
    public static int SpeedZone(int halfTrack) => GcrDisk.SpeedZone(halfTrack);

    /// <summary>The backing bit stream, MSB first. Bits beyond <see cref="BitLength"/> in the last byte are unused.</summary>
    public byte[] Data => _data;

    /// <summary>Number of bit cells on the track.</summary>
    public int BitLength => _bitLength;

    /// <summary>Number of bytes needed to hold <see cref="BitLength"/> bits.</summary>
    public int ByteLength => (_bitLength + 7) >> 3;

    /// <summary>Set by every write; the host clears it after saving the disk.</summary>
    public bool Modified { get; set; }

    /// <summary>Normalises a bit position into 0..<see cref="BitLength"/>-1 (also for negative positions).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Wrap(int position)
    {
        if ((uint)position < (uint)_bitLength)
            return position;
        position %= _bitLength;
        return position < 0 ? position + _bitLength : position;
    }

    /// <summary>Reads the bit cell at <paramref name="position"/> (wrapping). Returns 0 or 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit(int position)
    {
        position = Wrap(position);
        return (_data[position >> 3] >> (7 - (position & 7))) & 1;
    }

    /// <summary>Writes the bit cell at <paramref name="position"/> (wrapping); only bit 0 of <paramref name="bit"/> is used.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBit(int position, int bit)
    {
        position = Wrap(position);
        int mask = 0x80 >> (position & 7);
        ref byte b = ref _data[position >> 3];
        if ((bit & 1) != 0)
            b |= (byte)mask;
        else
            b &= (byte)~mask;
        Modified = true;
    }

    /// <summary>Reads 8 consecutive bit cells starting at <paramref name="bitPosition"/> (wrapping), MSB first.</summary>
    public byte ReadByte(int bitPosition)
    {
        int value = 0;
        for (int i = 0; i < 8; i++)
            value = (value << 1) | ReadBit(bitPosition + i);
        return (byte)value;
    }

    /// <summary>Writes 8 consecutive bit cells starting at <paramref name="bitPosition"/> (wrapping), MSB first.</summary>
    public void WriteByte(int bitPosition, byte value)
    {
        for (int i = 0; i < 8; i++)
            WriteBit(bitPosition + i, value >> (7 - i));
    }

    /// <summary>Reads <paramref name="destination"/>.Length consecutive bytes starting at <paramref name="bitPosition"/> (wrapping).</summary>
    public void ReadBytes(int bitPosition, Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
            destination[i] = ReadByte(bitPosition + i * 8);
    }

    /// <summary>Writes <paramref name="source"/> as consecutive bytes starting at <paramref name="bitPosition"/> (wrapping).</summary>
    public void WriteBytes(int bitPosition, ReadOnlySpan<byte> source)
    {
        for (int i = 0; i < source.Length; i++)
            WriteByte(bitPosition + i * 8, source[i]);
    }

    /// <summary>Sets every byte of the stream to <paramref name="value"/> (unused tail bits are cleared).</summary>
    public void Fill(byte value)
    {
        Array.Fill(_data, value);
        MaskTail();
        Modified = true;
    }

    /// <summary>Erases the track (all bit cells 0).</summary>
    public void Clear() => Fill(0);

    /// <summary>True if no bit cell is set (an unformatted / erased track).</summary>
    public bool IsBlank
    {
        get
        {
            int bytes = ByteLength;
            for (int i = 0; i < bytes; i++)
            {
                if (_data[i] != 0)
                    return false;
            }
            return true;
        }
    }

    /// <summary>Returns a copy of the bit stream (<see cref="ByteLength"/> bytes, unused tail bits cleared).</summary>
    public byte[] ToBytes()
    {
        var copy = new byte[ByteLength];
        Array.Copy(_data, copy, copy.Length);
        int tail = _bitLength & 7;
        if (tail != 0)
            copy[^1] &= (byte)(0xFF << (8 - tail));
        return copy;
    }

    /// <summary>
    /// Rotates the stream so that the bit currently at <paramref name="bitOffset"/> becomes bit 0. The physical
    /// content is unchanged, only the origin moves (a disk has no index hole as far as the 1541 is concerned).
    /// </summary>
    public void Rotate(int bitOffset)
    {
        bitOffset = Wrap(bitOffset);
        if (bitOffset == 0)
            return;
        var rotated = new byte[_data.Length];
        for (int i = 0; i < _bitLength; i++)
        {
            if (ReadBit(i + bitOffset) != 0)
                rotated[i >> 3] |= (byte)(0x80 >> (i & 7));
        }
        Array.Copy(rotated, _data, _data.Length);
        Modified = true;
    }

    private void MaskTail()
    {
        int tail = _bitLength & 7;
        if (tail != 0)
            _data[ByteLength - 1] &= (byte)(0xFF << (8 - tail));
        for (int i = ByteLength; i < _data.Length; i++)
            _data[i] = 0;
    }
}
