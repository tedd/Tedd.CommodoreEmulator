using System.Net.Http;
using System.Runtime.Versioning;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Web.Interop;

namespace Tedd.Commodore.Web.Services;

/// <summary>Which ROM image an uploaded file is meant for.</summary>
public enum RomSlot
{
    Basic, Kernal, Chargen, Drive, Combined,
    /// <summary>C128 BASIC 7 low half (16K, $4000).</summary>
    BasicLow,
    /// <summary>C128 BASIC 7 high half (16K, $8000).</summary>
    BasicHigh,
    /// <summary>C128 BASIC 7 as one 32K image (318022).</summary>
    Basic128Combined,
    /// <summary>C128 editor + Z80 BIOS + KERNAL (16K), or the 32K "complete" image (318023) that also holds the C64 ROMs.</summary>
    Kernal128,
    /// <summary>C128 character generator (8K).</summary>
    Chargen128,
}

/// <summary>
/// Collects the ROM images the browser build can use, from three sources in priority order:
/// images the user uploaded earlier (kept in localStorage as base64), files served by the site at
/// <c>roms/basic.bin</c>, <c>roms/kernal.bin</c>, <c>roms/chargen.bin</c>, <c>roms/1541.bin</c> (the deploy job
/// puts OpenROMs there) and <c>roms/c128/basiclo.bin</c>, <c>basichi.bin</c>, <c>kernal128.bin</c>,
/// <c>chargen128.bin</c> (never shipped: there is no free C128 ROM set), and fresh uploads.
/// A C128 needs its own four images plus the C64 BASIC/KERNAL (used in C64 mode).
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class RomStore
{
    private const string KeyPrefix = "tedd.commodore.rom.";

    public byte[]? Basic { get; private set; }
    public byte[]? Kernal { get; private set; }
    public byte[]? Chargen { get; private set; }
    public byte[]? Drive { get; private set; }
    public byte[]? BasicLow { get; private set; }
    public byte[]? BasicHigh { get; private set; }
    public byte[]? Kernal128 { get; private set; }
    public byte[]? Chargen128 { get; private set; }
    private byte[]? _driveLow, _driveHigh;

    public string BasicName { get; private set; } = "";
    public string KernalName { get; private set; } = "";
    public string ChargenName { get; private set; } = "";
    public string DriveName { get; private set; } = "";
    public string BasicLowName { get; private set; } = "";
    public string BasicHighName { get; private set; } = "";
    public string Kernal128Name { get; private set; } = "";
    public string Chargen128Name { get; private set; } = "";

    /// <summary>Where the current set came from ("localStorage", "site", "upload" or a mix).</summary>
    public string Source { get; private set; } = "";

    /// <summary>The C64 set is complete.</summary>
    public bool IsComplete => Basic is not null && Kernal is not null && Chargen is not null;
    /// <summary>The C128 set is complete (it includes the C64 BASIC/KERNAL for C64 mode).</summary>
    public bool IsC128Complete => BasicLow is not null && BasicHigh is not null && Kernal128 is not null && Chargen128 is not null && Basic is not null && Kernal is not null;
    /// <summary>Any C128 image has been loaded (to show the panel).</summary>
    public bool HasAnyC128 => BasicLow is not null || BasicHigh is not null || Kernal128 is not null || Chargen128 is not null;
    public bool HasDrive => Drive is not null;

    /// <summary>True when the KERNAL looks like OpenROMs (their KERNAL contains an "OPEN ROMS" banner).</summary>
    public bool LooksLikeOpenRoms => Kernal is not null && Contains(Kernal, "OPEN ROMS"u8) || Basic is not null && Contains(Basic, "OPEN ROMS"u8);

    public string Description
    {
        get
        {
            if (!IsComplete) return "no ROM set";
            var parts = new List<string> { "BASIC=" + BasicName, "KERNAL=" + KernalName, "CHAR=" + ChargenName };
            if (Drive is not null) parts.Add("1541=" + DriveName);
            if (IsC128Complete) parts.Add("C128=" + BasicLowName + "+" + BasicHighName + "+" + Kernal128Name + "+" + Chargen128Name);
            return string.Join(", ", parts) + " (" + Source + ")";
        }
    }

    /// <summary>What is still missing for a C128, for the ROM panel.</summary>
    public string C128Missing
    {
        get
        {
            var missing = new List<string>();
            if (BasicLow is null) missing.Add("BASIC low");
            if (BasicHigh is null) missing.Add("BASIC high");
            if (Kernal128 is null) missing.Add("KERNAL");
            if (Chargen128 is null) missing.Add("character ROM");
            if (Basic is null || Kernal is null) missing.Add("C64 BASIC/KERNAL");
            return string.Join(", ", missing);
        }
    }

    public RomSet ToRomSet(bool includeDrive = true) =>
        new(Basic ?? throw new InvalidOperationException("no BASIC ROM"), Kernal!, Chargen!, includeDrive ? Drive : null, "browser", Description);

    public C128RomSet ToC128RomSet(bool includeDrive = true)
    {
        if (!IsC128Complete) throw new InvalidOperationException("C128 ROM set incomplete: " + C128Missing);
        return new C128RomSet(BasicLow!, BasicHigh!, Kernal128!, Chargen128!, Basic!, Kernal!, includeDrive ? Drive : null, "browser", Description);
    }

    #region localStorage

    private static readonly (string Slot, int Size)[] StoredSlots =
    {
        ("basic", RomSet.BasicSize), ("kernal", RomSet.KernalSize), ("chargen", RomSet.CharSize), ("drive", RomSet.DriveRomSize),
        ("basiclo", C128RomSet.BasicHalfSize), ("basichi", C128RomSet.BasicHalfSize), ("kernal128", C128RomSet.KernalSize), ("chargen128", C128RomSet.CharSize),
    };

    public bool LoadFromStorage()
    {
        byte[]? Get(string slot, int size, out string name)
        {
            name = C64Js.StorageGet(KeyPrefix + slot + ".name") ?? "";
            var b64 = C64Js.StorageGet(KeyPrefix + slot);
            if (string.IsNullOrEmpty(b64)) return null;
            try
            {
                var data = Convert.FromBase64String(b64);
                return data.Length == size ? data : null;
            }
            catch { return null; }
        }
        var basic = Get("basic", RomSet.BasicSize, out var bn);
        var kernal = Get("kernal", RomSet.KernalSize, out var kn);
        var chargen = Get("chargen", RomSet.CharSize, out var cn);
        var drive = Get("drive", RomSet.DriveRomSize, out var dn);
        var lo = Get("basiclo", C128RomSet.BasicHalfSize, out var lon);
        var hi = Get("basichi", C128RomSet.BasicHalfSize, out var hin);
        var k128 = Get("kernal128", C128RomSet.KernalSize, out var k128n);
        var c128 = Get("chargen128", C128RomSet.CharSize, out var c128n);
        if (basic is null || kernal is null || chargen is null)
            return false;
        Basic = basic; BasicName = bn;
        Kernal = kernal; KernalName = kn;
        Chargen = chargen; ChargenName = cn;
        if (drive is not null) { Drive = drive; DriveName = dn; }
        if (lo is not null) { BasicLow = lo; BasicLowName = lon; }
        if (hi is not null) { BasicHigh = hi; BasicHighName = hin; }
        if (k128 is not null) { Kernal128 = k128; Kernal128Name = k128n; }
        if (c128 is not null) { Chargen128 = c128; Chargen128Name = c128n; }
        Source = "localStorage";
        return true;
    }

    /// <summary>Persists the current set (only complete C64 sets are stored). Returns false when storage is unavailable/full.</summary>
    public bool SaveToStorage()
    {
        if (!IsComplete) return false;
        bool ok = true;
        void Put(string slot, byte[]? data, string name)
        {
            if (data is null) { C64Js.StorageRemove(KeyPrefix + slot); C64Js.StorageRemove(KeyPrefix + slot + ".name"); return; }
            ok &= C64Js.StorageSet(KeyPrefix + slot, Convert.ToBase64String(data));
            ok &= C64Js.StorageSet(KeyPrefix + slot + ".name", name);
        }
        Put("basic", Basic, BasicName);
        Put("kernal", Kernal, KernalName);
        Put("chargen", Chargen, ChargenName);
        Put("drive", Drive, DriveName);
        Put("basiclo", BasicLow, BasicLowName);
        Put("basichi", BasicHigh, BasicHighName);
        Put("kernal128", Kernal128, Kernal128Name);
        Put("chargen128", Chargen128, Chargen128Name);
        return ok;
    }

    public static void ClearStorage()
    {
        foreach (var (slot, _) in StoredSlots)
        {
            C64Js.StorageRemove(KeyPrefix + slot);
            C64Js.StorageRemove(KeyPrefix + slot + ".name");
        }
    }

    #endregion

    #region site

    /// <summary>Fetches roms/*.bin (and roms/c128/*.bin) relative to the site root. Returns true when a complete C64 set was found.</summary>
    public async Task<bool> LoadFromSiteAsync(HttpClient http, CancellationToken ct = default)
    {
        async Task<byte[]?> Fetch(string name, int size)
        {
            try
            {
                using var response = await http.GetAsync("roms/" + name, ct);
                if (!response.IsSuccessStatusCode) return null;
                var data = await response.Content.ReadAsByteArrayAsync(ct);
                return data.Length == size ? data : null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        var basic = await Fetch("basic.bin", RomSet.BasicSize);
        var kernal = await Fetch("kernal.bin", RomSet.KernalSize);
        var chargen = await Fetch("chargen.bin", RomSet.CharSize);
        if (basic is null || kernal is null || chargen is null) return false;
        Basic = basic; BasicName = "basic.bin";
        Kernal = kernal; KernalName = "kernal.bin";
        Chargen = chargen; ChargenName = "chargen.bin";
        var drive = await Fetch("1541.bin", RomSet.DriveRomSize);
        if (drive is not null) { Drive = drive; DriveName = "1541.bin"; }
        else { Drive = null; DriveName = ""; }
        var lo = await Fetch("c128/basiclo.bin", C128RomSet.BasicHalfSize);
        var hi = await Fetch("c128/basichi.bin", C128RomSet.BasicHalfSize);
        var k128 = await Fetch("c128/kernal128.bin", C128RomSet.KernalSize);
        var c128 = await Fetch("c128/chargen128.bin", C128RomSet.CharSize);
        if (lo is not null && hi is not null && k128 is not null && c128 is not null)
        {
            BasicLow = lo; BasicLowName = "c128/basiclo.bin";
            BasicHigh = hi; BasicHighName = "c128/basichi.bin";
            Kernal128 = k128; Kernal128Name = "c128/kernal128.bin";
            Chargen128 = c128; Chargen128Name = "c128/chargen128.bin";
        }
        Source = "site";
        return true;
    }

    #endregion

    #region uploads

    /// <summary>
    /// Accepts an uploaded file for a slot. Sizes are validated (BASIC/KERNAL 8192, character 4096, 1541
    /// 16384 or two 8192 halves, combined 16384 = BASIC + KERNAL; C128: 16384 per BASIC half or 32768 combined,
    /// KERNAL 16384 or the 32768 "complete" image, character ROM 8192). Returns null on success, else an error text.
    /// </summary>
    public string? Accept(RomSlot slot, string fileName, byte[] data)
    {
        switch (slot)
        {
            case RomSlot.Basic:
                if (data.Length != RomSet.BasicSize) return $"{fileName}: BASIC ROM must be {RomSet.BasicSize} bytes (got {data.Length})";
                Basic = data; BasicName = fileName; break;
            case RomSlot.Kernal:
                if (data.Length != RomSet.KernalSize) return $"{fileName}: KERNAL ROM must be {RomSet.KernalSize} bytes (got {data.Length})";
                Kernal = data; KernalName = fileName; break;
            case RomSlot.Chargen:
                if (data.Length != RomSet.CharSize) return $"{fileName}: character ROM must be {RomSet.CharSize} bytes (got {data.Length})";
                Chargen = data; ChargenName = fileName; break;
            case RomSlot.Combined:
                if (data.Length != RomSet.BasicSize + RomSet.KernalSize) return $"{fileName}: combined BASIC+KERNAL image must be {RomSet.BasicSize + RomSet.KernalSize} bytes (got {data.Length})";
                Basic = data[..RomSet.BasicSize]; BasicName = fileName + "[0..8K]";
                Kernal = data[RomSet.BasicSize..]; KernalName = fileName + "[8K..16K]";
                break;
            case RomSlot.Drive:
                if (data.Length == RomSet.DriveRomSize)
                {
                    Drive = data; DriveName = fileName; _driveLow = _driveHigh = null;
                }
                else if (data.Length == 8192)
                {
                    // split 1541 ROM: the $E000 half ends with the CPU vectors (high bytes >= $C0)
                    bool high = data[0x1FFB] >= 0xC0 && data[0x1FFD] >= 0xC0 && data[0x1FFF] >= 0xC0;
                    if (high) _driveHigh = data; else _driveLow = data;
                    if (_driveLow is not null && _driveHigh is not null)
                    {
                        Drive = new byte[RomSet.DriveRomSize];
                        _driveLow.CopyTo(Drive, 0);
                        _driveHigh.CopyTo(Drive, 8192);
                        DriveName = fileName + "+" + (high ? "low" : "high");
                        _driveLow = _driveHigh = null;
                    }
                    else
                    {
                        return null; // waiting for the other half; not an error
                    }
                }
                else return $"{fileName}: 1541 ROM must be {RomSet.DriveRomSize} bytes or two 8192 byte halves (got {data.Length})";
                break;
            case RomSlot.BasicLow:
                if (data.Length == 2 * C128RomSet.BasicHalfSize) goto case RomSlot.Basic128Combined;
                if (data.Length != C128RomSet.BasicHalfSize) return $"{fileName}: C128 BASIC low ROM must be {C128RomSet.BasicHalfSize} bytes (got {data.Length})";
                BasicLow = data; BasicLowName = fileName; break;
            case RomSlot.BasicHigh:
                if (data.Length == 2 * C128RomSet.BasicHalfSize) goto case RomSlot.Basic128Combined;
                if (data.Length != C128RomSet.BasicHalfSize) return $"{fileName}: C128 BASIC high ROM must be {C128RomSet.BasicHalfSize} bytes (got {data.Length})";
                BasicHigh = data; BasicHighName = fileName; break;
            case RomSlot.Basic128Combined:
                if (data.Length != 2 * C128RomSet.BasicHalfSize) return $"{fileName}: combined C128 BASIC image must be {2 * C128RomSet.BasicHalfSize} bytes (got {data.Length})";
                BasicLow = data[..C128RomSet.BasicHalfSize]; BasicLowName = fileName + "[0..16K]";
                BasicHigh = data[C128RomSet.BasicHalfSize..]; BasicHighName = fileName + "[16K..32K]";
                break;
            case RomSlot.Kernal128:
                if (data.Length == C128RomSet.KernalSize)
                {
                    Kernal128 = data; Kernal128Name = fileName;
                }
                else if (data.Length == 2 * C128RomSet.KernalSize)
                {
                    // The DCR "complete" image: C64 BASIC + KERNAL in the lower half, the C128 KERNAL in the upper half.
                    Basic = data[..RomSet.BasicSize]; BasicName = fileName + "[0..8K]";
                    Kernal = data[RomSet.BasicSize..C128RomSet.KernalSize]; KernalName = fileName + "[8K..16K]";
                    Kernal128 = data[C128RomSet.KernalSize..]; Kernal128Name = fileName + "[16K..32K]";
                }
                else return $"{fileName}: C128 KERNAL must be {C128RomSet.KernalSize} bytes or the {2 * C128RomSet.KernalSize} byte complete image (got {data.Length})";
                break;
            case RomSlot.Chargen128:
                if (data.Length != C128RomSet.CharSize) return $"{fileName}: C128 character ROM must be {C128RomSet.CharSize} bytes (got {data.Length})";
                Chargen128 = data; Chargen128Name = fileName;
                // Its lower half is the C64 character set: use it when no C64 one is loaded yet.
                if (Chargen is null) { Chargen = data[..RomSet.CharSize]; ChargenName = fileName + "[0..4K]"; }
                break;
        }
        Source = Source == "upload" || Source == "" ? "upload" : Source + "+upload";
        return null;
    }

    /// <summary>True while one half of a split 1541 ROM has been received and the other is still missing.</summary>
    public bool DriveHalfPending => (_driveLow is null) != (_driveHigh is null);

    public void RemoveDrive()
    {
        Drive = null;
        DriveName = "";
        _driveLow = _driveHigh = null;
    }

    /// <summary>Recognises ROM dumps by size and name so they can be dropped anywhere on the emulator.</summary>
    public static RomSlot? Classify(string name, int size)
    {
        var n = name.ToLowerInvariant();
        bool romish = n.EndsWith(".bin") || n.EndsWith(".rom") || !n.Contains('.');
        if (!romish) return null;
        if (size == C128RomSet.CharSize && (n.Contains("char") || n.Contains("390059") || n.Contains("325"))) return RomSlot.Chargen128;
        if (size == RomSet.CharSize && (n.Contains("char") || n.Contains("901225") || n.Contains("325018"))) return RomSlot.Chargen;
        if (size == RomSet.BasicSize && (n.Contains("basic") || n.Contains("901226"))) return RomSlot.Basic;
        if (size == RomSet.KernalSize && (n.Contains("kernal") || n.Contains("kernel") || n.Contains("901227"))) return RomSlot.Kernal;
        if (size == C128RomSet.BasicHalfSize && (n.Contains("318018") || n.Contains("basiclo") || n.Contains("basic-4000"))) return RomSlot.BasicLow;
        if (size == C128RomSet.BasicHalfSize && (n.Contains("318019") || n.Contains("basichi") || n.Contains("basic-8000"))) return RomSlot.BasicHigh;
        if (size == 2 * C128RomSet.BasicHalfSize && (n.Contains("318022") || n.Contains("390393") || n.Contains("basic128") || n.Contains("252343-03"))) return RomSlot.Basic128Combined;
        if (size == C128RomSet.KernalSize && (n.Contains("318020") || n.Contains("kernal128") || n.Contains("kernal.128"))) return RomSlot.Kernal128;
        if (size == 2 * C128RomSet.KernalSize && (n.Contains("318023") || n.Contains("complete") || n.Contains("252343-04"))) return RomSlot.Kernal128;
        if (size is 8192 or 16384 && (n.Contains("1541") || n.Contains("1540") || n.Contains("dos") || n.Contains("325302") || n.Contains("901229"))) return RomSlot.Drive;
        if (size == 16384 && (n.Contains("64c") || n.Contains("251913") || n.Contains("basic+kernal") || n.Contains("c64part"))) return RomSlot.Combined;
        return null;
    }

    #endregion

    private static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
