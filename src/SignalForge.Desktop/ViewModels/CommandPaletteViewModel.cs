using System.Collections.ObjectModel;

namespace SignalForge.Desktop.ViewModels;

/// <summary>
/// Filtering and keyboard-selection state for a host-populated command palette.
/// Supply only actions that the host can actually execute.
/// </summary>
public sealed class CommandPaletteViewModel : ViewModelBase
{
    private readonly List<CommandPaletteItem> _items = [];
    private string _query = string.Empty;
    private CommandPaletteItem? _selectedItem;
    private bool _isOpen;
    private string _status = string.Empty;

    public CommandPaletteViewModel(IEnumerable<CommandPaletteItem>? items = null)
    {
        if (items is not null)
            SetItems(items);
    }

    /// <summary>Current results, ordered by relevance with stable host order for ties.</summary>
    public ObservableCollection<CommandPaletteItem> FilteredItems { get; } = [];

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
                RefreshResults();
        }
    }

    public CommandPaletteItem? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Replace the host-provided action set, for example after active features change.</summary>
    public void SetItems(IEnumerable<CommandPaletteItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var replacement = items.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in replacement)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!ids.Add(item.Id))
                throw new ArgumentException($"Duplicate command palette item id '{item.Id}'.", nameof(items));
        }

        foreach (var item in _items)
            item.AvailabilityChanged -= OnItemAvailabilityChanged;

        _items.Clear();
        foreach (var item in replacement)
        {
            _items.Add(item);
            item.AvailabilityChanged += OnItemAvailabilityChanged;
        }

        RefreshResults();
    }

    public void Open(string? initialQuery = null)
    {
        Status = string.Empty;
        Query = initialQuery ?? string.Empty;
        IsOpen = true;
        SelectFirst();
    }

    public void Close() => IsOpen = false;

    /// <summary>Move by one result; selection wraps at either end.</summary>
    public void MoveSelection(int offset)
    {
        if (FilteredItems.Count == 0 || offset == 0)
        {
            SelectedItem = null;
            return;
        }

        var index = SelectedItem is null ? -1 : FilteredItems.IndexOf(SelectedItem);
        if (index < 0)
            index = offset > 0 ? -1 : 0;

        var direction = Math.Sign(offset);
        var steps = Math.Abs((long)offset) % FilteredItems.Count;
        var nextIndex = (int)(((long)index + direction * steps + FilteredItems.Count) % FilteredItems.Count);
        SelectedItem = FilteredItems[nextIndex];
    }

    public async Task<bool> ExecuteSelectedAsync(CancellationToken cancellationToken = default)
    {
        var item = SelectedItem;
        if (item is null)
            return false;
        if (!item.CanExecute)
        {
            Status = "This action is currently unavailable.";
            return false;
        }

        try
        {
            await item.ExecuteAsync(cancellationToken);
            Status = string.Empty;
            Close();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Action cancelled.";
            return false;
        }
        catch (Exception exception)
        {
            Status = $"Action failed: {exception.Message}";
            return false;
        }
    }

    private void RefreshResults()
    {
        var normalized = Query.Trim();
        var tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = _items
            .Select((item, index) => (Item: item, Index: index))
            .Where(entry => entry.Item.Matches(tokens))
            .OrderBy(entry => entry.Item.MatchScore(normalized))
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToArray();

        FilteredItems.Clear();
        foreach (var item in matches)
            FilteredItems.Add(item);

        if (SelectedItem is null || !FilteredItems.Contains(SelectedItem))
            SelectFirst();
    }

    private void SelectFirst() => SelectedItem = FilteredItems.Count > 0 ? FilteredItems[0] : null;

    private void OnItemAvailabilityChanged(object? sender, EventArgs args)
    {
        if (ReferenceEquals(sender, SelectedItem))
            OnPropertyChanged(nameof(SelectedItem));
    }
}
