using NUnit.Framework;
using Tedd.Commodore.Emulator.IO;

namespace Tedd.Commodore.Tests.IO;

[TestFixture]
public class IecBusTests
{
    [Test]
    public void Lines_Are_High_When_Nobody_Pulls()
    {
        var bus = new IecBus();
        bus.Attach("a");
        bus.Attach("b");
        Assert.That(bus.AtnLow, Is.False);
        Assert.That(bus.ClkLow, Is.False);
        Assert.That(bus.DataLow, Is.False);
    }

    [Test]
    public void WiredAnd_Any_Puller_Makes_Line_Low()
    {
        var bus = new IecBus();
        var a = bus.Attach("a");
        var b = bus.Attach("b");
        a.PullData = true;
        Assert.That(bus.DataLow, Is.True);
        b.PullData = true;
        a.PullData = false;
        Assert.That(bus.DataLow, Is.True, "still held by b");
        b.PullData = false;
        Assert.That(bus.DataLow, Is.False);
    }

    [Test]
    public void Changed_Event_Only_On_Real_Changes()
    {
        var bus = new IecBus();
        var a = bus.Attach("a");
        var b = bus.Attach("b");
        int changes = 0;
        bus.Changed += () => changes++;
        a.PullClk = true;
        b.PullClk = true;   // no change on the bus
        a.PullClk = false;  // still low through b
        Assert.That(changes, Is.EqualTo(1));
        b.PullClk = false;
        Assert.That(changes, Is.EqualTo(2));
        a.Set(true, true, true);
        Assert.That(changes, Is.EqualTo(3));
        Assert.That(bus.AtnLow && bus.ClkLow && bus.DataLow, Is.True);
    }

    [Test]
    public void Detach_Releases_Lines()
    {
        var bus = new IecBus();
        var a = bus.Attach("a");
        a.PullAtn = true;
        bus.Detach(a);
        Assert.That(bus.AtnLow, Is.False);
        Assert.That(bus.Ports, Is.Empty);
    }
}
