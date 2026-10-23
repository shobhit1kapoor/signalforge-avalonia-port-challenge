/*
 * Compatibility tests derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.Globalization;
using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Tests.Protocols.Terminal2;

namespace SignalForge.Core.Tests.Compatibility.Terminal2;

public sealed class Terminal2FilterMatcherTests
{
    [Fact]
    public void Matches_first_slot_by_starts_with_or_contains_and_obeys_ignore_case()
    {
        // Source behavior: external/Terminal2/Terminal2/Filters.cs:11-61 loops in slot
        // order, applies the caller offset, and uses modes 0 (starts-with) and 1 (contains).
        var filters = new[]
        {
            new Terminal2Filter { Mode = 1, Text = "BC" },
            new Terminal2Filter { Mode = 0, Text = "AB" },
            new Terminal2Filter { Mode = 2, Text = "AB" },
            new Terminal2Filter { Mode = 0, Text = string.Empty }
        };

        Assert.Equal(0, Terminal2FilterMatcher.FindFirstMatch(filters, "xABc", 1, ignoreCase: true));
        filters[0].Text = "not found";
        Assert.Equal(1, Terminal2FilterMatcher.FindFirstMatch(filters, "xABc", 1, ignoreCase: false));
        Assert.Equal(-1, Terminal2FilterMatcher.FindFirstMatch(filters, "xAbc", 1, ignoreCase: false));
        Assert.Equal(-1, Terminal2FilterMatcher.FindFirstMatch(filters, "xAbc", 4, ignoreCase: true));
    }

    [Theory]
    [InlineData("xABc", 1, true, "", 0)]
    [InlineData("xABc", 1, false, "", 1)]
    [InlineData("xAbc", 1, false, "", -1)]
    [InlineData("xAbc", 4, true, "", -1)]
    [InlineData("Istanbul", 0, true, "tr-TR", -1)]
    [InlineData("İzmir", 0, true, "tr-TR", 0)]
    public void Filter_matching_matches_pinned_terminal2_source(
        string text,
        int offset,
        bool ignoreCase,
        string cultureName,
        int expected)
    {
        var filters = new[]
        {
            new Terminal2Filter { Mode = 1, Text = cultureName == "tr-TR" ? "i" : "BC" },
            new Terminal2Filter { Mode = 0, Text = cultureName == "tr-TR" ? "iz" : "AB" },
            new Terminal2Filter { Mode = 2, Text = "AB" },
            new Terminal2Filter { Mode = 0, Text = string.Empty }
        };

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            if (!string.IsNullOrEmpty(cultureName))
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            var sourceResult = Terminal2PinnedSourceReference.FindApplicableFilter(filters, text, offset, ignoreCase);
            var migratedResult = Terminal2FilterMatcher.FindFirstMatch(filters, text, offset, ignoreCase);

            Assert.Equal(expected, sourceResult);
            Assert.Equal(sourceResult, migratedResult);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
