using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Desktop.ViewModels;

public sealed class Terminal2FiltersViewModel : ObservableObject
{
    private Terminal2Profile? _profile;
    private bool _ignoreCase = true;
    private string _profileContext = "No profile loaded · editing a session-only filter set.";
    private int _matchedRxRows;
    private int _unmatchedRxRows;
    private Terminal2Filter[] _filterSnapshot = [];
    private bool _loading;

    public ObservableCollection<Terminal2FilterEditorSlot> Slots { get; } = [];

    public bool IgnoreCase
    {
        get => _ignoreCase;
        set
        {
            if (!SetProperty(ref _ignoreCase, value))
                return;

            if (!_loading && _profile is not null)
                _profile.IgnoreCase = value;
            FiltersChanged?.Invoke();
        }
    }

    public string ProfileContext
    {
        get => _profileContext;
        private set => SetProperty(ref _profileContext, value);
    }

    public int MatchedRxRows
    {
        get => _matchedRxRows;
        private set
        {
            if (SetProperty(ref _matchedRxRows, value))
                OnPropertyChanged(nameof(MatchCountSummary));
        }
    }

    public int UnmatchedRxRows
    {
        get => _unmatchedRxRows;
        private set
        {
            if (SetProperty(ref _unmatchedRxRows, value))
                OnPropertyChanged(nameof(MatchCountSummary));
        }
    }

    public string MatchCountSummary => $"VISIBLE RX ROWS · {MatchedRxRows} MATCHED · {UnmatchedRxRows} UNMATCHED";

    public event Action? FiltersChanged;

    public Terminal2FiltersViewModel() => LoadProfile(null);

    public void LoadProfile(Terminal2Profile? profile)
    {
        _loading = true;
        try
        {
            _profile = profile;
            var filters = profile?.Filters;
            Slots.Clear();
            for (var index = 0; index < Terminal2Profile.FilterCapacity; index++)
            {
                var filter = filters is not null && index < filters.Length && filters[index] is not null
                    ? filters[index]
                    : new Terminal2Filter();
                Slots.Add(new Terminal2FilterEditorSlot(index, filter, OnFilterSlotChanged));
            }
            _filterSnapshot = Slots.Select(slot => slot.Filter).ToArray();

            IgnoreCase = profile?.IgnoreCase ?? true;
            ProfileContext = profile is null
                ? "No profile loaded · editing a session-only 12-slot filter set."
                : $"Editing {profile.Name} · changes are kept with this imported profile.";
        }
        finally
        {
            _loading = false;
        }

        FiltersChanged?.Invoke();
    }

    public int FindFirstMatch(string text) =>
        Terminal2FilterMatcher.FindFirstMatch(
            _filterSnapshot,
            text,
            offset: 0,
            ignoreCase: IgnoreCase);

    public Terminal2Filter GetFilter(int index) => Slots[index].Filter;

    public void SetMatchCounts(int matched, int unmatched)
    {
        MatchedRxRows = matched;
        UnmatchedRxRows = unmatched;
    }

    private void OnFilterSlotChanged()
    {
        if (!_loading)
            FiltersChanged?.Invoke();
    }
}

public sealed class Terminal2FilterEditorSlot : ObservableObject
{
    private readonly Action _changed;
    private string _text;
    private bool _isEnabled;
    private string _selectedMode;
    private string _foregroundHex;
    private string _backgroundHex;
    private string _macroTitle;
    private string _validationMessage = "";
    private bool _foregroundValid = true;
    private bool _backgroundValid = true;

    public int Index { get; }
    public string SlotLabel => $"F{Index + 1:00}";
    public Terminal2Filter Filter { get; }
    public ObservableCollection<string> Modes { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value ?? ""))
                return;
            SyncText();
            _changed();
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
                return;
            SyncText();
            _changed();
        }
    }

    public string SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value))
                return;
            if (value == "Starts with")
                Filter.Mode = 0;
            else if (value == "Contains")
                Filter.Mode = 1;
            _changed();
        }
    }

    public string ForegroundHex
    {
        get => _foregroundHex;
        set
        {
            var normalizedValue = value ?? "";
            if (!SetProperty(ref _foregroundHex, normalizedValue))
                return;
            _foregroundValid = TryParseColor(normalizedValue, out var argb);
            if (_foregroundValid)
            {
                Filter.ForegroundArgb = argb;
                OnPropertyChanged(nameof(ForegroundPreview));
                _changed();
            }
            RefreshValidation();
        }
    }

    public string BackgroundHex
    {
        get => _backgroundHex;
        set
        {
            var normalizedValue = value ?? "";
            if (!SetProperty(ref _backgroundHex, normalizedValue))
                return;
            _backgroundValid = TryParseColor(normalizedValue, out var argb);
            if (_backgroundValid)
            {
                Filter.BackgroundArgb = argb;
                OnPropertyChanged(nameof(BackgroundPreview));
                _changed();
            }
            RefreshValidation();
        }
    }

    public IBrush ForegroundPreview => new SolidColorBrush(ToColor(Filter.ForegroundArgb));
    public IBrush BackgroundPreview => new SolidColorBrush(ToColor(Filter.BackgroundArgb));

    /// <summary>Exact Terminal2 macro title to run when a completed RX line matches this slot.</summary>
    public string MacroTitle
    {
        get => _macroTitle;
        set
        {
            var normalizedValue = value ?? "";
            if (!SetProperty(ref _macroTitle, normalizedValue))
                return;
            Filter.Macro = normalizedValue;
            _changed();
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public Terminal2FilterEditorSlot(int index, Terminal2Filter filter, Action changed)
    {
        Index = index;
        Filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _text = filter.Text ?? "";
        _isEnabled = _text.Length > 0;
        _selectedMode = filter.Mode switch
        {
            0 => "Starts with",
            1 => "Contains",
            _ => $"Unsupported mode ({filter.Mode})"
        };
        Modes = ["Starts with", "Contains"];
        if (filter.Mode is not (0 or 1))
            Modes.Add(_selectedMode);
        _foregroundHex = FormatColor(filter.ForegroundArgb);
        _backgroundHex = FormatColor(filter.BackgroundArgb);
        _macroTitle = filter.Macro ?? "";
    }

    private void SyncText() => Filter.Text = IsEnabled ? Text : "";

    private void RefreshValidation()
    {
        var messages = new List<string>();
        if (!_foregroundValid)
            messages.Add("Foreground: use #RRGGBB or #AARRGGBB.");
        if (!_backgroundValid)
            messages.Add("Background: use #RRGGBB or #AARRGGBB.");
        ValidationMessage = string.Join(" ", messages);
    }

    private static bool TryParseColor(string value, out int argb)
    {
        argb = 0;
        if (value.Length is not (7 or 9) || value[0] != '#')
            return false;

        try
        {
            var color = Color.Parse(value);
            argb = unchecked((int)(((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string FormatColor(int argb) => $"#{unchecked((uint)argb):X8}";

    private static Color ToColor(int argb)
    {
        var value = unchecked((uint)argb);
        return Color.FromArgb(
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value);
    }
}
