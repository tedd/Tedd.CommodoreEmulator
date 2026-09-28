using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tedd.Commodore.Emulator.Drive;

/// <summary>Commodore DOS file types (bits 0-2 of the directory entry type byte).</summary>
public enum D64FileType : byte
{
    Del = 0,
    Seq = 1,
    Prg = 2,
    Usr = 3,
    Rel = 4,
}

/// <summary>
/// Per-sector error codes stored in the optional error block at the end of a D64 file. The values are the
/// FDC job error codes of the 1541 ("Inside Commodore DOS", table 8-1; Peter Schepers, D64.TXT "Error codes");
/// the DOS error number shown to the user is <c>code + 18</c> (e.g. $05 → "23, READ ERROR").
/// </summary>
public static class D64ErrorCode
{
    /// <summary>$01: no error ("00, OK").</summary>
    public const byte Ok = 0x01;
    /// <summary>$02: header block not found (20, READ ERROR).</summary>
    public const byte HeaderNotFound = 0x02;
    /// <summary>$03: no SYNC character on the track (21, READ ERROR).</summary>
    public const byte NoSync = 0x03;
    /// <summary>$04: data block not present after the header (22, READ ERROR).</summary>
    public const byte DataBlockNotFound = 0x04;
    /// <summary>$05: checksum error in the data block (23, READ ERROR).</summary>
    public const byte DataChecksum = 0x05;
    /// <summary>$07: write-verify error (25, WRITE ERROR).</summary>
    public const byte WriteVerify = 0x07;
    /// <summary>$08: write protect on (26, WRITE PROTECT ON).</summary>
    public const byte WriteProtect = 0x08;
    /// <summary>$09: checksum error in the header block (27, READ ERROR).</summary>
    public const byte HeaderChecksum = 0x09;
    /// <summary>$0A: long data block / write error (28, WRITE ERROR).</summary>
    public const byte WriteError = 0x0A;
    /// <summary>$0B: disk ID in the header does not match the ID the disk was initialised with (29, DISK ID MISMATCH).</summary>
    public const byte IdMismatch = 0x0B;
    /// <summary>$0F: drive not ready (74, DRIVE NOT READY).</summary>
    public const byte DriveNotReady = 0x0F;

    /// <summary>True for every code other than $00 (no information) and $01 (OK).</summary>
    public static bool IsError(byte code) => code > Ok;

    /// <summary>The DOS error number ("2x, READ ERROR") for an FDC code; 0 for OK / no information.</summary>
    public static int ToDosError(byte code) => code switch
    {
        0 or Ok => 0,
        DriveNotReady => 74,
        _ => code + 18,
    };
}

/// <summary>
/// One entry of a Commodore DOS directory ("Inside Commodore DOS", chapter 3; Peter Schepers, D64.TXT).
/// </summary>
public sealed class D64DirectoryEntry
{
    /// <summary>Position of the entry in the directory listing (0 = first shown).</summary>
    public int Index { get; init; }
    /// <summary>Directory sector holding the entry (normally on track 18).</summary>
    public int DirectoryTrack { get; init; }
    /// <summary>Directory sector holding the entry.</summary>
    public int DirectorySector { get; init; }
    /// <summary>Slot within the directory sector (0..7, 32 bytes each).</summary>
    public int EntrySlot { get; init; }
    /// <summary>The raw file type byte (+2): bits 0-2 type, bit 6 locked ("&lt;"), bit 7 closed (clear = "splat" file "*").</summary>
    public byte RawType { get; init; }
    /// <summary>File type from bits 0-2 of <see cref="RawType"/>.</summary>
    public D64FileType FileType { get; init; }
    /// <summary>Bit 6 of the type byte: the file cannot be scratched.</summary>
    public bool Locked { get; init; }
    /// <summary>Bit 7 of the type byte: the file was properly closed (otherwise shown with "*").</summary>
    public bool Closed { get; init; }
    /// <summary>Track of the first data sector (+3).</summary>
    public int Track { get; init; }
    /// <summary>Sector of the first data sector (+4).</summary>
    public int Sector { get; init; }
    /// <summary>The 16 raw PETSCII name bytes (+5), padded with $A0.</summary>
    public byte[] RawName { get; init; } = Array.Empty<byte>();
    /// <summary>The name converted with <see cref="D64Image.PetsciiToAscii(ReadOnlySpan{byte}, bool)"/> (trailing $A0 padding removed).</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>REL files: track of the first side-sector block (+21).</summary>
    public int SideTrack { get; init; }
    /// <summary>REL files: sector of the first side-sector block (+22).</summary>
    public int SideSector { get; init; }
    /// <summary>REL files: record length (+23).</summary>
    public int RecordLength { get; init; }
    /// <summary>File size in blocks (+30/+31, little endian) as shown by the directory listing.</summary>
    public int Blocks { get; init; }

    /// <summary>"DEL", "SEQ", "PRG", "USR", "REL" or "???".</summary>
    public string TypeName => FileType switch
    {
        D64FileType.Del => "DEL",
        D64FileType.Seq => "SEQ",
        D64FileType.Prg => "PRG",
        D64FileType.Usr => "USR",
        D64FileType.Rel => "REL",
        _ => "???",
    };

    /// <summary>Formats the entry like the C64 directory listing: blocks, quoted name, type with * and &lt; markers.</summary>
    public override string ToString()
    {
        string quoted = "\"" + Name + "\"";
        return $"{Blocks,-5}{quoted,-19}{(Closed ? " " : "*")}{TypeName}{(Locked ? "<" : "")}";
    }
}

/// <summary>
/// A 1541 disk image in the D64 format: the 256-byte sectors of a 35- or 40-track disk in track order,
/// optionally followed by one error byte per sector (Peter Schepers, D64.TXT). Provides sector access and the
/// Commodore DOS on-disk structures: BAM, disk name/ID, directory and file chains ("Inside Commodore DOS",
/// Immers/Neufeld, chapters 3-5).
///
/// Track numbers are 1-based like on the drive, sector numbers 0-based.
/// </summary>
public sealed class D64Image
{
    /// <summary>Bytes per sector.</summary>
    public const int SectorSize = 256;
    /// <summary>35 tracks, no error bytes: 683 × 256.</summary>
    public const int Size35Tracks = 174848;
    /// <summary>35 tracks + 683 error bytes.</summary>
    public const int Size35TracksWithErrors = 175531;
    /// <summary>40 tracks, no error bytes: 768 × 256.</summary>
    public const int Size40Tracks = 196608;
    /// <summary>40 tracks + 768 error bytes.</summary>
    public const int Size40TracksWithErrors = 197376;
    /// <summary>Highest track number supported by the D64 variants handled here.</summary>
    public const int MaxTrack = 40;

    /// <summary>The directory track.</summary>
    public const int DirectoryTrack = 18;
    /// <summary>Sector of the BAM on the directory track.</summary>
    public const int BamSector = 0;
    /// <summary>First directory sector (the DOS always starts the directory at 18/1).</summary>
    public const int FirstDirectorySector = 1;
    /// <summary>Directory entries per sector.</summary>
    public const int EntriesPerSector = 8;
    /// <summary>Bytes per directory entry.</summary>
    public const int EntrySize = 32;
    /// <summary>Offset of the 16-byte disk name in the BAM.</summary>
    public const int BamOffsetDiskName = 0x90;
    /// <summary>Offset of the 2-byte disk ID in the BAM.</summary>
    public const int BamOffsetDiskId = 0xA2;
    /// <summary>Offset of the 2-byte DOS type ("2A") in the BAM.</summary>
    public const int BamOffsetDosType = 0xA5;
    /// <summary>Offset of the first BAM entry (4 bytes per track: free count + 3 bitmap bytes).</summary>
    public const int BamOffsetEntries = 0x04;
    /// <summary>Offset of the BAM entries for tracks 36-40 in the Dolphin DOS 40-track layout.</summary>
    public const int BamOffsetExtendedEntries = 0xAC;
    /// <summary>PETSCII shifted space, used as padding in names and IDs.</summary>
    public const byte Padding = 0xA0;
    /// <summary>Bytes of file data per sector (bytes 2..255; bytes 0-1 are the link to the next sector).</summary>
    public const int DataBytesPerSector = SectorSize - 2;

    // Cumulative sector count before each track; index = track number (1..41).
    private static readonly int[] TrackStartSector = BuildTrackStartSectors();

    private static int[] BuildTrackStartSectors()
    {
        var table = new int[MaxTrack + 2];
        int total = 0;
        for (int track = 1; track <= MaxTrack; track++)
        {
            table[track] = total;
            total += SectorsPerTrack(track);
        }
        table[MaxTrack + 1] = total;
        return table;
    }

    /// <summary>Sector data: <see cref="TotalSectors(int)"/>(<see cref="TrackCount"/>) × 256 bytes in track/sector order (no error bytes).</summary>
    public byte[] Data { get; }

    /// <summary>One FDC error code per sector (see <see cref="D64ErrorCode"/>), or null when the image carries no error information.</summary>
    public byte[]? ErrorBytes { get; private set; }

    /// <summary>True when the file had the extra error block.</summary>
    public bool HasErrorBytes => ErrorBytes is not null;

    /// <summary>35 or 40.</summary>
    public int TrackCount { get; }

    /// <summary>Total number of sectors (683 or 768).</summary>
    public int SectorCount => TotalSectors(TrackCount);

    /// <summary>The physical write protect tab. <see cref="WriteSector"/> refuses to write while set; propagated to <see cref="GcrDisk.WriteProtected"/>.</summary>
    public bool WriteProtected { get; set; }

    /// <summary>File the image was loaded from / last saved to (informational).</summary>
    public string? Path { get; set; }

    /// <summary>Parses a complete D64 file (one of the four supported sizes). The bytes are copied.</summary>
    public D64Image(byte[] bytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        bool hasErrors;
        switch (bytes.Length)
        {
            case Size35Tracks: TrackCount = 35; hasErrors = false; break;
            case Size35TracksWithErrors: TrackCount = 35; hasErrors = true; break;
            case Size40Tracks: TrackCount = 40; hasErrors = false; break;
            case Size40TracksWithErrors: TrackCount = 40; hasErrors = true; break;
            default:
                throw new InvalidDataException($"Unsupported D64 size {bytes.Length}; expected {Size35Tracks}, {Size35TracksWithErrors}, {Size40Tracks} or {Size40TracksWithErrors} bytes.");
        }
        int sectors = TotalSectors(TrackCount);
        Data = new byte[sectors * SectorSize];
        Buffer.BlockCopy(bytes, 0, Data, 0, Data.Length);
        if (hasErrors)
        {
            ErrorBytes = new byte[sectors];
            Buffer.BlockCopy(bytes, Data.Length, ErrorBytes, 0, sectors);
        }
    }

    private D64Image(int trackCount, byte[] data, byte[]? errorBytes)
    {
        TrackCount = trackCount;
        Data = data;
        ErrorBytes = errorBytes;
    }

    /// <summary>Wraps existing sector data (683 or 768 sectors) and an optional error block without copying.</summary>
    public static D64Image FromSectors(byte[] sectorData, byte[]? errorBytes = null)
    {
        if (sectorData is null) throw new ArgumentNullException(nameof(sectorData));
        int trackCount = sectorData.Length switch
        {
            Size35Tracks => 35,
            Size40Tracks => 40,
            _ => throw new ArgumentException($"Sector data must be {Size35Tracks} or {Size40Tracks} bytes.", nameof(sectorData)),
        };
        if (errorBytes is not null && errorBytes.Length != TotalSectors(trackCount))
            throw new ArgumentException($"Error block must have {TotalSectors(trackCount)} bytes.", nameof(errorBytes));
        return new D64Image(trackCount, sectorData, errorBytes);
    }

    /// <summary>Loads a D64 file.</summary>
    public static D64Image Load(string path)
    {
        var image = new D64Image(File.ReadAllBytes(path)) { Path = path };
        return image;
    }

    /// <summary>Returns the complete file image: sector data followed by the error block when present.</summary>
    public byte[] ToBytes()
    {
        int errorLength = ErrorBytes?.Length ?? 0;
        var bytes = new byte[Data.Length + errorLength];
        Buffer.BlockCopy(Data, 0, bytes, 0, Data.Length);
        if (ErrorBytes is not null)
            Buffer.BlockCopy(ErrorBytes, 0, bytes, Data.Length, errorLength);
        return bytes;
    }

    /// <summary>Writes the image (<see cref="ToBytes"/>) to <paramref name="path"/> and records it in <see cref="Path"/>.</summary>
    public void Save(string path)
    {
        File.WriteAllBytes(path, ToBytes());
        Path = path;
    }

    /// <summary>Returns a deep copy (data, error bytes, write protect flag).</summary>
    public D64Image Clone()
    {
        return new D64Image(TrackCount, (byte[])Data.Clone(), (byte[]?)ErrorBytes?.Clone())
        {
            WriteProtected = WriteProtected,
            Path = Path,
        };
    }

    /// <summary>Attaches (or removes, with null) the per-sector error block.</summary>
    public void SetErrorBytes(byte[]? errorBytes)
    {
        if (errorBytes is not null && errorBytes.Length != SectorCount)
            throw new ArgumentException($"Error block must have {SectorCount} bytes.", nameof(errorBytes));
        ErrorBytes = errorBytes;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Geometry
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sectors on a track: 21 for tracks 1-17, 19 for 18-24, 18 for 25-30, 17 for 31-35 (and the non-standard
    /// tracks 36-40) — the four speed zones of the 1541 (Inside Commodore DOS, table 1-2).
    /// </summary>
    public static int SectorsPerTrack(int track)
    {
        if (track < 1 || track > MaxTrack)
            throw new ArgumentOutOfRangeException(nameof(track), track, "Track must be 1..40.");
        if (track <= 17) return 21;
        if (track <= 24) return 19;
        if (track <= 30) return 18;
        return 17;
    }

    /// <summary>Total number of sectors on a disk with <paramref name="trackCount"/> tracks (683 for 35, 768 for 40).</summary>
    public static int TotalSectors(int trackCount)
    {
        if (trackCount < 1 || trackCount > MaxTrack)
            throw new ArgumentOutOfRangeException(nameof(trackCount), trackCount, "Track count must be 1..40.");
        return TrackStartSector[trackCount + 1];
    }

    /// <summary>Linear sector index (0 = 1/0) of a track/sector pair.</summary>
    public static int SectorIndex(int track, int sector)
    {
        int count = SectorsPerTrack(track);
        if (sector < 0 || sector >= count)
            throw new ArgumentOutOfRangeException(nameof(sector), sector, $"Track {track} has sectors 0..{count - 1}.");
        return TrackStartSector[track] + sector;
    }

    /// <summary>Byte offset of a sector within the image (18/0 is at $16500).</summary>
    public static int SectorOffset(int track, int sector) => SectorIndex(track, sector) * SectorSize;

    private void CheckTrack(int track)
    {
        if (track < 1 || track > TrackCount)
            throw new ArgumentOutOfRangeException(nameof(track), track, $"This image has tracks 1..{TrackCount}.");
    }

    /// <summary>The 256 bytes of a sector (a live view into <see cref="Data"/>).</summary>
    public Span<byte> GetSector(int track, int sector)
    {
        CheckTrack(track);
        return Data.AsSpan(SectorOffset(track, sector), SectorSize);
    }

    /// <summary>Replaces the 256 bytes of a sector. Throws <see cref="InvalidOperationException"/> when <see cref="WriteProtected"/>.</summary>
    public void WriteSector(int track, int sector, ReadOnlySpan<byte> data)
    {
        if (WriteProtected)
            throw new InvalidOperationException("The disk image is write protected.");
        if (data.Length != SectorSize)
            throw new ArgumentException($"A sector is {SectorSize} bytes.", nameof(data));
        data.CopyTo(GetSector(track, sector));
    }

    /// <summary>The FDC error code of a sector; <see cref="D64ErrorCode.Ok"/> when the image has no error block.</summary>
    public byte GetErrorCode(int track, int sector)
    {
        CheckTrack(track);
        return ErrorBytes is null ? D64ErrorCode.Ok : ErrorBytes[SectorIndex(track, sector)];
    }

    // ---------------------------------------------------------------------------------------------------------
    // BAM / disk header
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The BAM sector (18/0).</summary>
    public Span<byte> Bam => GetSector(DirectoryTrack, BamSector);

    /// <summary>Disk name (BAM $90-$9F, 16 PETSCII bytes padded with $A0) converted to ASCII. Setting it writes the padded PETSCII name.</summary>
    public string DiskName
    {
        get => PetsciiToAscii(Bam.Slice(BamOffsetDiskName, 16));
        set => AsciiToPetscii(value, 16).CopyTo(Bam.Slice(BamOffsetDiskName, 16));
    }

    /// <summary>Disk ID (BAM $A2-$A3, 2 bytes). Setting it writes the PETSCII ID.</summary>
    public string DiskId
    {
        get => PetsciiToAscii(Bam.Slice(BamOffsetDiskId, 2), trimPadding: false);
        set => AsciiToPetscii(value, 2).CopyTo(Bam.Slice(BamOffsetDiskId, 2));
    }

    /// <summary>DOS type (BAM $A5-$A6), "2A" for 1541 disks.</summary>
    public string DosType => PetsciiToAscii(Bam.Slice(BamOffsetDosType, 2), trimPadding: false);

    /// <summary>Offset of the 4-byte BAM entry for a track (tracks 36-40 use the Dolphin DOS extension at $AC).</summary>
    public static int BamEntryOffset(int track)
    {
        if (track < 1 || track > MaxTrack)
            throw new ArgumentOutOfRangeException(nameof(track), track, "Track must be 1..40.");
        return track <= 35 ? BamOffsetEntries + (track - 1) * 4 : BamOffsetExtendedEntries + (track - 36) * 4;
    }

    /// <summary>Free blocks on a track according to the BAM count byte.</summary>
    public int FreeBlocksOnTrack(int track)
    {
        CheckTrack(track);
        return Bam[BamEntryOffset(track)];
    }

    /// <summary>
    /// "BLOCKS FREE." as reported by the DOS: sum of the BAM count bytes of all tracks except the directory
    /// track 18 (664 for an empty 35-track disk). Tracks 36-40 of a 40-track image are included from the
    /// Dolphin DOS BAM extension.
    /// </summary>
    public int FreeBlocks
    {
        get
        {
            int total = 0;
            for (int track = 1; track <= TrackCount; track++)
            {
                if (track != DirectoryTrack)
                    total += FreeBlocksOnTrack(track);
            }
            return total;
        }
    }

    /// <summary>True when the BAM bitmap bit for the sector is set (1 = free).</summary>
    public bool IsSectorFree(int track, int sector)
    {
        CheckTrack(track);
        SectorIndex(track, sector); // range check
        int offset = BamEntryOffset(track) + 1 + (sector >> 3);
        return (Bam[offset] & (1 << (sector & 7))) != 0;
    }

    /// <summary>Sets or clears the BAM bitmap bit of a sector and keeps the track's free count byte in sync.</summary>
    public void SetSectorFree(int track, int sector, bool free)
    {
        if (WriteProtected)
            throw new InvalidOperationException("The disk image is write protected.");
        CheckTrack(track);
        SectorIndex(track, sector); // range check
        var bam = Bam;
        int entry = BamEntryOffset(track);
        int offset = entry + 1 + (sector >> 3);
        int mask = 1 << (sector & 7);
        bool wasFree = (bam[offset] & mask) != 0;
        if (wasFree == free)
            return;
        if (free)
        {
            bam[offset] |= (byte)mask;
            bam[entry]++;
        }
        else
        {
            bam[offset] &= (byte)~mask;
            bam[entry]--;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Directory and files
    // ---------------------------------------------------------------------------------------------------------

    private bool IsValidLink(int track, int sector) =>
        track >= 1 && track <= TrackCount && sector >= 0 && sector < SectorsPerTrack(track);

    /// <summary>
    /// Reads the directory: follows the sector chain starting at 18/1 (each sector holds 8 entries of 32 bytes,
    /// entries with type byte 0 are unused). Stops at a track-0 link, an invalid link or a loop.
    /// </summary>
    public IReadOnlyList<D64DirectoryEntry> ReadDirectory()
    {
        var entries = new List<D64DirectoryEntry>();
        var visited = new bool[SectorCount];
        int track = DirectoryTrack, sector = FirstDirectorySector;
        while (IsValidLink(track, sector))
        {
            int index = SectorIndex(track, sector);
            if (visited[index])
                break;
            visited[index] = true;
            var block = GetSector(track, sector);
            for (int slot = 0; slot < EntriesPerSector; slot++)
            {
                var e = block.Slice(slot * EntrySize, EntrySize);
                byte type = e[2];
                if (type == 0)
                    continue;
                var rawName = e.Slice(5, 16).ToArray();
                entries.Add(new D64DirectoryEntry
                {
                    Index = entries.Count,
                    DirectoryTrack = track,
                    DirectorySector = sector,
                    EntrySlot = slot,
                    RawType = type,
                    FileType = (D64FileType)(type & 0x07),
                    Locked = (type & 0x40) != 0,
                    Closed = (type & 0x80) != 0,
                    Track = e[3],
                    Sector = e[4],
                    RawName = rawName,
                    Name = PetsciiToAscii(rawName),
                    SideTrack = e[21],
                    SideSector = e[22],
                    RecordLength = e[23],
                    Blocks = e[30] | (e[31] << 8),
                });
            }
            track = block[0];
            sector = block[1];
        }
        return entries;
    }

    /// <summary>Reads the data of a file by following its sector chain from <see cref="D64DirectoryEntry.Track"/>/<see cref="D64DirectoryEntry.Sector"/>.</summary>
    public byte[] ReadFile(D64DirectoryEntry entry)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        if (entry.Track == 0)
            return Array.Empty<byte>();
        return ReadChain(entry.Track, entry.Sector);
    }

    /// <summary>
    /// Follows a sector chain: bytes 0-1 of every sector link to the next track/sector; in the last sector the
    /// track byte is 0 and the sector byte holds the index of the last valid byte, so bytes 2..n are data
    /// (Inside Commodore DOS, chapter 4). Throws <see cref="InvalidDataException"/> on an invalid link or a loop.
    /// </summary>
    public byte[] ReadChain(int track, int sector)
    {
        var result = new MemoryStream();
        var visited = new bool[SectorCount];
        while (track != 0)
        {
            if (!IsValidLink(track, sector))
                throw new InvalidDataException($"Invalid sector link {track}/{sector} in file chain.");
            int index = SectorIndex(track, sector);
            if (visited[index])
                throw new InvalidDataException($"Loop in file chain at {track}/{sector}.");
            visited[index] = true;
            var block = GetSector(track, sector);
            int nextTrack = block[0];
            int nextSector = block[1];
            if (nextTrack == 0)
            {
                // Last sector: nextSector = index of the last valid byte (2..255); 0/1 mean an empty sector.
                int count = nextSector < 2 ? 0 : nextSector - 1;
                result.Write(block.Slice(2, count));
                break;
            }
            result.Write(block.Slice(2, DataBytesPerSector));
            track = nextTrack;
            sector = nextSector;
        }
        return result.ToArray();
    }

    /// <summary>
    /// Creates a freshly formatted ("NEW") disk: an empty BAM at 18/0 with all sectors free except 18/0 and 18/1,
    /// the disk name/ID, DOS version 'A' and type "2A", and an empty directory sector at 18/1 (Inside Commodore
    /// DOS, chapter 3, "The BAM"). A 40-track disk additionally marks tracks 36-40 free in the Dolphin DOS
    /// BAM extension at $AC.
    /// </summary>
    public static D64Image CreateEmpty(string name, string id, int trackCount = 35)
    {
        if (trackCount != 35 && trackCount != 40)
            throw new ArgumentOutOfRangeException(nameof(trackCount), trackCount, "Track count must be 35 or 40.");
        var image = new D64Image(trackCount, new byte[TotalSectors(trackCount) * SectorSize], null);
        var bam = image.Bam;
        bam[0] = DirectoryTrack;        // link to the first directory sector
        bam[1] = FirstDirectorySector;
        bam[2] = 0x41;                  // DOS version 'A' (1540/1541/4040)
        bam[3] = 0x00;                  // unused ($80 = soft write protect on some DOS versions)
        for (int track = 1; track <= trackCount; track++)
        {
            int sectors = SectorsPerTrack(track);
            int entry = BamEntryOffset(track);
            bam[entry] = (byte)sectors;
            for (int sector = 0; sector < sectors; sector++)
                bam[entry + 1 + (sector >> 3)] |= (byte)(1 << (sector & 7));
        }
        bam.Slice(BamOffsetDiskName, 0x1B).Fill(Padding);   // $90-$AA: name, $A0 $A0, ID, $A0, DOS type, $A0 $A0 $A0 $A0
        AsciiToPetscii(name, 16).CopyTo(bam.Slice(BamOffsetDiskName, 16));
        AsciiToPetscii(id, 2).CopyTo(bam.Slice(BamOffsetDiskId, 2));
        bam[BamOffsetDosType] = (byte)'2';
        bam[BamOffsetDosType + 1] = (byte)'A';

        // 18/0 and 18/1 are in use.
        image.SetSectorFree(DirectoryTrack, BamSector, false);
        image.SetSectorFree(DirectoryTrack, FirstDirectorySector, false);

        // Empty directory sector: last in chain, all 256 bytes "valid".
        var dir = image.GetSector(DirectoryTrack, FirstDirectorySector);
        dir[0] = 0x00;
        dir[1] = 0xFF;
        return image;
    }

    // ---------------------------------------------------------------------------------------------------------
    // PETSCII
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Converts a PETSCII name to text as it appears on a C64 in its default upper-case/graphics mode:
    /// $41-$5A → 'A'-'Z', the shifted letters $C1-$DA (and their aliases $61-$7A) → 'a'-'z', $A0 → space,
    /// $5C → '£', $5E → '^' (up arrow), $5F → '_' (left arrow), graphics characters → '?'.
    /// </summary>
    public static string PetsciiToAscii(ReadOnlySpan<byte> petscii, bool trimPadding = true)
    {
        int end = petscii.Length;
        if (trimPadding)
        {
            while (end > 0 && petscii[end - 1] == Padding)
                end--;
        }
        var sb = new StringBuilder(end);
        for (int i = 0; i < end; i++)
            sb.Append(PetsciiToAscii(petscii[i]));
        return sb.ToString();
    }

    /// <summary>Converts one PETSCII code (see <see cref="PetsciiToAscii(ReadOnlySpan{byte}, bool)"/>).</summary>
    public static char PetsciiToAscii(byte petscii) => petscii switch
    {
        >= 0x20 and <= 0x40 => (char)petscii,           // space, punctuation, digits, '@'
        >= 0x41 and <= 0x5A => (char)petscii,           // unshifted letters: upper case on the default screen
        0x5B => '[',
        0x5C => '£',
        0x5D => ']',
        0x5E => '^',                                    // up arrow
        0x5F => '_',                                    // left arrow
        >= 0x61 and <= 0x7A => (char)petscii,           // shifted letters (alias of $C1-$DA)
        >= 0xC1 and <= 0xDA => (char)(petscii - 0x60),  // shifted letters → 'a'-'z'
        Padding or 0xE0 => ' ',                         // shifted space
        0x60 or 0xC0 => '-',                            // horizontal bar
        0x7D or 0xDD => '|',                            // vertical bar
        _ => '?',
    };

    /// <summary>Converts one character to PETSCII: 'A'-'Z' → $41-$5A, 'a'-'z' → $C1-$DA, unknown → '?'.</summary>
    public static byte AsciiToPetscii(char c) => c switch
    {
        >= ' ' and <= '@' => (byte)c,
        >= 'A' and <= 'Z' => (byte)c,
        >= 'a' and <= 'z' => (byte)(c - 'a' + 0xC1),
        '[' => 0x5B,
        '£' => 0x5C,
        ']' => 0x5D,
        '^' => 0x5E,
        '_' => 0x5F,
        '|' => 0xDD,
        _ => (byte)'?',
    };

    /// <summary>Converts text to a fixed-length PETSCII field, truncating or padding with <paramref name="padding"/> ($A0 by default).</summary>
    public static byte[] AsciiToPetscii(string text, int length, byte padding = Padding)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var result = new byte[length];
        Array.Fill(result, padding);
        int n = Math.Min(length, text.Length);
        for (int i = 0; i < n; i++)
            result[i] = AsciiToPetscii(text[i]);
        return result;
    }
}
