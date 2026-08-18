using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// The monolith's HotkeyListenerParseHotkeyTests, now the only copy: same inputs, same expected
/// outputs, on the engine's HotkeyListener. A hotkey string is user-authored config, so these
/// cases stay exactly as they were accumulated rather than re-derived from the parser.
/// </summary>
public class HotkeyListenerParseHotkeyTests
{
    // MOD_ALT=0x1, MOD_CONTROL=0x2, MOD_SHIFT=0x4, MOD_WIN=0x8 (mirrors the private
    // constants in HotkeyListener; VK codes match ASCII for A-Z/0-9, VK_F1=0x70).
    [Theory]
    [InlineData("Ctrl+Shift+F12", 0x2u | 0x4u, 0x70u + 11)]
    [InlineData("Alt+M", 0x1u, (uint)'M')]
    [InlineData("A", 0u, (uint)'A')]
    [InlineData("5", 0u, (uint)'5')]
    [InlineData("F1", 0u, 0x70u)]
    [InlineData("F24", 0u, 0x70u + 23)]
    [InlineData("Win+Alt+Ctrl+Shift+F1", 0x1u | 0x2u | 0x4u | 0x8u, 0x70u)]
    public void ParseHotkey_ValidCombos_ReturnsExpectedModifiersAndKey(string hotkey, uint expectedModifiers, uint expectedVk)
    {
        var (modifiers, vk) = HotkeyListener.ParseHotkey(hotkey);

        Assert.Equal(expectedModifiers, modifiers);
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData("A+B")]           // two non-modifier keys
    [InlineData("Ctrl+Shift")]    // no non-modifier key
    [InlineData("F25")]           // out of range function key
    [InlineData("Ctrl+@")]        // unsupported token
    public void ParseHotkey_InvalidInput_Throws(string hotkey)
        => Assert.Throws<FormatException>(() => HotkeyListener.ParseHotkey(hotkey));
}
