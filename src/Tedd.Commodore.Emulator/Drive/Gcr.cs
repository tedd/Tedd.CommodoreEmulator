using System;
using System.Runtime.CompilerServices;

namespace Tedd.Commodore.Emulator.Drive;

/// <summary>
/// Commodore "GCR" (group code recording) 4-to-5 bit encoding used on 1541 disks.
///
/// Every nibble of user data is written as a 5-bit group chosen so that the bit stream never contains more
/// than two consecutive 0 bits (the 1541 read circuitry needs a flux transition at least every three bit
/// cells) and never more than eight consecutive 1 bits (ten or more 1 bits are reserved for the SYNC mark).
/// Four data bytes (eight nibbles) therefore become five GCR bytes, transmitted MSB first.
///
/// The table is the one in the 1541 DOS ROM at $F77F ("Inside Commodore DOS", Immers/Neufeld, chapter 6;
/// Peter Schepers, G64.TXT, "GCR encoding").
/// </summary>
public static class Gcr
{
    /// <summary>Value stored in <see cref="DecodeTable"/> for the 16 invalid 5-bit groups.</summary>
    public const byte Invalid = 0xFF;

    // 4-bit nibble -> 5-bit GCR group (1541 ROM $F77F).
    private static readonly byte[] EncodeTableData =
    {
        0x0A, 0x0B, 0x12, 0x13, 0x0E, 0x0F, 0x16, 0x17,
        0x09, 0x19, 0x1A, 0x1B, 0x0D, 0x1D, 0x1E, 0x15,
    };

    // 5-bit GCR group -> nibble, or Invalid.
    private static readonly byte[] DecodeTableData = BuildDecodeTable();

    private static byte[] BuildDecodeTable()
    {
        var table = new byte[32];
        Array.Fill(table, Invalid);
        for (int nibble = 0; nibble < 16; nibble++)
            table[EncodeTableData[nibble]] = (byte)nibble;
        return table;
    }

    /// <summary>The 16-entry nibble → 5-bit group table.</summary>
    public static ReadOnlySpan<byte> EncodeTable => EncodeTableData;

    /// <summary>The 32-entry 5-bit group → nibble table (<see cref="Invalid"/> for the 16 unused groups).</summary>
    public static ReadOnlySpan<byte> DecodeTable => DecodeTableData;

    /// <summary>Returns the 5-bit GCR group for a nibble (only the low 4 bits of <paramref name="nibble"/> are used).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte EncodeNibble(int nibble) => EncodeTableData[nibble & 0x0F];

    /// <summary>True if the 5-bit group is one of the 16 valid GCR codes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidCode(int code) => (uint)code < 32 && DecodeTableData[code] != Invalid;

    /// <summary>Returns the nibble (0..15) for a 5-bit GCR group, or -1 if the group is not a valid code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeCode(int code)
    {
        if ((uint)code >= 32)
            return -1;
        byte nibble = DecodeTableData[code];
        return nibble == Invalid ? -1 : nibble;
    }

    /// <summary>Number of GCR bytes produced by <paramref name="decodedLength"/> data bytes (must be a multiple of 4).</summary>
    public static int EncodedLength(int decodedLength)
    {
        if (decodedLength < 0 || (decodedLength & 3) != 0)
            throw new ArgumentException("Decoded length must be a non-negative multiple of 4.", nameof(decodedLength));
        return decodedLength / 4 * 5;
    }

    /// <summary>Number of data bytes produced by <paramref name="encodedLength"/> GCR bytes (must be a multiple of 5).</summary>
    public static int DecodedLength(int encodedLength)
    {
        if (encodedLength < 0 || encodedLength % 5 != 0)
            throw new ArgumentException("Encoded length must be a non-negative multiple of 5.", nameof(encodedLength));
        return encodedLength / 5 * 4;
    }

    /// <summary>
    /// Encodes exactly 4 data bytes into 5 GCR bytes. The eight 5-bit groups are packed MSB first:
    /// group 0 occupies bits 7-3 of the first byte, group 1 bits 2-0 of the first byte and bits 7-6 of the
    /// second, and so on ("Inside Commodore DOS", figure 6-2).
    /// </summary>
    public static void Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length < 4)
            throw new ArgumentException("Need 4 source bytes.", nameof(source));
        if (destination.Length < 5)
            throw new ArgumentException("Need 5 destination bytes.", nameof(destination));
        EncodeGroup(source, destination);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeGroup(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int g0 = EncodeTableData[source[0] >> 4];
        int g1 = EncodeTableData[source[0] & 0x0F];
        int g2 = EncodeTableData[source[1] >> 4];
        int g3 = EncodeTableData[source[1] & 0x0F];
        int g4 = EncodeTableData[source[2] >> 4];
        int g5 = EncodeTableData[source[2] & 0x0F];
        int g6 = EncodeTableData[source[3] >> 4];
        int g7 = EncodeTableData[source[3] & 0x0F];

        destination[0] = (byte)((g0 << 3) | (g1 >> 2));
        destination[1] = (byte)((g1 << 6) | (g2 << 1) | (g3 >> 4));
        destination[2] = (byte)((g3 << 4) | (g4 >> 1));
        destination[3] = (byte)((g4 << 7) | (g5 << 2) | (g6 >> 3));
        destination[4] = (byte)((g6 << 5) | g7);
    }

    /// <summary>
    /// Decodes exactly 5 GCR bytes into 4 data bytes. Returns false if any of the eight 5-bit groups is not a
    /// valid GCR code; the invalid nibbles are decoded as 0 so the output is always fully written (the real
    /// 1541 has no way of detecting invalid groups either, it relies on the block checksum).
    /// </summary>
    public static bool Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length < 5)
            throw new ArgumentException("Need 5 source bytes.", nameof(source));
        if (destination.Length < 4)
            throw new ArgumentException("Need 4 destination bytes.", nameof(destination));
        return DecodeGroup(source, destination);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DecodeGroup(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int b0 = source[0], b1 = source[1], b2 = source[2], b3 = source[3], b4 = source[4];

        int c0 = b0 >> 3;
        int c1 = ((b0 & 0x07) << 2) | (b1 >> 6);
        int c2 = (b1 >> 1) & 0x1F;
        int c3 = ((b1 & 0x01) << 4) | (b2 >> 4);
        int c4 = ((b2 & 0x0F) << 1) | (b3 >> 7);
        int c5 = (b3 >> 2) & 0x1F;
        int c6 = ((b3 & 0x03) << 3) | (b4 >> 5);
        int c7 = b4 & 0x1F;

        int n0 = DecodeTableData[c0], n1 = DecodeTableData[c1], n2 = DecodeTableData[c2], n3 = DecodeTableData[c3];
        int n4 = DecodeTableData[c4], n5 = DecodeTableData[c5], n6 = DecodeTableData[c6], n7 = DecodeTableData[c7];
        // Valid nibbles are 0..15, an invalid group yields Invalid (0xFF): the OR of all eight is < 16 only when all are valid.
        bool valid = (n0 | n1 | n2 | n3 | n4 | n5 | n6 | n7) < 16;

        destination[0] = (byte)(((n0 & 0x0F) << 4) | (n1 & 0x0F));
        destination[1] = (byte)(((n2 & 0x0F) << 4) | (n3 & 0x0F));
        destination[2] = (byte)(((n4 & 0x0F) << 4) | (n5 & 0x0F));
        destination[3] = (byte)(((n6 & 0x0F) << 4) | (n7 & 0x0F));
        return valid;
    }

    /// <summary>Encodes a whole block: <paramref name="source"/>.Length must be a multiple of 4, the destination receives 5/4 as many bytes.</summary>
    public static void EncodeBlock(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int outLength = EncodedLength(source.Length);
        if (destination.Length < outLength)
            throw new ArgumentException($"Need {outLength} destination bytes.", nameof(destination));
        for (int i = 0, o = 0; i < source.Length; i += 4, o += 5)
            EncodeGroup(source.Slice(i, 4), destination.Slice(o, 5));
    }

    /// <summary>Decodes a whole block: <paramref name="source"/>.Length must be a multiple of 5. Returns false if any group was invalid.</summary>
    public static bool DecodeBlock(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int outLength = DecodedLength(source.Length);
        if (destination.Length < outLength)
            throw new ArgumentException($"Need {outLength} destination bytes.", nameof(destination));
        bool valid = true;
        for (int i = 0, o = 0; i < source.Length; i += 5, o += 4)
            valid &= DecodeGroup(source.Slice(i, 5), destination.Slice(o, 4));
        return valid;
    }

    /// <summary>Allocating convenience wrapper around <see cref="EncodeBlock"/>.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> source)
    {
        var result = new byte[EncodedLength(source.Length)];
        EncodeBlock(source, result);
        return result;
    }

    /// <summary>Allocating convenience wrapper around <see cref="DecodeBlock"/>.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> source, out bool valid)
    {
        var result = new byte[DecodedLength(source.Length)];
        valid = DecodeBlock(source, result);
        return result;
    }
}
