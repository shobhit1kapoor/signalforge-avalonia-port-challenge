using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SignalForge.Core.Forensics;
using SignalForge.Core.Traces;

namespace SignalForge.Desktop.ViewModels;

public partial class TraceComparisonViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _expectedTracePath = "";

    [ObservableProperty]
    private string _actualTracePath = "";

    [ObservableProperty]
    private string _status = "Load two SIGNALFORGE JSONL traces to compare ordered events, exact bytes and relative timing.";

    [ObservableProperty]
    private string _firstDivergenceSummary = "First divergence: waiting for a comparison.";

    [ObservableProperty]
    private string _firstTimingDivergenceSummary = "Timing divergence: waiting for a comparison.";

    [ObservableProperty]
    private string _rootCauseHint = "Evidence assessment: waiting for a comparison.";

    [ObservableProperty]
    private IBrush _statusBrush = new SolidColorBrush(Color.Parse("#748296"));

    [ObservableProperty]
    private bool _isComparing;

    [ObservableProperty]
    private TraceComparisonRow? _selectedRow;

    public ObservableCollection<TraceComparisonRow> Rows { get; } = [];
    public bool CanCompare => !IsComparing;
    public bool CanOpenExpectedEvent => SelectedRow?.CanOpenExpectedEvent == true;
    public bool CanOpenActualEvent => SelectedRow?.CanOpenActualEvent == true;

    public event EventHandler<TraceComparisonEventRequestedEventArgs>? TraceEventRequested;

    partial void OnIsComparingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCompare));
        CompareCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRowChanged(TraceComparisonRow? value)
    {
        OnPropertyChanged(nameof(CanOpenExpectedEvent));
        OnPropertyChanged(nameof(CanOpenActualEvent));
        OpenExpectedEventCommand.NotifyCanExecuteChanged();
        OpenActualEventCommand.NotifyCanExecuteChanged();

        if (value is null || string.Equals(value.State, "UNCHANGED", StringComparison.Ordinal))
            return;

        // Differences open the actual event when one exists; missing rows open their expected event.
        if (!RequestNavigation(value.ActualTraceId, value.ActualEventSequence, "actual"))
            RequestNavigation(value.ExpectedTraceId, value.ExpectedEventSequence, "expected");
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (IsComparing)
            return;
        IsComparing = true;
        SelectedRow = null;
        Rows.Clear();
        FirstDivergenceSummary = "First divergence: comparing traces…";
        FirstTimingDivergenceSummary = "Timing divergence: comparing traces…";
        RootCauseHint = "Evidence assessment: comparing traces…";
        try
        {
            if (string.IsNullOrWhiteSpace(ExpectedTracePath) || string.IsNullOrWhiteSpace(ActualTracePath))
                throw new ArgumentException("Enter both the expected and actual trace paths.");

            var expectedPath = Path.GetFullPath(ExpectedTracePath.Trim());
            var actualPath = Path.GetFullPath(ActualTracePath.Trim());
            var expected = await TraceReader.LoadAsync(expectedPath);
            var actual = await TraceReader.LoadAsync(actualPath);
            var expectedObservations = expected.Events.Select((item, index) => ToObservation(item, index));
            var actualObservations = actual.Events.Select((item, index) => ToObservation(item, index));
            var comparison = ForensicSessionComparer.Compare(expectedObservations, actualObservations);
            comparison = NormalizeTimingComparison(
                comparison,
                expected.Metadata.StopwatchFrequency,
                actual.Metadata.StopwatchFrequency);

            var timelineDurationMs = comparison.Observations
                .SelectMany(item => new[]
                {
                    ToMilliseconds(item.ExpectedRelativeTicks, expected.Metadata.StopwatchFrequency),
                    ToMilliseconds(item.ActualRelativeTicks, actual.Metadata.StopwatchFrequency)
                })
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty(0)
                .Max();

            const int maximumRows = 5000;
            foreach (var item in comparison.Observations.Take(maximumRows))
                Rows.Add(TraceComparisonRow.From(
                    item,
                    expected.Metadata.StopwatchFrequency,
                    actual.Metadata.StopwatchFrequency,
                    timelineDurationMs,
                    expectedPath,
                    GetEventSequence(expected.Events, item.ExpectedSourceIndex),
                    actualPath,
                    GetEventSequence(actual.Events, item.ActualSourceIndex)));

            FirstDivergenceSummary = BuildFirstDivergenceSummary(
                comparison, expected, actual, expectedPath, actualPath);
            FirstTimingDivergenceSummary = BuildFirstTimingDivergenceSummary(
                comparison, expected, actual, expectedPath, actualPath);
            RootCauseHint = BuildRootCauseHint(
                comparison, expected, actual, expectedPath, actualPath);

            var shown = Math.Min(maximumRows, comparison.Observations.Count);
            Status = $"{comparison.Observations.Count} aligned observation(s) · {comparison.UnchangedCount} unchanged · {comparison.ChangedCount} changed · {comparison.MissingCount} missing · {comparison.ExtraCount} extra. Showing {shown}.\nEXPECTED · {expected.Metadata.SessionName} · {expected.Metadata.Transport}\nACTUAL · {actual.Metadata.SessionName} · {actual.Metadata.Transport}";
            StatusBrush = comparison.ChangedCount + comparison.MissingCount + comparison.ExtraCount == 0
                ? new SolidColorBrush(Color.Parse("#56D4C8"))
                : new SolidColorBrush(Color.Parse("#E3B869"));
        }
        catch (Exception exception)
        {
            Status = $"TRACE COMPARISON FAILED · {exception.Message}";
            FirstDivergenceSummary = "First divergence unavailable because comparison failed.";
            FirstTimingDivergenceSummary = "Timing divergence unavailable because comparison failed.";
            RootCauseHint = "Evidence assessment unavailable because comparison failed.";
            StatusBrush = new SolidColorBrush(Color.Parse("#DE7979"));
        }
        finally
        {
            IsComparing = false;
        }
    }

    private static ForensicObservation ToObservation(TraceEvent traceEvent, int index) => new(
        traceEvent.Detail ?? traceEvent.Kind.ToString(),
        traceEvent.Kind.ToString(),
        traceEvent.MonotonicTicks,
        traceEvent.Payload ?? Array.Empty<byte>(),
        traceEvent.Direction,
        index);

    private static ForensicSessionComparison NormalizeTimingComparison(
        ForensicSessionComparison comparison,
        long expectedFrequency,
        long actualFrequency)
    {
        var observations = comparison.Observations.Select(item =>
        {
            var differences = item.Differences
                .Where(difference => !difference.StartsWith("Relative timing differs by ", StringComparison.Ordinal))
                .ToList();
            var expectedMs = ToMilliseconds(item.ExpectedRelativeTicks, expectedFrequency);
            var actualMs = ToMilliseconds(item.ActualRelativeTicks, actualFrequency);
            var deltaMs = expectedMs is { } expectedTime && actualMs is { } actualTime
                ? actualTime - expectedTime
                : (double?)null;
            if (deltaMs is { } delta && Math.Abs(delta) > 0.000001d)
                differences.Add($"Relative timing differs by {Math.Abs(delta).ToString("N2", CultureInfo.InvariantCulture)} ms (actual is {(delta > 0 ? "later" : "earlier")}).");

            var kind = item.Kind is ForensicChangeKind.MissingFromActual or ForensicChangeKind.ExtraInActual
                ? item.Kind
                : differences.Count == 0 ? ForensicChangeKind.Unchanged : ForensicChangeKind.Changed;
            return item with
            {
                Kind = kind,
                Differences = differences,
                RelativeTimingDeltaTicks = expectedFrequency == actualFrequency
                    ? item.RelativeTimingDeltaTicks
                    : null
            };
        }).ToArray();

        return new(observations, comparison.ExpectedOriginTicks, comparison.ActualOriginTicks);
    }

    private static long? GetEventSequence(IReadOnlyList<TraceEvent> events, int? sourceIndex) =>
        sourceIndex is { } index && index >= 0 && index < events.Count
            ? events[index].Sequence
            : null;

    private static string BuildFirstDivergenceSummary(
        ForensicSessionComparison comparison,
        TraceDocument expected,
        TraceDocument actual,
        string expectedPath,
        string actualPath)
    {
        if (comparison.FirstDifferenceIndex is not { } index)
            return "FIRST DIVERGENCE · None; aligned labels, directions, bytes and relative times match.";

        var item = comparison.Observations[index];
        var expectedSequence = GetEventSequence(expected.Events, item.ExpectedSourceIndex);
        var actualSequence = GetEventSequence(actual.Events, item.ActualSourceIndex);
        return $"FIRST DIVERGENCE · Observation {index} · {item.ExpectedLabel ?? "missing"} → {item.ActualLabel ?? "missing"} · " +
            $"Expected {FormatTraceReference(expectedPath, expectedSequence)} · Actual {FormatTraceReference(actualPath, actualSequence)} · " +
            string.Join(" ", item.Differences);
    }

    private static string BuildFirstTimingDivergenceSummary(
        ForensicSessionComparison comparison,
        TraceDocument expected,
        TraceDocument actual,
        string expectedPath,
        string actualPath)
    {
        var index = comparison.Observations.FirstOrDefault(item =>
        {
            var expectedMs = ToMilliseconds(item.ExpectedRelativeTicks, expected.Metadata.StopwatchFrequency);
            var actualMs = ToMilliseconds(item.ActualRelativeTicks, actual.Metadata.StopwatchFrequency);
            return expectedMs is { } left && actualMs is { } right && left != right;
        })?.ComparisonIndex;
        if (index is null)
            return "FIRST TIMING DIVERGENCE · None; aligned events have matching relative times.";

        var item = comparison.Observations[index.Value];
        var row = TraceComparisonRow.From(
            item,
            expected.Metadata.StopwatchFrequency,
            actual.Metadata.StopwatchFrequency,
            0,
            expectedPath,
            GetEventSequence(expected.Events, item.ExpectedSourceIndex),
            actualPath,
            GetEventSequence(actual.Events, item.ActualSourceIndex));
        return $"FIRST TIMING DIVERGENCE · Observation {index} · {row.Timing} · " +
            $"Expected {row.ExpectedTraceReference} · Actual {row.ActualTraceReference}";
    }

    private static string BuildRootCauseHint(
        ForensicSessionComparison comparison,
        TraceDocument expected,
        TraceDocument actual,
        string expectedPath,
        string actualPath)
    {
        if (comparison.FirstDifferenceIndex is not { } index)
            return "EVIDENCE ASSESSMENT · No divergence found in the aligned events. These captures do not establish behavior outside the recorded interval.";

        var item = comparison.Observations[index];
        var expectedReference = FormatTraceReference(
            expectedPath, GetEventSequence(expected.Events, item.ExpectedSourceIndex));
        var actualReference = FormatTraceReference(
            actualPath, GetEventSequence(actual.Events, item.ActualSourceIndex));

        if (item.Kind == ForensicChangeKind.MissingFromActual)
            return $"EVIDENCE ASSESSMENT · {item.ExpectedLabel ?? "Expected event"} is present at {expectedReference} and has no aligned event at {actualReference}. The trace proves an omission from the actual capture; it cannot distinguish device silence from a capture gap.";

        if (item.Kind == ForensicChangeKind.ExtraInActual)
            return $"EVIDENCE ASSESSMENT · {item.ActualLabel ?? "Actual event"} appears at {actualReference} with no aligned baseline event at {expectedReference}. This supports an additional actual-side event; the trace alone cannot determine whether it is unexpected protocol behavior or a capture-scope difference.";

        var evidence = new List<string>();
        if (item.Bytes is { Equal: false } bytes)
        {
            var first = bytes.Differences[0];
            var expectedByte = first.Expected is { } left ? $"0x{left:X2}" : "EOF";
            var actualByte = first.Actual is { } right ? $"0x{right:X2}" : "EOF";
            evidence.Add($"payload first differs at offset {first.Offset}: {expectedByte} → {actualByte} ({bytes.ExpectedLength} → {bytes.ActualLength} bytes)");
        }
        if (item.ExpectedLabel != item.ActualLabel)
            evidence.Add($"event label differs ('{item.ExpectedLabel}' → '{item.ActualLabel}')");
        if (item.ExpectedSourceIndex is { } expectedIndex && item.ActualSourceIndex is { } actualIndex &&
            expected.Events[expectedIndex].Kind != actual.Events[actualIndex].Kind)
            evidence.Add($"event kind differs ({expected.Events[expectedIndex].Kind} → {actual.Events[actualIndex].Kind})");
        if (item.ExpectedSourceIndex is { } expectedDirectionIndex && item.ActualSourceIndex is { } actualDirectionIndex &&
            expected.Events[expectedDirectionIndex].Direction != actual.Events[actualDirectionIndex].Direction)
            evidence.Add($"direction differs ({expected.Events[expectedDirectionIndex].Direction?.ToString() ?? "unspecified"} → {actual.Events[actualDirectionIndex].Direction?.ToString() ?? "unspecified"})");
        var expectedMs = ToMilliseconds(item.ExpectedRelativeTicks, expected.Metadata.StopwatchFrequency);
        var actualMs = ToMilliseconds(item.ActualRelativeTicks, actual.Metadata.StopwatchFrequency);
        if (expectedMs is { } leftTime && actualMs is { } rightTime && leftTime != rightTime)
        {
            evidence.Add($"actual event is {(rightTime > leftTime ? "later" : "earlier")} by {Math.Abs(rightTime - leftTime).ToString("N2", CultureInfo.InvariantCulture)} ms");
        }

        if (evidence.Count == 0)
            evidence.Add("event metadata differs");
        return $"EVIDENCE ASSESSMENT · {string.Join("; ", evidence)}. This supports a trace-level divergence; these captures do not identify the responsible device, transport, or application layer.";
    }

    private static string FormatTraceReference(string path, long? sequence) =>
        sequence is { } value ? $"{Path.GetFileName(path)} · #{value}" : $"{Path.GetFileName(path)} · no event";

    private static double? ToMilliseconds(long? ticks, long frequency) =>
        ticks is { } value && frequency > 0 ? value * 1000d / frequency : null;

    [RelayCommand(CanExecute = nameof(CanOpenExpectedEvent))]
    private void OpenExpectedEvent() =>
        RequestNavigation(SelectedRow?.ExpectedTraceId, SelectedRow?.ExpectedEventSequence, "expected");

    [RelayCommand(CanExecute = nameof(CanOpenActualEvent))]
    private void OpenActualEvent() =>
        RequestNavigation(SelectedRow?.ActualTraceId, SelectedRow?.ActualEventSequence, "actual");

    private bool RequestNavigation(string? traceId, long? sequence, string side)
    {
        if (string.IsNullOrWhiteSpace(traceId) || sequence is null)
            return false;

        TraceEventRequested?.Invoke(this, new(traceId, sequence.Value, side));
        return true;
    }
}

public sealed record TraceComparisonRow(
    string State,
    string Position,
    string ExpectedEvent,
    string ActualEvent,
    string ByteDifference,
    string Timing,
    string Detail,
    string ExpectedTimeLabel,
    string ActualTimeLabel,
    double ExpectedTimelinePercent,
    double ActualTimelinePercent,
    string? ExpectedTraceId,
    long? ExpectedEventSequence,
    string? ActualTraceId,
    long? ActualEventSequence)
{
    public bool CanOpenExpectedEvent => ExpectedTraceId is not null && ExpectedEventSequence is not null;
    public bool CanOpenActualEvent => ActualTraceId is not null && ActualEventSequence is not null;
    public string ExpectedTraceReference => FormatReference(ExpectedTraceId, ExpectedEventSequence);
    public string ActualTraceReference => FormatReference(ActualTraceId, ActualEventSequence);

    public static TraceComparisonRow From(
        ForensicObservationComparison item,
        long expectedFrequency,
        long actualFrequency,
        double timelineDurationMs,
        string expectedTraceId,
        long? expectedEventSequence,
        string actualTraceId,
        long? actualEventSequence)
    {
        var byteDifference = item.Bytes is { Equal: false } bytes
            ? bytes.Differences.Take(3).Select(difference =>
                $"@{difference.Offset.ToString(CultureInfo.InvariantCulture)} {FormatByte(difference.Expected)}→{FormatByte(difference.Actual)}")
                .Aggregate((left, right) => $"{left}, {right}")
            : item.Bytes is null ? "—" : "MATCH";
        var expectedMs = ToMilliseconds(item.ExpectedRelativeTicks, expectedFrequency);
        var actualMs = ToMilliseconds(item.ActualRelativeTicks, actualFrequency);
        var timing = expectedMs is null || actualMs is null
            ? "n/a"
            : $"{expectedMs.Value.ToString("N2", CultureInfo.InvariantCulture)} → {actualMs.Value.ToString("N2", CultureInfo.InvariantCulture)} ms (Δ {(actualMs.Value - expectedMs.Value).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} ms)";
        var safeDurationMs = Math.Max(0, timelineDurationMs);
        return new(
            item.Kind.ToString().ToUpperInvariant(),
            item.ComparisonIndex.ToString(CultureInfo.InvariantCulture),
            item.ExpectedLabel ?? "—",
            item.ActualLabel ?? "—",
            byteDifference,
            timing,
            string.Join(" ", item.Differences),
            expectedMs is { } expectedTime ? $"{expectedTime.ToString("N2", CultureInfo.InvariantCulture)} ms" : "—",
            actualMs is { } actualTime ? $"{actualTime.ToString("N2", CultureInfo.InvariantCulture)} ms" : "—",
            ToTimelinePercent(expectedMs, safeDurationMs),
            ToTimelinePercent(actualMs, safeDurationMs),
            expectedEventSequence is null ? null : expectedTraceId,
            expectedEventSequence,
            actualEventSequence is null ? null : actualTraceId,
            actualEventSequence);
    }

    private static string FormatReference(string? traceId, long? sequence) =>
        traceId is not null && sequence is { } value
            ? $"{Path.GetFileName(traceId)} · #{value}"
            : "NO SOURCE EVENT";

    private static double? ToMilliseconds(long? ticks, long frequency) =>
        ticks is { } value && frequency > 0 ? value * 1000d / frequency : null;

    private static double ToTimelinePercent(double? eventTimeMs, double durationMs) =>
        eventTimeMs is { } value && durationMs > 0
            ? Math.Clamp(value / durationMs * 100d, 0d, 100d)
            : 0d;

    private static string FormatByte(byte? value) => value is { } item
        ? item.ToString("X2", CultureInfo.InvariantCulture)
        : "EOF";
}

public sealed class TraceComparisonEventRequestedEventArgs(string traceId, long sequence, string side) : EventArgs
{
    public string TraceId { get; } = traceId;
    public long Sequence { get; } = sequence;
    public string Side { get; } = side;
}
