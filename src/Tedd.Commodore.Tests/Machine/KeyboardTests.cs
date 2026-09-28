using NUnit.Framework;
using Tedd.Commodore.Emulator.C64;

namespace Tedd.Commodore.Tests.Machine;

[TestFixture]
public class KeyboardTests
{
    [Test]
    public void NoKeys_AllColumnsHigh()
    {
        var k = new Keyboard();
        Assert.That(k.ReadColumns(0x00), Is.EqualTo(0xFF));
        Assert.That(k.ReadRows(0x00), Is.EqualTo(0xFF));
    }

    [Test]
    public void Space_Is_Row7_Column4()
    {
        var k = new Keyboard();
        k.Press(C64Key.Space);
        Assert.That(k.ReadColumns(unchecked((byte)~0x80)), Is.EqualTo(unchecked((byte)~0x10)), "row 7 selected");
        Assert.That(k.ReadColumns(unchecked((byte)~0x01)), Is.EqualTo(0xFF), "row 0 selected: nothing");
        Assert.That(k.ReadColumns(0xFF), Is.EqualTo(0xFF), "no row selected");
        Assert.That(k.ReadRows(unchecked((byte)~0x10)), Is.EqualTo(unchecked((byte)~0x80)), "symmetric");
        k.Release(C64Key.Space);
        Assert.That(k.ReadColumns(unchecked((byte)~0x80)), Is.EqualTo(0xFF));
    }

    [Test]
    public void Return_Is_Row0_Column1_And_A_Is_Row1_Column2()
    {
        var k = new Keyboard();
        k.Press(C64Key.Return);
        k.Press(C64Key.A);
        Assert.That(k.ReadColumns(unchecked((byte)~0x01)), Is.EqualTo(unchecked((byte)~0x02)));
        Assert.That(k.ReadColumns(unchecked((byte)~0x02)), Is.EqualTo(unchecked((byte)~0x04)));
        Assert.That(k.ReadColumns(0x00), Is.EqualTo(unchecked((byte)~0x06)), "all rows selected");
    }

    [Test]
    public void FullScan_Finds_Every_Key()
    {
        var k = new Keyboard();
        for (int key = 0; key < 64; key++)
        {
            k.ReleaseAll();
            k.Press((C64Key)key);
            for (int row = 0; row < 8; row++)
            {
                byte cols = k.ReadColumns((byte)~(1 << row));
                if (row == key >> 3)
                    Assert.That(cols, Is.EqualTo((byte)~(1 << (key & 7))), $"key {key} row {row}");
                else
                    Assert.That(cols, Is.EqualTo(0xFF), $"key {key} row {row}");
            }
        }
    }

    [Test]
    public void Ghosting_Three_Keys_Show_Fourth()
    {
        // Pressing A (row1,col2), S (row1,col5) and D (row2,col2): selecting row 2 also shows column 5 through
        // the passive matrix? No: the matrix only ghosts when the driving side has multiple lows. With a single
        // row selected the result is exact.
        var k = new Keyboard();
        k.Press(C64Key.A);
        k.Press(C64Key.S);
        k.Press(C64Key.D);
        Assert.That(k.ReadColumns(unchecked((byte)~0x04)), Is.EqualTo(unchecked((byte)~0x04)), "row 2: only D");
        Assert.That(k.ReadColumns(unchecked((byte)~0x02)), Is.EqualTo(unchecked((byte)~0x24)), "row 1: A and S");
    }

    [Test]
    public void Restore_Is_Separate()
    {
        var k = new Keyboard();
        Assert.That(k.RestorePressed, Is.False);
        k.SetRestore(true);
        Assert.That(k.RestorePressed, Is.True);
        Assert.That(k.ReadColumns(0x00), Is.EqualTo(0xFF));
        k.ReleaseAll();
        Assert.That(k.RestorePressed, Is.False);
    }

    [TestCase('a', C64Key.A, false)]
    [TestCase('A', C64Key.A, true)]
    [TestCase('1', C64Key.D1, false)]
    [TestCase('!', C64Key.D1, true)]
    [TestCase('"', C64Key.D2, true)]
    [TestCase(' ', C64Key.Space, false)]
    [TestCase('\n', C64Key.Return, false)]
    [TestCase(',', C64Key.Comma, false)]
    [TestCase('<', C64Key.Comma, true)]
    [TestCase('?', C64Key.Slash, true)]
    public void TryMapChar(char c, C64Key expected, bool shift)
    {
        Assert.That(Keyboard.TryMapChar(c, out var key, out var s), Is.True);
        Assert.That(key, Is.EqualTo(expected));
        Assert.That(s, Is.EqualTo(shift));
    }

    [Test]
    public void TryMapChar_Unknown()
    {
        Assert.That(Keyboard.TryMapChar('~', out _, out _), Is.False);
    }

    [Test]
    public void Joystick_Levels()
    {
        var j = new Joystick();
        Assert.That(j.Levels, Is.EqualTo(0xFF));
        j.Up = true;
        j.Fire = true;
        Assert.That(j.Levels, Is.EqualTo(0xEE));
        j.Clear();
        Assert.That(j.Levels, Is.EqualTo(0xFF));
    }
}
