using System.Reflection;
using Avalonia.Input;
using SignalForge.Desktop.Views;

namespace SignalForge.Core.Tests.Desktop;

public sealed class MainWindowShortcutTests
{
    [Theory]
    [InlineData(Key.Tab, KeyModifiers.Control, false, "NextTab", -1)]
    [InlineData(Key.PageDown, KeyModifiers.Control, false, "NextTab", -1)]
    [InlineData(Key.PageUp, KeyModifiers.Control, false, "PreviousTab", -1)]
    [InlineData(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift, false, "PreviousTab", -1)]
    [InlineData(Key.D1, KeyModifiers.Control, false, "SelectTab", 0)]
    [InlineData(Key.D9, KeyModifiers.Control, false, "SelectTab", 8)]
    [InlineData(Key.NumPad3, KeyModifiers.Control, false, "SelectTab", 2)]
    [InlineData(Key.R, KeyModifiers.Control | KeyModifiers.Shift, false, "ResetLayout", -1)]
    [InlineData(Key.P, KeyModifiers.Control | KeyModifiers.Shift, true, "ToggleCommandPalette", -1)]
    [InlineData(Key.P, KeyModifiers.Meta | KeyModifiers.Shift, true, "ToggleCommandPalette", -1)]
    [InlineData(Key.Tab, KeyModifiers.Meta, false, "NextTab", -1)]
    public void ResolverMapsWorkspaceShortcuts(
        Key key,
        KeyModifiers modifiers,
        bool textEntryFocused,
        string expectedAction,
        int expectedTabIndex)
    {
        var actual = Resolve(key, modifiers, textEntryFocused, isMacOS: modifiers.HasFlag(KeyModifiers.Meta));

        Assert.Equal(expectedAction, actual.Action);
        Assert.Equal(expectedTabIndex, actual.TabIndex);
    }

    [Theory]
    [InlineData(Key.Tab, KeyModifiers.Control)]
    [InlineData(Key.D2, KeyModifiers.Control)]
    [InlineData(Key.R, KeyModifiers.Control | KeyModifiers.Shift)]
    public void NavigationAndResetShortcutsYieldToTextEntry(Key key, KeyModifiers modifiers)
    {
        var actual = Resolve(key, modifiers, textEntryFocused: true, isMacOS: false);

        Assert.Null(actual.Action);
        Assert.Equal(-1, actual.TabIndex);
    }

    [Theory]
    [InlineData(Key.Tab, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(Key.D2, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.R, KeyModifiers.Control)]
    [InlineData(Key.P, KeyModifiers.Control)]
    public void ResolverRequiresExactModifierCombination(Key key, KeyModifiers modifiers)
    {
        var actual = Resolve(key, modifiers, textEntryFocused: false, isMacOS: false);

        Assert.Null(actual.Action);
    }

    private static (string? Action, int TabIndex) Resolve(
        Key key,
        KeyModifiers modifiers,
        bool textEntryFocused,
        bool isMacOS)
    {
        var method = typeof(MainWindow).GetMethod(
            "ResolveWorkspaceShortcut",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Workspace shortcut resolver was not found.");
        var arguments = new object?[] { key, modifiers, textEntryFocused, isMacOS, -1 };
        var action = method.Invoke(null, arguments);
        return (action?.ToString(), (int)arguments[4]!);
    }
}
