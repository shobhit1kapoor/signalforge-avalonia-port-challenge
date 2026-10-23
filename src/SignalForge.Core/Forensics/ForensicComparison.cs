using SignalForge.Core.Transport;

namespace SignalForge.Core.Forensics;

public sealed record ByteDifference(int Offset, byte? Expected, byte? Actual, byte Mask);

public sealed class ByteComparisonResult(
    int expectedLength,
    int actualLength,
    IEnumerable<ByteDifference> differences)
{
    public int ExpectedLength { get; } = expectedLength;
    public int ActualLength { get; } = actualLength;
    public IReadOnlyList<ByteDifference> Differences { get; } = differences.ToArray();
    public bool Equal => Differences.Count == 0;
    public int? FirstDivergenceOffset => Differences.Count == 0 ? null : Differences[0].Offset;
}

public static class ByteSequenceComparer
{
    /// <summary>Compares bytes directly; set mask bits to one where byte bits must match.</summary>
    public static ByteComparisonResult Compare(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, ReadOnlySpan<byte> mask = default)
    {
        if (!mask.IsEmpty && mask.Length != expected.Length)
            throw new ArgumentException("Mask length must equal expected byte length.", nameof(mask));

        var differences = new List<ByteDifference>();
        var commonLength = Math.Min(expected.Length, actual.Length);
        for (var offset = 0; offset < commonLength; offset++)
        {
            var maskByte = mask.IsEmpty ? (byte)0xff : mask[offset];
            if ((expected[offset] & maskByte) != (actual[offset] & maskByte))
                differences.Add(new(offset, expected[offset], actual[offset], maskByte));
        }
        for (var offset = commonLength; offset < expected.Length; offset++)
            differences.Add(new(offset, expected[offset], null, mask.IsEmpty ? (byte)0xff : mask[offset]));
        for (var offset = commonLength; offset < actual.Length; offset++)
            differences.Add(new(offset, null, actual[offset], 0xff));
        return new(expected.Length, actual.Length, differences);
    }
}

public sealed record ForensicObservation(
    string Label,
    string Category,
    long MonotonicTicks,
    ReadOnlyMemory<byte> Bytes = default,
    ByteDirection? Direction = null,
    int? SourceIndex = null);

public enum ForensicChangeKind
{
    Unchanged,
    Changed,
    MissingFromActual,
    ExtraInActual
}

public sealed record ForensicObservationComparison(
    int ComparisonIndex,
    ForensicChangeKind Kind,
    int? ExpectedSourceIndex,
    int? ActualSourceIndex,
    string? ExpectedLabel,
    string? ActualLabel,
    long? ExpectedRelativeTicks,
    long? ActualRelativeTicks,
    long? RelativeTimingDeltaTicks,
    ByteComparisonResult? Bytes,
    IReadOnlyList<string> Differences);

public sealed class ForensicSessionComparison(
    IEnumerable<ForensicObservationComparison> observations,
    long expectedOriginTicks,
    long actualOriginTicks)
{
    public IReadOnlyList<ForensicObservationComparison> Observations { get; } = observations.ToArray();
    public long ExpectedOriginTicks { get; } = expectedOriginTicks;
    public long ActualOriginTicks { get; } = actualOriginTicks;
    public int UnchangedCount => Observations.Count(item => item.Kind == ForensicChangeKind.Unchanged);
    public int ChangedCount => Observations.Count(item => item.Kind == ForensicChangeKind.Changed);
    public int MissingCount => Observations.Count(item => item.Kind == ForensicChangeKind.MissingFromActual);
    public int ExtraCount => Observations.Count(item => item.Kind == ForensicChangeKind.ExtraInActual);
    public int? FirstDifferenceIndex => Observations.FirstOrDefault(item => item.Kind != ForensicChangeKind.Unchanged)?.ComparisonIndex;

    /// <summary>Gets the first position whose relative timing differs, independent of other differences.</summary>
    public int? FirstTimingDifferenceIndex => Observations.FirstOrDefault(item =>
        item.RelativeTimingDeltaTicks is { } delta && delta != 0)?.ComparisonIndex;
}

/// <summary>
/// Compares ordered captures with a bounded-lookahead sequence alignment and reports byte and relative-time evidence.
/// Alignment keys use label, category (event kind), and direction; payload bytes are compared after alignment so
/// payload changes remain visible as Changed observations. Unique keys form monotonic patience-diff anchors.
/// Within unanchored regions, the nearest key match by total skipped observations wins; ties skip expected events
/// first (MissingFromActual). The local search is capped at 32 observations, after which unmatched positions are
/// compared positionally as Changed. These rules make ties deterministic while keeping work near-linear.
/// </summary>
public static class ForensicSessionComparer
{
    private const int LocalLookahead = 32;

    private readonly record struct AlignmentKey(string Label, string Category, ByteDirection? Direction);
    private readonly record struct AlignmentAnchor(int ExpectedIndex, int ActualIndex);

    public static ForensicSessionComparison Compare(
        IEnumerable<ForensicObservation> expected,
        IEnumerable<ForensicObservation> actual,
        ReadOnlyMemory<byte> byteMask = default)
    {
        var left = expected?.ToArray() ?? throw new ArgumentNullException(nameof(expected));
        var right = actual?.ToArray() ?? throw new ArgumentNullException(nameof(actual));
        var leftOrigin = left.Length == 0 ? 0 : left[0].MonotonicTicks;
        var rightOrigin = right.Length == 0 ? 0 : right[0].MonotonicTicks;
        var leftKeys = left.Select(ToAlignmentKey).ToArray();
        var rightKeys = right.Select(ToAlignmentKey).ToArray();
        var leftPositions = IndexPositions(leftKeys);
        var rightPositions = IndexPositions(rightKeys);
        var anchors = FindUniqueAnchors(leftKeys, rightKeys, leftPositions, rightPositions);
        var observations = new List<ForensicObservationComparison>();

        var leftIndex = 0;
        var rightIndex = 0;
        var localRightPositions = new Dictionary<AlignmentKey, int>(LocalLookahead);
        foreach (var anchor in anchors)
        {
            AlignSegment(anchor.ExpectedIndex, anchor.ActualIndex);
            AddPaired(anchor.ExpectedIndex, anchor.ActualIndex);
            leftIndex = anchor.ExpectedIndex + 1;
            rightIndex = anchor.ActualIndex + 1;
        }
        AlignSegment(left.Length, right.Length);
        return new(observations, leftOrigin, rightOrigin);

        void AlignSegment(int expectedEnd, int actualEnd)
        {
            while (leftIndex < expectedEnd && rightIndex < actualEnd)
            {
                if (leftKeys[leftIndex] == rightKeys[rightIndex])
                {
                    AddPaired(leftIndex, rightIndex);
                    leftIndex++;
                    rightIndex++;
                    continue;
                }

                var candidate = FindNextAlignmentCandidate(expectedEnd, actualEnd);
                if (candidate is { } match)
                {
                    if (match.ExpectedIndex > leftIndex)
                    {
                        AddMissing(leftIndex++);
                        continue;
                    }
                    if (match.ActualIndex > rightIndex)
                    {
                        AddExtra(rightIndex++);
                        continue;
                    }
                }

                // No identity anchor was found nearby; preserve an explicit comparison for this event pair.
                AddPaired(leftIndex++, rightIndex++);
            }

            while (leftIndex < expectedEnd)
                AddMissing(leftIndex++);
            while (rightIndex < actualEnd)
                AddExtra(rightIndex++);
        }

        AlignmentAnchor? FindNextAlignmentCandidate(int expectedEnd, int actualEnd)
        {
            var best = (AlignmentAnchor?)null;
            long bestScore = long.MaxValue;
            var bestExpectedSkip = -1;

            void Consider(int expectedCandidate, int actualCandidate)
            {
                var expectedSkip = expectedCandidate - leftIndex;
                var actualSkip = actualCandidate - rightIndex;
                var score = (long)expectedSkip + actualSkip;
                // For equal total skips, prefer skipping expected rows first, then retain the first scanned match.
                if (score > bestScore || score == bestScore && expectedSkip <= bestExpectedSkip)
                    return;

                best = new(expectedCandidate, actualCandidate);
                bestScore = score;
                bestExpectedSkip = expectedSkip;
            }

            var nextActual = FindNextIndex(rightPositions, leftKeys[leftIndex], rightIndex, actualEnd);
            if (nextActual is { } actualMatch)
                Consider(leftIndex, actualMatch);
            var nextExpected = FindNextIndex(leftPositions, rightKeys[rightIndex], leftIndex, expectedEnd);
            if (nextExpected is { } expectedMatch)
                Consider(expectedMatch, rightIndex);

            // Search for a shared key within a fixed window. This catches a nearby delete+insert pair even when
            // neither current key appears on the opposite side, without a quadratic LCS table.
            localRightPositions.Clear();
            var localActualEnd = rightIndex + Math.Min(actualEnd - rightIndex, LocalLookahead + 1);
            for (var index = rightIndex; index < localActualEnd; index++)
                localRightPositions.TryAdd(rightKeys[index], index);

            var localExpectedEnd = leftIndex + Math.Min(expectedEnd - leftIndex, LocalLookahead + 1);
            for (var index = leftIndex; index < localExpectedEnd; index++)
            {
                if (localRightPositions.TryGetValue(leftKeys[index], out var matchingActual))
                    Consider(index, matchingActual);
            }

            return best;
        }

        void AddPaired(int expectedIndex, int actualIndex)
        {
            var l = left[expectedIndex];
            var r = right[actualIndex];
            ByteComparisonResult? byteDiff = null;
            if (!l.Bytes.IsEmpty || !r.Bytes.IsEmpty)
                byteDiff = ByteSequenceComparer.Compare(l.Bytes.Span, r.Bytes.Span, byteMask.Span);
            var leftRelative = l.MonotonicTicks - leftOrigin;
            var rightRelative = r.MonotonicTicks - rightOrigin;
            var delta = rightRelative - leftRelative;
            var differences = new List<string>();
            if (!StringComparer.Ordinal.Equals(l.Label, r.Label)) differences.Add($"Label differs: '{l.Label}' vs '{r.Label}'.");
            if (!StringComparer.Ordinal.Equals(l.Category, r.Category)) differences.Add($"Category differs: '{l.Category}' vs '{r.Category}'.");
            if (l.Direction != r.Direction) differences.Add($"Direction differs: {l.Direction?.ToString() ?? "unspecified"} vs {r.Direction?.ToString() ?? "unspecified"}.");
            if (byteDiff is { Equal: false }) differences.Add($"Bytes differ at offset {byteDiff.FirstDivergenceOffset} ({byteDiff.Differences.Count} differing or missing bytes).");
            if (delta != 0)
                differences.Add($"Relative timing differs by {delta} ticks (actual is {(delta > 0 ? "later" : "earlier")}).");
            var kind = differences.Count == 0 ? ForensicChangeKind.Unchanged : ForensicChangeKind.Changed;
            observations.Add(new(observations.Count, kind,
                l.SourceIndex ?? expectedIndex, r.SourceIndex ?? actualIndex, l.Label, r.Label,
                leftRelative, rightRelative, delta, byteDiff, differences));
        }

        void AddMissing(int expectedIndex)
        {
            var item = left[expectedIndex];
            observations.Add(new(observations.Count, ForensicChangeKind.MissingFromActual,
                item.SourceIndex ?? expectedIndex, null, item.Label, null,
                item.MonotonicTicks - leftOrigin, null, null, null,
                ["Observation is absent from the actual capture."]));
        }

        void AddExtra(int actualIndex)
        {
            var item = right[actualIndex];
            observations.Add(new(observations.Count, ForensicChangeKind.ExtraInActual,
                null, item.SourceIndex ?? actualIndex, null, item.Label,
                null, item.MonotonicTicks - rightOrigin, null, null,
                ["Observation exists only in the actual capture."]));
        }
    }

    private static AlignmentKey ToAlignmentKey(ForensicObservation observation) =>
        new(observation.Label, observation.Category, observation.Direction);

    private static Dictionary<AlignmentKey, List<int>> IndexPositions(IReadOnlyList<AlignmentKey> keys)
    {
        var positions = new Dictionary<AlignmentKey, List<int>>();
        for (var index = 0; index < keys.Count; index++)
        {
            if (!positions.TryGetValue(keys[index], out var matches))
            {
                matches = [];
                positions.Add(keys[index], matches);
            }
            matches.Add(index);
        }
        return positions;
    }

    private static int? FindNextIndex(
        Dictionary<AlignmentKey, List<int>> positions,
        AlignmentKey key,
        int startInclusive,
        int endExclusive)
    {
        if (!positions.TryGetValue(key, out var matches))
            return null;

        var position = matches.BinarySearch(startInclusive);
        if (position < 0)
            position = ~position;
        return position < matches.Count && matches[position] < endExclusive
            ? matches[position]
            : null;
    }

    private static List<AlignmentAnchor> FindUniqueAnchors(
        IReadOnlyList<AlignmentKey> leftKeys,
        IReadOnlyList<AlignmentKey> rightKeys,
        Dictionary<AlignmentKey, List<int>> leftPositions,
        Dictionary<AlignmentKey, List<int>> rightPositions)
    {
        var candidates = new List<AlignmentAnchor>();
        for (var index = 0; index < leftKeys.Count; index++)
        {
            var key = leftKeys[index];
            if (leftPositions[key].Count == 1 &&
                rightPositions.TryGetValue(key, out var matches) && matches.Count == 1)
                candidates.Add(new(index, matches[0]));
        }

        if (candidates.Count < 2)
            return candidates;

        // Strict LIS of actual positions: lower-bound replacement keeps the minimum actual-index tail for each
        // length. Candidate iteration is in expected order, so the selected anchor sequence is deterministic.
        var tails = new List<int>();
        var tailCandidates = new List<int>();
        var previous = new int[candidates.Count];
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var actualIndex = candidates[candidateIndex].ActualIndex;
            var low = 0;
            var high = tails.Count;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (tails[middle] < actualIndex)
                    low = middle + 1;
                else
                    high = middle;
            }

            previous[candidateIndex] = low == 0 ? -1 : tailCandidates[low - 1];
            if (low == tails.Count)
            {
                tails.Add(actualIndex);
                tailCandidates.Add(candidateIndex);
            }
            else
            {
                tails[low] = actualIndex;
                tailCandidates[low] = candidateIndex;
            }
        }

        var anchors = new List<AlignmentAnchor>(tails.Count);
        for (var candidateIndex = tailCandidates[^1]; candidateIndex >= 0; candidateIndex = previous[candidateIndex])
            anchors.Add(candidates[candidateIndex]);
        anchors.Reverse();
        return anchors;
    }
}
