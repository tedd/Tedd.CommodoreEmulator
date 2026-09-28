using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Tedd.Commodore.Emulator.Bus;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Hosting;
using Tedd.Commodore.Maui.Views;

namespace Tedd.Commodore.Maui.Pages;

/// <summary>
/// Hex viewer/editor for the C64/C128 (and 1541) memory. Editing is done through the emulator runner so that the
/// machine is never touched from the UI thread while it is running; "Freeze" stops the machine so values are
/// stable and single stepping is possible.
/// <para>
/// Bytes light up as the CPU touches them (green = read, orange = written) and fade over half a second. The CPUs
/// record their accesses in a <see cref="MemoryAccessTracker"/> (a bit per address, no locking); once per
/// display frame this page takes those bits, maps them to the dump being shown and feeds a
/// <see cref="HexHeatMap"/>, which fades what is lit and reports which rows changed. Only rows that are on screen
/// are repainted, and a row reads the current bytes and heat when it paints, so nothing is done for the rows
/// that are scrolled away and nothing has to catch up when they scroll in.
/// </para>
/// </summary>
public partial class MemoryViewerPage : ContentPage
{
    /// <summary>Full re-read of the dump and the machine state.</summary>
    private const int FullRefreshMs = 300;
    /// <summary>Highlight fade and repaint cadence.</summary>
    private const int FrameMs = 16;

    private readonly EmulatorRunner _runner;
    private readonly Action<bool> _setPaused;
    private readonly IDispatcherTimer _refreshTimer;
    private readonly IDispatcherTimer _frameTimer;
    private readonly MemoryAccessTracker _tracker = new();
    private readonly MemoryAccessTracker _driveTracker = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<HexRow> _pending = new();
    private readonly Action<int> _collectDirtyRow;
    private readonly Action _readPendingRows;
    private readonly byte[] _scratch = new byte[0x10000];
    private HexDocument _document = null!;
    private MemorySource _source = MemorySource.Cpu;
    private long _lastFrame;
    private bool _ready;
    private bool _attached;

    private enum MemorySource { Cpu, Ram, ColorRam, Vic, DriveRam, DriveCpu }

    /// <param name="runner">The runner that owns the machine.</param>
    /// <param name="setPaused">Freezes/resumes the machine and keeps the main window's menu in sync.</param>
    public MemoryViewerPage(EmulatorRunner runner, Action<bool> setPaused)
    {
        InitializeComponent();
        _runner = runner;
        _setPaused = setPaused;
        _collectDirtyRow = CollectDirtyRow;
        _readPendingRows = ReadPendingRows;
        BuildDocument();
        Refresh(force: true);
        // Only now may Source_Changed run: it rebuilds the document the refresh above just filled.
        _ready = true;
        SourceBox.SelectedIndex = 0;

        _refreshTimer = Dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(FullRefreshMs);
        _refreshTimer.Tick += (_, _) => { if (AutoRefresh.IsChecked && !_runner.Paused) Refresh(force: false); };
        _refreshTimer.Start();

        _frameTimer = Dispatcher.CreateTimer();
        _frameTimer.Interval = TimeSpan.FromMilliseconds(FrameMs);
        _frameTimer.Tick += (_, _) => Frame();
        _frameTimer.Start();

        if (Application.Current is { } app)
            app.RequestedThemeChanged += Theme_Changed;
        OnFreezeChanged();
    }

    private CommodoreMachine Machine => _runner.Session.Machine;

    /// <summary>Called by the main window when the pause state changes elsewhere.</summary>
    public void OnFreezeChanged()
    {
        FreezeButton.Text = _runner.Paused ? "Resume" : "Freeze";
        StepCycleButton.IsEnabled = StepInstructionButton.IsEnabled = StepFrameButton.IsEnabled = _runner.Paused;
        Refresh(force: true);
    }

    /// <summary>The window is gone: stop polling and take the trackers off the CPUs.</summary>
    private void Teardown()
    {
        _attached = false;
        _refreshTimer.Stop();
        _frameTimer.Stop();
        if (Application.Current is { } app)
            app.RequestedThemeChanged -= Theme_Changed;
        DetachTrackers();
    }

    private void Theme_Changed(object? sender, AppThemeChangedEventArgs e)
    {
        Header.Invalidate();
        _document.InvalidateVisible();
    }

    private void Freeze_Clicked(object? sender, EventArgs e)
    {
        _setPaused(!_runner.Paused);
        OnFreezeChanged();
    }

    private void Source_Changed(object? sender, EventArgs e)
    {
        if (!_ready) return;
        _source = (MemorySource)Math.Max(0, SourceBox.SelectedIndex);
        BuildDocument();
        Refresh(force: true);
    }

    private void BuildDocument()
    {
        int size = _source switch
        {
            MemorySource.ColorRam => 0x400,
            MemorySource.Vic => 0x4000,
            MemorySource.DriveRam => 0x800,
            _ => 0x10000,
        };
        _document = new HexDocument(size, _source == MemorySource.ColorRam ? 0xD800 : 0) { WriteRequested = WriteByte };
        _pending.Clear();
        _tracker.Clear();
        _driveTracker.Clear();
        HexGrid.ItemsSource = _document.Rows;
    }

    #region Access highlights

    /// <summary>Points the machine's CPUs at this page's trackers (again, should the session have swapped machines).</summary>
    private void AttachTrackers()
    {
        var m = Machine;
        if (m.Cpu.AccessTracker != _tracker)
            m.Cpu.AccessTracker = _tracker;
        if (m is C128 c128 && c128.Z80.AccessTracker != _tracker)
            c128.Z80.AccessTracker = _tracker;
        if (m.Drive is { } drive && drive.Cpu.AccessTracker != _driveTracker)
            drive.Cpu.AccessTracker = _driveTracker;
    }

    private void DetachTrackers()
    {
        var m = Machine;
        if (m.Cpu.AccessTracker == _tracker)
            m.Cpu.AccessTracker = null;
        if (m is C128 c128 && c128.Z80.AccessTracker == _tracker)
            c128.Z80.AccessTracker = null;
        if (m.Drive is { } drive && drive.Cpu.AccessTracker == _driveTracker)
            drive.Cpu.AccessTracker = null;
    }

    /// <summary>
    /// Once per display frame: fade what is lit, light what the CPU touched since last time, then repaint the
    /// rows on screen whose look changed, with their bytes read again first.
    /// </summary>
    private void Frame()
    {
        // Teardown hangs off the window going away rather than the page's Unloaded event, which WinUI also
        // raises while the page is still very much on screen.
        if (Window is null)
        {
            if (_attached) Teardown();
            return;
        }
        _attached = true;

        long now = _clock.ElapsedMilliseconds;
        int elapsed = (int)Math.Min(now - _lastFrame, HexHeatMap.FadeMilliseconds);
        _lastFrame = now;
        AttachTrackers();

        var heat = _document.Heat;
        heat.Decay(elapsed);
        var tracker = _source is MemorySource.DriveRam or MemorySource.DriveCpu ? _driveTracker : _tracker;
        TakeAccesses(tracker.Reads, write: false);
        TakeAccesses(tracker.Writes, write: true);   // after the reads, so a byte read and written shows as written

        heat.DrainDirtyRows(_collectDirtyRow);
        if (_pending.Count == 0) return;
        if (!_runner.Paused && AutoRefresh.IsChecked)
            TryReadLive(_readPendingRows);
        foreach (var row in _pending)
            row.Invalidate();
        _pending.Clear();
    }

    private void CollectDirtyRow(int row)
    {
        var r = _document.Rows[row];
        if (r.View is not null) _pending.Add(r);
    }

    private void ReadPendingRows()
    {
        foreach (var row in _pending)
            ReadRange(row.Offset, HexMetrics.Columns, _document.Data);
    }

    /// <summary>Takes the addresses a CPU touched out of one tracker bitmap and lights the bytes they show as.</summary>
    private void TakeAccesses(ulong[] bits, bool write)
    {
        var heat = _document.Heat;
        for (int w = HexHeatMap.NextNonZeroWord(bits, 0); w >= 0; w = HexHeatMap.NextNonZeroWord(bits, w + 1))
        {
            ulong word = Interlocked.Exchange(ref bits[w], 0);
            for (; word != 0; word &= word - 1)
            {
                int offset = ToOffset((w << 6) | BitOperations.TrailingZeroCount(word));
                if (offset >= 0) heat.Touch(offset, write);
            }
        }
    }

    /// <summary>Where a CPU address shows up in the current dump, or -1 when it does not.</summary>
    private int ToOffset(int address) => _source switch
    {
        MemorySource.ColorRam => address is >= 0xD800 and < 0xDC00 ? address - 0xD800 : -1,
        MemorySource.Vic => address >> 14 == Machine.VicBank ? address & 0x3FFF : -1,
        MemorySource.DriveRam => address < 0x8000 && (address & 0x1FFF) < 0x800 ? address & 0x7FF : -1,
        _ => address,
    };

    #endregion

    /// <summary>
    /// Re-reads the whole dump and the machine state: under the machine's lock when <paramref name="force"/>
    /// (user actions, which want the exact state), otherwise as a live look (see <see cref="TryReadLive"/>).
    /// </summary>
    private void Refresh(bool force)
    {
        string registers = "", disassembly = "", machine = "";
        var data = _document.Data;
        var scratch = _scratch;
        void Read()
        {
            ReadRange(0, data.Length, scratch);
            registers = FormatRegisters();
            disassembly = FormatDisassembly();
            machine = FormatMachine();
        }
        if (force)
            _runner.Invoke(Read);
        else if (!TryReadLive(Read))
            return;

        var heat = _document.Heat;
        for (int row = 0, offset = 0; offset < data.Length; row++, offset += HexMetrics.Columns)
        {
            var fresh = scratch.AsSpan(offset, HexMetrics.Columns);
            var shown = data.AsSpan(offset, HexMetrics.Columns);
            if (fresh.SequenceEqual(shown)) continue;
            fresh.CopyTo(shown);
            heat.MarkRowDirty(row);
        }
        heat.DrainDirtyRows(_collectDirtyRow);
        foreach (var r in _pending)
            r.Invalidate();
        _pending.Clear();

        RegistersText.Text = registers;
        DisassemblyText.Text = disassembly;
        MachineText.Text = machine;
    }

    /// <summary>Reads <paramref name="count"/> bytes of the current source from <paramref name="offset"/> into the same offsets of <paramref name="into"/>. Emulation thread only.</summary>
    private void ReadRange(int offset, int count, byte[] into)
    {
        var m = Machine;
        int end = offset + count;
        switch (_source)
        {
            case MemorySource.Cpu:
                for (int i = offset; i < end; i++) into[i] = m.PeekMemory((ushort)i);
                break;
            case MemorySource.Ram:
                CopyRange(m.Ram, offset, count, into);
                break;
            case MemorySource.ColorRam:
                CopyRange(m.ColorRam, offset, count, into);
                break;
            case MemorySource.Vic:
                for (int i = offset; i < end; i++) into[i] = m.VicMemory.PeekVic(i);
                break;
            case MemorySource.DriveRam:
                if (m.Drive is { } d) CopyRange(d.Memory.Ram, offset, count, into);
                break;
            case MemorySource.DriveCpu:
                if (m.Drive is { } d2)
                    for (int i = offset; i < end; i++) into[i] = d2.Memory.Peek((ushort)i);
                break;
        }
    }

    /// <summary>
    /// Runs a read-only look at the machine from the UI thread without taking the machine's lock. A value read
    /// while the emulation thread is between two writes is at worst a frame stale, which is fine for a display
    /// that is refreshed continuously; waiting for the lock is not, because a machine that cannot keep up with
    /// real time, or is warping, holds it almost continuously. The one thing that can go wrong is a read that
    /// lands in the middle of a reconfiguration (a cartridge being detached, say), so a failure is swallowed
    /// and the next tick simply tries again. Writes and stepping still go through the runner.
    /// </summary>
    private static bool TryReadLive(Action read)
    {
        try
        {
            read();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void CopyRange(byte[] source, int offset, int count, byte[] into)
    {
        int n = Math.Min(offset + count, source.Length) - offset;
        if (n > 0) Array.Copy(source, offset, into, offset, n);
    }

    /// <summary>Writes an edited byte to the machine and shows what the machine made of it.</summary>
    private void WriteByte(int offset, byte value)
    {
        int rowStart = offset & ~(HexMetrics.Columns - 1);
        _runner.Invoke(() =>
        {
            var m = Machine;
            switch (_source)
            {
                case MemorySource.Cpu: m.WriteMemory((ushort)offset, value); break;
                case MemorySource.Ram: if (offset < m.Ram.Length) m.Ram[offset] = value; break;
                case MemorySource.ColorRam: m.ColorRam[offset & 0x3FF] = (byte)(value & 0x0F); break;
                case MemorySource.Vic: m.Ram[((m.VicBank << 14) | (offset & 0x3FFF)) & 0xFFFF] = value; break;
                case MemorySource.DriveRam: if (m.Drive is { } d) d.Memory.Ram[offset & 0x7FF] = value; break;
                case MemorySource.DriveCpu: if (m.Drive is { } d2) d2.Memory.Write((ushort)offset, value); break;
            }
            ReadRange(rowStart, HexMetrics.Columns, _document.Data);
        });
        _document.Rows[rowStart / HexMetrics.Columns].Invalidate();
    }

    private string FormatRegisters()
    {
        var c = _source is MemorySource.DriveRam or MemorySource.DriveCpu ? Machine.Drive?.Cpu : Machine.Cpu;
        if (c is null) return "(no drive)";
        var sb = new StringBuilder();
        sb.Append($"PC ${c.PC:X4}  A ${c.A:X2}  X ${c.X:X2}  Y ${c.Y:X2}  S ${c.S:X2}\n");
        sb.Append("P  ").Append((c.P & 0x80) != 0 ? 'N' : '.').Append((c.P & 0x40) != 0 ? 'V' : '.').Append('-')
          .Append((c.P & 0x10) != 0 ? 'B' : '.').Append((c.P & 0x08) != 0 ? 'D' : '.').Append((c.P & 0x04) != 0 ? 'I' : '.')
          .Append((c.P & 0x02) != 0 ? 'Z' : '.').Append((c.P & 0x01) != 0 ? 'C' : '.').Append($"  (${c.P:X2})\n");
        sb.Append($"Cycles {c.Cycles:N0}  IRQ {(c.Irq ? "1" : "0")}  NMI {(c.Nmi ? "1" : "0")}  RDY {(c.Rdy ? "1" : "0")}");
        if (c.Jammed) sb.Append("  JAMMED");
        return sb.ToString();
    }

    private string FormatDisassembly()
    {
        var sb = new StringBuilder();
        bool drive = _source is MemorySource.DriveRam or MemorySource.DriveCpu;
        Func<ushort, byte> read;
        ushort pc;
        if (drive)
        {
            if (Machine.Drive is not { } d) return "";
            read = a => d.Memory.Peek(a);
            pc = d.Cpu.PC;
        }
        else
        {
            read = a => Machine.PeekMemory(a);
            pc = Machine.Cpu.PC;
        }
        for (int i = 0; i < 12; i++)
        {
            var (line, len) = Disassembler.FormatLine(read, pc);
            sb.Append(i == 0 ? "> " : "  ").AppendLine(line);
            pc = (ushort)(pc + len);
        }
        return sb.ToString();
    }

    private string FormatMachine()
    {
        var m = Machine;
        var sb = new StringBuilder();
        sb.Append($"Frame {m.Frames}  Raster {m.Vic.RasterLine}/{m.Vic.RasterCycle}  Cycles {m.Cycles:N0}\n");
        if (m is C64 c64)
            sb.Append($"$01 = ${c64.Memory.PortData:X2} (DDR ${c64.Memory.PortDdr:X2})  VIC bank {m.VicBank}  Screen ${m.ScreenAddress:X4}\n");
        else
            sb.Append($"VIC bank {m.VicBank}  Screen ${m.ScreenAddress:X4}\n");
        sb.Append($"IEC ATN {(m.Iec.AtnLow ? "L" : "H")} CLK {(m.Iec.ClkLow ? "L" : "H")} DATA {(m.Iec.DataLow ? "L" : "H")}\n");
        if (m.Drive is { } d)
            sb.Append($"1541: track {d.Disk.Track:0.#} motor {(d.MotorOn ? "on" : "off")} LED {(d.Led ? "on" : "off")} PC ${d.Cpu.PC:X4}\n");
        return sb.ToString();
    }

    private void Goto_Clicked(object? sender, EventArgs e) => GoTo();

    private void Goto_Completed(object? sender, EventArgs e) => GoTo();

    private void GoTo()
    {
        var text = (GotoBox.Text ?? "").Trim().TrimStart('$');
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int address)) return;
        address -= _document.BaseAddress;
        int rowIndex = Math.Clamp(address / HexMetrics.Columns, 0, _document.Rows.Count - 1);
        HexGrid.ScrollTo(rowIndex, position: ScrollToPosition.Start, animate: false);
    }

    private void Refresh_Clicked(object? sender, EventArgs e) => Refresh(force: true);

    private void StepCycle_Clicked(object? sender, EventArgs e)
    {
        _runner.Invoke(_runner.Session.StepCycle);
        Refresh(force: true);
    }

    private void StepInstruction_Clicked(object? sender, EventArgs e)
    {
        _runner.Invoke(_runner.Session.StepInstruction);
        Refresh(force: true);
    }

    private void StepFrame_Clicked(object? sender, EventArgs e)
    {
        // Session.RunFrame also presents the frame, so the screen follows the stepping.
        _runner.Invoke(_runner.Session.RunFrame);
        Refresh(force: true);
    }
}
