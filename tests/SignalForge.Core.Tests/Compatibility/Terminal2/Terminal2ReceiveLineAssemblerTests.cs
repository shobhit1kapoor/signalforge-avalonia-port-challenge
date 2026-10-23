/*
 * Compatibility tests derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.Text;
using System.Runtime.InteropServices;
using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Tests.Compatibility.Terminal2;

public sealed class Terminal2ReceiveLineAssemblerTests
{
    [Fact]
    public void Recognizes_cr_lf_and_crlf_and_keeps_exact_delimiter_bytes()
    {
        // Pinned source: Comms.cs:100-160 folds CRLF to one displayed newline;
        // FrmMain.cs:337-389 assembles those newlines into complete input lines.
        var assembler = new Terminal2ReceiveLineAssembler();
        var result = assembler.Append(Encoding.ASCII.GetBytes("a\r\nb\rc\nd\r\r\n\n"), 17);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Lines,
            line => AssertLine(line, "a\r\n", "a", Terminal2ReceiveLineEnding.CarriageReturnLineFeed, 1),
            line => AssertLine(line, "b\r", "b", Terminal2ReceiveLineEnding.CarriageReturn, 2),
            line => AssertLine(line, "c\n", "c", Terminal2ReceiveLineEnding.LineFeed, 3),
            line => AssertLine(line, "d\r", "d", Terminal2ReceiveLineEnding.CarriageReturn, 4),
            line => AssertLine(line, "\r\n", "", Terminal2ReceiveLineEnding.CarriageReturnLineFeed, 5),
            line => AssertLine(line, "\n", "", Terminal2ReceiveLineEnding.LineFeed, 6));
        Assert.Empty(assembler.Complete().Lines);
    }

    [Fact]
    public void Combines_crlf_split_across_chunks_and_flushes_unterminated_suffix()
    {
        var assembler = new Terminal2ReceiveLineAssembler();
        Assert.Empty(assembler.Append("alpha\r"u8, 100).Lines);

        var first = assembler.Append("\nbeta"u8, 200);

        Assert.True(first.IsSuccess);
        var line = Assert.Single(first.Lines);
        AssertLine(line, "alpha\r\n", "alpha", Terminal2ReceiveLineEnding.CarriageReturnLineFeed, 1);
        Assert.Equal(100, line.FirstMonotonicTicks);
        Assert.Equal(200, line.LastMonotonicTicks);
        Assert.Equal(4, assembler.BufferedByteCount);

        var final = assembler.Complete();
        var last = Assert.Single(final.Lines);
        AssertLine(last, "beta", "beta", Terminal2ReceiveLineEnding.None, 2);
        Assert.Equal(200, last.FirstMonotonicTicks);
        Assert.Equal(200, last.LastMonotonicTicks);
        Assert.True(assembler.IsCompleted);
    }

    [Fact]
    public void Preserves_first_and_last_chunk_ticks_including_split_delimiter()
    {
        var assembler = new Terminal2ReceiveLineAssembler();
        Assert.Empty(assembler.Append("ab"u8, 10).Lines);
        Assert.Empty(assembler.Append("c\r"u8, 20).Lines);

        var result = assembler.Append("\n"u8, 30);

        var line = Assert.Single(result.Lines);
        Assert.Equal(Encoding.ASCII.GetBytes("abc\r\n"), line.Bytes.ToArray());
        Assert.Equal(10, line.FirstMonotonicTicks);
        Assert.Equal(30, line.LastMonotonicTicks);
    }

    [Fact]
    public void Empty_chunks_do_not_affect_line_timestamps_or_contents()
    {
        var assembler = new Terminal2ReceiveLineAssembler();
        Assert.Empty(assembler.Append(ReadOnlySpan<byte>.Empty, 1).Lines);
        Assert.Empty(assembler.Append("x"u8, 4).Lines);
        Assert.Empty(assembler.Append(ReadOnlySpan<byte>.Empty, 99).Lines);

        var line = Assert.Single(assembler.Append("\n"u8, 7).Lines);

        Assert.Equal(Encoding.ASCII.GetBytes("x\n"), line.Bytes.ToArray());
        Assert.Equal(4, line.FirstMonotonicTicks);
        Assert.Equal(7, line.LastMonotonicTicks);
    }

    [Fact]
    public void Complete_flushes_pending_cr_as_a_delimiter_and_is_idempotent()
    {
        var assembler = new Terminal2ReceiveLineAssembler();
        Assert.Empty(assembler.Append("tail\r"u8, 88).Lines);

        var completed = assembler.Complete();
        var line = Assert.Single(completed.Lines);

        AssertLine(line, "tail\r", "tail", Terminal2ReceiveLineEnding.CarriageReturn, 1);
        Assert.Equal(88, line.FirstMonotonicTicks);
        Assert.Equal(88, line.LastMonotonicTicks);
        Assert.Empty(assembler.Complete().Lines);
    }

    [Fact]
    public void Line_limit_fails_visibly_and_never_emits_a_truncated_line()
    {
        var assembler = new Terminal2ReceiveLineAssembler(maximumLineBytes: 4);
        Assert.Empty(assembler.Append("abcd"u8, 1).Lines);

        var result = assembler.Append("e\n"u8, 2);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Lines);
        Assert.Equal(4, assembler.BufferedByteCount);
        Assert.True(assembler.IsFaulted);
        var issue = Assert.IsType<Terminal2ValidationIssue>(result.Issue);
        Assert.Equal("terminal2.receive-line-too-long", issue.Code);
        Assert.Equal(1, issue.LineNumber);
        Assert.Contains("not emitted or truncated", issue.Message, StringComparison.Ordinal);
        Assert.Empty(assembler.Complete().Lines);
    }

    [Fact]
    public void Preserves_lines_completed_before_a_later_oversized_line_in_the_same_chunk()
    {
        var assembler = new Terminal2ReceiveLineAssembler(maximumLineBytes: 3);

        var result = assembler.Append("ok\nlong"u8, 5);

        Assert.False(result.IsSuccess);
        var completed = Assert.Single(result.Lines);
        Assert.Equal(Encoding.ASCII.GetBytes("ok\n"), completed.Bytes.ToArray());
        Assert.Equal(1, completed.LineNumber);
        Assert.Equal(3, assembler.BufferedByteCount);
        Assert.Equal(2, result.Issue?.LineNumber);
    }

    [Fact]
    public void Reset_allows_a_new_stream_after_a_visible_size_failure()
    {
        var assembler = new Terminal2ReceiveLineAssembler(maximumLineBytes: 3);
        Assert.False(assembler.Append("abcd"u8, 1).IsSuccess);

        assembler.Reset();
        var result = assembler.Append("ok\n"u8, 2);

        Assert.True(result.IsSuccess);
        var line = Assert.Single(result.Lines);
        Assert.Equal(1, line.LineNumber);
        Assert.Equal(Encoding.ASCII.GetBytes("ok\n"), line.Bytes.ToArray());
    }

    [Fact]
    public void Maximum_line_size_includes_the_delimiter_bytes()
    {
        var assembler = new Terminal2ReceiveLineAssembler(maximumLineBytes: 3);

        var result = assembler.Append("ab\r"u8, 1);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Lines);
        Assert.Equal(Terminal2ReceiveLineEnding.CarriageReturn, Assert.Single(assembler.Complete().Lines).Ending);
    }

    [Fact]
    public void Rejects_a_nonpositive_line_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Terminal2ReceiveLineAssembler(0));
    }

    [Fact]
    public void Snapshot_tracks_partial_bytes_and_ticks_without_exposing_internal_storage()
    {
        var assembler = new Terminal2ReceiveLineAssembler();
        Assert.Empty(assembler.GetSnapshot().Bytes.ToArray());

        Assert.Empty(assembler.Append("par"u8, 10).Lines);
        var first = assembler.GetSnapshot();
        Assert.Equal(Encoding.ASCII.GetBytes("par"), first.Bytes.ToArray());
        Assert.Equal(10, first.FirstMonotonicTicks);
        Assert.Equal(10, first.LastMonotonicTicks);
        Assert.False(first.IsCarriageReturnPending);

        Assert.True(MemoryMarshal.TryGetArray(first.Bytes, out var segment));
        segment.Array![segment.Offset] = (byte)'X';
        Assert.Equal(Encoding.ASCII.GetBytes("par"), assembler.GetSnapshot().Bytes.ToArray());

        Assert.Empty(assembler.Append("tial"u8, 20).Lines);
        var updated = assembler.GetSnapshot();
        Assert.Equal(Encoding.ASCII.GetBytes("partial"), updated.Bytes.ToArray());
        Assert.Equal(10, updated.FirstMonotonicTicks);
        Assert.Equal(20, updated.LastMonotonicTicks);

        assembler.Reset();
        var reset = assembler.GetSnapshot();
        Assert.Empty(reset.Bytes.ToArray());
        Assert.Null(reset.FirstMonotonicTicks);
        Assert.Null(reset.LastMonotonicTicks);
        Assert.False(reset.IsCarriageReturnPending);
    }

    [Fact]
    public void Snapshot_keeps_cr_byte_and_pending_state_until_split_lf_arrives()
    {
        var assembler = new Terminal2ReceiveLineAssembler();
        Assert.Empty(assembler.Append("text\r"u8, 41).Lines);

        var pending = assembler.GetSnapshot();
        Assert.Equal(Encoding.ASCII.GetBytes("text\r"), pending.Bytes.ToArray());
        Assert.Equal(41, pending.FirstMonotonicTicks);
        Assert.Equal(41, pending.LastMonotonicTicks);
        Assert.True(pending.IsCarriageReturnPending);

        var result = assembler.Append("\nnext"u8, 42);
        Assert.Equal(Encoding.ASCII.GetBytes("text\r\n"), Assert.Single(result.Lines).Bytes.ToArray());

        var current = assembler.GetSnapshot();
        Assert.Equal(Encoding.ASCII.GetBytes("next"), current.Bytes.ToArray());
        Assert.Equal(42, current.FirstMonotonicTicks);
        Assert.Equal(42, current.LastMonotonicTicks);
        Assert.False(current.IsCarriageReturnPending);
    }

    private static void AssertLine(
        Terminal2ReceiveLine line,
        string exactBytes,
        string content,
        Terminal2ReceiveLineEnding ending,
        int lineNumber)
    {
        Assert.Equal(Encoding.ASCII.GetBytes(exactBytes), line.Bytes.ToArray());
        Assert.Equal(Encoding.ASCII.GetBytes(content), line.Content.ToArray());
        Assert.Equal(ending, line.Ending);
        Assert.Equal(lineNumber, line.LineNumber);
    }
}
