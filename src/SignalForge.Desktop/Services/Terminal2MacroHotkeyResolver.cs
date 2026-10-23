using Avalonia.Input;

namespace SignalForge.Desktop.Services;

/// <summary>Maps Terminal2's F1–F12 modifier grid into the selected 48-slot macro bank.</summary>
public static class Terminal2MacroHotkeyResolver
{
    public const int BankCount = 4;
    public const int SlotsPerBank = 48;

    public static bool TryResolve(Key key, KeyModifiers modifiers, int selectedBank, out int macroIndex)
    {
        macroIndex = -1;
        if (selectedBank is < 1 or > BankCount || key < Key.F1 || key > Key.F12)
            return false;

        var modifierRow = modifiers switch
        {
            KeyModifiers.None => 0,
            KeyModifiers.Shift => 1,
            KeyModifiers.Control => 2,
            KeyModifiers.Alt => 3,
            _ => -1,
        };
        if (modifierRow < 0)
            return false;

        var functionKeyOffset = (int)key - (int)Key.F1;
        macroIndex = ((selectedBank - 1) * SlotsPerBank) + (modifierRow * 12) + functionKeyOffset;
        return true;
    }
}
