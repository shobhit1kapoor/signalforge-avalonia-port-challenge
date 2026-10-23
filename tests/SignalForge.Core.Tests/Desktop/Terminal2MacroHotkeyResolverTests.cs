using Avalonia.Input;
using SignalForge.Desktop.Services;

namespace SignalForge.Core.Tests.Desktop;

public sealed class Terminal2MacroHotkeyResolverTests
{
    [Theory]
    [InlineData(Key.F1, KeyModifiers.None, 1, 0)]
    [InlineData(Key.F12, KeyModifiers.None, 1, 11)]
    [InlineData(Key.F1, KeyModifiers.Shift, 1, 12)]
    [InlineData(Key.F12, KeyModifiers.Control, 1, 35)]
    [InlineData(Key.F1, KeyModifiers.Alt, 1, 36)]
    [InlineData(Key.F12, KeyModifiers.Shift, 2, 71)]
    [InlineData(Key.F12, KeyModifiers.Control, 3, 131)]
    [InlineData(Key.F12, KeyModifiers.Alt, 4, 191)]
    public void Maps_function_key_and_modifier_row_into_selected_bank(
        Key key,
        KeyModifiers modifiers,
        int bank,
        int expectedIndex)
    {
        Assert.True(Terminal2MacroHotkeyResolver.TryResolve(key, modifiers, bank, out var actualIndex));
        Assert.Equal(expectedIndex, actualIndex);
    }

    [Theory]
    [InlineData(Key.Enter, KeyModifiers.None, 1)]
    [InlineData(Key.F1, KeyModifiers.Control | KeyModifiers.Shift, 1)]
    [InlineData(Key.F1, KeyModifiers.Meta, 1)]
    [InlineData(Key.F1, KeyModifiers.None, 0)]
    [InlineData(Key.F1, KeyModifiers.None, 5)]
    public void Rejects_non_hotkeys_and_invalid_banks(Key key, KeyModifiers modifiers, int bank)
    {
        Assert.False(Terminal2MacroHotkeyResolver.TryResolve(key, modifiers, bank, out var actualIndex));
        Assert.Equal(-1, actualIndex);
    }
}
