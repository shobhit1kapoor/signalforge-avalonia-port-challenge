namespace SignalForge.Desktop.ViewModels;

/// <summary>One populated slot from an imported Terminal2 macro bank.</summary>
public sealed record Terminal2MacroSlot(int Index, string Title, string Summary)
{
    public string SlotLabel => $"{Index + 1:000} · {Title}";
    public string BankAndSlotLabel => $"BANK {Index / 48 + 1} · SLOT {Index % 48 + 1:00}";
}
