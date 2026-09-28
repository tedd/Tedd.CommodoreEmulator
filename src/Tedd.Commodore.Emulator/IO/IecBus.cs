using System;
using System.Collections.Generic;

namespace Tedd.Commodore.Emulator.IO;

/// <summary>
/// The Commodore serial (IEC) bus. ATN, CLK and DATA are open-collector lines: each participant can only pull a
/// line low; a line is high when nobody pulls it. See docs/ARCHITECTURE.md, "IEC serial bus".
/// </summary>
public sealed class IecBus
{
    private readonly List<IecPort> _ports = new();
    private bool _atnLow, _clkLow, _dataLow;

    /// <summary>true = ATN asserted (line low).</summary>
    public bool AtnLow => _atnLow;
    /// <summary>true = CLK line low.</summary>
    public bool ClkLow => _clkLow;
    /// <summary>true = DATA line low.</summary>
    public bool DataLow => _dataLow;

    /// <summary>Raised whenever one of the three lines changes level.</summary>
    public event Action? Changed;

    public IReadOnlyList<IecPort> Ports => _ports;

    /// <summary>Connects a new participant.</summary>
    public IecPort Attach(string name)
    {
        var port = new IecPort(this, name);
        _ports.Add(port);
        return port;
    }

    public void Detach(IecPort port)
    {
        if (_ports.Remove(port))
            Recalculate();
    }

    internal void Recalculate()
    {
        bool atn = false, clk = false, data = false;
        foreach (var p in _ports)
        {
            atn |= p.PullAtn;
            clk |= p.PullClk;
            data |= p.PullData;
        }
        if (atn == _atnLow && clk == _clkLow && data == _dataLow)
            return;
        _atnLow = atn;
        _clkLow = clk;
        _dataLow = data;
        Changed?.Invoke();
    }
}

/// <summary>One participant's connection to the <see cref="IecBus"/>.</summary>
public sealed class IecPort
{
    private bool _atn, _clk, _data;

    internal IecPort(IecBus bus, string name)
    {
        Bus = bus;
        Name = name;
    }

    public IecBus Bus { get; }
    public string Name { get; }

    /// <summary>true = this participant pulls ATN low.</summary>
    public bool PullAtn
    {
        get => _atn;
        set { if (_atn != value) { _atn = value; Bus.Recalculate(); } }
    }

    /// <summary>true = this participant pulls CLK low.</summary>
    public bool PullClk
    {
        get => _clk;
        set { if (_clk != value) { _clk = value; Bus.Recalculate(); } }
    }

    /// <summary>true = this participant pulls DATA low.</summary>
    public bool PullData
    {
        get => _data;
        set { if (_data != value) { _data = value; Bus.Recalculate(); } }
    }

    /// <summary>Sets all three at once (one recalculation).</summary>
    public void Set(bool pullAtn, bool pullClk, bool pullData)
    {
        if (_atn == pullAtn && _clk == pullClk && _data == pullData) return;
        _atn = pullAtn;
        _clk = pullClk;
        _data = pullData;
        Bus.Recalculate();
    }
}
