using System;
using System.Collections.Generic;

namespace Tedd.Commodore.Emulator.Drive;

/// <summary>
/// A 1541 disk as the drive mechanics see it: 84 half-tracks of raw GCR bit streams (<see cref="GcrTrack"/>).
/// Half-track index 0 is track 1, index 2 is track 2, ... index 68 is track 35, index 78 is track 40; the odd
/// indexes are the half-track positions in between. The head of a 1541 steps in half-track units, which is why
/// the disk is modelled this way (Inside Commodore DOS, chapter 8, "The stepper motor").
///
/// <see cref="FromD64"/> lays every sector out exactly like the 1541 DOS FORMAT routine does ($FAC7 in the
/// DOS ROM; Inside Commodore DOS chapter 6; Peter Schepers G64.TXT "Sector layout"):
/// <code>
///   SYNC          5 × $FF                 40 one bits (the DOS recognises ≥ 10 one bits as SYNC)
///   header block  10 GCR bytes = GCR($08, checksum, sector, track, ID2, ID1, $0F, $0F)
///   header gap    9 × $55
///   SYNC          5 × $FF
///   data block    325 GCR bytes = GCR($07, 256 data bytes, checksum, $00, $00)
///   tail gap      n × $55                  n chosen so the track has its nominal length
/// </code>
/// The checksum of a block is the XOR of its payload bytes (sector^track^ID2^ID1 for the header, the 256 data
/// bytes for the data block). Odd half-tracks are left unformatted (all bit cells 0).
///
/// Nominal track lengths: the spindle turns at 300 RPM (200 ms per revolution) and the bit rate depends on the
/// speed zone (16 MHz / 4 / (16 - zone) bits per second, selected by VIA2 PB5-6), giving 7692 bytes for zone 3
/// (tracks 1-17), 7142 for zone 2 (18-24), 6666 for zone 1 (25-30) and 6250 for zone 0 (31-35 and beyond)
/// (Inside Commodore DOS, table 1-2; G64.TXT "Track sizes").
/// </summary>
public sealed class GcrDisk
{
    /// <summary>Number of half-track positions the 1541 head can reach (tracks 1.0 .. 42.5).</summary>
    public const int HalfTrackCount = 84;
    /// <summary>Highest full track number representable (half-track 82).</summary>
    public const int MaxTrack = HalfTrackCount / 2;

    /// <summary>Bytes of $FF written as a SYNC mark (40 one bits).</summary>
    public const int SyncLength = 5;
    /// <summary>Minimum number of consecutive one bits the 1541 recognises as SYNC (hardware sync detector, 74LS191 counter).</summary>
    public const int SyncBits = 10;
    /// <summary>GCR bytes in a header block (8 decoded bytes).</summary>
    public const int HeaderBlockLength = 10;
    /// <summary>Bytes of $55 between the header block and the data block SYNC.</summary>
    public const int HeaderGapLength = 9;
    /// <summary>GCR bytes in a data block (260 decoded bytes: ID, 256 data, checksum, 2 × $00).</summary>
    public const int DataBlockLength = 325;
    /// <summary>Bytes per sector excluding the tail gap: 5 + 10 + 9 + 5 + 325 = 354.</summary>
    public const int SectorOverhead = SyncLength + HeaderBlockLength + HeaderGapLength + SyncLength + DataBlockLength;
    /// <summary>First decoded byte of a header block.</summary>
    public const byte HeaderBlockId = 0x08;
    /// <summary>First decoded byte of a data block.</summary>
    public const byte DataBlockId = 0x07;
    /// <summary>Gap filler byte.</summary>
    public const byte GapByte = 0x55;
    /// <summary>SYNC byte.</summary>
    public const byte SyncByte = 0xFF;
    /// <summary>Spindle speed.</summary>
    public const int Rpm = 300;
    /// <summary>Largest distance (in bits) between a header's SYNC and its data block's SYNC that <see cref="DecodeTrack"/> still pairs (nominal: 80 + 72 + 40 = 192).</summary>
    public const int MaxHeaderToDataBits = 2000;

    // Nominal track length in bytes per speed zone 0..3.
    private static readonly int[] TrackLengthByZone = { 6250, 6666, 7142, 7692 };

    private readonly GcrTrack[] _tracks = new GcrTrack[HalfTrackCount];

    /// <summary>Number of full tracks a D64 made from this disk holds (35 or 40); set by <see cref="FromD64"/>.</summary>
    public int TrackCount { get; }

    /// <summary>The write protect tab: the mechanics report it on VIA2 PB4 and refuse to write while set.</summary>
    public bool WriteProtected { get; set; }

    /// <summary>True when the source D64 carried an error block; <see cref="ToD64()"/> then always emits one.</summary>
    public bool HasErrorInfo { get; set; }

    /// <summary>Creates a disk with all 84 half-tracks unformatted (nominal length for their zone, all bit cells 0).</summary>
    public GcrDisk(int trackCount = 35)
    {
        if (trackCount < 1 || trackCount > MaxTrack)
            throw new ArgumentOutOfRangeException(nameof(trackCount), trackCount, $"Track count must be 1..{MaxTrack}.");
        TrackCount = trackCount;
        for (int h = 0; h < HalfTrackCount; h++)
            _tracks[h] = new GcrTrack(TrackLength(h) * 8);
    }

    /// <summary>The bit stream of a half-track (0..83). Never null.</summary>
    public GcrTrack this[int halfTrack] => _tracks[halfTrack];

    /// <summary>The bit stream of a half-track (0..83). Never null.</summary>
    public GcrTrack GetTrack(int halfTrack) => _tracks[halfTrack];

    /// <summary>The bit stream of a full track (1..42).</summary>
    public GcrTrack GetFullTrack(int track) => _tracks[TrackToHalfTrack(track)];

    /// <summary>Replaces the bit stream of a half-track (e.g. with a custom-length track from a G64 file).</summary>
    public void SetTrack(int halfTrack, GcrTrack track)
    {
        if (track is null) throw new ArgumentNullException(nameof(track));
        if (halfTrack < 0 || halfTrack >= HalfTrackCount)
            throw new ArgumentOutOfRangeException(nameof(halfTrack));
        _tracks[halfTrack] = track;
    }

    /// <summary>True if any track was written since the flag was last cleared (setting false clears every track's flag).</summary>
    public bool Modified
    {
        get
        {
            for (int h = 0; h < HalfTrackCount; h++)
            {
                if (_tracks[h].Modified)
                    return true;
            }
            return false;
        }
        set
        {
            for (int h = 0; h < HalfTrackCount; h++)
                _tracks[h].Modified = value;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Geometry / timing
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Half-track index of a full track: (track - 1) × 2.</summary>
    public static int TrackToHalfTrack(int track)
    {
        if (track < 1 || track > MaxTrack)
            throw new ArgumentOutOfRangeException(nameof(track), track, $"Track must be 1..{MaxTrack}.");
        return (track - 1) * 2;
    }

    /// <summary>Full track number a half-track index belongs to (odd indexes round down: 1 → track 1, i.e. track 1.5).</summary>
    public static int HalfTrackToTrack(int halfTrack)
    {
        if (halfTrack < 0 || halfTrack >= HalfTrackCount)
            throw new ArgumentOutOfRangeException(nameof(halfTrack), halfTrack, $"Half-track must be 0..{HalfTrackCount - 1}.");
        return halfTrack / 2 + 1;
    }

    /// <summary>
    /// Speed zone of a half-track, equal to the value the DOS writes to VIA2 PB5-6 (the density bits) for the
    /// track: 3 for tracks 1-17, 2 for 18-24, 1 for 25-30, 0 for 31 and up (DOS ROM table at $FED7 / $F5C4;
    /// Inside Commodore DOS, table 1-2). Zone 3 has the highest bit rate.
    /// </summary>
    public static int SpeedZone(int halfTrack) => SpeedZoneOfTrack(HalfTrackToTrack(halfTrack));

    /// <summary>Speed zone of a full track (see <see cref="SpeedZone"/>).</summary>
    public static int SpeedZoneOfTrack(int track)
    {
        if (track < 1 || track > MaxTrack)
            throw new ArgumentOutOfRangeException(nameof(track), track, $"Track must be 1..{MaxTrack}.");
        if (track <= 17) return 3;
        if (track <= 24) return 2;
        if (track <= 30) return 1;
        return 0;
    }

    /// <summary>
    /// Length of one bit cell in 4 MHz clocks (16 MHz crystal / 4): 16, 15, 14, 13 for zones 0..3. The bit clock
    /// is 16 MHz / (16 - zone) / 4, so zone 0 (tracks 31-35) is the slowest at 250 kbit/s and zone 3 (tracks
    /// 1-17) the fastest at 307.7 kbit/s (1541 schematic, UF6/UE7 divider chain; Inside Commodore DOS ch. 7).
    /// One byte takes twice that many 1 MHz CPU cycles: see <see cref="CpuCyclesPerByte"/>.
    /// </summary>
    public static int ClocksPerBit(int zone)
    {
        if (zone < 0 || zone > 3)
            throw new ArgumentOutOfRangeException(nameof(zone), zone, "Zone must be 0..3.");
        return 16 - zone;
    }

    /// <summary>1 MHz CPU cycles per byte: 32, 30, 28, 26 for zones 0..3 (8 bits × (16 - zone) / 4).</summary>
    public static int CpuCyclesPerByte(int zone) => ClocksPerBit(zone) * 2;

    /// <summary>Bit rate of a zone: 250000, 266666, 285714, 307692 bit/s for zones 0..3.</summary>
    public static int BitsPerSecond(int zone) => 16_000_000 / 4 / ClocksPerBit(zone);

    /// <summary>Nominal track length in bytes for a speed zone: 6250, 6666, 7142, 7692 (200 ms × bit rate / 8).</summary>
    public static int TrackLengthForZone(int zone)
    {
        if (zone < 0 || zone > 3)
            throw new ArgumentOutOfRangeException(nameof(zone), zone, "Zone must be 0..3.");
        return TrackLengthByZone[zone];
    }

    /// <summary>Nominal track length in bytes of a half-track (see <see cref="TrackLengthForZone"/>).</summary>
    public static int TrackLength(int halfTrack) => TrackLengthByZone[SpeedZone(halfTrack)];

    // ---------------------------------------------------------------------------------------------------------
    // D64 → GCR
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the standard 1541 track layout for every full track of a D64 image. The disk ID in the sector
    /// headers is taken from the BAM ($A2/$A3). When the image has an error block, sectors with an error code are
    /// written damaged so that the drive reproduces the error (see <see cref="EncodeSector"/>).
    /// </summary>
    public static GcrDisk FromD64(D64Image image)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        var disk = new GcrDisk(image.TrackCount)
        {
            WriteProtected = image.WriteProtected,
            HasErrorInfo = image.HasErrorBytes,
        };
        var bam = image.Bam;
        byte id1 = bam[D64Image.BamOffsetDiskId];
        byte id2 = bam[D64Image.BamOffsetDiskId + 1];
        for (int track = 1; track <= image.TrackCount; track++)
        {
            int sectors = D64Image.SectorsPerTrack(track);
            int first = D64Image.SectorIndex(track, 0);
            var data = image.Data.AsSpan(first * D64Image.SectorSize, sectors * D64Image.SectorSize);
            ReadOnlySpan<byte> errors = image.ErrorBytes is null ? ReadOnlySpan<byte>.Empty : image.ErrorBytes.AsSpan(first, sectors);
            disk._tracks[TrackToHalfTrack(track)] = FormatTrack(track, data, id1, id2, errors);
        }
        return disk;
    }

    /// <summary>
    /// Formats one full track: all sectors in physical order 0..n-1 (the DOS FORMAT routine writes them
    /// sequentially; the interleave of 10 is a logical allocation strategy only), each followed by a tail gap.
    /// The nominal track length minus 354 bytes per sector is distributed evenly over the tail gaps, the
    /// remainder goes into the last gap. <paramref name="errors"/> is empty or one <see cref="D64ErrorCode"/> per sector.
    /// </summary>
    public static GcrTrack FormatTrack(int track, ReadOnlySpan<byte> data, byte id1, byte id2, ReadOnlySpan<byte> errors = default)
    {
        int sectors = D64Image.SectorsPerTrack(track);
        if (data.Length != sectors * D64Image.SectorSize)
            throw new ArgumentException($"Track {track} needs {sectors * D64Image.SectorSize} data bytes.", nameof(data));
        if (!errors.IsEmpty && errors.Length != sectors)
            throw new ArgumentException($"Track {track} needs {sectors} error bytes.", nameof(errors));

        int length = TrackLength(TrackToHalfTrack(track));
        var buffer = new byte[length];
        int gapTotal = length - sectors * SectorOverhead;
        int gap = gapTotal / sectors;
        int remainder = gapTotal % sectors;
        int position = 0;
        for (int sector = 0; sector < sectors; sector++)
        {
            byte error = errors.IsEmpty ? D64ErrorCode.Ok : errors[sector];
            int tailGap = gap + (sector == sectors - 1 ? remainder : 0);
            position += EncodeSector(buffer.AsSpan(position), track, sector, data.Slice(sector * D64Image.SectorSize, D64Image.SectorSize), id1, id2, tailGap, error);
        }
        return new GcrTrack(buffer, length * 8);
    }

    /// <summary>
    /// Writes one sector (SYNC, header, gap, SYNC, data block, tail gap of <paramref name="gapLength"/> × $55) into
    /// <paramref name="destination"/> and returns the number of bytes written (354 + <paramref name="gapLength"/>).
    /// <paramref name="errorCode"/> damages the sector the way the 1541 would see it: $02 header block ID ≠ $08,
    /// $03 no SYNC marks, $04 data block ID ≠ $07, $05 wrong data checksum, $09 wrong header checksum, $0B
    /// inverted ID bytes. Other codes are written as a good sector.
    /// </summary>
    public static int EncodeSector(Span<byte> destination, int track, int sector, ReadOnlySpan<byte> data, byte id1, byte id2, int gapLength, byte errorCode = D64ErrorCode.Ok)
    {
        if (data.Length != D64Image.SectorSize)
            throw new ArgumentException($"A sector is {D64Image.SectorSize} bytes.", nameof(data));
        if (gapLength < 0)
            throw new ArgumentOutOfRangeException(nameof(gapLength));
        int needed = SectorOverhead + gapLength;
        if (destination.Length < needed)
            throw new ArgumentException($"Need {needed} destination bytes.", nameof(destination));

        // Header: $08, checksum, sector, track, ID2, ID1, $0F, $0F ("Inside Commodore DOS", figure 6-4).
        Span<byte> header = stackalloc byte[8];
        header[0] = errorCode == D64ErrorCode.HeaderNotFound ? (byte)0x00 : HeaderBlockId;
        header[2] = (byte)sector;
        header[3] = (byte)track;
        header[4] = id2;
        header[5] = id1;
        if (errorCode == D64ErrorCode.IdMismatch)
        {
            header[4] ^= 0xFF;
            header[5] ^= 0xFF;
        }
        header[6] = 0x0F;
        header[7] = 0x0F;
        byte headerChecksum = (byte)(header[2] ^ header[3] ^ header[4] ^ header[5]);
        if (errorCode == D64ErrorCode.HeaderChecksum)
            headerChecksum ^= 0xFF;
        header[1] = headerChecksum;

        // Data block: $07, 256 data bytes, checksum, $00, $00.
        Span<byte> block = stackalloc byte[260];
        block[0] = errorCode == D64ErrorCode.DataBlockNotFound ? (byte)0x00 : DataBlockId;
        data.CopyTo(block.Slice(1, D64Image.SectorSize));
        byte dataChecksum = 0;
        for (int i = 0; i < D64Image.SectorSize; i++)
            dataChecksum ^= data[i];
        if (errorCode == D64ErrorCode.DataChecksum)
            dataChecksum ^= 0xFF;
        block[257] = dataChecksum;
        block[258] = 0;
        block[259] = 0;

        byte sync = errorCode == D64ErrorCode.NoSync ? (byte)0x00 : SyncByte;
        int position = 0;
        destination.Slice(position, SyncLength).Fill(sync);
        position += SyncLength;
        Gcr.EncodeBlock(header, destination.Slice(position, HeaderBlockLength));
        position += HeaderBlockLength;
        destination.Slice(position, HeaderGapLength).Fill(GapByte);
        position += HeaderGapLength;
        destination.Slice(position, SyncLength).Fill(sync);
        position += SyncLength;
        Gcr.EncodeBlock(block, destination.Slice(position, DataBlockLength));
        position += DataBlockLength;
        destination.Slice(position, gapLength).Fill(GapByte);
        position += gapLength;
        return position;
    }

    // ---------------------------------------------------------------------------------------------------------
    // GCR → D64
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Decodes the full tracks 1..<see cref="TrackCount"/> back into a D64 image (see <see cref="ToD64(int)"/>).</summary>
    public D64Image ToD64() => ToD64(TrackCount);

    /// <summary>
    /// Decodes the full tracks back into a D64 image with <paramref name="trackCount"/> (35 or 40) tracks:
    /// SYNC marks are located, header and data blocks decoded and the sectors placed by the sector number in
    /// their header, so any rotation of a track is fine. Sectors that cannot be read get an error code in the
    /// error block (emitted when any sector has an error or <see cref="HasErrorInfo"/> is set); their data is
    /// whatever could be recovered, or zeros.
    /// </summary>
    public D64Image ToD64(int trackCount)
    {
        if (trackCount != 35 && trackCount != 40)
            throw new ArgumentOutOfRangeException(nameof(trackCount), trackCount, "Track count must be 35 or 40.");
        int sectorCount = D64Image.TotalSectors(trackCount);
        var data = new byte[sectorCount * D64Image.SectorSize];
        var errors = new byte[sectorCount];
        bool checkId = TryReadDiskId(out byte id1, out byte id2);
        bool anyError = false;
        for (int track = 1; track <= trackCount; track++)
        {
            int sectors = D64Image.SectorsPerTrack(track);
            int first = D64Image.SectorIndex(track, 0);
            var trackErrors = errors.AsSpan(first, sectors);
            DecodeTrack(_tracks[TrackToHalfTrack(track)], track, data.AsSpan(first * D64Image.SectorSize, sectors * D64Image.SectorSize), trackErrors, checkId, id1, id2);
            for (int i = 0; i < trackErrors.Length; i++)
                anyError |= trackErrors[i] != D64ErrorCode.Ok;
        }
        var image = D64Image.FromSectors(data, HasErrorInfo || anyError ? errors : null);
        image.WriteProtected = WriteProtected;
        return image;
    }

    /// <summary>
    /// Determines the disk ID the way the DOS does at INITIALIZE: from the first readable sector header on the
    /// directory track 18 (Inside Commodore DOS, chapter 5, "Initialize"); falls back to any other track.
    /// </summary>
    public bool TryReadDiskId(out byte id1, out byte id2)
    {
        if (TryReadHeaderId(_tracks[TrackToHalfTrack(D64Image.DirectoryTrack)], D64Image.DirectoryTrack, out id1, out id2))
            return true;
        for (int track = 1; track <= MaxTrack; track++)
        {
            if (track != D64Image.DirectoryTrack && TryReadHeaderId(_tracks[TrackToHalfTrack(track)], track, out id1, out id2))
                return true;
        }
        id1 = id2 = 0;
        return false;
    }

    private static bool TryReadHeaderId(GcrTrack track, int trackNumber, out byte id1, out byte id2)
    {
        Span<byte> gcr = stackalloc byte[HeaderBlockLength];
        Span<byte> header = stackalloc byte[8];
        foreach (int position in FindSyncs(track))
        {
            track.ReadBytes(position, gcr);
            Gcr.DecodeBlock(gcr, header);
            if (header[0] != HeaderBlockId || header[3] != trackNumber)
                continue;
            if (header[1] != (byte)(header[2] ^ header[3] ^ header[4] ^ header[5]))
                continue;
            id2 = header[4];
            id1 = header[5];
            return true;
        }
        id1 = id2 = 0;
        return false;
    }

    /// <summary>
    /// Finds every SYNC mark on a track: the bit positions of the first 0 bit after a run of at least
    /// <see cref="SyncBits"/> one bits (that is where the 1541's bit counter is reset, so the following bytes
    /// start there). The track is scanned as a loop, so a run crossing the origin is found too. The positions are
    /// returned in ascending order.
    /// </summary>
    public static List<int> FindSyncs(GcrTrack track)
    {
        if (track is null) throw new ArgumentNullException(nameof(track));
        int n = track.BitLength;
        var result = new List<int>();
        var seen = new bool[n];
        int run = 0;
        // Two revolutions: the second pass catches a run that straddles the origin (the run counter is not
        // reset at the wrap), the "seen" set removes the duplicates.
        for (int i = 0; i < 2 * n; i++)
        {
            if (track.ReadBit(i) != 0)
            {
                run++;
                continue;
            }
            if (run >= SyncBits)
            {
                int position = i % n;
                if (!seen[position])
                {
                    seen[position] = true;
                    result.Add(position);
                }
            }
            run = 0;
        }
        result.Sort();
        return result;
    }

    /// <summary>
    /// Decodes one full track into <paramref name="sectorData"/> (sectors × 256 bytes, cleared first) and one
    /// error code per sector into <paramref name="errorCodes"/>. Mirrors the DOS read job ($F4CA/$F3B1 in the
    /// DOS ROM): find SYNC, read the block; a block starting with $08 whose track number matches is a header;
    /// the next block after a header must start with $07 to be its data block, else "22, data block not found";
    /// a wrong header checksum gives 27, a header ID different from the disk's gives 29 (only when
    /// <paramref name="checkId"/>), a wrong data checksum 23; sectors without a header get 20 and a track without
    /// any SYNC gives 21 for all of its sectors. Unlike the DOS, the bytes of a block with a damaged ID byte
    /// (errors 20 and 22) are still copied into <paramref name="sectorData"/> so that nothing readable is lost.
    /// </summary>
    public static void DecodeTrack(GcrTrack track, int trackNumber, Span<byte> sectorData, Span<byte> errorCodes, bool checkId = false, byte id1 = 0, byte id2 = 0)
    {
        if (track is null) throw new ArgumentNullException(nameof(track));
        int sectors = D64Image.SectorsPerTrack(trackNumber);
        if (sectorData.Length != sectors * D64Image.SectorSize)
            throw new ArgumentException($"Track {trackNumber} needs {sectors * D64Image.SectorSize} data bytes.", nameof(sectorData));
        if (errorCodes.Length != sectors)
            throw new ArgumentException($"Track {trackNumber} needs {sectors} error bytes.", nameof(errorCodes));
        sectorData.Clear();

        var syncs = FindSyncs(track);
        if (syncs.Count == 0)
        {
            errorCodes.Fill(D64ErrorCode.NoSync);
            return;
        }

        Span<byte> gcr = stackalloc byte[DataBlockLength];
        Span<byte> plain = stackalloc byte[260];
        Span<bool> headerFound = stackalloc bool[sectors];
        Span<bool> dataFound = stackalloc bool[sectors];
        errorCodes.Fill(D64ErrorCode.Ok);

        int n = track.BitLength;
        int pendingSector = -1;
        int pendingPosition = 0;
        // Two passes over the SYNC list so a header at the end of the track finds its data block at the start.
        for (int i = 0; i < syncs.Count * 2; i++)
        {
            int position = syncs[i % syncs.Count];
            track.ReadBytes(position, gcr.Slice(0, HeaderBlockLength));
            Gcr.DecodeBlock(gcr.Slice(0, HeaderBlockLength), plain.Slice(0, 8));

            bool isHeader = plain[0] == HeaderBlockId;
            // A block whose ID byte is damaged but which otherwise is a well-formed header for this track is
            // treated as "header not found" (the DOS would never accept it) while still letting the data block
            // behind it be recovered into the image.
            bool isDamagedHeader = !isHeader && plain[0] != DataBlockId && LooksLikeHeader(plain, trackNumber, sectors);
            if (isHeader || isDamagedHeader)
            {
                int sector = plain[2];
                if (sector >= sectors || plain[3] != trackNumber)
                {
                    pendingSector = -1;
                    continue;
                }
                if (!headerFound[sector])
                {
                    headerFound[sector] = true;
                    byte expected = (byte)(plain[2] ^ plain[3] ^ plain[4] ^ plain[5]);
                    if (isDamagedHeader)
                        errorCodes[sector] = D64ErrorCode.HeaderNotFound;
                    else if (plain[1] != expected)
                        errorCodes[sector] = D64ErrorCode.HeaderChecksum;
                    else if (checkId && (plain[5] != id1 || plain[4] != id2))
                        errorCodes[sector] = D64ErrorCode.IdMismatch;
                }
                pendingSector = sector;
                pendingPosition = position;
                continue;
            }

            if (pendingSector < 0)
                continue;
            int sectorIndex = pendingSector;
            pendingSector = -1;
            int distance = position - pendingPosition;
            if (distance < 0)
                distance += n;
            if (distance > MaxHeaderToDataBits || dataFound[sectorIndex])
                continue;

            // The block following a header is its data block. If its ID byte is not $07 the DOS reports
            // "22, data block not found"; the bytes are still recovered into the image.
            track.ReadBytes(position, gcr);
            bool valid = Gcr.DecodeBlock(gcr, plain);
            dataFound[sectorIndex] = true;
            var destination = sectorData.Slice(sectorIndex * D64Image.SectorSize, D64Image.SectorSize);
            plain.Slice(1, D64Image.SectorSize).CopyTo(destination);
            byte checksum = 0;
            for (int j = 1; j <= D64Image.SectorSize; j++)
                checksum ^= plain[j];
            if (errorCodes[sectorIndex] != D64ErrorCode.Ok)
                continue;
            if (plain[0] != DataBlockId)
                errorCodes[sectorIndex] = D64ErrorCode.DataBlockNotFound;
            else if (checksum != plain[257] || !valid)
                errorCodes[sectorIndex] = D64ErrorCode.DataChecksum;
        }

        for (int sector = 0; sector < sectors; sector++)
        {
            if (!headerFound[sector])
                errorCodes[sector] = D64ErrorCode.HeaderNotFound;
            else if (!dataFound[sector] && errorCodes[sector] == D64ErrorCode.Ok)
                errorCodes[sector] = D64ErrorCode.DataBlockNotFound;
        }
    }

    // Sector, track, checksum and the two $0F padding bytes of a decoded 8-byte block are consistent with a
    // header of this track (used to recognise headers whose ID byte was damaged).
    private static bool LooksLikeHeader(ReadOnlySpan<byte> plain, int trackNumber, int sectors)
    {
        return plain[3] == trackNumber
            && plain[2] < sectors
            && plain[1] == (byte)(plain[2] ^ plain[3] ^ plain[4] ^ plain[5])
            && plain[6] == 0x0F
            && plain[7] == 0x0F;
    }
}
