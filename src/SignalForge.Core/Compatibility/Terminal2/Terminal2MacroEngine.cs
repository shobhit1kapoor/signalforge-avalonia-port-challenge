/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.Globalization;
using System.Diagnostics;
using SignalForge.Core.Protocols.Terminal2;

namespace SignalForge.Core.Compatibility.Terminal2;

/// <summary>Side effects requested by a Terminal2 macro directive.</summary>
public enum Terminal2MacroEffectKind
{
    Note,
    Disconnect,
    StartLog,
    StopLog,
    Break,
    Freeze,
    ToggleDtr,
    ToggleRts
}

/// <summary>
/// An application-neutral macro effect. <see cref="Argument"/> is used for
/// BREAK duration in microseconds and <see cref="Text"/> carries note text.
/// </summary>
public sealed record Terminal2MacroEffect(
    Terminal2MacroEffectKind Kind,
    string? Text = null,
    int? Argument = null);

/// <summary>Boundary through which a host performs macro sends, waits and effects.</summary>
public interface ITerminal2MacroExecutor
{
    /// <summary>Whether the host still permits work, for example while connected and not stopped.</summary>
    bool CanContinue { get; }

    ValueTask SendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);

    ValueTask ExecuteEffectAsync(Terminal2MacroEffect effect, CancellationToken cancellationToken);
}

/// <summary>Summary of an attempted macro run and any visible compatibility diagnostics.</summary>
public sealed record Terminal2MacroExecutionResult(
    int CompletedRuns,
    IReadOnlyList<Terminal2ValidationIssue> Issues)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity == Terminal2ValidationSeverity.Error);
}

/// <summary>
/// Executes Terminal2 profile macros without depending on UI, transport or log
/// implementations. Delays, writes and directive effects are delegated to an executor.
/// </summary>
public static class Terminal2MacroEngine
{
    private const int MaximumMacroNesting = 64;
    private static readonly string[] LineSeparators = { "{0D}", "{0A}" };

    /// <summary>Runs one profile macro. Cancellation is the host's stop mechanism.</summary>
    public static async Task<Terminal2MacroExecutionResult> RunAsync(
        Terminal2Profile profile,
        int macroIndex,
        ITerminal2MacroExecutor executor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(executor);

        var issues = new List<Terminal2ValidationIssue>();
        if ((uint)macroIndex >= (uint)profile.Macros.Length || profile.Macros[macroIndex] is null)
        {
            issues.Add(new Terminal2ValidationIssue(
                "macro.missing",
                $"Profile macro slot {macroIndex} does not contain a macro.",
                Field: nameof(macroIndex)));
            return new Terminal2MacroExecutionResult(0, issues);
        }

        var activeMacros = new HashSet<int>();
        var completedRuns = await RunMacroAsync(
            profile,
            macroIndex,
            executor,
            activeMacros,
            issues,
            cancellationToken).ConfigureAwait(false);
        return new Terminal2MacroExecutionResult(completedRuns, issues);
    }

    private static async Task<int> RunMacroAsync(
        Terminal2Profile profile,
        int macroIndex,
        ITerminal2MacroExecutor executor,
        HashSet<int> activeMacros,
        List<Terminal2ValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (activeMacros.Count >= MaximumMacroNesting || !activeMacros.Add(macroIndex))
        {
            issues.Add(new Terminal2ValidationIssue(
                "macro.recursive-call",
                $"Macro slot {macroIndex} recursively calls an active macro; the recursive call was skipped.",
                Terminal2ValidationSeverity.Error));
            return 0;
        }

        try
        {
            var macro = profile.Macros[macroIndex]!;
            if (!HasValidBounds(macro, macroIndex, issues))
                return 0;

            var lines = macro.Text.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
                return 0;

            var completedRuns = 0;
            var repeatCount = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!executor.CanContinue)
                    break;
                var nextRun = Stopwatch.GetTimestamp() + ToStopwatchTicks(macro.RepeatEveryMs);

                for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!executor.CanContinue)
                        return completedRuns;
                    var outcome = await ExecuteLineAsync(
                        profile,
                        macro,
                        lines[lineIndex],
                        lineIndex + 1,
                        executor,
                        activeMacros,
                        issues,
                        cancellationToken).ConfigureAwait(false);
                    if (outcome == LineOutcome.AbortMacro)
                        return completedRuns;
                }

                completedRuns++;
                if (!macro.RepeatEnabled)
                    break;

                repeatCount++;
                // Terminal2 counts additional passes after the initial pass:
                // StopAfterRepeats=1 therefore means two total runs.
                if (macro.StopAfterRepeats > 0 && repeatCount > macro.StopAfterRepeats)
                    break;

                var remainingTicks = nextRun - Stopwatch.GetTimestamp();
                while (remainingTicks > 0)
                {
                    if (!executor.CanContinue)
                        return completedRuns;

                    var remaining = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
                    var delaySlice = remaining > TimeSpan.FromMilliseconds(5)
                        ? TimeSpan.FromMilliseconds(5)
                        : remaining;
                    await executor.DelayAsync(delaySlice, cancellationToken).ConfigureAwait(false);
                    remainingTicks = nextRun - Stopwatch.GetTimestamp();
                }
            }

            return completedRuns;
        }
        finally
        {
            activeMacros.Remove(macroIndex);
        }
    }

    private static async Task<LineOutcome> ExecuteLineAsync(
        Terminal2Profile profile,
        Terminal2Macro macro,
        string originalLine,
        int lineNumber,
        ITerminal2MacroExecutor executor,
        HashSet<int> activeMacros,
        List<Terminal2ValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        var line = originalLine;

        // The source recognizes directives only at the start of a trimmed line,
        // though several checks first search for their token anywhere in the line.
        if (line.Contains("#MACRO ", StringComparison.Ordinal))
        {
            var candidate = line.TrimEnd();
            if (candidate.StartsWith("#MACRO ", StringComparison.Ordinal))
            {
                var title = candidate[7..].Trim();
                var targetIndex = FindMacroIndex(profile, title);
                if (targetIndex < 0)
                {
                    AddIssue(issues, "macro.not-found", $"Macro title '{title}' was not found.", lineNumber,
                        Terminal2ValidationSeverity.Warning);
                }
                else
                {
                    await RunMacroAsync(profile, targetIndex, executor, activeMacros, issues, cancellationToken)
                        .ConfigureAwait(false);
                }

                return LineOutcome.Continue;
            }
        }

        if (line.Contains("#DISCONNECT", StringComparison.Ordinal))
        {
            await executor.ExecuteEffectAsync(new Terminal2MacroEffect(Terminal2MacroEffectKind.Disconnect), cancellationToken)
                .ConfigureAwait(false);
            line = originalLine;
        }

        if (line.Contains("#STARTLOG", StringComparison.Ordinal))
        {
            await executor.ExecuteEffectAsync(new Terminal2MacroEffect(Terminal2MacroEffectKind.StartLog), cancellationToken)
                .ConfigureAwait(false);
            line = originalLine;
        }

        if (line.Contains("#STOPLOG", StringComparison.Ordinal))
        {
            await executor.ExecuteEffectAsync(new Terminal2MacroEffect(Terminal2MacroEffectKind.StopLog), cancellationToken)
                .ConfigureAwait(false);
            line = originalLine;
        }

        if (line.Contains("#DELAY ", StringComparison.Ordinal))
        {
            var candidate = line.TrimEnd();
            if (candidate.StartsWith("#DELAY ", StringComparison.Ordinal))
            {
                var rawDelay = candidate[7..];
                if (!int.TryParse(rawDelay, NumberStyles.Integer, CultureInfo.CurrentCulture, out var delayMs) || delayMs <= 0)
                {
                    AddIssue(issues, "macro.invalid-delay", "#DELAY requires a positive integer number of milliseconds.", lineNumber,
                        Terminal2ValidationSeverity.Warning);
                }
                else
                {
                    await executor.DelayAsync(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
                }

                return LineOutcome.Continue;
            }

            line = originalLine;
        }

        if (line.Contains("#BREAK", StringComparison.Ordinal))
        {
            var candidate = line.TrimEnd();
            if (candidate.StartsWith("#BREAK", StringComparison.Ordinal))
            {
                var tokens = candidate.Split(' ');
                var microseconds = 1000;
                if (tokens.Length > 1 && !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.CurrentCulture, out microseconds))
                {
                    AddIssue(issues, "macro.invalid-break", "#BREAK expects an integer duration in microseconds.", lineNumber);
                    return LineOutcome.Continue;
                }

                if (microseconds < 1)
                    microseconds = 1000;
                await executor.ExecuteEffectAsync(
                    new Terminal2MacroEffect(Terminal2MacroEffectKind.Break, Argument: microseconds),
                    cancellationToken).ConfigureAwait(false);
                return LineOutcome.Continue;
            }

            line = originalLine;
        }

        if (line.Contains("#FREEZE", StringComparison.Ordinal))
        {
            var candidate = line.TrimEnd();
            if (candidate.StartsWith("#FREEZE", StringComparison.Ordinal))
            {
                await executor.ExecuteEffectAsync(new Terminal2MacroEffect(Terminal2MacroEffectKind.Freeze), cancellationToken)
                    .ConfigureAwait(false);
            }

            line = originalLine;
        }

        if (line.Contains("#DTR", StringComparison.Ordinal))
        {
            var candidate = line.TrimEnd();
            if (candidate.StartsWith("#DTR", StringComparison.Ordinal))
            {
                await executor.ExecuteEffectAsync(new Terminal2MacroEffect(Terminal2MacroEffectKind.ToggleDtr), cancellationToken)
                    .ConfigureAwait(false);
            }

            line = originalLine;
        }

        if (line.Contains("#RTS", StringComparison.Ordinal))
        {
            var candidate = line.TrimEnd();
            if (candidate.StartsWith("#RTS", StringComparison.Ordinal))
            {
                await executor.ExecuteEffectAsync(new Terminal2MacroEffect(Terminal2MacroEffectKind.ToggleRts), cancellationToken)
                    .ConfigureAwait(false);
            }

            line = originalLine;
        }

        var noteStart = line.IndexOf("##", StringComparison.Ordinal);
        if (noteStart >= 0)
        {
            await executor.ExecuteEffectAsync(
                new Terminal2MacroEffect(
                    Terminal2MacroEffectKind.Note,
                    Text: "-NOTE-> " + line[(noteStart + 2)..] + "\n"),
                cancellationToken).ConfigureAwait(false);
        }

        var encapsulate = false;
        if (line.Contains("#STX ", StringComparison.Ordinal))
        {
            var candidate = line.TrimEnd();
            if (candidate.StartsWith("#STX ", StringComparison.Ordinal))
            {
                line = candidate[5..];
                encapsulate = true;
            }
        }

        var commentStart = line.IndexOf('#');
        if (commentStart >= 0)
            line = line[..commentStart];
        line = line.TrimEnd();

        byte[] bytes;
        if (encapsulate)
        {
            var frame = Terminal2StxEtxCodec.EncodeText(line);
            if (!frame.IsValid)
            {
                AddParserIssues(issues, frame.Issues, lineNumber);
                return LineOutcome.Continue;
            }

            bytes = frame.Value!;
        }
        else
        {
            if (line.Length == 0)
                return LineOutcome.Continue;

            var parsed = Terminal2SendTextParser.Parse(line);
            if (!parsed.IsValid)
            {
                AddParserIssues(issues, parsed.Issues, lineNumber);
                return LineOutcome.Continue;
            }

            bytes = parsed.Value!;
        }

        // The source applies CR/LF only after a non-empty line has been encoded.
        if (macro.AddCR)
            bytes = bytes.Append((byte)'\r').ToArray();
        if (macro.AddLF)
            bytes = bytes.Append((byte)'\n').ToArray();

        foreach (var value in bytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await executor.SendBytesAsync(new[] { value }, cancellationToken).ConfigureAwait(false);
            if (macro.DelayBetweenCharacters > 0)
            {
                await executor.DelayAsync(
                    TimeSpan.FromMilliseconds(macro.DelayBetweenCharacters),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (macro.DelayBetweenLinesMs > 0)
        {
            await executor.DelayAsync(TimeSpan.FromMilliseconds(macro.DelayBetweenLinesMs), cancellationToken)
                .ConfigureAwait(false);
        }

        return LineOutcome.Continue;
    }

    /// <summary>Finds the first profile macro with an exact, case-sensitive title match.</summary>
    public static int FindMacroIndex(Terminal2Profile profile, string title)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(title);
        if (title.Length == 0)
            return -1;

        for (var index = 0; index < profile.Macros.Length; index++)
        {
            var candidate = profile.Macros[index];
            if (candidate is not null && candidate.Title.Equals(title, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

    private static bool HasValidBounds(Terminal2Macro macro, int index, List<Terminal2ValidationIssue> issues)
    {
        var isValid = true;
        isValid &= CheckBound(macro.DelayBetweenCharacters, 0, 60_000, nameof(macro.DelayBetweenCharacters));
        isValid &= CheckBound(macro.DelayBetweenLinesMs, 0, 999_999_999, nameof(macro.DelayBetweenLinesMs));
        isValid &= CheckBound(macro.RepeatEveryMs, 0, 999_999_999, nameof(macro.RepeatEveryMs));
        isValid &= CheckBound(macro.StopAfterRepeats, 0, 60_000, nameof(macro.StopAfterRepeats));
        return isValid;

        bool CheckBound(int value, int minimum, int maximum, string field)
        {
            if (value >= minimum && value <= maximum)
                return true;

            issues.Add(new Terminal2ValidationIssue(
                "macro.out-of-range",
                $"Macro slot {index} field {field} must be between {minimum} and {maximum}.",
                Field: field));
            return false;
        }
    }

    private static void AddParserIssues(
        List<Terminal2ValidationIssue> issues,
        IReadOnlyList<Terminal2ValidationIssue> parserIssues,
        int lineNumber)
    {
        foreach (var issue in parserIssues)
            issues.Add(issue with { LineNumber = lineNumber });
    }

    private static void AddIssue(
        List<Terminal2ValidationIssue> issues,
        string code,
        string message,
        int lineNumber,
        Terminal2ValidationSeverity severity = Terminal2ValidationSeverity.Error) =>
        issues.Add(new Terminal2ValidationIssue(code, message, severity, LineNumber: lineNumber));

    private static long ToStopwatchTicks(int milliseconds) =>
        (long)(milliseconds * (Stopwatch.Frequency / 1000d));

    private enum LineOutcome
    {
        Continue,
        AbortMacro
    }
}
