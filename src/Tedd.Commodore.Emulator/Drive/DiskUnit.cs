using System;

namespace Tedd.Commodore.Emulator.Drive;

/// <summary>
/// The mechanical/analogue part of a 1541: spindle motor, stepper, read/write head, bit clock, sync detector and
/// the serial-to-parallel shift register that produces "byte ready".
///
/// Timing: the 1541 derives its bit clock from the 16 MHz master clock divided by 13/14/15/16 (density bits 3..0),
/// giving 3.25/3.5/3.75/4.0 µs per bit, i.e. 26/28/30/32 CPU cycles per GCR byte. With the standard track lengths
/// (7692/7142/6666/6250 bytes) one revolution takes 200 ms = 300 RPM. (1541 service manual; VICE rotation.c.)
/// </summary>
public sealed class DiskUnit
{
    /// <summary>16 MHz ticks per bit cell for density 0..3 (PB5-6 of VIA2).</summary>
    private static readonly int[] TicksPerBit = { 64, 60, 56, 52 };
    private const int TicksPerCpuCycle = 16;

    private GcrDisk? _disk;
    private int _halfTrack = 34; // track 18 (directory) - where the ROM expects the head after power-on anyway
    private int _density = 3;
    private int _stepperPhase;
    private int _tickAccumulator;
    private int _bitPosition;
    private int _shiftRegister;   // last bits read (for sync detection: 10 ones)
    private int _bitsInByte;
    private byte _readLatch;
    private byte _writeShift;
    private int _writeBitsLeft;
    private bool _dirty;

    /// <summary>Spindle motor (VIA2 PB2).</summary>
    public bool MotorOn { get; private set; }
    /// <summary>Drive LED (VIA2 PB3).</summary>
    public bool Led { get; private set; }
    /// <summary>Write mode (VIA2 CB2 low).</summary>
    public bool WriteMode { get; set; }
    /// <summary>Byte to be written next (VIA2 port A output while in write mode).</summary>
    public byte WriteData { get; set; }
    /// <summary>true = the disk (or the drive) is write protected. VIA2 PB4 reads 0 in that case.</summary>
    public bool WriteProtected { get; set; }
    /// <summary>True while 10 or more consecutive 1 bits pass under the head (VIA2 PB7 reads 0). Never true in write mode.</summary>
    public bool Sync => !WriteMode && MotorOn && _disk is not null && (_shiftRegister & 0x3FF) == 0x3FF;
    /// <summary>Set for exactly the cycle in which a full byte was assembled (or written).</summary>
    public bool ByteReady { get; private set; }
    /// <summary>The last byte read from the disk (VIA2 port A input).</summary>
    public byte ReadLatch => _readLatch;
    /// <summary>Current head position in half-tracks (0 = track 1).</summary>
    public int HalfTrack => _halfTrack;
    /// <summary>Current track number as shown to humans (1-based, .5 for half tracks).</summary>
    public double Track => 1 + _halfTrack / 2.0;
    /// <summary>Current bit position within the track (for tests / debuggers).</summary>
    public int BitPosition => _bitPosition;
    /// <summary>Density selected by the DOS (0 = slowest, 3 = fastest).</summary>
    public int Density => _density;
    /// <summary>Current stepper motor phase (VIA2 PB0-1).</summary>
    public int StepperPhase => _stepperPhase;
    /// <summary>True if the disk has been written to since it was inserted or saved.</summary>
    public bool IsDirty => _dirty;
    public GcrDisk? Disk => _disk;

    /// <summary>Inserts a disk (null = eject).</summary>
    public void Insert(GcrDisk? disk, bool writeProtected = false)
    {
        _disk = disk;
        WriteProtected = writeProtected;
        _dirty = false;
        _bitPosition = 0;
        _shiftRegister = 0;
        _bitsInByte = 0;
    }

    public void MarkSaved() => _dirty = false;

    public void Reset()
    {
        MotorOn = false;
        Led = false;
        WriteMode = false;
        _density = 3;
        _stepperPhase = 0;
        _tickAccumulator = 0;
        _bitsInByte = 0;
        _shiftRegister = 0;
        ByteReady = false;
    }

    /// <summary>
    /// Applies the VIA2 port B output: PB0-1 stepper phase, PB2 motor, PB3 LED, PB5-6 density.
    /// A change of the stepper phase by +1 (mod 4) moves the head one half-track inwards, -1 outwards.
    /// </summary>
    public void SetControl(byte portB)
    {
        int phase = portB & 3;
        if (phase != _stepperPhase)
        {
            int delta = (phase - _stepperPhase) & 3;
            if (delta == 1 && _halfTrack < 83) _halfTrack++;
            else if (delta == 3 && _halfTrack > 0) _halfTrack--;
            _stepperPhase = phase;
            // Keep the angular position when changing tracks (different track lengths)
            if (_disk is not null)
            {
                var t = _disk[_halfTrack];
                if (t.BitLength > 0) _bitPosition %= t.BitLength; else _bitPosition = 0;
            }
        }
        MotorOn = (portB & 0x04) != 0;
        Led = (portB & 0x08) != 0;
        _density = (portB >> 5) & 3;
    }

    /// <summary>Advances the mechanics by one 1 MHz cycle.</summary>
    public void Clock()
    {
        ByteReady = false;
        if (!MotorOn)
            return;

        _tickAccumulator += TicksPerCpuCycle;
        int period = TicksPerBit[_density];
        if (_tickAccumulator < period)
            return;
        _tickAccumulator -= period;

        var track = _disk?[_halfTrack];
        int length = track?.BitLength ?? 0;

        if (WriteMode)
        {
            if (_writeBitsLeft == 0)
            {
                _writeShift = WriteData;
                _writeBitsLeft = 8;
            }
            int bit = (_writeShift & 0x80) != 0 ? 1 : 0;
            _writeShift <<= 1;
            _writeBitsLeft--;
            if (track is not null && length > 0 && !WriteProtected)
            {
                track.WriteBit(_bitPosition, bit);
                _dirty = true;
            }
            _shiftRegister = (_shiftRegister << 1) | bit;
            if (_writeBitsLeft == 0)
                ByteReady = true;
        }
        else
        {
            int bit = (track is not null && length > 0) ? track.ReadBit(_bitPosition) : 0;
            _shiftRegister = ((_shiftRegister << 1) | bit) & 0xFFFFF;
            if ((_shiftRegister & 0x3FF) == 0x3FF)
            {
                // SYNC: the byte counter is held in reset so the next byte aligns with the end of the sync run.
                _bitsInByte = 0;
            }
            else
            {
                _bitsInByte++;
                if (_bitsInByte == 8)
                {
                    _bitsInByte = 0;
                    _readLatch = (byte)_shiftRegister;
                    ByteReady = true;
                }
            }
        }

        if (length > 0)
        {
            _bitPosition++;
            if (_bitPosition >= length) _bitPosition = 0;
        }
    }
}
