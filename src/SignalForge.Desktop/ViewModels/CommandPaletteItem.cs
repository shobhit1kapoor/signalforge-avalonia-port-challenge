using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SignalForge.Desktop.ViewModels;

/// <summary>
/// A host-provided action that can appear in <see cref="CommandPaletteViewModel"/>.
/// The palette never invents actions; callers supply an ICommand or an execute delegate.
/// </summary>
public sealed class CommandPaletteItem : ObservableObject
{
    private readonly ICommand? _command;
    private readonly Func<CancellationToken, Task>? _executeAsync;
    private readonly Func<bool>? _canExecute;

    public CommandPaletteItem(
        string id,
        string title,
        string category,
        ICommand command,
        string? description = null,
        string? shortcut = null,
        IEnumerable<string>? keywords = null,
        object? commandParameter = null)
        : this(id, title, category, description, shortcut, keywords)
    {
        _command = command ?? throw new ArgumentNullException(nameof(command));
        CommandParameter = commandParameter;
        _command.CanExecuteChanged += OnCanExecuteChanged;
    }

    public CommandPaletteItem(
        string id,
        string title,
        string category,
        Func<CancellationToken, Task> executeAsync,
        string? description = null,
        string? shortcut = null,
        IEnumerable<string>? keywords = null,
        Func<bool>? canExecute = null)
        : this(id, title, category, description, shortcut, keywords)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public CommandPaletteItem(
        string id,
        string title,
        string category,
        Action execute,
        string? description = null,
        string? shortcut = null,
        IEnumerable<string>? keywords = null,
        Func<bool>? canExecute = null)
        : this(id, title, category,
            _ =>
            {
                execute();
                return Task.CompletedTask;
            }, description, shortcut, keywords, canExecute)
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    private CommandPaletteItem(
        string id,
        string title,
        string category,
        string? description,
        string? shortcut,
        IEnumerable<string>? keywords)
    {
        Id = RequireText(id, nameof(id));
        Title = RequireText(title, nameof(title));
        Category = RequireText(category, nameof(category));
        Description = description?.Trim() ?? string.Empty;
        Shortcut = shortcut?.Trim() ?? string.Empty;
        Keywords = (keywords ?? []).Where(static word => !string.IsNullOrWhiteSpace(word))
            .Select(static word => word.Trim())
            .ToArray();
    }

    public string Id { get; }
    public string Title { get; }
    public string Category { get; }
    public string Description { get; }
    public string Shortcut { get; }
    public bool HasShortcut => Shortcut.Length > 0;
    public IReadOnlyList<string> Keywords { get; }
    public object? CommandParameter { get; }

    public bool CanExecute => _command?.CanExecute(CommandParameter) ?? (_canExecute?.Invoke() ?? true);

    public event EventHandler? AvailabilityChanged;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanExecute)
            return;

        if (_command is not null)
            _command.Execute(CommandParameter);
        else if (_executeAsync is not null)
            await _executeAsync(cancellationToken).ConfigureAwait(false);
    }

    internal bool Matches(string[] queryTokens)
    {
        if (queryTokens.Length == 0)
            return true;

        var haystack = SearchText;
        foreach (var token in queryTokens)
        {
            if (!haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    internal int MatchScore(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0;
        if (Title.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        return 4;
    }

    private string SearchText => _searchText ??= string.Join(' ', new[]
    {
        Title, Category, Description, Shortcut, string.Join(' ', Keywords)
    });

    private string? _searchText;

    private void OnCanExecuteChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(CanExecute));
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty value is required.", parameterName) : value.Trim();
}
