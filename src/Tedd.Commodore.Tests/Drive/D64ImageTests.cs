using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Drive;

namespace Tedd.Commodore.Tests.Drive;

/// <summary>
/// Tests for <see cref="D64Image"/>: geometry, BAM, directory, file chains, PETSCII and the real disk images
/// in src/Tedd.Commodore.GUI (skipped when they are not present).
/// </summary>
[TestFixture]
public class D64ImageTests
{
    public const string FroggerImage = "Frogger '93 (Europe).D64";
    public const string BatmanImage = "Batman - The Movie (Europe).D64";

    /// <summary>
    /// Finds a media file: COMMODORE_MEDIA_DIR if set, otherwise src/Tedd.Commodore.GUI/&lt;name&gt; under any
    /// ancestor of the test directory (works from a git worktree nested inside the main checkout too).
    /// </summary>
    internal static string? LocateMedia(string fileName)
    {
        var env = Environment.GetEnvironmentVariable("COMMODORE_MEDIA_DIR");
        if (!string.IsNullOrEmpty(env))
        {
            var candidate = System.IO.Path.Combine(env, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "src", "Tedd.Commodore.GUI", fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }

    internal static D64Image LoadRealImageOrIgnore(string fileName)
    {
        var path = LocateMedia(fileName);
        if (path is null)
            Assert.Ignore($"{fileName} not found under src/Tedd.Commodore.GUI (set COMMODORE_MEDIA_DIR to override).");
        return D64Image.Load(path!);
    }

    private static int PopCount(ReadOnlySpan<byte> bytes)
    {
        int count = 0;
        foreach (var b in bytes)
            count += System.Numerics.BitOperations.PopCount(b);
        return count;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Geometry
    // ---------------------------------------------------------------------------------------------------------

    [TestCase(1, 21)]
    [TestCase(17, 21)]
    [TestCase(18, 19)]
    [TestCase(24, 19)]
    [TestCase(25, 18)]
    [TestCase(30, 18)]
    [TestCase(31, 17)]
    [TestCase(35, 17)]
    [TestCase(36, 17)]
    [TestCase(40, 17)]
    public void SectorsPerTrack_MatchesTheFourSpeedZones(int track, int expected)
    {
        Assert.That(D64Image.SectorsPerTrack(track), Is.EqualTo(expected));
    }

    [Test]
    public void SectorsPerTrack_RejectsInvalidTracks()
    {
        Assert.That(() => D64Image.SectorsPerTrack(0), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => D64Image.SectorsPerTrack(41), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void SectorOffsets_MatchTheD64Layout()
    {
        Assert.That(D64Image.SectorOffset(1, 0), Is.EqualTo(0));
        Assert.That(D64Image.SectorOffset(1, 20), Is.EqualTo(20 * 256));
        Assert.That(D64Image.SectorIndex(18, 0), Is.EqualTo(357));
        Assert.That(D64Image.SectorOffset(18, 0), Is.EqualTo(0x16500));
        Assert.That(D64Image.SectorOffset(35, 16), Is.EqualTo(D64Image.Size35Tracks - 256));
        Assert.That(D64Image.SectorOffset(40, 16), Is.EqualTo(D64Image.Size40Tracks - 256));
        Assert.That(D64Image.TotalSectors(35), Is.EqualTo(683));
        Assert.That(D64Image.TotalSectors(40), Is.EqualTo(768));
        Assert.That(() => D64Image.SectorIndex(1, 21), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => D64Image.SectorIndex(18, 19), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(D64Image.Size35Tracks, 35, false)]
    [TestCase(D64Image.Size35TracksWithErrors, 35, true)]
    [TestCase(D64Image.Size40Tracks, 40, false)]
    [TestCase(D64Image.Size40TracksWithErrors, 40, true)]
    public void Constructor_AcceptsAllFourSizes(int size, int tracks, bool errors)
    {
        var image = new D64Image(new byte[size]);
        Assert.That(image.TrackCount, Is.EqualTo(tracks));
        Assert.That(image.HasErrorBytes, Is.EqualTo(errors));
        Assert.That(image.Data.Length, Is.EqualTo(D64Image.TotalSectors(tracks) * 256));
        Assert.That(image.SectorCount, Is.EqualTo(D64Image.TotalSectors(tracks)));
        Assert.That(image.ToBytes().Length, Is.EqualTo(size));
        Assert.That(image.GetSector(tracks, 16).Length, Is.EqualTo(256));
        Assert.That(() => image.GetSector(tracks + 1, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(0)]
    [TestCase(174847)]
    [TestCase(174849)]
    [TestCase(200000)]
    public void Constructor_RejectsOtherSizes(int size)
    {
        Assert.That(() => new D64Image(new byte[size]), Throws.TypeOf<InvalidDataException>());
    }

    // ---------------------------------------------------------------------------------------------------------
    // CreateEmpty / BAM
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public void CreateEmpty_HasAFormattedBamAndAnEmptyDirectory()
    {
        var image = D64Image.CreateEmpty("TEST DISK", "01");

        Assert.That(image.TrackCount, Is.EqualTo(35));
        Assert.That(image.FreeBlocks, Is.EqualTo(664), "a NEW 1541 disk reports 664 BLOCKS FREE");
        Assert.That(image.DiskName, Is.EqualTo("TEST DISK"));
        Assert.That(image.DiskId, Is.EqualTo("01"));
        Assert.That(image.DosType, Is.EqualTo("2A"));
        Assert.That(image.ReadDirectory(), Is.Empty);
        Assert.That(image.HasErrorBytes, Is.False);

        var bam = image.Bam;
        Assert.That(bam[0], Is.EqualTo(18));
        Assert.That(bam[1], Is.EqualTo(1));
        Assert.That(bam[2], Is.EqualTo(0x41));
        Assert.That(bam[3], Is.EqualTo(0));
        Assert.That(bam[0xA0], Is.EqualTo(0xA0));
        Assert.That(bam[0xA1], Is.EqualTo(0xA0));
        Assert.That(bam[0xA4], Is.EqualTo(0xA0));
        Assert.That(bam[0xA7], Is.EqualTo(0xA0));
        Assert.That(bam[0xAA], Is.EqualTo(0xA0));
        Assert.That(bam[0xAB], Is.EqualTo(0x00));

        for (int track = 1; track <= 35; track++)
        {
            int sectors = D64Image.SectorsPerTrack(track);
            int entry = D64Image.BamEntryOffset(track);
            int expectedFree = track == 18 ? sectors - 2 : sectors;
            Assert.That(image.FreeBlocksOnTrack(track), Is.EqualTo(expectedFree), $"track {track}");
            Assert.That(PopCount(bam.Slice(entry + 1, 3)), Is.EqualTo(expectedFree), $"bitmap of track {track}");
            for (int sector = sectors; sector < 24; sector++)
                Assert.That((bam[entry + 1 + (sector >> 3)] >> (sector & 7)) & 1, Is.EqualTo(0), $"bit for non-existent sector {track}/{sector}");
        }
        Assert.That(image.IsSectorFree(18, 0), Is.False);
        Assert.That(image.IsSectorFree(18, 1), Is.False);
        Assert.That(image.IsSectorFree(18, 2), Is.True);
        Assert.That(image.IsSectorFree(1, 0), Is.True);

        var dir = image.GetSector(18, 1);
        Assert.That(dir[0], Is.EqualTo(0));
        Assert.That(dir[1], Is.EqualTo(0xFF));
        Assert.That(dir.Slice(2).ToArray(), Is.All.EqualTo(0));
    }

    [Test]
    public void CreateEmpty_RoundTripsThroughToBytes()
    {
        var image = D64Image.CreateEmpty("ROUND TRIP", "RT");
        var bytes = image.ToBytes();
        Assert.That(bytes.Length, Is.EqualTo(D64Image.Size35Tracks));

        var reloaded = new D64Image(bytes);
        Assert.That(reloaded.ToBytes(), Is.EqualTo(bytes));
        Assert.That(reloaded.DiskName, Is.EqualTo("ROUND TRIP"));
        Assert.That(reloaded.DiskId, Is.EqualTo("RT"));
        Assert.That(reloaded.FreeBlocks, Is.EqualTo(664));
    }

    [Test]
    public void CreateEmpty_Supports40Tracks()
    {
        var image = D64Image.CreateEmpty("FORTY", "40", 40);
        Assert.That(image.TrackCount, Is.EqualTo(40));
        Assert.That(image.ToBytes().Length, Is.EqualTo(D64Image.Size40Tracks));
        Assert.That(image.FreeBlocks, Is.EqualTo(664 + 5 * 17));
        Assert.That(image.FreeBlocksOnTrack(40), Is.EqualTo(17));
        Assert.That(image.IsSectorFree(38, 16), Is.True);
        Assert.That(image.GetSector(40, 16).Length, Is.EqualTo(256));
        Assert.That(() => image.GetSector(41, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => D64Image.CreateEmpty("X", "Y", 36), Throws.TypeOf<ArgumentOutOfRangeException>());

        var reloaded = new D64Image(image.ToBytes());
        Assert.That(reloaded.TrackCount, Is.EqualTo(40));
        Assert.That(reloaded.FreeBlocks, Is.EqualTo(749));
    }

    [Test]
    public void CreateEmpty_TruncatesAndPadsTheName()
    {
        var image = D64Image.CreateEmpty("THIS NAME IS TOO LONG", "AB");
        Assert.That(image.DiskName, Is.EqualTo("THIS NAME IS TOO"));

        var shortName = D64Image.CreateEmpty("HI", "AB");
        Assert.That(shortName.Bam.Slice(0x90, 16).ToArray(), Is.EqualTo(new byte[] { (byte)'H', (byte)'I', 0xA0, 0xA0, 0xA0, 0xA0, 0xA0, 0xA0, 0xA0, 0xA0, 0xA0, 0xA0, 0xA0, 0xA0, 0xA0, 0xA0 }));
        Assert.That(shortName.DiskName, Is.EqualTo("HI"));

        shortName.DiskName = "RENAMED";
        Assert.That(shortName.DiskName, Is.EqualTo("RENAMED"));
        shortName.DiskId = "ZZ";
        Assert.That(shortName.DiskId, Is.EqualTo("ZZ"));
    }

    [Test]
    public void SetSectorFree_KeepsBitmapAndCountInSync()
    {
        var image = D64Image.CreateEmpty("BAM", "01");
        image.SetSectorFree(1, 5, false);
        Assert.That(image.IsSectorFree(1, 5), Is.False);
        Assert.That(image.FreeBlocksOnTrack(1), Is.EqualTo(20));
        Assert.That(image.FreeBlocks, Is.EqualTo(663));
        image.SetSectorFree(1, 5, false); // idempotent
        Assert.That(image.FreeBlocksOnTrack(1), Is.EqualTo(20));
        image.SetSectorFree(1, 5, true);
        Assert.That(image.IsSectorFree(1, 5), Is.True);
        Assert.That(image.FreeBlocksOnTrack(1), Is.EqualTo(21));
    }

    // ---------------------------------------------------------------------------------------------------------
    // PETSCII
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public void Petscii_RoundTripsPrintableText()
    {
        const string text = "HELLO WORLD 0123 !#$%&'()*+,-./:;<=>?@[]";
        var petscii = D64Image.AsciiToPetscii(text, text.Length);
        Assert.That(petscii[0], Is.EqualTo(0x48));
        Assert.That(D64Image.PetsciiToAscii(petscii), Is.EqualTo(text));

        var lower = D64Image.AsciiToPetscii("abc", 3);
        Assert.That(lower, Is.EqualTo(new byte[] { 0xC1, 0xC2, 0xC3 }), "lower case letters are the shifted letters");
        Assert.That(D64Image.PetsciiToAscii(lower), Is.EqualTo("abc"));
        Assert.That(D64Image.PetsciiToAscii(new byte[] { 0x61, 0x62 }), Is.EqualTo("ab"), "$61-$7A alias $C1-$DA");

        Assert.That(D64Image.AsciiToPetscii('£'), Is.EqualTo(0x5C));
        Assert.That(D64Image.PetsciiToAscii((byte)0x5C), Is.EqualTo('£'));
        Assert.That(D64Image.AsciiToPetscii("AB", 4), Is.EqualTo(new byte[] { 0x41, 0x42, 0xA0, 0xA0 }));
    }

    [Test]
    public void Petscii_HandlesPaddingAndGraphics()
    {
        Assert.That(D64Image.PetsciiToAscii(new byte[] { 0x41, 0xA0, 0x42, 0xA0, 0xA0 }), Is.EqualTo("A B"));
        Assert.That(D64Image.PetsciiToAscii(new byte[] { 0x41, 0xA0, 0xA0 }, trimPadding: false), Is.EqualTo("A  "));
        Assert.That(D64Image.PetsciiToAscii(new byte[] { 0x7F, 0xA1, 0xFF }), Is.EqualTo("???"));
        Assert.That(D64Image.AsciiToPetscii('é'), Is.EqualTo((byte)'?'));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Sector access / write protect
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public void WriteSector_ReplacesTheSectorData()
    {
        var image = D64Image.CreateEmpty("W", "01");
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        image.WriteSector(3, 4, data);
        Assert.That(image.GetSector(3, 4).ToArray(), Is.EqualTo(data));
        Assert.That(image.Data.AsSpan(D64Image.SectorOffset(3, 4), 256).ToArray(), Is.EqualTo(data));
        Assert.That(() => image.WriteSector(3, 4, new byte[255]), Throws.ArgumentException);
    }

    [Test]
    public void WriteProtected_BlocksWrites()
    {
        var image = D64Image.CreateEmpty("WP", "01");
        image.WriteProtected = true;
        Assert.That(image.WriteProtected, Is.True);
        Assert.That(() => image.WriteSector(1, 0, new byte[256]), Throws.InvalidOperationException);
        Assert.That(() => image.SetSectorFree(1, 0, false), Throws.InvalidOperationException);
        Assert.That(image.GetSector(1, 0).Length, Is.EqualTo(256), "reading still works");

        image.WriteProtected = false;
        Assert.That(() => image.WriteSector(1, 0, new byte[256]), Throws.Nothing);
        var clone = image.Clone();
        Assert.That(clone.WriteProtected, Is.False);
        clone.GetSector(1, 0)[0] = 0x42;
        Assert.That(image.GetSector(1, 0)[0], Is.EqualTo(0), "clone is independent");
    }

    [Test]
    public void ErrorBytes_ArePreservedAndAccessible()
    {
        var bytes = new byte[D64Image.Size35TracksWithErrors];
        Array.Fill(bytes, D64ErrorCode.Ok, D64Image.Size35Tracks, 683);
        bytes[D64Image.Size35Tracks + D64Image.SectorIndex(18, 0)] = D64ErrorCode.DataChecksum;
        var image = new D64Image(bytes);

        Assert.That(image.HasErrorBytes, Is.True);
        Assert.That(image.GetErrorCode(18, 0), Is.EqualTo(D64ErrorCode.DataChecksum));
        Assert.That(image.GetErrorCode(1, 0), Is.EqualTo(D64ErrorCode.Ok));
        Assert.That(image.ToBytes(), Is.EqualTo(bytes));
        Assert.That(D64ErrorCode.ToDosError(D64ErrorCode.DataChecksum), Is.EqualTo(23));
        Assert.That(D64ErrorCode.ToDosError(D64ErrorCode.Ok), Is.EqualTo(0));
        Assert.That(D64ErrorCode.IsError(D64ErrorCode.HeaderNotFound), Is.True);
        Assert.That(D64ErrorCode.IsError(D64ErrorCode.Ok), Is.False);

        image.SetErrorBytes(null);
        Assert.That(image.HasErrorBytes, Is.False);
        Assert.That(image.GetErrorCode(18, 0), Is.EqualTo(D64ErrorCode.Ok));
        Assert.That(image.ToBytes().Length, Is.EqualTo(D64Image.Size35Tracks));
        Assert.That(() => image.SetErrorBytes(new byte[10]), Throws.ArgumentException);
    }

    [Test]
    public void SaveAndLoad_RoundTrip()
    {
        var image = D64Image.CreateEmpty("SAVED", "SV");
        image.GetSector(5, 5)[10] = 0x99;
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"commodore-{Guid.NewGuid():N}.d64");
        try
        {
            image.Save(path);
            Assert.That(image.Path, Is.EqualTo(path));
            var loaded = D64Image.Load(path);
            Assert.That(loaded.Path, Is.EqualTo(path));
            Assert.That(loaded.ToBytes(), Is.EqualTo(image.ToBytes()));
            Assert.That(loaded.GetSector(5, 5)[10], Is.EqualTo(0x99));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Directory and files (hand-built)
    // ---------------------------------------------------------------------------------------------------------

    private static void WriteEntry(Span<byte> sector, int slot, byte type, int track, int sectorNo, string name, int blocks)
    {
        var e = sector.Slice(slot * 32, 32);
        e[2] = type;
        e[3] = (byte)track;
        e[4] = (byte)sectorNo;
        D64Image.AsciiToPetscii(name, 16).CopyTo(e.Slice(5, 16));
        e[30] = (byte)(blocks & 0xFF);
        e[31] = (byte)(blocks >> 8);
    }

    private static D64Image BuildDirectoryImage()
    {
        var image = D64Image.CreateEmpty("DIR TEST", "DT");

        // 18/1: two entries, linked to 18/4.
        var dir1 = image.GetSector(18, 1);
        dir1[0] = 18;
        dir1[1] = 4;
        WriteEntry(dir1, 0, 0x82, 1, 0, "GAME", 2);            // closed PRG
        WriteEntry(dir1, 3, 0x41, 2, 3, "NOTES", 1);           // locked, not closed SEQ (slot 1-2 empty)
        // 18/4: one entry, last sector of the chain.
        var dir4 = image.GetSector(18, 4);
        dir4[0] = 0;
        dir4[1] = 0xFF;
        WriteEntry(dir4, 7, 0xC4, 3, 0, "DATA", 300);          // closed + locked REL
        dir4[7 * 32 + 21] = 4;
        dir4[7 * 32 + 22] = 5;
        dir4[7 * 32 + 23] = 100;

        // GAME: 1/0 (full) -> 1/10 (last, 32 valid bytes).
        var s0 = image.GetSector(1, 0);
        s0[0] = 1;
        s0[1] = 10;
        for (int i = 2; i < 256; i++)
            s0[i] = (byte)i;
        s0[2] = 0x01;
        s0[3] = 0x08;
        var s10 = image.GetSector(1, 10);
        s10[0] = 0;
        s10[1] = 0x21;
        for (int i = 2; i < 256; i++)
            s10[i] = (byte)(0xF0 + (i & 0x0F));
        return image;
    }

    [Test]
    public void ReadDirectory_ParsesEntriesAndFollowsTheChain()
    {
        var image = BuildDirectoryImage();
        var entries = image.ReadDirectory();

        Assert.That(entries, Has.Count.EqualTo(3));

        var game = entries[0];
        Assert.That(game.Index, Is.EqualTo(0));
        Assert.That(game.Name, Is.EqualTo("GAME"));
        Assert.That(game.RawName, Is.EqualTo(D64Image.AsciiToPetscii("GAME", 16)));
        Assert.That(game.FileType, Is.EqualTo(D64FileType.Prg));
        Assert.That(game.TypeName, Is.EqualTo("PRG"));
        Assert.That(game.Closed, Is.True);
        Assert.That(game.Locked, Is.False);
        Assert.That(game.Track, Is.EqualTo(1));
        Assert.That(game.Sector, Is.EqualTo(0));
        Assert.That(game.Blocks, Is.EqualTo(2));
        Assert.That(game.DirectoryTrack, Is.EqualTo(18));
        Assert.That(game.DirectorySector, Is.EqualTo(1));
        Assert.That(game.EntrySlot, Is.EqualTo(0));
        Assert.That(game.ToString(), Does.Contain("\"GAME\"").And.Contain("PRG"));

        var notes = entries[1];
        Assert.That(notes.Name, Is.EqualTo("NOTES"));
        Assert.That(notes.FileType, Is.EqualTo(D64FileType.Seq));
        Assert.That(notes.Closed, Is.False, "bit 7 clear = splat file");
        Assert.That(notes.Locked, Is.True);
        Assert.That(notes.EntrySlot, Is.EqualTo(3));
        Assert.That(notes.ToString(), Does.Contain("*SEQ<"));

        var data = entries[2];
        Assert.That(data.Name, Is.EqualTo("DATA"));
        Assert.That(data.FileType, Is.EqualTo(D64FileType.Rel));
        Assert.That(data.Blocks, Is.EqualTo(300), "16-bit block count");
        Assert.That(data.SideTrack, Is.EqualTo(4));
        Assert.That(data.SideSector, Is.EqualTo(5));
        Assert.That(data.RecordLength, Is.EqualTo(100));
        Assert.That(data.DirectorySector, Is.EqualTo(4));
        Assert.That(data.EntrySlot, Is.EqualTo(7));
    }

    [Test]
    public void ReadFile_FollowsTheSectorChainAndHonoursTheLastSectorLength()
    {
        var image = BuildDirectoryImage();
        var game = image.ReadDirectory()[0];
        var file = image.ReadFile(game);

        Assert.That(file.Length, Is.EqualTo(254 + 32));
        Assert.That(file[0] | (file[1] << 8), Is.EqualTo(0x0801));
        Assert.That(file[2], Is.EqualTo(4), "byte 4 of sector 1/0");
        Assert.That(file[253], Is.EqualTo(255), "last byte of the full sector");
        Assert.That(file[254], Is.EqualTo(0xF2), "first data byte of the last sector");
        Assert.That(file[285], Is.EqualTo(0xF0 + (0x21 & 0x0F)), "byte $21 of the last sector is the last valid byte");
        Assert.That(Math.Abs(file.Length - game.Blocks * 254), Is.LessThanOrEqualTo(254));
    }

    [Test]
    public void ReadFile_EmptyChainsAndTrackZero()
    {
        var image = D64Image.CreateEmpty("E", "01");
        var s = image.GetSector(2, 0);
        s[0] = 0;
        s[1] = 1; // "last byte index" 1 = no data
        Assert.That(image.ReadChain(2, 0), Is.Empty);
        Assert.That(image.ReadFile(new D64DirectoryEntry { Track = 0, Sector = 0 }), Is.Empty);
    }

    [Test]
    public void ReadFile_DetectsLoopsAndInvalidLinks()
    {
        var image = D64Image.CreateEmpty("LOOP", "01");
        var a = image.GetSector(1, 0);
        a[0] = 1;
        a[1] = 1;
        var b = image.GetSector(1, 1);
        b[0] = 1;
        b[1] = 0;
        Assert.That(() => image.ReadChain(1, 0), Throws.TypeOf<InvalidDataException>());

        b[0] = 40; // beyond the 35 tracks of this image
        b[1] = 0;
        Assert.That(() => image.ReadChain(1, 0), Throws.TypeOf<InvalidDataException>());

        b[0] = 1;
        b[1] = 21; // track 1 has sectors 0..20
        Assert.That(() => image.ReadChain(1, 0), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void ReadDirectory_StopsOnALoopedChain()
    {
        var image = BuildDirectoryImage();
        var dir4 = image.GetSector(18, 4);
        dir4[0] = 18;
        dir4[1] = 1; // back to the start
        var entries = image.ReadDirectory();
        Assert.That(entries, Has.Count.EqualTo(3));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Real images (skipped when absent)
    // ---------------------------------------------------------------------------------------------------------

    [TestCase(FroggerImage)]
    [TestCase(BatmanImage)]
    public void RealImage_HasADirectoryAndAConsistentBam(string fileName)
    {
        var image = LoadRealImageOrIgnore(fileName);
        Assert.That(image.TrackCount, Is.EqualTo(35));

        var entries = image.ReadDirectory();
        TestContext.Out.WriteLine($"0 \"{image.DiskName}\" {image.DiskId} {image.DosType}");
        foreach (var e in entries)
            TestContext.Out.WriteLine(e.ToString());
        TestContext.Out.WriteLine($"{image.FreeBlocks} BLOCKS FREE.");

        Assert.That(image.DiskName, Is.Not.Empty);
        Assert.That(entries.Any(e => e.FileType == D64FileType.Prg), Is.True, "at least one PRG");

        int bamTotal = 0;
        var bam = image.Bam;
        for (int track = 1; track <= 35; track++)
        {
            if (track != 18)
                bamTotal += bam[4 + (track - 1) * 4];
        }
        Assert.That(image.FreeBlocks, Is.EqualTo(bamTotal));
        Assert.That(image.FreeBlocks, Is.InRange(0, 664));
    }

    [TestCase(FroggerImage)]
    [TestCase(BatmanImage)]
    public void RealImage_FirstPrgLoadsAt0801AndMatchesItsBlockCount(string fileName)
    {
        var image = LoadRealImageOrIgnore(fileName);
        var prg = image.ReadDirectory().First(e => e.FileType == D64FileType.Prg);
        var file = image.ReadFile(prg);

        TestContext.Out.WriteLine($"{prg}: {file.Length} bytes, load address ${file[0] | (file[1] << 8):X4}");
        Assert.That(file.Length, Is.GreaterThanOrEqualTo(2));
        Assert.That(file[0] | (file[1] << 8), Is.EqualTo(0x0801), "BASIC start");
        Assert.That(Math.Abs(file.Length - prg.Blocks * 254), Is.LessThanOrEqualTo(254));
    }

    [TestCase(FroggerImage)]
    [TestCase(BatmanImage)]
    public void RealImage_RoundTripsThroughToBytes(string fileName)
    {
        var path = LocateMedia(fileName);
        if (path is null)
            Assert.Ignore($"{fileName} not found.");
        var original = File.ReadAllBytes(path!);
        var image = new D64Image(original);
        Assert.That(image.ToBytes(), Is.EqualTo(original));
    }
}
