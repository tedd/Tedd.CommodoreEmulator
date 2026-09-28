using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Drive;

namespace Tedd.Commodore.Tests.Drive;

/// <summary>
/// Tests for <see cref="Gcr"/>, <see cref="GcrTrack"/> and <see cref="GcrDisk"/>: the GCR code, the bit
/// stream, the standard 1541 track layout produced from a D64 and the decoding back.
/// </summary>
[TestFixture]
public class GcrDiskTests
{
    // 1541 DOS ROM $F77F.
    private static readonly byte[] DosGcrTable =
    {
        0x0A, 0x0B, 0x12, 0x13, 0x0E, 0x0F, 0x16, 0x17,
        0x09, 0x19, 0x1A, 0x1B, 0x0D, 0x1D, 0x1E, 0x15,
    };

    /// <summary>Independent SYNC finder: bit position of the first 0 after ≥ 10 one bits, scanning the loop once.</summary>
    private static List<int> SyncEnds(GcrTrack track)
    {
        int n = track.BitLength;
        int start = 0;
        while (start < n && track.ReadBit(start) != 0)
            start++;
        if (start == n)
            return new List<int>();
        var result = new List<int>();
        int run = 0;
        for (int k = 1; k <= n; k++)
        {
            int i = (start + k) % n;
            if (track.ReadBit(i) != 0)
            {
                run++;
                continue;
            }
            if (run >= 10)
                result.Add(i);
            run = 0;
        }
        result.Sort();
        return result;
    }

    private static byte[] ReadBlock(GcrTrack track, int bitPosition, int gcrBytes)
    {
        var gcr = new byte[gcrBytes];
        track.ReadBytes(bitPosition, gcr);
        return Gcr.Decode(gcr, out _);
    }

    private static D64Image RandomImage(int trackCount, int seed)
    {
        var image = D64Image.CreateEmpty("RANDOM", "RN", trackCount);
        var random = new Random(seed);
        random.NextBytes(image.Data);
        return image;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Gcr
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public void Gcr_TableMatchesTheDosRom()
    {
        Assert.That(Gcr.EncodeTable.ToArray(), Is.EqualTo(DosGcrTable));
        for (int nibble = 0; nibble < 16; nibble++)
        {
            Assert.That(Gcr.EncodeNibble(nibble), Is.EqualTo(DosGcrTable[nibble]));
            Assert.That(Gcr.DecodeCode(DosGcrTable[nibble]), Is.EqualTo(nibble));
            Assert.That(Gcr.DecodeTable[DosGcrTable[nibble]], Is.EqualTo(nibble));
        }
    }

    [Test]
    public void Gcr_RoundTripsAll256ValuesInEveryPosition()
    {
        var source = new byte[4];
        var encoded = new byte[5];
        var decoded = new byte[4];
        for (int value = 0; value < 256; value++)
        {
            for (int position = 0; position < 4; position++)
            {
                Array.Clear(source);
                source[position] = (byte)value;
                source[(position + 1) & 3] = (byte)~value;
                Gcr.Encode(source, encoded);
                Assert.That(Gcr.Decode(encoded, decoded), Is.True, $"value {value:X2} at {position}");
                Assert.That(decoded, Is.EqualTo(source), $"value {value:X2} at {position}");
            }
        }
    }

    [Test]
    public void Gcr_KnownVectors()
    {
        // 4 × $00: nibble 0 = 01010 repeated; 4 × $FF: nibble F = 10101 repeated.
        Assert.That(Gcr.Encode(new byte[] { 0x00, 0x00, 0x00, 0x00 }), Is.EqualTo(new byte[] { 0x52, 0x94, 0xA5, 0x29, 0x4A }));
        Assert.That(Gcr.Encode(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }), Is.EqualTo(new byte[] { 0xAD, 0x6B, 0x5A, 0xD6, 0xB5 }));
        // Data block ID $07 followed by $00 bytes: 0 → 01010, 7 → 10111.
        Assert.That(Gcr.Encode(new byte[] { 0x07, 0x00, 0x00, 0x00 })[0], Is.EqualTo(0x55));
        Assert.That(Gcr.Decode(new byte[] { 0xAD, 0x6B, 0x5A, 0xD6, 0xB5 }, out bool valid), Is.EqualTo(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));
        Assert.That(valid, Is.True);
    }

    [Test]
    public void Gcr_RejectsInvalidCodes()
    {
        var invalid = Enumerable.Range(0, 32).Where(c => !DosGcrTable.Contains((byte)c)).ToArray();
        Assert.That(invalid, Has.Length.EqualTo(16));
        foreach (int code in invalid)
        {
            Assert.That(Gcr.IsValidCode(code), Is.False, $"code {code:X2}");
            Assert.That(Gcr.DecodeCode(code), Is.EqualTo(-1), $"code {code:X2}");
            Assert.That(Gcr.DecodeTable[code], Is.EqualTo(Gcr.Invalid));
        }
        Assert.That(Gcr.IsValidCode(32), Is.False);
        Assert.That(Gcr.DecodeCode(-1), Is.EqualTo(-1));

        var decoded = new byte[4];
        Assert.That(Gcr.Decode(new byte[5], decoded), Is.False, "00000 is not a GCR code");
        Assert.That(Gcr.Decode(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, decoded), Is.False, "11111 is not a GCR code");
        // A single bad group (00000 as the first group) in an otherwise valid block.
        var block = Gcr.Encode(new byte[] { 0x12, 0x34, 0x56, 0x78 });
        block[0] &= 0x07;
        Assert.That(Gcr.Decode(block, decoded), Is.False);
        Assert.That(decoded[1], Is.EqualTo(0x34), "the other groups still decode");
        Assert.That(Gcr.DecodeBlock(new byte[10], new byte[8]), Is.False);
    }

    [Test]
    public void Gcr_BlockHelpersAndLengthValidation()
    {
        Assert.That(Gcr.EncodedLength(260), Is.EqualTo(325));
        Assert.That(Gcr.DecodedLength(325), Is.EqualTo(260));
        Assert.That(() => Gcr.EncodedLength(3), Throws.ArgumentException);
        Assert.That(() => Gcr.DecodedLength(4), Throws.ArgumentException);
        Assert.That(() => Gcr.EncodeBlock(new byte[6], new byte[10]), Throws.ArgumentException);
        Assert.That(() => Gcr.EncodeBlock(new byte[4], new byte[4]), Throws.ArgumentException);
        Assert.That(() => Gcr.Encode(new byte[3], new byte[5]), Throws.ArgumentException);
        Assert.That(() => Gcr.Decode(new byte[5], new byte[3]), Throws.ArgumentException);

        var source = new byte[260];
        new Random(7).NextBytes(source);
        var encoded = Gcr.Encode(source);
        Assert.That(encoded, Has.Length.EqualTo(325));
        var decoded = Gcr.Decode(encoded, out bool valid);
        Assert.That(valid, Is.True);
        Assert.That(decoded, Is.EqualTo(source));

        // Property of the code: never more than 8 consecutive one bits (so SYNC is unambiguous)
        // and never more than 2 consecutive zero bits.
        var track = GcrTrack.FromBytes(encoded);
        int ones = 0, zeros = 0, maxOnes = 0, maxZeros = 0;
        for (int i = 0; i < track.BitLength; i++)
        {
            if (track.ReadBit(i) != 0) { ones++; zeros = 0; } else { zeros++; ones = 0; }
            maxOnes = Math.Max(maxOnes, ones);
            maxZeros = Math.Max(maxZeros, zeros);
        }
        Assert.That(maxOnes, Is.LessThanOrEqualTo(8));
        Assert.That(maxZeros, Is.LessThanOrEqualTo(2));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Geometry / timing
    // ---------------------------------------------------------------------------------------------------------

    [TestCase(0, 3)]    // track 1
    [TestCase(1, 3)]    // track 1.5
    [TestCase(32, 3)]   // track 17
    [TestCase(33, 3)]   // track 17.5
    [TestCase(34, 2)]   // track 18
    [TestCase(46, 2)]   // track 24
    [TestCase(48, 1)]   // track 25
    [TestCase(58, 1)]   // track 30
    [TestCase(60, 0)]   // track 31
    [TestCase(68, 0)]   // track 35
    [TestCase(78, 0)]   // track 40
    [TestCase(83, 0)]   // track 42.5
    public void SpeedZone_FollowsTheDosDensityTable(int halfTrack, int zone)
    {
        Assert.That(GcrDisk.SpeedZone(halfTrack), Is.EqualTo(zone));
        Assert.That(GcrTrack.SpeedZone(halfTrack), Is.EqualTo(zone));
        Assert.That(GcrDisk.SpeedZoneOfTrack(GcrDisk.HalfTrackToTrack(halfTrack)), Is.EqualTo(zone));
        Assert.That(GcrTrack.CreateUnformatted(halfTrack).BitLength, Is.EqualTo(GcrDisk.TrackLengthForZone(zone) * 8));
    }

    [Test]
    public void SpeedZone_RejectsOutOfRange()
    {
        Assert.That(() => GcrDisk.SpeedZone(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => GcrDisk.SpeedZone(84), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => GcrDisk.TrackToHalfTrack(0), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => GcrDisk.TrackToHalfTrack(43), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void HalfTrackConversions()
    {
        Assert.That(GcrDisk.TrackToHalfTrack(1), Is.EqualTo(0));
        Assert.That(GcrDisk.TrackToHalfTrack(2), Is.EqualTo(2));
        Assert.That(GcrDisk.TrackToHalfTrack(18), Is.EqualTo(34));
        Assert.That(GcrDisk.TrackToHalfTrack(35), Is.EqualTo(68));
        Assert.That(GcrDisk.TrackToHalfTrack(40), Is.EqualTo(78));
        Assert.That(GcrDisk.HalfTrackToTrack(0), Is.EqualTo(1));
        Assert.That(GcrDisk.HalfTrackToTrack(1), Is.EqualTo(1));
        Assert.That(GcrDisk.HalfTrackToTrack(2), Is.EqualTo(2));
        Assert.That(GcrDisk.HalfTrackToTrack(69), Is.EqualTo(35));
        Assert.That(GcrDisk.HalfTrackToTrack(83), Is.EqualTo(42));
    }

    [Test]
    public void TrackLengths_MatchTheSpeedZones()
    {
        Assert.That(GcrDisk.TrackLengthForZone(0), Is.EqualTo(6250));
        Assert.That(GcrDisk.TrackLengthForZone(1), Is.EqualTo(6666));
        Assert.That(GcrDisk.TrackLengthForZone(2), Is.EqualTo(7142));
        Assert.That(GcrDisk.TrackLengthForZone(3), Is.EqualTo(7692));
        Assert.That(GcrDisk.TrackLength(0), Is.EqualTo(7692));
        Assert.That(GcrDisk.TrackLength(34), Is.EqualTo(7142));
        Assert.That(GcrDisk.TrackLength(48), Is.EqualTo(6666));
        Assert.That(GcrDisk.TrackLength(60), Is.EqualTo(6250));
        Assert.That(GcrDisk.TrackLength(83), Is.EqualTo(6250));
    }

    [Test]
    public void Timing_OneRevolutionAt300RpmIs200Milliseconds()
    {
        Assert.That(GcrDisk.ClocksPerBit(0), Is.EqualTo(16));
        Assert.That(GcrDisk.ClocksPerBit(3), Is.EqualTo(13));
        Assert.That(GcrDisk.CpuCyclesPerByte(0), Is.EqualTo(32));
        Assert.That(GcrDisk.CpuCyclesPerByte(1), Is.EqualTo(30));
        Assert.That(GcrDisk.CpuCyclesPerByte(2), Is.EqualTo(28));
        Assert.That(GcrDisk.CpuCyclesPerByte(3), Is.EqualTo(26));
        Assert.That(GcrDisk.BitsPerSecond(0), Is.EqualTo(250_000));
        Assert.That(GcrDisk.BitsPerSecond(3), Is.EqualTo(307_692));
        for (int zone = 0; zone < 4; zone++)
        {
            // track bytes × µs per byte = one revolution = 200 000 µs (within one byte).
            int revolution = GcrDisk.TrackLengthForZone(zone) * GcrDisk.CpuCyclesPerByte(zone);
            Assert.That(revolution, Is.InRange(200_000 - 32, 200_000), $"zone {zone}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // GcrTrack
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public void GcrTrack_BitAndByteAccessWrapAround()
    {
        var track = new GcrTrack(20);
        Assert.That(track.BitLength, Is.EqualTo(20));
        Assert.That(track.ByteLength, Is.EqualTo(3));
        Assert.That(track.IsBlank, Is.True);
        Assert.That(track.Modified, Is.False);

        track.WriteBit(0, 1);
        track.WriteBit(19, 1);
        Assert.That(track.Modified, Is.True);
        Assert.That(track.IsBlank, Is.False);
        Assert.That(track.ReadBit(0), Is.EqualTo(1));
        Assert.That(track.ReadBit(1), Is.EqualTo(0));
        Assert.That(track.ReadBit(19), Is.EqualTo(1));
        Assert.That(track.ReadBit(20), Is.EqualTo(1), "wraps to bit 0");
        Assert.That(track.ReadBit(-1), Is.EqualTo(1), "negative wraps to bit 19");
        Assert.That(track.ReadBit(21), Is.EqualTo(0));
        Assert.That(track.Wrap(40), Is.EqualTo(0));
        Assert.That(track.Wrap(-21), Is.EqualTo(19));

        track.Clear();
        track.WriteByte(16, 0xA5); // bits 16..19 = 1010, bits 0..3 = 0101
        Assert.That(track.ReadByte(16), Is.EqualTo(0xA5));
        Assert.That(track.ReadBit(16), Is.EqualTo(1));
        Assert.That(track.ReadBit(17), Is.EqualTo(0));
        Assert.That(track.ReadBit(0), Is.EqualTo(0));
        Assert.That(track.ReadBit(1), Is.EqualTo(1));
        Assert.That(track.ReadBit(3), Is.EqualTo(1));
        Assert.That(track.Data[0], Is.EqualTo(0x50));
        Assert.That(track.Data[2], Is.EqualTo(0xA0));

        var bytes = new byte[2];
        track.ReadBytes(16, bytes);
        Assert.That(bytes[0], Is.EqualTo(0xA5));
        track.WriteBytes(4, new byte[] { 0xFF });
        Assert.That(track.ReadByte(4), Is.EqualTo(0xFF));

        var small = new GcrTrack(12);
        small.Fill(0xFF);
        Assert.That(small.ToBytes(), Is.EqualTo(new byte[] { 0xFF, 0xF0 }), "tail bits beyond BitLength are masked");
        Assert.That(small.ReadBit(11), Is.EqualTo(1));
        Assert.That(small.ReadBit(12), Is.EqualTo(1), "wraps to bit 0, not into the tail");
    }

    [Test]
    public void GcrTrack_ConstructorValidationAndFromBytes()
    {
        Assert.That(() => new GcrTrack(0), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new GcrTrack(new byte[1], 9), Throws.ArgumentException);
        Assert.That(() => new GcrTrack(null!, 8), Throws.ArgumentNullException);
        var track = GcrTrack.FromBytes(new byte[] { 0x12, 0x34 });
        Assert.That(track.BitLength, Is.EqualTo(16));
        Assert.That(track.ReadByte(0), Is.EqualTo(0x12));
        Assert.That(track.ReadByte(8), Is.EqualTo(0x34));
        Assert.That(track.ReadByte(4), Is.EqualTo(0x23));
    }

    [Test]
    public void GcrTrack_RotateMovesTheOriginOnly()
    {
        var track = GcrTrack.FromBytes(new byte[] { 0x12, 0x34, 0x56 });
        track.Rotate(4);
        Assert.That(track.ReadByte(0), Is.EqualTo(0x23));
        Assert.That(track.ReadByte(8), Is.EqualTo(0x45));
        Assert.That(track.ReadByte(16), Is.EqualTo(0x61));
        track.Rotate(20);
        Assert.That(track.ToBytes(), Is.EqualTo(new byte[] { 0x12, 0x34, 0x56 }));
        track.Rotate(-4);
        Assert.That(track.ReadByte(0), Is.EqualTo(0x61));
    }

    [Test]
    public void FindSyncs_HandlesRunsAcrossTheOriginAndIgnoresShortRuns()
    {
        var track = new GcrTrack(100);
        for (int i = 95; i < 100; i++) track.WriteBit(i, 1);
        for (int i = 0; i < 7; i++) track.WriteBit(i, 1);       // 12 one bits across the origin, first 0 at bit 7
        for (int i = 50; i < 59; i++) track.WriteBit(i, 1);     // only 9 one bits: not a SYNC
        Assert.That(GcrDisk.FindSyncs(track), Is.EqualTo(new[] { 7 }));

        track.WriteBit(59, 1);                                  // now 10
        Assert.That(GcrDisk.FindSyncs(track), Is.EqualTo(new[] { 7, 60 }));
        Assert.That(GcrDisk.FindSyncs(new GcrTrack(64)), Is.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------
    // FromD64 layout
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public void NewDisk_HasAllHalfTracksUnformattedAtNominalLength()
    {
        var disk = new GcrDisk();
        Assert.That(disk.TrackCount, Is.EqualTo(35));
        Assert.That(disk.WriteProtected, Is.False);
        Assert.That(disk.Modified, Is.False);
        for (int h = 0; h < GcrDisk.HalfTrackCount; h++)
        {
            Assert.That(disk[h], Is.Not.Null);
            Assert.That(disk.GetTrack(h), Is.SameAs(disk[h]));
            Assert.That(disk[h].BitLength, Is.EqualTo(GcrDisk.TrackLength(h) * 8));
            Assert.That(disk[h].IsBlank, Is.True);
        }
        Assert.That(disk.GetFullTrack(18), Is.SameAs(disk[34]));
    }

    [Test]
    public void FromD64_TrackLengthsFollowTheZonesAndOddHalfTracksAreBlank()
    {
        var disk = GcrDisk.FromD64(D64Image.CreateEmpty("LEN", "01"));
        Assert.That(disk.TrackCount, Is.EqualTo(35));
        Assert.That(disk.HasErrorInfo, Is.False);
        for (int track = 1; track <= 35; track++)
        {
            int expected = track <= 17 ? 7692 : track <= 24 ? 7142 : track <= 30 ? 6666 : 6250;
            var t = disk.GetFullTrack(track);
            Assert.That(t.BitLength, Is.EqualTo(expected * 8), $"track {track}");
            Assert.That(t.IsBlank, Is.False, $"track {track}");
            Assert.That(t.Modified, Is.False, $"track {track}");
            var half = disk[GcrDisk.TrackToHalfTrack(track) + 1];
            Assert.That(half.IsBlank, Is.True, $"half-track {track}.5");
            Assert.That(half.BitLength, Is.EqualTo(GcrDisk.TrackLengthForZone(GcrDisk.SpeedZoneOfTrack(track)) * 8));
        }
        for (int h = 70; h < GcrDisk.HalfTrackCount; h++)
            Assert.That(disk[h].IsBlank, Is.True, $"half-track {h} beyond track 35");
    }

    [Test]
    public void FromD64_WritesTwoSyncMarksPerSector()
    {
        var disk = GcrDisk.FromD64(D64Image.CreateEmpty("SYNC", "01"));
        for (int track = 1; track <= 35; track++)
        {
            var syncs = SyncEnds(disk.GetFullTrack(track));
            Assert.That(syncs, Has.Count.EqualTo(2 * D64Image.SectorsPerTrack(track)), $"track {track}");
            Assert.That(GcrDisk.FindSyncs(disk.GetFullTrack(track)), Is.EqualTo(syncs), $"track {track} (library sync finder)");
        }
    }

    [Test]
    public void FromD64_HeaderBlocksCarrySectorTrackAndId()
    {
        var disk = GcrDisk.FromD64(D64Image.CreateEmpty("HDR", "QZ"));
        foreach (int track in new[] { 1, 17, 18, 24, 25, 30, 31, 35 })
        {
            var t = disk.GetFullTrack(track);
            var syncs = SyncEnds(t);
            int sectors = D64Image.SectorsPerTrack(track);
            for (int sector = 0; sector < sectors; sector++)
            {
                var header = ReadBlock(t, syncs[2 * sector], 10);
                Assert.That(header[0], Is.EqualTo(0x08), $"{track}/{sector} header ID");
                Assert.That(header[2], Is.EqualTo(sector), $"{track}/{sector} sector");
                Assert.That(header[3], Is.EqualTo(track), $"{track}/{sector} track");
                Assert.That(header[4], Is.EqualTo((byte)'Z'), $"{track}/{sector} ID2 (BAM $A3)");
                Assert.That(header[5], Is.EqualTo((byte)'Q'), $"{track}/{sector} ID1 (BAM $A2)");
                Assert.That(header[1], Is.EqualTo((byte)(sector ^ track ^ 'Z' ^ 'Q')), $"{track}/{sector} checksum");
                Assert.That(header[6], Is.EqualTo(0x0F));
                Assert.That(header[7], Is.EqualTo(0x0F));
            }
        }
    }

    [Test]
    public void FromD64_DataBlocksCarryTheSectorDataAndChecksum()
    {
        var image = RandomImage(35, 1234);
        var disk = GcrDisk.FromD64(image);
        foreach (int track in new[] { 1, 18, 26, 35 })
        {
            var t = disk.GetFullTrack(track);
            var syncs = SyncEnds(t);
            for (int sector = 0; sector < D64Image.SectorsPerTrack(track); sector++)
            {
                var block = ReadBlock(t, syncs[2 * sector + 1], 325);
                Assert.That(block, Has.Length.EqualTo(260));
                Assert.That(block[0], Is.EqualTo(0x07), $"{track}/{sector} data ID");
                var expected = image.GetSector(track, sector).ToArray();
                Assert.That(block.AsSpan(1, 256).ToArray(), Is.EqualTo(expected), $"{track}/{sector} data");
                byte checksum = 0;
                foreach (var b in expected)
                    checksum ^= b;
                Assert.That(block[257], Is.EqualTo(checksum), $"{track}/{sector} checksum");
                Assert.That(block[258], Is.EqualTo(0));
                Assert.That(block[259], Is.EqualTo(0));
            }
        }
    }

    [Test]
    public void FromD64_GapsFillTheTrackToItsNominalLength()
    {
        var disk = GcrDisk.FromD64(D64Image.CreateEmpty("GAP", "01"));

        // Zone 3, 21 sectors: 7692 - 21 × 354 = 258 gap bytes = 12 per sector + 6 in the last gap.
        var bytes = disk.GetFullTrack(1).ToBytes();
        Assert.That(bytes, Has.Length.EqualTo(7692));
        Assert.That(bytes.AsSpan(0, 5).ToArray(), Is.All.EqualTo(0xFF), "header SYNC");
        Assert.That(bytes.AsSpan(15, 9).ToArray(), Is.All.EqualTo(0x55), "header gap");
        Assert.That(bytes.AsSpan(24, 5).ToArray(), Is.All.EqualTo(0xFF), "data SYNC");
        Assert.That(bytes.AsSpan(354, 12).ToArray(), Is.All.EqualTo(0x55), "tail gap of sector 0");
        Assert.That(bytes[366], Is.EqualTo(0xFF), "SYNC of sector 1");
        int lastSector = 20 * 366;
        Assert.That(bytes.AsSpan(lastSector, 5).ToArray(), Is.All.EqualTo(0xFF), "SYNC of sector 20");
        Assert.That(bytes.AsSpan(lastSector + 354, 18).ToArray(), Is.All.EqualTo(0x55), "last gap = 12 + 6");
        Assert.That(lastSector + 354 + 18, Is.EqualTo(7692));

        // Zone 0, 17 sectors: 6250 - 17 × 354 = 232 = 13 per sector + 11 in the last gap.
        bytes = disk.GetFullTrack(35).ToBytes();
        Assert.That(bytes, Has.Length.EqualTo(6250));
        Assert.That(bytes.AsSpan(354, 13).ToArray(), Is.All.EqualTo(0x55));
        Assert.That(bytes[367], Is.EqualTo(0xFF));
        Assert.That(bytes.AsSpan(16 * 367 + 354, 24).ToArray(), Is.All.EqualTo(0x55));

        // Zone 2 (19 sectors): 7142 - 19 × 354 = 416 = 21 + 17; zone 1 (18 sectors): 6666 - 18 × 354 = 294 = 16 + 6.
        Assert.That(disk.GetFullTrack(18).ToBytes()[354 + 21], Is.EqualTo(0xFF));
        Assert.That(disk.GetFullTrack(25).ToBytes()[354 + 16], Is.EqualTo(0xFF));
    }

    [Test]
    public void EncodeSector_ValidatesArguments()
    {
        var destination = new byte[400];
        Assert.That(GcrDisk.EncodeSector(destination, 1, 0, new byte[256], 0x41, 0x42, 10), Is.EqualTo(364));
        Assert.That(() => GcrDisk.EncodeSector(destination, 1, 0, new byte[255], 0x41, 0x42, 10), Throws.ArgumentException);
        Assert.That(() => GcrDisk.EncodeSector(new byte[100], 1, 0, new byte[256], 0x41, 0x42, 0), Throws.ArgumentException);
        Assert.That(() => GcrDisk.EncodeSector(destination, 1, 0, new byte[256], 0x41, 0x42, -1), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => GcrDisk.FormatTrack(1, new byte[256], 0x41, 0x42), Throws.ArgumentException);
        Assert.That(() => GcrDisk.FormatTrack(1, new byte[21 * 256], 0x41, 0x42, new byte[3]), Throws.ArgumentException);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Round trips
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public void RoundTrip_EmptyDisk()
    {
        var image = D64Image.CreateEmpty("ROUND", "RT");
        var back = GcrDisk.FromD64(image).ToD64();
        Assert.That(back.HasErrorBytes, Is.False);
        Assert.That(back.WriteProtected, Is.False);
        Assert.That(back.ToBytes(), Is.EqualTo(image.ToBytes()));
        Assert.That(back.DiskName, Is.EqualTo("ROUND"));
        Assert.That(back.FreeBlocks, Is.EqualTo(664));
    }

    [TestCase(35, 42)]
    [TestCase(40, 99)]
    public void RoundTrip_RandomData(int trackCount, int seed)
    {
        var image = RandomImage(trackCount, seed);
        var disk = GcrDisk.FromD64(image);
        Assert.That(disk.TrackCount, Is.EqualTo(trackCount));
        if (trackCount == 40)
            Assert.That(disk.GetFullTrack(40).IsBlank, Is.False);
        var back = disk.ToD64();
        Assert.That(back.TrackCount, Is.EqualTo(trackCount));
        Assert.That(back.HasErrorBytes, Is.False);
        Assert.That(back.ToBytes(), Is.EqualTo(image.ToBytes()));
    }

    [TestCase(D64ImageTests.FroggerImage)]
    [TestCase(D64ImageTests.BatmanImage)]
    public void RoundTrip_RealImage(string fileName)
    {
        var image = D64ImageTests.LoadRealImageOrIgnore(fileName);
        var original = image.ToBytes();
        var disk = GcrDisk.FromD64(image);
        var back = disk.ToD64();
        Assert.That(back.HasErrorBytes, Is.EqualTo(image.HasErrorBytes));
        Assert.That(back.ToBytes(), Is.EqualTo(original));
    }

    [Test]
    public void RoundTrip_SurvivesArbitraryRotationOfEveryTrack()
    {
        var image = RandomImage(35, 777);
        var disk = GcrDisk.FromD64(image);
        for (int track = 1; track <= 35; track++)
        {
            var t = disk.GetFullTrack(track);
            // Odd offsets that are not byte aligned, some inside a SYNC, some inside a data block.
            t.Rotate((track * 1237 + 5) % t.BitLength);
        }
        disk.GetFullTrack(18).Rotate(3); // partial SYNC at the origin
        Assert.That(disk.Modified, Is.True);
        var back = disk.ToD64();
        Assert.That(back.HasErrorBytes, Is.False, "no read errors after rotation");
        Assert.That(back.ToBytes(), Is.EqualTo(image.ToBytes()));
    }

    [Test]
    public void ToD64_ReportsDamagedSectorsInTheErrorBlock()
    {
        var image = RandomImage(35, 4242);
        var disk = GcrDisk.FromD64(image);

        // (a) Track 30 erased completely: no SYNC at all → 21 for every sector.
        disk.GetFullTrack(30).Clear();

        // (b) Track 5 sector 2: one data bit flipped → 23.
        var t5 = disk.GetFullTrack(5);
        var syncs5 = GcrDisk.FindSyncs(t5);
        int bit = syncs5[2 * 2 + 1] + 8 * 100 + 3;
        t5.WriteBit(bit, 1 - t5.ReadBit(bit));

        // (c) Track 7 sector 0: header SYNC erased (the track starts with it) → 20.
        var t7 = disk.GetFullTrack(7);
        for (int i = 0; i < 40; i++)
            t7.WriteBit(i, 0);

        // (d) Track 9 sector 1: data SYNC erased → 22.
        var t9 = disk.GetFullTrack(9);
        int dataSync = GcrDisk.FindSyncs(t9)[2 * 1 + 1];
        for (int i = dataSync - 40; i < dataSync; i++)
            t9.WriteBit(i, 0);

        // (e) Track 11: all sectors formatted with another ID → 29.
        disk.SetTrack(GcrDisk.TrackToHalfTrack(11), GcrDisk.FormatTrack(11, image.Data.AsSpan(D64Image.SectorOffset(11, 0), 21 * 256), (byte)'X', (byte)'X'));

        var back = disk.ToD64();
        Assert.That(back.HasErrorBytes, Is.True);
        for (int sector = 0; sector < 18; sector++)
            Assert.That(back.GetErrorCode(30, sector), Is.EqualTo(D64ErrorCode.NoSync), $"30/{sector}");
        Assert.That(back.GetErrorCode(5, 2), Is.EqualTo(D64ErrorCode.DataChecksum));
        Assert.That(back.GetErrorCode(5, 1), Is.EqualTo(D64ErrorCode.Ok));
        Assert.That(back.GetErrorCode(5, 3), Is.EqualTo(D64ErrorCode.Ok));
        Assert.That(back.GetErrorCode(7, 0), Is.EqualTo(D64ErrorCode.HeaderNotFound));
        Assert.That(back.GetErrorCode(7, 1), Is.EqualTo(D64ErrorCode.Ok));
        Assert.That(back.GetErrorCode(9, 1), Is.EqualTo(D64ErrorCode.DataBlockNotFound));
        Assert.That(back.GetErrorCode(9, 2), Is.EqualTo(D64ErrorCode.Ok));
        for (int sector = 0; sector < 21; sector++)
            Assert.That(back.GetErrorCode(11, sector), Is.EqualTo(D64ErrorCode.IdMismatch), $"11/{sector}");
        Assert.That(back.GetErrorCode(18, 0), Is.EqualTo(D64ErrorCode.Ok));

        // Data of the good sectors is intact, the damaged data sector still holds what was read.
        Assert.That(back.GetSector(5, 1).ToArray(), Is.EqualTo(image.GetSector(5, 1).ToArray()));
        Assert.That(back.GetSector(11, 4).ToArray(), Is.EqualTo(image.GetSector(11, 4).ToArray()));
        Assert.That(back.GetSector(7, 0).ToArray(), Is.All.EqualTo(0), "unreadable sector is zero");
        Assert.That(back.GetSector(30, 0).ToArray(), Is.All.EqualTo(0));
        int differences = 0;
        var damaged = back.GetSector(5, 2);
        var original = image.GetSector(5, 2);
        for (int i = 0; i < 256; i++)
            differences += damaged[i] != original[i] ? 1 : 0;
        Assert.That(differences, Is.EqualTo(1), "a single flipped bit changes exactly one data byte");
    }

    [Test]
    public void ToD64_WithMoreTracksThanFormattedReportsNoSync()
    {
        var disk = GcrDisk.FromD64(D64Image.CreateEmpty("35", "01"));
        var back = disk.ToD64(40);
        Assert.That(back.TrackCount, Is.EqualTo(40));
        Assert.That(back.HasErrorBytes, Is.True);
        Assert.That(back.GetErrorCode(35, 0), Is.EqualTo(D64ErrorCode.Ok));
        Assert.That(back.GetErrorCode(36, 0), Is.EqualTo(D64ErrorCode.NoSync));
        Assert.That(back.GetErrorCode(40, 16), Is.EqualTo(D64ErrorCode.NoSync));
        Assert.That(() => disk.ToD64(36), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void ErrorBytes_RoundTripThroughTheGcrEncoding()
    {
        var image = RandomImage(35, 31337);
        var errors = new byte[683];
        Array.Fill(errors, D64ErrorCode.Ok);
        errors[D64Image.SectorIndex(1, 0)] = D64ErrorCode.HeaderNotFound;
        errors[D64Image.SectorIndex(2, 5)] = D64ErrorCode.DataBlockNotFound;
        errors[D64Image.SectorIndex(3, 7)] = D64ErrorCode.DataChecksum;
        errors[D64Image.SectorIndex(4, 1)] = D64ErrorCode.HeaderChecksum;
        errors[D64Image.SectorIndex(6, 3)] = D64ErrorCode.IdMismatch;
        errors[D64Image.SectorIndex(18, 5)] = D64ErrorCode.DataChecksum;
        for (int sector = 0; sector < 18; sector++)
            errors[D64Image.SectorIndex(30, sector)] = D64ErrorCode.NoSync;
        image.SetErrorBytes(errors);
        var original = image.ToBytes();
        Assert.That(original, Has.Length.EqualTo(D64Image.Size35TracksWithErrors));

        var disk = GcrDisk.FromD64(image);
        Assert.That(disk.HasErrorInfo, Is.True);
        Assert.That(disk.GetFullTrack(30).IsBlank, Is.False, "the sector bytes are still there, only the SYNC marks are missing");
        Assert.That(GcrDisk.FindSyncs(disk.GetFullTrack(30)), Is.Empty);
        Assert.That(GcrDisk.FindSyncs(disk.GetFullTrack(1)), Has.Count.EqualTo(42), "a header with a wrong ID byte still has its SYNC");

        var back = disk.ToD64();
        Assert.That(back.HasErrorBytes, Is.True);
        Assert.That(back.ErrorBytes, Is.EqualTo(errors), "every error code is reproduced");
        for (int track = 1; track <= 35; track++)
        {
            for (int sector = 0; sector < D64Image.SectorsPerTrack(track); sector++)
            {
                // A track without SYNC cannot be read at all; every other damaged sector still yields its bytes.
                var expected = errors[D64Image.SectorIndex(track, sector)] == D64ErrorCode.NoSync
                    ? new byte[256]
                    : image.GetSector(track, sector).ToArray();
                Assert.That(back.GetSector(track, sector).ToArray(), Is.EqualTo(expected), $"{track}/{sector}");
            }
        }

        // An image with an all-OK error block keeps its error block.
        var clean = RandomImage(35, 1);
        clean.SetErrorBytes(Enumerable.Repeat(D64ErrorCode.Ok, 683).ToArray());
        Assert.That(GcrDisk.FromD64(clean).ToD64().ToBytes(), Is.EqualTo(clean.ToBytes()));
    }

    [Test]
    public void TryReadDiskId_UsesTheDirectoryTrack()
    {
        var disk = GcrDisk.FromD64(D64Image.CreateEmpty("ID", "AB"));
        Assert.That(disk.TryReadDiskId(out byte id1, out byte id2), Is.True);
        Assert.That((char)id1, Is.EqualTo('A'));
        Assert.That((char)id2, Is.EqualTo('B'));

        disk.GetFullTrack(18).Clear();
        Assert.That(disk.TryReadDiskId(out id1, out id2), Is.True, "falls back to another track");
        Assert.That((char)id1, Is.EqualTo('A'));

        Assert.That(new GcrDisk().TryReadDiskId(out _, out _), Is.False);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Flags
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public void WriteProtect_PropagatesBothWays()
    {
        var image = D64Image.CreateEmpty("WP", "01");
        image.WriteProtected = true;
        var disk = GcrDisk.FromD64(image);
        Assert.That(disk.WriteProtected, Is.True);
        Assert.That(disk.ToD64().WriteProtected, Is.True);

        disk.WriteProtected = false;
        Assert.That(disk.ToD64().WriteProtected, Is.False);
        Assert.That(GcrDisk.FromD64(D64Image.CreateEmpty("RW", "01")).WriteProtected, Is.False);
    }

    [Test]
    public void Modified_TracksWrites()
    {
        var disk = GcrDisk.FromD64(D64Image.CreateEmpty("MOD", "01"));
        Assert.That(disk.Modified, Is.False);
        var t = disk.GetFullTrack(3);
        t.WriteBit(100, t.ReadBit(100));
        Assert.That(t.Modified, Is.True);
        Assert.That(disk.Modified, Is.True);
        disk.Modified = false;
        Assert.That(t.Modified, Is.False);
        Assert.That(disk.Modified, Is.False);
        Assert.That(() => disk.SetTrack(84, new GcrTrack(8)), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => disk.SetTrack(0, null!), Throws.ArgumentNullException);
        Assert.That(() => GcrDisk.FromD64(null!), Throws.ArgumentNullException);
    }
}
