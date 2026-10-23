using SignalForge.Core.Forensics;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Forensics;

public sealed class ForensicComparisonTests
{
    [Fact]
    public void ByteComparerReportsFirstDivergenceAndRespectsBitMask()
    {
        var expected = new byte[] { 0x10, 0x20, 0x30 };
        var actual = new byte[] { 0x10, 0x2f, 0x30 };
        var mask = new byte[] { 0xff, 0xf0, 0xff };

        var masked = ByteSequenceComparer.Compare(expected, actual, mask);
        var strict = ByteSequenceComparer.Compare(expected, actual);

        Assert.True(masked.Equal);
        Assert.Equal(1, strict.FirstDivergenceOffset);
        Assert.Single(strict.Differences);
    }

    [Fact]
    public void SessionComparisonTracksByteTimingAndMissingEvidence()
    {
        var expected = new[]
        {
            new ForensicObservation("request", "frame", 1000, new byte[] { 1, 2 }, ByteDirection.Tx, 4),
            new ForensicObservation("response", "frame", 1500, new byte[] { 3 }, ByteDirection.Rx, 5),
            new ForensicObservation("close", "state", 1700)
        };
        var actual = new[]
        {
            new ForensicObservation("request", "frame", 2000, new byte[] { 1, 2 }, ByteDirection.Tx, 9),
            new ForensicObservation("response", "frame", 2700, new byte[] { 4 }, ByteDirection.Rx, 10)
        };

        var result = ForensicSessionComparer.Compare(expected, actual);

        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(1, result.MissingCount);
        Assert.Equal(1, result.FirstDifferenceIndex);
        Assert.Equal(200, result.Observations[1].RelativeTimingDeltaTicks);
        Assert.Equal(5, result.Observations[1].ExpectedSourceIndex);
        Assert.Equal(1, result.FirstTimingDifferenceIndex);
    }

    [Fact]
    public void SessionComparisonMarksEqualPayloadsChangedWhenRelativeTimingDiffers()
    {
        var expected = new[]
        {
            new ForensicObservation("request", "frame", 1000, new byte[] { 0x10 }, ByteDirection.Tx),
            new ForensicObservation("response", "frame", 1500, new byte[] { 0x20 }, ByteDirection.Rx)
        };
        var actual = new[]
        {
            new ForensicObservation("request", "frame", 5000, new byte[] { 0x10 }, ByteDirection.Tx),
            new ForensicObservation("response", "frame", 5600, new byte[] { 0x20 }, ByteDirection.Rx)
        };

        var result = ForensicSessionComparer.Compare(expected, actual);

        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(1, result.FirstDifferenceIndex);
        Assert.Equal(1, result.FirstTimingDifferenceIndex);
        Assert.Equal(100, result.Observations[1].RelativeTimingDeltaTicks);
        Assert.True(result.Observations[1].Bytes!.Equal);
        Assert.Contains("100 ticks", Assert.Single(result.Observations[1].Differences));
        Assert.Contains("later", result.Observations[1].Differences[0]);
    }

    [Fact]
    public void SessionComparisonIgnoresAbsoluteOriginWhenRelativeTimingMatches()
    {
        var expected = new[]
        {
            new ForensicObservation("request", "frame", 1000, new byte[] { 0x10 }, ByteDirection.Tx),
            new ForensicObservation("response", "frame", 1500, new byte[] { 0x20 }, ByteDirection.Rx)
        };
        var actual = new[]
        {
            new ForensicObservation("request", "frame", 90000, new byte[] { 0x10 }, ByteDirection.Tx),
            new ForensicObservation("response", "frame", 90500, new byte[] { 0x20 }, ByteDirection.Rx)
        };

        var result = ForensicSessionComparer.Compare(expected, actual);

        Assert.Equal(2, result.UnchangedCount);
        Assert.Equal(0, result.ChangedCount);
        Assert.Null(result.FirstDifferenceIndex);
        Assert.Null(result.FirstTimingDifferenceIndex);
        Assert.All(result.Observations, observation => Assert.Equal(0, observation.RelativeTimingDeltaTicks));
    }

    [Fact]
    public void SessionComparisonStillReportsByteAndEventDifferencesAtEqualTiming()
    {
        var expected = new[]
        {
            new ForensicObservation("request", "frame", 1000, new byte[] { 0x10 }, ByteDirection.Tx)
        };
        var actual = new[]
        {
            new ForensicObservation("command", "frame", 2000, new byte[] { 0x11 }, ByteDirection.Rx)
        };

        var result = ForensicSessionComparer.Compare(expected, actual);

        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(0, result.FirstDifferenceIndex);
        Assert.Null(result.FirstTimingDifferenceIndex);
        Assert.Equal(0, result.Observations[0].RelativeTimingDeltaTicks);
        Assert.Contains(result.Observations[0].Differences, difference => difference.StartsWith("Label differs:", StringComparison.Ordinal));
        Assert.Contains(result.Observations[0].Differences, difference => difference.StartsWith("Direction differs:", StringComparison.Ordinal));
        Assert.Contains(result.Observations[0].Differences, difference => difference.StartsWith("Bytes differ at offset", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionComparisonAlignsAfterAnObservationWasDropped()
    {
        var expected = new[]
        {
            new ForensicObservation("connect", "state", 1000, SourceIndex: 10),
            new ForensicObservation("request", "frame", 1100, new byte[] { 0x01 }, ByteDirection.Tx, 11),
            new ForensicObservation("response", "frame", 1300, new byte[] { 0x02 }, ByteDirection.Rx, 12),
            new ForensicObservation("disconnect", "state", 1400, SourceIndex: 13)
        };
        var actual = new[]
        {
            new ForensicObservation("connect", "state", 5000, SourceIndex: 20),
            new ForensicObservation("response", "frame", 5400, new byte[] { 0x02 }, ByteDirection.Rx, 22),
            new ForensicObservation("disconnect", "state", 5600, SourceIndex: 23)
        };

        var result = ForensicSessionComparer.Compare(expected, actual);

        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(2, result.ChangedCount);
        Assert.Equal(1, result.MissingCount);
        Assert.Equal(0, result.ExtraCount);
        Assert.Equal(new[] { ForensicChangeKind.Unchanged, ForensicChangeKind.MissingFromActual,
            ForensicChangeKind.Changed, ForensicChangeKind.Changed }, result.Observations.Select(item => item.Kind));
        Assert.Equal(11, result.Observations[1].ExpectedSourceIndex);
        Assert.Equal(100, result.Observations[2].RelativeTimingDeltaTicks);
    }

    [Fact]
    public void SessionComparisonAlignsAroundAnExtraActualObservation()
    {
        var expected = new[]
        {
            new ForensicObservation("start", "state", 100, SourceIndex: 1),
            new ForensicObservation("response", "frame", 300, new byte[] { 0x10 }, ByteDirection.Rx, 2),
            new ForensicObservation("end", "state", 400, SourceIndex: 3)
        };
        var actual = new[]
        {
            new ForensicObservation("start", "state", 1000, SourceIndex: 7),
            new ForensicObservation("debug", "state", 1100, SourceIndex: 8),
            new ForensicObservation("response", "frame", 1200, new byte[] { 0x10 }, ByteDirection.Rx, 9),
            new ForensicObservation("end", "state", 1300, SourceIndex: 10)
        };

        var result = ForensicSessionComparer.Compare(expected, actual);

        Assert.Equal(3, result.UnchangedCount);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(0, result.MissingCount);
        Assert.Equal(1, result.ExtraCount);
        Assert.Equal(ForensicChangeKind.ExtraInActual, result.Observations[1].Kind);
        Assert.Equal("debug", result.Observations[1].ActualLabel);
        Assert.Equal(8, result.Observations[1].ActualSourceIndex);
    }

    [Fact]
    public void SessionComparisonHandlesReorderedEventsWithDeterministicMissingThenExtraTieBreak()
    {
        var expected = new[]
        {
            new ForensicObservation("request", "frame", 100, new byte[] { 0x01 }, ByteDirection.Tx, 1),
            new ForensicObservation("response", "frame", 100, new byte[] { 0x02 }, ByteDirection.Rx, 2)
        };
        var actual = new[]
        {
            new ForensicObservation("response", "frame", 500, new byte[] { 0x02 }, ByteDirection.Rx, 5),
            new ForensicObservation("request", "frame", 500, new byte[] { 0x01 }, ByteDirection.Tx, 6)
        };

        var result = ForensicSessionComparer.Compare(expected, actual);

        Assert.Equal(1, result.MissingCount);
        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(1, result.ExtraCount);
        Assert.Equal(new[] { ForensicChangeKind.MissingFromActual, ForensicChangeKind.Unchanged,
            ForensicChangeKind.ExtraInActual }, result.Observations.Select(item => item.Kind));
        Assert.Equal("request", result.Observations[0].ExpectedLabel);
        Assert.Equal("request", result.Observations[2].ActualLabel);
    }

    [Fact]
    public void SessionComparisonUsesRepeatedLabelsAndNearbyShiftedAnchorWithoutLosingPayloadChecks()
    {
        var expected = new[]
        {
            new ForensicObservation("repeat", "frame", 100, new byte[] { 0x11 }, ByteDirection.Rx, 1),
            new ForensicObservation("repeat", "frame", 200, new byte[] { 0x22 }, ByteDirection.Rx, 2),
            new ForensicObservation("tail", "state", 300, SourceIndex: 3)
        };
        var actual = new[]
        {
            new ForensicObservation("inserted", "state", 900, SourceIndex: 8),
            new ForensicObservation("repeat", "frame", 900, new byte[] { 0x11 }, ByteDirection.Rx, 9),
            new ForensicObservation("repeat", "frame", 1000, new byte[] { 0x23 }, ByteDirection.Rx, 10),
            new ForensicObservation("tail", "state", 1100, SourceIndex: 11)
        };

        var result = ForensicSessionComparer.Compare(expected, actual);

        Assert.Equal(2, result.UnchangedCount);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(0, result.MissingCount);
        Assert.Equal(1, result.ExtraCount);
        Assert.Equal(ForensicChangeKind.ExtraInActual, result.Observations[0].Kind);
        Assert.Equal(ForensicChangeKind.Unchanged, result.Observations[1].Kind);
        Assert.Equal(ForensicChangeKind.Changed, result.Observations[2].Kind);
        Assert.Equal(2, result.Observations[2].ExpectedSourceIndex);
        Assert.Equal(10, result.Observations[2].ActualSourceIndex);
        Assert.Contains(result.Observations[2].Differences,
            difference => difference.StartsWith("Bytes differ at offset 0", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionComparisonAlignsLargeDroppedRunWithLinearSpaceAnchors()
    {
        var expected = new List<ForensicObservation>
        {
            new("start", "state", 0)
        };
        for (var index = 0; index < 80; index++)
            expected.Add(new("pulse", "state", index + 1));
        expected.Add(new("end", "state", 100));
        var actual = new[]
        {
            new ForensicObservation("start", "state", 1000),
            new ForensicObservation("end", "state", 1100)
        };

        var result = ForensicSessionComparer.Compare(expected, actual);

        Assert.Equal(2, result.UnchangedCount);
        Assert.Equal(80, result.MissingCount);
        Assert.Equal(0, result.ExtraCount);
        Assert.Equal(82, result.Observations.Count);
    }

    [Fact]
    public void SessionComparisonScalesToFiveThousandRowsWithOneShiftedEvent()
    {
        const int eventCount = 5_000;
        var expected = new List<ForensicObservation>(eventCount);
        var actual = new List<ForensicObservation>(eventCount + 1);
        for (var index = 0; index < eventCount; index++)
        {
            expected.Add(new($"event-{index}", "frame", index, new byte[] { (byte)index }, ByteDirection.Rx));
            actual.Add(new($"event-{index}", "frame", 5_000 + index, new byte[] { (byte)index }, ByteDirection.Rx));
            if (index == 2_500)
                actual.Add(new("injected", "state", 7_500));
        }

        var result = ForensicSessionComparer.Compare(expected, actual);

        Assert.Equal(eventCount, result.UnchangedCount);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(0, result.MissingCount);
        Assert.Equal(1, result.ExtraCount);
        Assert.Equal(2_501, result.FirstDifferenceIndex);
    }
}
