using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Drive;

namespace Tedd.Commodore.Tests.Drive;

/// <summary>Tests for the 1541 mechanics model (spindle, bit clock, sync detector, byte shifter, stepper, writing).</summary>
[TestFixture]
public class DiskUnitTests
{
    private static GcrDisk DiskWithTrack(int halfTrack, byte[] bytes)
    {
        var disk = new GcrDisk();
        disk.SetTrack(halfTrack, GcrTrack.FromBytes(bytes));
        return disk;
    }

    /// <summary>Port B value: stepper phase, motor on, LED on, density.</summary>
    private static byte Control(int phase, bool motor, int density) =>
        (byte)((phase & 3) | (motor ? 0x04 : 0) | 0x08 | ((density & 3) << 5));

    private static List<byte> CollectBytes(DiskUnit unit, int cycles)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < cycles; i++)
        {
            unit.Clock();
            if (unit.ByteReady) bytes.Add(unit.ReadLatch);
        }
        return bytes;
    }

    [Test]
    public void Motor_Off_Nothing_Moves()
    {
        var unit = new DiskUnit();
        unit.Insert(DiskWithTrack(34, new byte[] { 0x55, 0x55, 0x55, 0x55 }));
        unit.SetControl(Control(0, motor: false, density: 3));
        for (int i = 0; i < 1000; i++)
        {
            unit.Clock();
            Assert.That(unit.ByteReady, Is.False);
        }
        Assert.That(unit.BitPosition, Is.EqualTo(0));
        Assert.That(unit.MotorOn, Is.False);
        Assert.That(unit.Led, Is.True);
    }

    [TestCase(3, 26)]
    [TestCase(2, 28)]
    [TestCase(1, 30)]
    [TestCase(0, 32)]
    public void Bytes_Arrive_At_The_Zone_Rate(int density, int cyclesPerByte)
    {
        // A track of $55 bytes never contains a sync, so bytes are delimited purely by the bit clock.
        var track = new byte[200];
        Array.Fill(track, (byte)0x55);
        var unit = new DiskUnit();
        unit.Insert(DiskWithTrack(34, track));
        unit.SetControl(Control(0, motor: true, density));
        int cycles = cyclesPerByte * 100;
        var bytes = CollectBytes(unit, cycles);
        Assert.That(bytes.Count, Is.EqualTo(100).Within(1), $"density {density}: one byte every {cyclesPerByte} cycles");
        Assert.That(bytes, Has.All.EqualTo(0x55));
    }

    [Test]
    public void One_Revolution_Takes_200ms_On_Every_Zone()
    {
        // Standard track lengths are chosen so that every zone spins in 200 ms at 300 RPM.
        foreach (var (halfTrack, density) in new[] { (0, 3), (34, 2), (48, 1), (60, 0) })
        {
            var disk = GcrDisk.FromD64(D64Image.CreateEmpty("TEST", "01"));
            var unit = new DiskUnit();
            unit.Insert(disk);
            // Step the head to the wanted half track from the power-on position (34).
            unit.SetControl(Control(0, motor: true, density));
            MoveHead(unit, halfTrack, density);
            Assert.That(unit.HalfTrack, Is.EqualTo(halfTrack));
            int bits = disk[halfTrack].BitLength;
            long cycles = 0;
            int startPos = unit.BitPosition;
            do
            {
                unit.Clock();
                cycles++;
            } while (unit.BitPosition != startPos || cycles < bits / 2);
            Assert.That(cycles, Is.EqualTo(200_000).Within(200), $"half track {halfTrack}: {bits} bits per revolution");
        }
    }

    private static void MoveHead(DiskUnit unit, int target, int density)
    {
        int phase = 0;
        while (unit.HalfTrack != target)
        {
            phase = unit.HalfTrack < target ? (phase + 1) & 3 : (phase + 3) & 3;
            unit.SetControl(Control(phase, motor: true, density));
        }
    }

    [Test]
    public void Sync_Aligns_The_Byte_Counter()
    {
        // 5 x $FF (sync) followed by a header id $52 (GCR of $08) and some data; the first byte after the
        // sync must come out aligned regardless of where the shifter was before.
        var bytes = new List<byte> { 0x55, 0x2A, 0x55 }; // junk before the sync
        bytes.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x52, 0xA5, 0x4A, 0x55, 0x55 });
        var unit = new DiskUnit();
        unit.Insert(DiskWithTrack(34, bytes.ToArray()));
        unit.SetControl(Control(0, motor: true, density: 3));
        var seen = new List<(byte value, bool syncBefore)>();
        bool sawSync = false;
        for (int i = 0; i < 26 * 14; i++)
        {
            unit.Clock();
            if (unit.Sync) sawSync = true;
            if (unit.ByteReady)
            {
                seen.Add((unit.ReadLatch, sawSync));
                sawSync = false;
            }
        }
        Assert.That(seen.Exists(s => s.syncBefore && s.value == 0x52), Is.True, "the first byte after the sync run is $52");
        int idx = seen.FindIndex(s => s.syncBefore && s.value == 0x52);
        Assert.That(seen[idx + 1].value, Is.EqualTo(0xA5));
        Assert.That(seen[idx + 2].value, Is.EqualTo(0x4A));
    }

    [Test]
    public void Sync_Flag_Is_Low_During_Ten_Ones_And_Not_In_Write_Mode()
    {
        var unit = new DiskUnit();
        unit.Insert(DiskWithTrack(34, new byte[] { 0x00, 0xFF, 0xFF, 0x00, 0x00 }));
        unit.SetControl(Control(0, motor: true, density: 3));
        bool everSync = false;
        for (int i = 0; i < 26 * 5; i++)
        {
            unit.Clock();
            everSync |= unit.Sync;
        }
        Assert.That(everSync, Is.True);
        unit.WriteMode = true;
        Assert.That(unit.Sync, Is.False, "no sync detection while writing");
    }

    [Test]
    public void Stepper_Moves_One_Half_Track_Per_Phase_And_Clamps()
    {
        var unit = new DiskUnit();
        unit.Insert(GcrDisk.FromD64(D64Image.CreateEmpty("T", "01")));
        Assert.That(unit.HalfTrack, Is.EqualTo(34), "power-on position is track 18");
        unit.SetControl(Control(1, true, 3));
        Assert.That(unit.HalfTrack, Is.EqualTo(35));
        unit.SetControl(Control(2, true, 3));
        Assert.That(unit.HalfTrack, Is.EqualTo(36));
        unit.SetControl(Control(1, true, 3));
        unit.SetControl(Control(0, true, 3));
        Assert.That(unit.HalfTrack, Is.EqualTo(34));
        Assert.That(unit.Track, Is.EqualTo(18.0));
        MoveHead(unit, 0, 3);
        Assert.That(unit.HalfTrack, Is.EqualTo(0));
        unit.SetControl(Control((unit.StepperPhase + 3) & 3, true, 3)); // one more step outwards is ignored
        Assert.That(unit.HalfTrack, Is.EqualTo(0));
        MoveHead(unit, 83, 0);
        Assert.That(unit.HalfTrack, Is.EqualTo(83));
        Assert.That(unit.Track, Is.EqualTo(42.5));
    }

    [Test]
    public void Density_Comes_From_PB5_PB6()
    {
        var unit = new DiskUnit();
        unit.SetControl(Control(0, true, 2));
        Assert.That(unit.Density, Is.EqualTo(2));
        unit.SetControl(Control(0, true, 0));
        Assert.That(unit.Density, Is.EqualTo(0));
    }

    [Test]
    public void Write_Mode_Writes_Bits_Into_The_Track()
    {
        var disk = DiskWithTrack(34, new byte[16]);
        var unit = new DiskUnit();
        unit.Insert(disk);
        unit.SetControl(Control(0, motor: true, density: 3));
        unit.WriteMode = true;
        unit.WriteData = 0xA5;
        int readies = 0;
        for (int i = 0; i < 26 * 2; i++)
        {
            unit.Clock();
            if (unit.ByteReady)
            {
                readies++;
                unit.WriteData = 0x3C;
            }
        }
        Assert.That(readies, Is.EqualTo(2));
        Assert.That(disk[34].ReadByte(0), Is.EqualTo(0xA5));
        Assert.That(disk[34].ReadByte(8), Is.EqualTo(0x3C));
        Assert.That(unit.IsDirty, Is.True);
        Assert.That(disk[34].Modified, Is.True);
    }

    [Test]
    public void Write_Protected_Disk_Is_Not_Modified()
    {
        var disk = DiskWithTrack(34, new byte[16]);
        var unit = new DiskUnit();
        unit.Insert(disk, writeProtected: true);
        unit.SetControl(Control(0, motor: true, density: 3));
        unit.WriteMode = true;
        unit.WriteData = 0xFF;
        for (int i = 0; i < 26 * 2; i++) unit.Clock();
        Assert.That(disk[34].ReadByte(0), Is.EqualTo(0x00));
        Assert.That(unit.IsDirty, Is.False);
    }

    [Test]
    public void Head_Position_Wraps_At_Track_End()
    {
        var unit = new DiskUnit();
        unit.Insert(DiskWithTrack(34, new byte[4])); // 32 bits
        unit.SetControl(Control(0, motor: true, density: 3));
        for (int i = 0; i < 26 * 5; i++) unit.Clock();
        Assert.That(unit.BitPosition, Is.LessThan(32));
    }

    [Test]
    public void Reset_Stops_Motor_And_Clears_State()
    {
        var unit = new DiskUnit();
        unit.Insert(DiskWithTrack(34, new byte[] { 0xFF, 0xFF }));
        unit.SetControl(Control(0, motor: true, density: 1));
        for (int i = 0; i < 100; i++) unit.Clock();
        unit.Reset();
        Assert.That(unit.MotorOn, Is.False);
        Assert.That(unit.Led, Is.False);
        Assert.That(unit.Density, Is.EqualTo(3));
        Assert.That(unit.Sync, Is.False);
    }

    [Test]
    public void Formatted_Disk_Produces_Sync_And_Header_Blocks_On_Every_Track()
    {
        var disk = GcrDisk.FromD64(D64Image.CreateEmpty("SYNCTEST", "S1"));
        var unit = new DiskUnit();
        unit.Insert(disk);
        unit.SetControl(Control(0, motor: true, density: 2));
        // Track 18: 19 sectors -> 38 sync marks and 19 header blocks (first byte after a sync = $52 = GCR($08))
        int syncRuns = 0, headers = 0, dataBlocks = 0;
        bool inSync = false, afterSync = false;
        for (int i = 0; i < 199_900; i++) // just under one revolution (7142 bytes x 28 cycles)
        {
            unit.Clock();
            if (unit.Sync && !inSync) { syncRuns++; inSync = true; afterSync = true; }
            if (!unit.Sync) inSync = false;
            if (unit.ByteReady && afterSync)
            {
                afterSync = false;
                if (unit.ReadLatch == 0x52) headers++;
                else if (unit.ReadLatch == 0x55) dataBlocks++; // GCR of $07 starts with %01010101
            }
        }
        Assert.That(syncRuns, Is.EqualTo(38).Within(1));
        Assert.That(headers, Is.EqualTo(19).Within(1));
        Assert.That(dataBlocks, Is.EqualTo(19).Within(1));
    }
}
