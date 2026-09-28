using System;
using Tedd.Commodore.Emulator.Bus;

namespace Tedd.Commodore.Emulator.IO;

/// <summary>
/// MOS 6522 Versatile Interface Adapter (VIA). Two of these sit in the Commodore 1541 disk drive
/// (VIA1 at $1800: IEC bus, VIA2 at $1C00: drive mechanics and read/write head).
///
/// The model follows the MOS Technology "6522 Versatile Interface Adapter" data sheet (referred to below as
/// "data sheet"; register/section names match the Rockwell R6522 edition, which contains the same text and
/// the timing figures). Every call to <see cref="Clock"/> is exactly one φ2 cycle.
///
/// Timing summary (all cited from the data sheet "Timer 1 / Timer 2 Operation" figures):
/// <list type="bullet">
/// <item>Timer 1/2 interval: writing the high counter byte in cycle W sets the interrupt flag after the
/// <c>Clock()</c> of cycle W+N+1, i.e. N+2 cycles after the write (data sheet: "N+1.5 cycles").</item>
/// <item>Timer 1 free-running: the counter shows $FFFF for one cycle after time-out and is then reloaded from
/// the latch, giving a period of N+2 cycles between interrupts.</item>
/// <item>Timer 1 one-shot and Timer 2 one-shot: after time-out the counter keeps decrementing from $FFFF and
/// no further flags are set until the high byte is written again.</item>
/// </list>
///
/// Signal conventions: <see cref="Ca1"/>, <see cref="Ca2"/>, <see cref="Cb1"/>, <see cref="Cb2"/> are raw pin
/// levels (<c>true</c> = high). Edges on them are detected immediately when the property is set (the 6522
/// samples these pins with φ2; callers set them once per cycle before calling <see cref="Clock"/>).
/// <see cref="PortAInput"/>/<see cref="PortBInput"/> return the external pin levels (default all high).
/// </summary>
public sealed class Via6522 : IClockable
{
    // Register indexes (data sheet "Register Select" table).
    public const int RegOrb = 0;
    public const int RegOra = 1;
    public const int RegDdrb = 2;
    public const int RegDdra = 3;
    public const int RegT1CL = 4;
    public const int RegT1CH = 5;
    public const int RegT1LL = 6;
    public const int RegT1LH = 7;
    public const int RegT2CL = 8;
    public const int RegT2CH = 9;
    public const int RegSr = 10;
    public const int RegAcr = 11;
    public const int RegPcr = 12;
    public const int RegIfr = 13;
    public const int RegIer = 14;
    public const int RegOraNoHandshake = 15;

    // Interrupt flag register bits (data sheet "Interrupt Flag Register").
    public const byte IfrCa2 = 0x01;
    public const byte IfrCa1 = 0x02;
    public const byte IfrSr = 0x04;
    public const byte IfrCb2 = 0x08;
    public const byte IfrCb1 = 0x10;
    public const byte IfrT2 = 0x20;
    public const byte IfrT1 = 0x40;
    public const byte IfrIrq = 0x80;

    /// <summary>Name used for debugging / logging (e.g. "VIA1").</summary>
    public string Name { get; }

    /// <summary>External pin levels of port A (1 = high). Default: all high (pull-ups / open inputs).</summary>
    public Func<byte>? PortAInput;
    /// <summary>External pin levels of port B (1 = high). Default: all high.</summary>
    public Func<byte>? PortBInput;

    /// <summary>Raised after any write that may change <see cref="PortAOutput"/>.</summary>
    public event Action? PortAChanged;
    /// <summary>Raised after any write (or timer 1 PB7 transition) that may change <see cref="PortBOutput"/>.</summary>
    public event Action? PortBChanged;
    /// <summary>Raised when <see cref="Ca2Output"/> changes.</summary>
    public event Action? Ca2Changed;
    /// <summary>Raised when <see cref="Cb2Output"/> changes.</summary>
    public event Action? Cb2Changed;

    // Port registers
    private byte _ora, _orb, _ddra, _ddrb;
    private byte _iraLatch, _irbLatch;

    // Control registers
    private byte _acr, _pcr, _ifr, _ier;

    // Timer 1
    private ushort _t1Latch;
    private ushort _t1Counter;
    private bool _t1LoadPending;   // next Clock() loads the latch instead of decrementing
    private bool _t1Armed;         // one-shot: a time-out will set the flag
    private bool _t1Pb7 = true;    // timer 1 PB7 output level

    // Timer 2
    private byte _t2LatchL;
    private ushort _t2Counter;
    private bool _t2LoadPending;   // next Clock() skips the decrement (write cycle)
    private bool _t2Armed;
    private bool _pb6Prev = true;

    // Shift register
    private byte _sr;
    private bool _srActive;
    private int _srBits;
    private bool _srClock = true;  // CB1 shift clock output level (idle high)
    private bool _srCb2 = true;    // CB2 data output level while shifting out
    private int _srT2Counter;

    // Control lines
    private bool _ca1 = true, _ca2 = true, _cb1 = true, _cb2 = true;   // input levels
    private bool _ca2Out = true, _cb2Out = true;                       // PCR controlled output levels
    private bool _ca2Pulse, _cb2Pulse;                                 // pulse mode: return high on next Clock()

    public Via6522(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Reset();
    }

    // ------------------------------------------------------------------------------------------------
    // Pins
    // ------------------------------------------------------------------------------------------------

    /// <summary>What the chip drives on port A: ORA for output bits, high (released) for input bits.</summary>
    public byte PortAOutput => (byte)(_ora | ~_ddra);

    /// <summary>
    /// What the chip drives on port B: ORB for output bits, high for input bits; PB7 is replaced by the timer 1
    /// output when ACR bit 7 is set (data sheet "Auxiliary Control Register", T1 control bit 7).
    /// </summary>
    public byte PortBOutput
    {
        get
        {
            byte v = (byte)(_orb | ~_ddrb);
            if ((_acr & 0x80) != 0)
                v = (byte)((v & 0x7F) | (_t1Pb7 ? 0x80 : 0));
            return v;
        }
    }

    /// <summary>CA1 input level (raw). Active edge per PCR bit 0 sets IFR bit 1.</summary>
    public bool Ca1
    {
        get => _ca1;
        set
        {
            if (_ca1 == value) return;
            _ca1 = value;
            // PCR bit 0: 0 = negative active edge, 1 = positive active edge (data sheet "Peripheral Control Register").
            if (value == ((_pcr & 0x01) != 0))
                OnCa1ActiveEdge();
        }
    }

    /// <summary>CA2 input level (raw). In the PCR input modes the selected edge sets IFR bit 0.</summary>
    public bool Ca2
    {
        get => _ca2;
        set
        {
            if (_ca2 == value) return;
            _ca2 = value;
            int mode = (_pcr >> 1) & 7;
            // Modes 000/001: negative edge, 010/011: positive edge (data sheet PCR table).
            if (mode < 4 && value == ((mode & 2) != 0))
                SetIfr(IfrCa2);
        }
    }

    /// <summary>CB1 input level (raw). Active edge per PCR bit 4 sets IFR bit 4; external shift clock in SR modes 011/111.</summary>
    public bool Cb1
    {
        get => _cb1;
        set
        {
            if (_cb1 == value) return;
            _cb1 = value;
            int srMode = SrMode;
            bool srExternal = srMode == 3 || srMode == 7;
            if (srExternal && _srActive)
                ShiftClockEdge(value);
            // When the shift register drives CB1 (internal clock modes) the pin is an output and the input is ignored.
            bool cb1IsInput = srMode == 0 || srExternal;
            if (cb1IsInput && value == ((_pcr & 0x10) != 0))
                OnCb1ActiveEdge();
        }
    }

    /// <summary>CB2 input level (raw). In the PCR input modes the selected edge sets IFR bit 3 (only while the SR is disabled).</summary>
    public bool Cb2
    {
        get => _cb2;
        set
        {
            if (_cb2 == value) return;
            _cb2 = value;
            if (SrMode != 0) return;   // CB2 is the shift register data pin while the SR is enabled.
            int mode = (_pcr >> 5) & 7;
            if (mode < 4 && value == ((mode & 2) != 0))
                SetIfr(IfrCb2);
        }
    }

    /// <summary>CA2 output level when PCR configures CA2 as an output (handshake, pulse, manual); true (released) otherwise.</summary>
    public bool Ca2Output => _ca2Out;

    /// <summary>
    /// CB2 output level: the shift register data bit while the SR is in an output mode (ACR bits 2-4 = 1xx),
    /// otherwise the PCR controlled level (true = released when CB2 is an input).
    /// </summary>
    public bool Cb2Output => SrOutputMode ? _srCb2 : _cb2Out;

    /// <summary>CB1 level driven by the shift register clock in the internal clock modes (idle high).</summary>
    public bool Cb1Output => _srClock;

    /// <summary>True when /IRQ is asserted: any flag whose IER bit is set (data sheet: IFR bit 7).</summary>
    public bool IrqLine => (_ifr & _ier & 0x7F) != 0;

    /// <summary>Timer 1 PB7 output level (meaningful when ACR bit 7 is set).</summary>
    public bool Timer1Pb7 => _t1Pb7;

    // ------------------------------------------------------------------------------------------------
    // Register access
    // ------------------------------------------------------------------------------------------------

    /// <summary>Reads register <paramref name="reg"/> (0..15) with all documented side effects.</summary>
    public byte Read(int reg)
    {
        switch (reg & 0x0F)
        {
            case RegOrb:
                // Reading ORB clears the CB1 flag and, unless CB2 is in an "independent" mode, the CB2 flag
                // (data sheet "Interrupt Flag Register" clearing table).
                ClearIfr(IfrCb1);
                if (!Cb2Independent) ClearIfr(IfrCb2);
                return ReadIrb();
            case RegOra:
                ClearIfr(IfrCa1);
                if (!Ca2Independent) ClearIfr(IfrCa2);
                PortAHandshake();
                return ReadIra();
            case RegDdrb: return _ddrb;
            case RegDdra: return _ddra;
            case RegT1CL:
                // "Read T1C-L: clears T1 interrupt flag" (data sheet Timer 1 register table).
                ClearIfr(IfrT1);
                return (byte)_t1Counter;
            case RegT1CH: return (byte)(_t1Counter >> 8);
            case RegT1LL: return (byte)_t1Latch;
            case RegT1LH: return (byte)(_t1Latch >> 8);
            case RegT2CL:
                // "Read T2C-L: clears T2 interrupt flag".
                ClearIfr(IfrT2);
                return (byte)_t2Counter;
            case RegT2CH: return (byte)(_t2Counter >> 8);
            case RegSr:
                // Reading the SR clears the SR flag and (re)starts shifting in the shift-in modes.
                ClearIfr(IfrSr);
                StartShift();
                return _sr;
            case RegAcr: return _acr;
            case RegPcr: return _pcr;
            case RegIfr: return IfrValue();
            case RegIer: return (byte)(_ier | 0x80);   // "bit 7 reads as a logic 1" (data sheet IER).
            default: return ReadIra();                  // Register 15: ORA without handshake / flag clearing.
        }
    }

    /// <summary>Reads register <paramref name="reg"/> without any side effects (for debuggers).</summary>
    public byte Peek(int reg)
    {
        switch (reg & 0x0F)
        {
            case RegOrb: return ReadIrb();
            case RegOra: return ReadIra();
            case RegDdrb: return _ddrb;
            case RegDdra: return _ddra;
            case RegT1CL: return (byte)_t1Counter;
            case RegT1CH: return (byte)(_t1Counter >> 8);
            case RegT1LL: return (byte)_t1Latch;
            case RegT1LH: return (byte)(_t1Latch >> 8);
            case RegT2CL: return (byte)_t2Counter;
            case RegT2CH: return (byte)(_t2Counter >> 8);
            case RegSr: return _sr;
            case RegAcr: return _acr;
            case RegPcr: return _pcr;
            case RegIfr: return IfrValue();
            case RegIer: return (byte)(_ier | 0x80);
            default: return ReadIra();
        }
    }

    /// <summary>Writes register <paramref name="reg"/> (0..15).</summary>
    public void Write(int reg, byte value)
    {
        switch (reg & 0x0F)
        {
            case RegOrb:
                _orb = value;
                ClearIfr(IfrCb1);
                if (!Cb2Independent) ClearIfr(IfrCb2);
                PortBHandshake();
                PortBChanged?.Invoke();
                break;
            case RegOra:
                _ora = value;
                ClearIfr(IfrCa1);
                if (!Ca2Independent) ClearIfr(IfrCa2);
                PortAHandshake();
                PortAChanged?.Invoke();
                break;
            case RegDdrb:
                _ddrb = value;
                PortBChanged?.Invoke();
                break;
            case RegDdra:
                _ddra = value;
                PortAChanged?.Invoke();
                break;
            case RegT1CL:
                // "Write T1C-L: writes into the low order latch" (data sheet Timer 1 register table).
                _t1Latch = (ushort)((_t1Latch & 0xFF00) | value);
                break;
            case RegT1CH:
                // "Write T1C-H: writes into the high order latch, transfers latches to the counter, clears the T1
                // interrupt flag, starts the timer". PB7 goes low on this write when enabled (Figure "Timer 1 One-Shot").
                _t1Latch = (ushort)((value << 8) | (_t1Latch & 0x00FF));
                _t1Counter = _t1Latch;
                _t1LoadPending = true;
                _t1Armed = true;
                ClearIfr(IfrT1);
                SetT1Pb7(false);
                break;
            case RegT1LL:
                _t1Latch = (ushort)((_t1Latch & 0xFF00) | value);
                break;
            case RegT1LH:
                // "Write T1L-H: writes into the high order latch, clears the T1 interrupt flag" (no transfer).
                _t1Latch = (ushort)((value << 8) | (_t1Latch & 0x00FF));
                ClearIfr(IfrT1);
                break;
            case RegT2CL:
                // "Write T2C-L: writes into the low order latch".
                _t2LatchL = value;
                break;
            case RegT2CH:
                // "Write T2C-H: writes into the high order counter, transfers the low order latch to the low order
                // counter, clears the T2 interrupt flag, starts the timer".
                _t2Counter = (ushort)((value << 8) | _t2LatchL);
                _t2LoadPending = (_acr & 0x20) == 0;   // interval mode: the write cycle itself does not decrement
                _t2Armed = true;
                ClearIfr(IfrT2);
                break;
            case RegSr:
                _sr = value;
                ClearIfr(IfrSr);
                StartShift();
                break;
            case RegAcr:
                WriteAcr(value);
                break;
            case RegPcr:
                WritePcr(value);
                break;
            case RegIfr:
                // "Writing a 1 into a bit of the IFR clears that flag; bit 7 is not a flag" (data sheet IFR).
                _ifr = (byte)(_ifr & ~(value & 0x7F));
                break;
            case RegIer:
                // "Bit 7 = 1: each 1 in bits 0-6 sets the corresponding enable; bit 7 = 0: each 1 clears it" (data sheet IER).
                if ((value & 0x80) != 0)
                    _ier = (byte)(_ier | (value & 0x7F));
                else
                    _ier = (byte)(_ier & ~(value & 0x7F));
                break;
            default:
                // Register 15: ORA without handshake and without flag clearing.
                _ora = value;
                PortAChanged?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Reset (data sheet "RES"): clears all internal registers (ports, DDRs, ACR, PCR, IFR, IER, SR). Timer latches
    /// are set to 0 and the counters to $FFFF (undefined on the real chip); PB7/CA2/CB2 outputs go high.
    /// </summary>
    public void Reset()
    {
        _ora = _orb = _ddra = _ddrb = 0;
        _iraLatch = _irbLatch = 0xFF;
        _acr = _pcr = _ifr = _ier = 0;
        _t1Latch = 0; _t1Counter = 0xFFFF; _t1LoadPending = false; _t1Armed = false; _t1Pb7 = true;
        _t2LatchL = 0; _t2Counter = 0xFFFF; _t2LoadPending = false; _t2Armed = false; _pb6Prev = true;
        _sr = 0; _srActive = false; _srBits = 0; _srClock = true; _srCb2 = true; _srT2Counter = 0;
        _ca2Out = _cb2Out = true;
        _ca2Pulse = _cb2Pulse = false;
    }

    // ------------------------------------------------------------------------------------------------
    // Clock
    // ------------------------------------------------------------------------------------------------

    /// <summary>Advances the chip by exactly one φ2 cycle.</summary>
    public void Clock()
    {
        ClockTimer1();
        ClockTimer2();
        ClockShiftRegister();

        // Pulse output mode: "CA2 goes low for one cycle following a read or write of ORA" (CB2: write of ORB);
        // the output returned high at the end of the cycle after the access (data sheet PCR modes 101).
        if (_ca2Pulse) { _ca2Pulse = false; SetCa2Out(true); }
        if (_cb2Pulse) { _cb2Pulse = false; SetCb2Out(true); }
    }

    private void ClockTimer1()
    {
        if (_t1LoadPending)
        {
            // Load cycle: the cycle of the T1C-H write and the cycle after a free-running time-out do not
            // decrement (data sheet Figures "Timer 1 One-Shot Mode Timing"/"Free-Running Mode Timing": the
            // interrupt occurs N+1.5 cycles after the write and every N+2 cycles thereafter).
            _t1Counter = _t1Latch;
            _t1LoadPending = false;
            return;
        }

        if (_t1Counter != 0)
        {
            _t1Counter--;
            return;
        }

        // Counter passes zero: time-out.
        _t1Counter = 0xFFFF;
        bool freeRun = (_acr & 0x40) != 0;
        if (freeRun)
        {
            // "Free-running mode: the counter is reloaded from the latch and the interrupt flag is set at each
            // time-out; PB7 is inverted at each time-out" (data sheet ACR bits 6/7). The counter reads $FFFF for
            // one cycle before the reload, which is where the N+2 period comes from.
            _t1LoadPending = true;
            SetIfr(IfrT1);
            SetT1Pb7(!_t1Pb7);
        }
        else if (_t1Armed)
        {
            // "One-shot mode: the interrupt flag is set once; the counter continues to decrement but the flag
            // will not be set again until T1C-H is written" (data sheet Timer 1 One-Shot Mode).
            _t1Armed = false;
            SetIfr(IfrT1);
            SetT1Pb7(true);
        }
    }

    private void ClockTimer2()
    {
        if ((_acr & 0x20) == 0)
        {
            // Interval timer mode (ACR bit 5 = 0): same N+2 behaviour as timer 1 one-shot.
            if (_t2LoadPending)
            {
                _t2LoadPending = false;
                return;
            }
            if (_t2Counter != 0)
            {
                _t2Counter--;
                return;
            }
            _t2Counter = 0xFFFF;
            if (_t2Armed)
            {
                // "The interrupt flag is set once; the timer continues to decrement but no further interrupts
                // occur until T2C-H is written" (data sheet Timer 2 Interval Timer Mode).
                _t2Armed = false;
                SetIfr(IfrT2);
            }
        }
        else
        {
            // Pulse counting mode (ACR bit 5 = 1): "the counter decrements on each negative transition of PB6;
            // when it reaches zero the interrupt flag is set" (data sheet Timer 2 Pulse Counting Mode).
            _t2LoadPending = false;
            bool pb6 = (PinsB() & 0x40) != 0;
            if (_pb6Prev && !pb6)
            {
                _t2Counter--;
                if (_t2Counter == 0 && _t2Armed)
                {
                    _t2Armed = false;
                    SetIfr(IfrT2);
                }
            }
            _pb6Prev = pb6;
        }
    }

    private void ClockShiftRegister()
    {
        switch (SrMode)
        {
            case 1:   // shift in under T2 control
            case 5:   // shift out under T2 control
                if (!_srActive) return;
                ClockShiftT2();
                break;
            case 4:   // shift out free-running under T2 control (never stops, no interrupt)
                ClockShiftT2();
                break;
            case 2:   // shift in under φ2 control
            case 6:   // shift out under φ2 control
                // "Shifting occurs at the φ2 rate / 2": CB1 toggles every cycle (data sheet SR modes 010/110).
                if (!_srActive) return;
                ToggleShiftClock();
                break;
            default:
                // 0: disabled; 3/7: external clock on CB1 (handled by the Cb1 setter).
                break;
        }
    }

    private void ClockShiftT2()
    {
        // "The shift rate is controlled by the low order T2 latch: T2 counts down and toggles the shift clock
        // when it reaches zero, then reloads" (data sheet SR modes 001/100/101). Half period = N+2 cycles,
        // like a timer time-out.
        if (_srT2Counter == 0)
        {
            _srT2Counter = _t2LatchL + 1;
            ToggleShiftClock();
        }
        else
        {
            _srT2Counter--;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Ports
    // ------------------------------------------------------------------------------------------------

    private byte PinsA() => (byte)((_ora | ~_ddra) & (PortAInput?.Invoke() ?? 0xFF));
    private byte PinsB() => (byte)((_orb | ~_ddrb) & (PortBInput?.Invoke() ?? 0xFF));

    /// <summary>
    /// IRA always reflects the pin levels (data sheet: "reading IRA returns the level on the pins, even for output
    /// bits"), or the value latched at the last CA1 active edge when ACR bit 0 is set.
    /// </summary>
    private byte ReadIra() => (_acr & 0x01) != 0 ? _iraLatch : PinsA();

    /// <summary>
    /// IRB returns ORB for output bits and the pin level (or the CB1 latched level with ACR bit 1) for input bits
    /// (data sheet: "reading IRB returns the contents of ORB for output pins"). PB7 shows the timer 1 output when
    /// ACR bit 7 is set.
    /// </summary>
    private byte ReadIrb()
    {
        byte pins = (_acr & 0x02) != 0 ? _irbLatch : PinsB();
        byte v = (byte)((_orb & _ddrb) | (pins & ~_ddrb));
        if ((_acr & 0x80) != 0)
            v = (byte)((v & 0x7F) | (_t1Pb7 ? 0x80 : 0));
        return v;
    }

    // ------------------------------------------------------------------------------------------------
    // Control lines
    // ------------------------------------------------------------------------------------------------

    private bool Ca2Independent => ((_pcr >> 1) & 7) is 1 or 3;
    private bool Cb2Independent => ((_pcr >> 5) & 7) is 1 or 3;

    private void OnCa1ActiveEdge()
    {
        SetIfr(IfrCa1);
        // Input latching: "data on PA is latched into IRA on the active transition of CA1" (data sheet ACR bit 0).
        if ((_acr & 0x01) != 0)
            _iraLatch = PinsA();
        // Handshake output: "CA2 goes high on the next active CA1 transition" (data sheet PCR mode 100).
        if (((_pcr >> 1) & 7) == 4)
            SetCa2Out(true);
    }

    private void OnCb1ActiveEdge()
    {
        SetIfr(IfrCb1);
        if ((_acr & 0x02) != 0)
            _irbLatch = PinsB();
        if (((_pcr >> 5) & 7) == 4)
            SetCb2Out(true);
    }

    /// <summary>After a read or write of ORA (register 1): handshake / pulse on CA2 (data sheet PCR modes 100/101).</summary>
    private void PortAHandshake()
    {
        int mode = (_pcr >> 1) & 7;
        if (mode == 4)
        {
            SetCa2Out(false);
        }
        else if (mode == 5)
        {
            SetCa2Out(false);
            _ca2Pulse = true;
        }
    }

    /// <summary>After a write of ORB (register 0): handshake / pulse on CB2. Reads of ORB do not trigger it (data sheet).</summary>
    private void PortBHandshake()
    {
        int mode = (_pcr >> 5) & 7;
        if (mode == 4)
        {
            SetCb2Out(false);
        }
        else if (mode == 5)
        {
            SetCb2Out(false);
            _cb2Pulse = true;
        }
    }

    private void WritePcr(byte value)
    {
        _pcr = value;
        _ca2Pulse = false;
        _cb2Pulse = false;
        // Manual output modes drive the pin directly (PCR modes 110 = low, 111 = high). In the handshake and pulse
        // modes the output idles high; in the input modes the pin is released (reported high).
        int ca2Mode = (value >> 1) & 7;
        SetCa2Out(ca2Mode != 6);
        int cb2Mode = (value >> 5) & 7;
        SetCb2Out(cb2Mode != 6);
    }

    private void SetCa2Out(bool level)
    {
        if (_ca2Out == level) return;
        _ca2Out = level;
        Ca2Changed?.Invoke();
    }

    private void SetCb2Out(bool level)
    {
        if (_cb2Out == level) return;
        _cb2Out = level;
        if (!SrOutputMode)
            Cb2Changed?.Invoke();
    }

    private void SetT1Pb7(bool level)
    {
        if (_t1Pb7 == level) return;
        _t1Pb7 = level;
        if ((_acr & 0x80) != 0)
            PortBChanged?.Invoke();
    }

    // ------------------------------------------------------------------------------------------------
    // Auxiliary control register / shift register
    // ------------------------------------------------------------------------------------------------

    private int SrMode => (_acr >> 2) & 7;
    private bool SrOutputMode => (_acr & 0x10) != 0;

    private void WriteAcr(byte value)
    {
        byte old = _acr;
        bool cb2Before = Cb2Output;
        _acr = value;

        // Enabling input latching: start with the current pin levels so that reads before the first CA1/CB1 edge
        // are sensible (the data sheet leaves the latch contents undefined at that point).
        if ((old & 0x01) == 0 && (value & 0x01) != 0) _iraLatch = PinsA();
        if ((old & 0x02) == 0 && (value & 0x02) != 0) _irbLatch = PinsB();

        // Entering pulse counting mode: sample PB6 so that the mode switch itself is not seen as a transition.
        if ((old & 0x20) == 0 && (value & 0x20) != 0) _pb6Prev = (PinsB() & 0x40) != 0;

        int oldMode = (old >> 2) & 7;
        int newMode = (value >> 2) & 7;
        if (oldMode != newMode)
        {
            _srBits = 0;
            _srClock = true;
            if (newMode == 0)
            {
                _srActive = false;
            }
            else if (newMode == 4)
            {
                // Free-running output runs as soon as the mode is selected (data sheet SR mode 100).
                _srActive = true;
                _srT2Counter = _t2LatchL + 1;
            }
            else
            {
                // Other modes start on the next read/write of the SR.
                _srActive = false;
            }
        }

        if (Cb2Output != cb2Before)
            Cb2Changed?.Invoke();
        if (((old ^ value) & 0x80) != 0)
            PortBChanged?.Invoke();
    }

    /// <summary>A read or write of the SR (re)starts an 8-bit shift in every mode except disabled.</summary>
    private void StartShift()
    {
        int mode = SrMode;
        if (mode == 0) return;
        _srBits = 0;
        _srActive = true;
        if (mode == 1 || mode == 4 || mode == 5)
            _srT2Counter = _t2LatchL + 1;
    }

    private void ToggleShiftClock()
    {
        _srClock = !_srClock;
        ShiftClockEdge(_srClock);
    }

    /// <summary>
    /// One edge of the shift clock (CB1). Shift-out modes present SR bit 7 on CB2 on the falling edge and rotate it
    /// back into bit 0; shift-in modes sample CB2 into bit 0 on the rising edge. After eight complete clocks the SR
    /// flag is set (except in free-running mode 100) and shifting stops (data sheet "Shift Register Operation").
    /// </summary>
    private void ShiftClockEdge(bool rising)
    {
        int mode = SrMode;
        bool output = (mode & 4) != 0;
        if (!rising)
        {
            if (output)
            {
                bool bit = (_sr & 0x80) != 0;
                _sr = (byte)((_sr << 1) | (bit ? 1 : 0));
                if (_srCb2 != bit)
                {
                    _srCb2 = bit;
                    Cb2Changed?.Invoke();
                }
            }
            return;
        }

        if (!output)
            _sr = (byte)((_sr << 1) | (_cb2 ? 1 : 0));

        if (mode == 4) return;
        _srBits++;
        if (_srBits >= 8)
        {
            _srActive = false;
            SetIfr(IfrSr);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Interrupts
    // ------------------------------------------------------------------------------------------------

    private void SetIfr(byte bit) => _ifr |= bit;
    private void ClearIfr(byte bit) => _ifr = (byte)(_ifr & ~bit);

    /// <summary>IFR as read: bits 0-6 are the flags, bit 7 is set when any enabled flag is set (data sheet IFR).</summary>
    private byte IfrValue() => (byte)((_ifr & 0x7F) | ((_ifr & _ier & 0x7F) != 0 ? 0x80 : 0));
}
