/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

namespace SignalForge.Core.Compatibility.Terminal2;

public enum Terminal2ValidationSeverity
{
    Warning,
    Error
}

public sealed record Terminal2ValidationIssue(
    string Code,
    string Message,
    Terminal2ValidationSeverity Severity = Terminal2ValidationSeverity.Error,
    int? LineNumber = null,
    int? CharacterOffset = null,
    string? Field = null);

/// <summary>A typed value together with diagnostics that must be shown to the caller.</summary>
public sealed class Terminal2ValidationResult<T>
{
    public Terminal2ValidationResult(T? value, IEnumerable<Terminal2ValidationIssue>? issues = null)
    {
        Value = value;
        Issues = (issues ?? Array.Empty<Terminal2ValidationIssue>()).ToArray();
    }

    public T? Value { get; }
    public IReadOnlyList<Terminal2ValidationIssue> Issues { get; }
    public bool HasErrors => Issues.Any(issue => issue.Severity == Terminal2ValidationSeverity.Error);
    public bool IsValid => Value is not null && !HasErrors;

    public static Terminal2ValidationResult<T> Success(T value) => new(value);

    public static Terminal2ValidationResult<T> Failure(string code, string message, int? characterOffset = null) =>
        new(default, new[]
        {
            new Terminal2ValidationIssue(code, message, CharacterOffset: characterOffset)
        });
}
