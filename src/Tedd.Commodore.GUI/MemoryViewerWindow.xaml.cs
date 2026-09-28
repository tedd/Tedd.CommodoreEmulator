using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Media;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.GUI;

/// <summary>
/// Hex viewer/editor for the C64 / C128 (and 1541) memory. Editing is done through the emulator runner so that the
/// machine is never touched from the UI thread while it is running; "Freeze" stops the machine so values are
/// stable and single stepping is possible.
/// </summary>
public partial class MemoryViewerWindow : Window
{
    private readonly EmulatorRunner _runner;
    private readonly Action<bool> _setPaused;
    private readonly ObservableCollection<Row> _rows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private MemorySource _source = MemorySource.Cpu;
    private int _size = 0x10000;
    private bool _updating;

    private enum MemorySource { Cpu, Ram, ColorRam, Vic, DriveRam, DriveCpu }

    /// <param name="runner">The runner that owns the machine.</param>
    /// <param name="setPaused">Freezes/resumes the machine and keeps the main window's menu in sync.</param>
    public MemoryViewerWindow(EmulatorRunner runner, Action<bool> setPaused)
    {
        InitializeComponent();
        _runner = runner;
        _setPaused = setPaused;
        Grid.ItemsSource = _rows;
        BuildRows();
        Refresh();
        _timer.Tick += (_, _) => { if (AutoRefresh.IsChecked == true && !_runner.Paused) Refresh(); };
        _timer.Start();
        OnFreezeChanged();
    }

    private CommodoreMachine Machine => _runner.Session.Machine;

    /// <summary>Called by the main window when the pause state changes elsewhere.</summary>
    public void OnFreezeChanged()
    {
        FreezeButton.IsChecked = _runner.Paused;
        FreezeButton.Content = _runner.Paused ? "Resume" : "Freeze";
        StepCycleButton.IsEnabled = StepInstructionButton.IsEnabled = StepFrameButton.IsEnabled = _runner.Paused;
        Refresh();
    }

    private void Freeze_Click(object sender, RoutedEventArgs e)
    {
        _setPaused(FreezeButton.IsChecked == true);
        OnFreezeChanged();
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _source = (MemorySource)SourceBox.SelectedIndex;
        _size = _source switch
        {
            MemorySource.ColorRam => 0x400,
            MemorySource.Vic => 0x4000,
            MemorySource.DriveRam => 0x800,
            MemorySource.Ram => Machine.Ram.Length,
            _ => 0x10000,
        };
        BuildRows();
        Refresh();
    }

    private void BuildRows()
    {
        _rows.Clear();
        int baseAddress = _source == MemorySource.ColorRam ? 0xD800 : 0;
        for (int a = 0; a < _size; a += 16)
            _rows.Add(new Row(this, a, baseAddress));
    }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var data = new byte[_size];
            string registers = "", disassembly = "", machine = "";
            _runner.Invoke(() =>
            {
                ReadAll(data);
                registers = FormatRegisters();
                disassembly = FormatDisassembly();
                machine = FormatMachine();
            });
            foreach (var row in _rows)
                row.Update(data);
            RegistersText.Text = registers;
            DisassemblyText.Text = disassembly;
            MachineText.Text = machine;
        }
        finally
        {
            _updating = false;
        }
    }

    private void ReadAll(byte[] data)
    {
        var m = Machine;
        switch (_source)
        {
            case MemorySource.Cpu:
                for (int i = 0; i < data.Length; i++) data[i] = m.PeekMemory((ushort)i);
                break;
            case MemorySource.Ram:
                Array.Copy(m.Ram, data, Math.Min(data.Length, m.Ram.Length));
                break;
            case MemorySource.ColorRam:
                Array.Copy(m.ColorRam, data, Math.Min(data.Length, m.ColorRam.Length));
                break;
            case MemorySource.Vic:
                for (int i = 0; i < data.Length; i++) data[i] = m.VicMemory.PeekVic(i);
                break;
            case MemorySource.DriveRam:
                if (m.Drive is { } d) Array.Copy(d.Memory.Ram, data, data.Length);
                break;
            case MemorySource.DriveCpu:
                if (m.Drive is { } d2)
                    for (int i = 0; i < data.Length; i++) data[i] = d2.Memory.Peek((ushort)i);
                break;
        }
    }

    private void WriteByte(int address, byte value)
    {
        _runner.Invoke(() =>
        {
            var m = Machine;
            switch (_source)
            {
                case MemorySource.Cpu: m.WriteMemory((ushort)address, value); break;
                case MemorySource.Ram: if (address < m.Ram.Length) m.Ram[address] = value; break;
                case MemorySource.ColorRam: m.ColorRam[address & 0x3FF] = (byte)(value & 0x0F); break;
                case MemorySource.Vic:
                {
                    int bank64 = m is C128 c ? c.Mmu.VicRamBank << 16 : 0;
                    m.Ram[bank64 | (m.VicBank << 14) | (address & 0x3FFF)] = value;
                    break;
                }
                case MemorySource.DriveRam: if (m.Drive is { } d) d.Memory.Ram[address & 0x7FF] = value; break;
                case MemorySource.DriveCpu: if (m.Drive is { } d2) d2.Memory.Write((ushort)address, value); break;
            }
        });
    }

    private string FormatRegisters()
    {
        var c = _source is MemorySource.DriveRam or MemorySource.DriveCpu ? Machine.Drive?.Cpu : Machine.Cpu;
        if (c is null) return "(no drive)";
        var sb = new StringBuilder();
        if (Machine is C128 c128 && c128.Z80Active && _source is not (MemorySource.DriveRam or MemorySource.DriveCpu))
        {
            var z = c128.Z80;
            sb.Append($"Z80 (active)  PC ${z.PC:X4}  SP ${z.SP:X4}  AF ${z.AF:X4}  BC ${z.BC:X4}  DE ${z.DE:X4}  HL ${z.HL:X4}\n");
            sb.Append($"IX ${z.IX:X4}  IY ${z.IY:X4}  I ${z.I:X2}  R ${z.R:X2}  IM{z.InterruptMode}  IFF1 {(z.Iff1 ? 1 : 0)}  T-states {z.Cycles:N0}{(z.Halted ? "  HALT" : "")}\n");
            sb.Append("8502 (halted) ");
        }
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
        else if (m is C128 c128)
        {
            var mmu = c128.Mmu;
            sb.Append($"$01 = ${c128.Memory.PortData:X2} (DDR ${c128.Memory.PortDdr:X2})  VIC bank {m.VicBank} (RAM bank {mmu.VicRamBank})  Screen ${m.ScreenAddress:X4}\n");
            sb.Append($"MMU CR ${mmu.Cr:X2} MCR ${mmu.Mcr:X2} RCR ${mmu.Rcr:X2} P0 ${mmu.Page0Bank}:{mmu.Page0:X2} P1 ${mmu.Page1Bank}:{mmu.Page1:X2}  " +
                      $"{(c128.C64Mode ? "C64 mode" : "C128 mode")}  {(c128.Z80Active ? "Z80" : "8502")}{(c128.Vic.FastMode ? " 2 MHz" : "")}  " +
                      $"40/80 key: {(c128.Display40Columns ? "40" : "80")}  VDC R{c128.Vdc.SelectedRegister} raster {c128.Vdc.RasterLine}\n");
        }
        sb.Append($"IEC ATN {(m.Iec.AtnLow ? "L" : "H")} CLK {(m.Iec.ClkLow ? "L" : "H")} DATA {(m.Iec.DataLow ? "L" : "H")}\n");
        if (m.Drive is { } d)
            sb.Append($"1541: track {d.Disk.Track:0.#} motor {(d.MotorOn ? "on" : "off")} LED {(d.Led ? "on" : "off")} PC ${d.Cpu.PC:X4}\n");
        return sb.ToString();
    }

    private void Goto_Click(object sender, RoutedEventArgs e) => GoTo();

    private void Goto_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) GoTo();
    }

    private void GoTo()
    {
        var text = GotoBox.Text.Trim().TrimStart('$');
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int address)) return;
        if (_source == MemorySource.ColorRam) address -= 0xD800;
        int rowIndex = Math.Clamp(address / 16, 0, _rows.Count - 1);
        Grid.ScrollIntoView(_rows[rowIndex]);
        Grid.SelectedItem = _rows[rowIndex];
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not Row row || e.EditingElement is not TextBox box) return;
        int column = e.Column.DisplayIndex - 1;
        if (column < 0 || column > 15) return;
        var text = box.Text.Trim().TrimStart('$');
        if (byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
        {
            WriteByte(row.Offset + column, value);
            Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        }
        else
        {
            e.Cancel = true;
        }
    }

    private void StepCycle_Click(object sender, RoutedEventArgs e)
    {
        _runner.Invoke(_runner.Session.StepCycle);
        Refresh();
    }

    private void StepInstruction_Click(object sender, RoutedEventArgs e)
    {
        _runner.Invoke(_runner.Session.StepInstruction);
        Refresh();
    }

    private void StepFrame_Click(object sender, RoutedEventArgs e)
    {
        // Session.RunFrame also presents the frame, so the screen follows the stepping.
        _runner.Invoke(_runner.Session.RunFrame);
        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    /// <summary>One line of 16 bytes. Setting a byte property writes to memory.</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private readonly MemoryViewerWindow _owner;
        private readonly string[] _hex = new string[16];
        private string _text = "";

        public Row(MemoryViewerWindow owner, int offset, int baseAddress)
        {
            _owner = owner;
            Offset = offset;
            Address = (baseAddress + offset).ToString("X4");
            Array.Fill(_hex, "..");
        }

        public int Offset { get; }
        public string Address { get; }
        public string Text => _text;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(byte[] data)
        {
            var sb = new StringBuilder(16);
            for (int i = 0; i < 16; i++)
            {
                byte b = data[Offset + i];
                var h = b.ToString("X2");
                if (_hex[i] != h)
                {
                    _hex[i] = h;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("B" + i));
                }
                char c = Petscii.ScreenCodeToAscii((byte)(b & 0x7F));
                sb.Append(c < ' ' || c > '~' ? '.' : c);
            }
            var t = sb.ToString();
            if (t != _text)
            {
                _text = t;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        private string Get(int i) => _hex[i];
        private void Set(int i, string value) => _hex[i] = value; // the actual write happens in CellEditEnding

        public string B0 { get => Get(0); set => Set(0, value); }
        public string B1 { get => Get(1); set => Set(1, value); }
        public string B2 { get => Get(2); set => Set(2, value); }
        public string B3 { get => Get(3); set => Set(3, value); }
        public string B4 { get => Get(4); set => Set(4, value); }
        public string B5 { get => Get(5); set => Set(5, value); }
        public string B6 { get => Get(6); set => Set(6, value); }
        public string B7 { get => Get(7); set => Set(7, value); }
        public string B8 { get => Get(8); set => Set(8, value); }
        public string B9 { get => Get(9); set => Set(9, value); }
        public string B10 { get => Get(10); set => Set(10, value); }
        public string B11 { get => Get(11); set => Set(11, value); }
        public string B12 { get => Get(12); set => Set(12, value); }
        public string B13 { get => Get(13); set => Set(13, value); }
        public string B14 { get => Get(14); set => Set(14, value); }
        public string B15 { get => Get(15); set => Set(15, value); }
    }
}
