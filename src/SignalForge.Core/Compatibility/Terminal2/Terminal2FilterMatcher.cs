/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

namespace SignalForge.Core.Compatibility.Terminal2;

/// <summary>Matches Terminal2 filters in profile order and returns the first match.</summary>
public static class Terminal2FilterMatcher
{
    /// <summary>
    /// Finds the first matching filter slot, or -1 when none matches. Mode 0 is
    /// starts-with and mode 1 is contains; other modes are ignored.
    /// </summary>
    public static int FindFirstMatch(
        IReadOnlyList<Terminal2Filter> filters,
        string text,
        int offset,
        bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(text);

        if (offset >= text.Length)
            return -1;

        // Match the legacy order of operations: lowercase the entire string
        // before applying the offset, using the current culture.
        var candidate = ignoreCase ? text.ToLower().Substring(offset) : text.Substring(offset);
        for (var index = 0; index < filters.Count; index++)
        {
            var filter = filters[index];
            if (filter is null || filter.Text.Length == 0)
                continue;

            var searchText = ignoreCase ? filter.Text.ToLower() : filter.Text;
            if (filter.Mode == 0 && candidate.StartsWith(searchText))
                return index;

            if (filter.Mode == 1 && candidate.Contains(searchText))
                return index;
        }

        return -1;
    }
}
