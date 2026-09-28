using NUnit.Framework;
using Tedd.Commodore.Embedding;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Tests.Embedding;

[TestFixture]
public class KeyCodeTranslationTests
{
    [TestCase("A", "KeyA")]
    [TestCase("Z", "KeyZ")]
    [TestCase("Digit1", "Digit1")]
    [TestCase("NumPad8", "Numpad8")]
    [TestCase("NumPadEnter", "NumpadEnter")]
    [TestCase("ArrowUp", "ArrowUp")]
    [TestCase("ShiftLeft", "ShiftLeft")]
    [TestCase("Enter", "Enter")]
    [TestCase("Escape", "Escape")]
    [TestCase("Backquote", "Backquote")]
    [TestCase("F1", "F1")]
    [TestCase("KeyA", "KeyA")]
    public void Avalonia_Physical_Key_Names(string physicalKey, string code)
    {
        Assert.That(AvaloniaKeyCodes.ToWebCode(physicalKey), Is.EqualTo(code));
    }

    [TestCase("None")]
    [TestCase("")]
    [TestCase(null)]
    public void Avalonia_None_Has_No_Code(string? physicalKey)
    {
        Assert.That(AvaloniaKeyCodes.ToWebCode(physicalKey), Is.Null);
    }

    // Values from glfw3.h (GLFW_KEY_*), which Silk.NET.Input.Key, OpenTK Keys and Raylib KeyboardKey share.
    [TestCase(32, "Space")]
    [TestCase(39, "Quote")]
    [TestCase(48, "Digit0")]
    [TestCase(57, "Digit9")]
    [TestCase(65, "KeyA")]
    [TestCase(90, "KeyZ")]
    [TestCase(96, "Backquote")]
    [TestCase(256, "Escape")]
    [TestCase(257, "Enter")]
    [TestCase(259, "Backspace")]
    [TestCase(265, "ArrowUp")]
    [TestCase(262, "ArrowRight")]
    [TestCase(290, "F1")]
    [TestCase(301, "F12")]
    [TestCase(320, "Numpad0")]
    [TestCase(328, "Numpad8")]
    [TestCase(335, "NumpadEnter")]
    [TestCase(340, "ShiftLeft")]
    [TestCase(344, "ShiftRight")]
    [TestCase(346, "AltRight")]
    [TestCase(348, "ContextMenu")]
    public void Glfw_Key_Codes(int glfwKey, string code)
    {
        Assert.That(GlfwKeyCodes.ToWebCode(glfwKey), Is.EqualTo(code));
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(314)]
    [TestCase(1000)]
    public void Unknown_Glfw_Keys_Have_No_Code(int glfwKey)
    {
        Assert.That(GlfwKeyCodes.ToWebCode(glfwKey), Is.Null);
    }

    [Test]
    public void Translated_Keys_Are_Bound_By_Default()
    {
        var bindings = KeyBindings.CreateDefault();
        foreach (var name in new[] { "A", "Digit1", "Enter", "ShiftLeft", "NumPad8", "ArrowUp", "F1", "Escape", "Space", "Comma" })
            Assert.That(bindings.Get(AvaloniaKeyCodes.ToWebCode(name)!), Is.Not.Null, name);
        foreach (var key in new[] { 65, 49, 257, 340, 328, 265, 290, 256, 32, 44 })
            Assert.That(bindings.Get(GlfwKeyCodes.ToWebCode(key)!), Is.Not.Null, key.ToString());
        Assert.That(GlfwKeyCodes.All.Count, Is.GreaterThan(100));
    }
}
