using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Traces;

namespace SignalForge.Desktop.ViewModels;

/// <summary>
/// A line-oriented view of RX data for Terminal2 migration workflows. Exact bytes stay
/// in the trace; this projection only adds line boundaries, search, filters, and view freeze.
/// </summary>
public partial class Terminal2ReceiveHistoryViewModel : ViewModelBase
{
    private const int MaximumVisibleLines = 5000;
    private const int MaximumFrozenPendingLines = 10000;
    private readonly Terminal2FiltersViewModel _filters;
    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.Ordinal);
    private readonly Queue<Terminal2ReceiveLineRow> _frozenLines = new();
    private long _activitySequence;
    private long _trimmedLines;
    private long _droppedFrozenLines;

    [ObservableProperty]
    private Terminal2ReceiveLineRow? _selectedLine;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _searchIgnoreCase = true;

    [ObservableProperty]
    private string _stopAtText = "";

    [ObservableProperty]
    private bool _stopAtEnabled;

    [ObservableProperty]
    private bool _isFrozen;

    [ObservableProperty]
    private string _currentLineSource = "—";

    [ObservableProperty]
    private string _currentLinePreview = "Waiting for RX bytes.";

    [ObservableProperty]
    private string _currentLineHex = "";

    [ObservableProperty]
    private string _currentLineStatus = "The partial line updates as chunks arrive; CR/LF boundaries remain explicit.";

    [ObservableProperty]
    private string _status = "Connect a byte transport or load a trace to build line history. Exact RX bytes remain in the event timeline.";

    public ObservableCollection<Terminal2ReceiveLineRow> Lines { get; } = [];

    /// <summary>Raised when a completed line is selected for command-editor recall.</summary>
    public event Action<string>? CommandTextRecallRequested;

    /// <summary>Raised for a matching live RX line whose first matching profile filter names a macro.</summary>
    public event Action<string, int>? FilterMacroTriggered;

    public bool CanSearch => !string.IsNullOrEmpty(SearchText) && Lines.Count > 0;
    public string FreezeButtonText => IsFrozen ? "RESUME VIEW" : "FREEZE VIEW";
    public string LineCountText => $"{Lines.Count:N0} COMPLETE LINES · {_trimmedLines:N0} TRIMMED · {_frozenLines.Count:N0} PENDING";
    public string SelectedLineBytes => SelectedLine?.Hex ?? "Select a receive line to inspect its exact bytes.";
    public string SelectedLineMeta => SelectedLine?.Metadata ?? "No receive line selected.";

    public Terminal2ReceiveHistoryViewModel(Terminal2FiltersViewModel filters)
    {
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));
        _filters.FiltersChanged += ReapplyFilters;
    }

    partial void OnSelectedLineChanged(Terminal2ReceiveLineRow? value)
    {
        OnPropertyChanged(nameof(SelectedLineBytes));
        OnPropertyChanged(nameof(SelectedLineMeta));
        OnPropertyChanged(nameof(CanRecallSelectedLine));
        if (value is not null)
            CommandTextRecallRequested?.Invoke(value.Text);
        RecallSelectedLineCommand.NotifyCanExecuteChanged();
    }

    public bool CanRecallSelectedLine => SelectedLine is not null;

    [RelayCommand(CanExecute = nameof(CanRecallSelectedLine))]
    private void RecallSelectedLine()
    {
        if (SelectedLine is { } line)
            CommandTextRecallRequested?.Invoke(line.Text);
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanSearch));
        SearchNextCommand.NotifyCanExecuteChanged();
        SearchPreviousCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFrozenChanged(bool value)
    {
        OnPropertyChanged(nameof(FreezeButtonText));
        if (!value)
            ResumeProjection();
        else
            Status = $"Display frozen · RX continues recording · {_frozenLines.Count:N0} completed line(s) pending.";
    }

    public void Append(string source, TraceEvent traceEvent, bool triggerProfileMacros = false)
    {
        if (traceEvent.Kind != TraceEventKind.Receive || traceEvent.Payload is not { Length: > 0 } bytes)
            return;

        var state = GetSource(source);
        state.RememberTimestamp(traceEvent.MonotonicTicks, traceEvent.TimestampUtc);
        var result = state.Assembler.Append(bytes, traceEvent.MonotonicTicks);
        foreach (var completed in result.Lines)
        {
            state.CompletedLineCount = completed.LineNumber;
            PublishCompleted(CreateRow(source, completed, state, triggerProfileMacros));
        }

        UpdateCurrentLine(source, state);
        if (result.Issue is { } issue)
            Status = $"{source} · receive-line assembly stopped: {issue.Message} Exact trace bytes are retained.";
        else if (!IsFrozen)
            Status = $"{source} · received {bytes.Length:N0} exact byte(s) · {Lines.Count:N0} complete line(s).";
    }

    public void CompleteSource(string source)
    {
        if (!_sources.TryGetValue(source, out var state))
            return;

        var result = state.Assembler.Complete();
        foreach (var completed in result.Lines)
        {
            state.CompletedLineCount = completed.LineNumber;
            PublishCompleted(CreateRow(source, completed, state, triggerProfileMacros: false));
        }
        state.LastSnapshot = state.Assembler.GetSnapshot();

        if (StringComparer.Ordinal.Equals(CurrentLineSource, source))
            ClearCurrentLine();

        if (result.Issue is { } issue)
            Status = $"{source} · receive-line assembly ended with an error: {issue.Message} Exact trace bytes are retained.";
        else if (!IsFrozen)
            Status = $"{source} · receive stream ended · {Lines.Count:N0} complete line(s).";
    }

    public void ToggleFreezeFromMacro() => IsFrozen = !IsFrozen;

    [RelayCommand]
    private void ToggleFreeze() => IsFrozen = !IsFrozen;

    [RelayCommand]
    private void ClearHistory()
    {
        Lines.Clear();
        _frozenLines.Clear();
        _trimmedLines = 0;
        _droppedFrozenLines = 0;
        SelectedLine = null;
        ClearCurrentLine();
        OnPropertyChanged(nameof(LineCountText));
        Status = "Visible receive history cleared. Active trace capture and line assembly continue.";
        NotifySearchStates();
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void SearchNext() => FindMatch(direction: 1);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void SearchPrevious() => FindMatch(direction: -1);

    private void PublishCompleted(Terminal2ReceiveLineRow row)
    {
        if (IsFrozen)
        {
            if (_frozenLines.Count < MaximumFrozenPendingLines)
                _frozenLines.Enqueue(row);
            else
            {
                _droppedFrozenLines++;
                Status = $"Frozen display queue reached {MaximumFrozenPendingLines:N0} lines; {_droppedFrozenLines:N0} display line(s) omitted. Exact trace bytes remain intact.";
            }
            OnPropertyChanged(nameof(LineCountText));
            return;
        }

        AddVisible(row);
        if (StopAtEnabled && StopAtText.Length > 0 && Contains(row.Text, StopAtText, _filters.IgnoreCase))
        {
            IsFrozen = true;
            Status = $"Stop-at matched line {row.LineNumber} in {row.Source}: “{StopAtText}”. RX continues recording while the line display is frozen.";
        }
    }

    private void AddVisible(Terminal2ReceiveLineRow row)
    {
        Lines.Add(row);
        while (Lines.Count > MaximumVisibleLines)
        {
            var removed = Lines[0];
            Lines.RemoveAt(0);
            _trimmedLines++;
            if (ReferenceEquals(SelectedLine, removed))
                SelectedLine = null;
        }
        OnPropertyChanged(nameof(LineCountText));
        NotifySearchStates();
    }

    private void ResumeProjection()
    {
        while (_frozenLines.TryDequeue(out var row))
            AddVisible(row);

        var latest = _sources
            .Where(pair => pair.Value.LastSnapshot is { Bytes.IsEmpty: false })
            .OrderByDescending(pair => pair.Value.LastActivity)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(latest.Key))
            UpdateCurrentLine(latest.Key, latest.Value);
        else
            ClearCurrentLine();

        Status = _droppedFrozenLines == 0
            ? $"Display resumed · RX trace was recorded during the freeze · {Lines.Count:N0} line(s) visible."
            : $"Display resumed · {_droppedFrozenLines:N0} frozen-view line(s) were omitted after the bounded queue filled; exact trace bytes remain intact.";
        OnPropertyChanged(nameof(LineCountText));
    }

    private void UpdateCurrentLine(string source, SourceState state)
    {
        var snapshot = state.Assembler.GetSnapshot();
        state.LastSnapshot = snapshot;
        state.LastActivity = Interlocked.Increment(ref _activitySequence);
        if (IsFrozen)
            return;
        if (snapshot.Bytes.IsEmpty)
        {
            if (StringComparer.Ordinal.Equals(CurrentLineSource, source))
                ClearCurrentLine();
            return;
        }

        CurrentLineSource = source;
        CurrentLinePreview = FormatContent(snapshot.Bytes.Span);
        CurrentLineHex = FormatHex(snapshot.Bytes.Span);
        var carriageReturnStatus = snapshot.IsCarriageReturnPending ? " · CR awaiting possible LF" : "";
        CurrentLineStatus = $"Partial line {state.CompletedLineCount + 1} · {snapshot.Bytes.Length:N0} buffered byte(s){carriageReturnStatus}";
    }

    private void ClearCurrentLine()
    {
        CurrentLineSource = "—";
        CurrentLinePreview = "No incomplete receive line.";
        CurrentLineHex = "";
        CurrentLineStatus = "The exact completed line bytes are available below and in the trace.";
    }

    private Terminal2ReceiveLineRow CreateRow(
        string source,
        Terminal2ReceiveLine line,
        SourceState state,
        bool triggerProfileMacros)
    {
        var text = FormatContent(line.Content.Span);
        var matchIndex = _filters.FindFirstMatch(text);
        if (triggerProfileMacros && matchIndex >= 0)
        {
            var macroTitle = _filters.GetFilter(matchIndex).Macro;
            if (!string.IsNullOrWhiteSpace(macroTitle))
                FilterMacroTriggered?.Invoke(macroTitle, matchIndex);
        }

        return new Terminal2ReceiveLineRow(
            source,
            line.LineNumber,
            line.Content.Span,
            line.Bytes.Span,
            line.Ending,
            state.TimestampFor(line.LastMonotonicTicks),
            matchIndex >= 0 ? matchIndex : null,
            matchIndex >= 0 ? FilterForeground(matchIndex) : null,
            matchIndex >= 0 ? FilterBackground(matchIndex) : null);
    }

    private SourceState GetSource(string source)
    {
        if (_sources.TryGetValue(source, out var state))
            return state;

        state = new SourceState();
        _sources.Add(source, state);
        return state;
    }

    private void FindMatch(int direction)
    {
        if (Lines.Count == 0 || SearchText.Length == 0)
            return;

        var start = SelectedLine is null ? (direction > 0 ? -1 : Lines.Count) : Lines.IndexOf(SelectedLine);
        for (var step = 1; step <= Lines.Count; step++)
        {
            var index = (start + direction * step) % Lines.Count;
            if (index < 0)
                index += Lines.Count;
            if (!Contains(Lines[index].Text, SearchText, SearchIgnoreCase))
                continue;

            SelectedLine = Lines[index];
            Status = $"Found “{SearchText}” at {SelectedLine.Metadata}.";
            return;
        }

        Status = $"No visible line contains “{SearchText}”.";
    }

    private void ReapplyFilters()
    {
        foreach (var row in Lines.Concat(_frozenLines))
            ApplyFilter(row);
    }

    private void ApplyFilter(Terminal2ReceiveLineRow row)
    {
        var slot = _filters.FindFirstMatch(row.Text);
        row.SetFilterMatch(slot >= 0 ? slot : null,
            slot >= 0 ? FilterForeground(slot) : null,
            slot >= 0 ? FilterBackground(slot) : null);
    }

    private IBrush FilterForeground(int slot) => ToBrush(_filters.GetFilter(slot).ForegroundArgb);
    private IBrush FilterBackground(int slot) => ToBrush(_filters.GetFilter(slot).BackgroundArgb);

    private static IBrush ToBrush(int argb)
    {
        var value = unchecked((uint)argb);
        return new SolidColorBrush(Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value));
    }

    private static bool Contains(string candidate, string text, bool ignoreCase) =>
        candidate.Contains(text, ignoreCase ? StringComparison.CurrentCultureIgnoreCase : StringComparison.CurrentCulture);

    private static string FormatContent(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (var value in bytes)
        {
            if (value == (byte)'\t')
                builder.Append('⇥');
            else if (value is >= 0x20 and <= 0x7E || value >= 0xA0)
                builder.Append((char)value);
            else
                builder.Append("\\x").Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static string FormatHex(ReadOnlySpan<byte> bytes) =>
        string.Join(' ', Convert.ToHexString(bytes).Chunk(2).Select(chunk => new string(chunk)));

    private void NotifySearchStates()
    {
        OnPropertyChanged(nameof(CanSearch));
        SearchNextCommand.NotifyCanExecuteChanged();
        SearchPreviousCommand.NotifyCanExecuteChanged();
    }

    private sealed class SourceState
    {
        private const int MaximumRememberedTimestamps = 4096;
        private readonly Queue<long> _timestampOrder = new();
        private readonly Dictionary<long, DateTimeOffset> _timestamps = [];

        public Terminal2ReceiveLineAssembler Assembler { get; } = new();
        public int CompletedLineCount { get; set; }
        public long LastActivity { get; set; }
        public Terminal2ReceiveLineSnapshot? LastSnapshot { get; set; }

        public void RememberTimestamp(long ticks, DateTimeOffset timestamp)
        {
            if (!_timestamps.ContainsKey(ticks))
                _timestampOrder.Enqueue(ticks);
            _timestamps[ticks] = timestamp;
            while (_timestampOrder.Count > MaximumRememberedTimestamps)
                _timestamps.Remove(_timestampOrder.Dequeue());
        }

        public DateTimeOffset TimestampFor(long ticks) =>
            _timestamps.TryGetValue(ticks, out var timestamp) ? timestamp : DateTimeOffset.UtcNow;
    }
}

public sealed class Terminal2ReceiveLineRow : ObservableObject
{
    private int? _matchedFilterSlot;
    private IBrush? _filterForeground;
    private IBrush? _filterBackground;

    public string Source { get; }
    public int LineNumber { get; }
    public string Text { get; }
    public string Hex { get; }
    public string Timestamp { get; }
    public string EndingLabel { get; }
    public string Metadata => $"{Source} · line {LineNumber} · {Timestamp} · {EndingLabel}";
    public string FilterLabel => _matchedFilterSlot is { } slot ? $"F{slot + 1:00}" : "";
    public IBrush? FilterForeground => _filterForeground;
    public IBrush? FilterBackground => _filterBackground;

    internal Terminal2ReceiveLineRow(
        string source,
        int lineNumber,
        ReadOnlySpan<byte> content,
        ReadOnlySpan<byte> exactBytes,
        Terminal2ReceiveLineEnding ending,
        DateTimeOffset timestamp,
        int? matchedFilterSlot,
        IBrush? foreground,
        IBrush? background)
    {
        Source = source;
        LineNumber = lineNumber;
        Text = FormatContent(content);
        Hex = FormatHex(exactBytes);
        Timestamp = timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        EndingLabel = ending switch
        {
            Terminal2ReceiveLineEnding.CarriageReturn => "CR",
            Terminal2ReceiveLineEnding.LineFeed => "LF",
            Terminal2ReceiveLineEnding.CarriageReturnLineFeed => "CRLF",
            _ => "EOF"
        };
        _matchedFilterSlot = matchedFilterSlot;
        _filterForeground = foreground;
        _filterBackground = background;
    }

    public void SetFilterMatch(int? slot, IBrush? foreground, IBrush? background)
    {
        if (_matchedFilterSlot != slot)
        {
            _matchedFilterSlot = slot;
            OnPropertyChanged(nameof(FilterLabel));
        }
        if (!ReferenceEquals(_filterForeground, foreground))
        {
            _filterForeground = foreground;
            OnPropertyChanged(nameof(FilterForeground));
        }
        if (!ReferenceEquals(_filterBackground, background))
        {
            _filterBackground = background;
            OnPropertyChanged(nameof(FilterBackground));
        }
    }

    private static string FormatContent(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (var value in bytes)
        {
            if (value == (byte)'\t') builder.Append('⇥');
            else if (value is >= 0x20 and <= 0x7E || value >= 0xA0) builder.Append((char)value);
            else builder.Append("\\x").Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static string FormatHex(ReadOnlySpan<byte> bytes) =>
        string.Join(' ', Convert.ToHexString(bytes).Chunk(2).Select(chunk => new string(chunk)));
}
