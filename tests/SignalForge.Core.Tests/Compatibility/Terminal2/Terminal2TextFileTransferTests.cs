/*
 * Compatibility tests derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.Text;
using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Tests.Compatibility.Terminal2;

public sealed class Terminal2TextFileTransferTests
{
    [Fact]
    public async Task Empty_lines_are_removed_and_an_empty_file_completes_without_sends()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("empty-lines.txt", "\r\n\r\n\n\r");
        var sent = new List<byte[]>();
        var transfer = new Terminal2TextFileTransfer((bytes, _) =>
        {
            sent.Add(bytes.ToArray());
            return ValueTask.CompletedTask;
        });

        var result = await transfer.RunFileAsync(path, new(SendCR: false, SendLF: false));

        Assert.Equal(Terminal2TextFileTransferStatus.Completed, result.Status);
        Assert.Equal(0, result.TotalLines);
        Assert.Equal(0, result.LinesSent);
        Assert.Empty(sent);
    }

    [Theory]
    [InlineData("alpha\rbeta")]
    [InlineData("alpha\nbeta")]
    [InlineData("alpha\r\nbeta")]
    [InlineData("alpha\n\rbeta")]
    [InlineData("one\r\ntwo\rthree\nfour\n\rfive")]
    public async Task Splits_every_cr_and_lf_separator_combination(string contents)
    {
        // Pinned Terminal2 source: FrmMain.cs:2287-2290.
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("separators.txt", contents);
        var sent = new List<byte[]>();
        var transfer = new Terminal2TextFileTransfer((bytes, _) =>
        {
            sent.Add(bytes.ToArray());
            return ValueTask.CompletedTask;
        });

        var result = await transfer.RunFileAsync(path, new(SendCR: false, SendLF: false));

        Assert.Equal(Terminal2TextFileTransferStatus.Completed, result.Status);
        Assert.Equal(sent.Count, result.TotalLines);
        Assert.Equal(result.TotalLines, result.LinesSent);
        Assert.Equal(contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Encoding.UTF8.GetBytes), sent.Select(bytes => bytes));
    }

    [Fact]
    public async Task Decodes_utf8_and_preserves_unicode_bytes()
    {
        using var temporary = new TemporaryDirectory();
        const string line = "µΩ · temperature 🌡";
        var path = temporary.Write("unicode.txt", line);
        byte[]? sent = null;
        var transfer = new Terminal2TextFileTransfer((bytes, _) =>
        {
            sent = bytes.ToArray();
            return ValueTask.CompletedTask;
        });

        var result = await transfer.RunFileAsync(path, new(SendCR: false, SendLF: false));

        Assert.Equal(Terminal2TextFileTransferStatus.Completed, result.Status);
        Assert.Equal(Encoding.UTF8.GetBytes(line), sent);
    }

    [Fact]
    public async Task Read_all_text_encoding_detects_a_utf16_bom()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "utf16.txt");
        await File.WriteAllTextAsync(path, "café", Encoding.Unicode);
        byte[]? sent = null;
        var transfer = new Terminal2TextFileTransfer((bytes, _) =>
        {
            sent = bytes.ToArray();
            return ValueTask.CompletedTask;
        });

        var result = await transfer.RunFileAsync(path, new(SendCR: false, SendLF: false));

        Assert.Equal(Terminal2TextFileTransferStatus.Completed, result.Status);
        Assert.Equal(Encoding.UTF8.GetBytes("café"), sent);
    }

    [Fact]
    public async Task Valid_dollar_escapes_and_literal_dollar_are_decoded_in_each_line()
    {
        // Pinned Terminal2 source: FrmMain.cs:1915-1975; valid $HH and $$ output is retained.
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("escapes.txt", "A$00$$$0d");
        byte[]? sent = null;
        var transfer = new Terminal2TextFileTransfer((bytes, _) =>
        {
            sent = bytes.ToArray();
            return ValueTask.CompletedTask;
        });

        var result = await transfer.RunFileAsync(path, new(SendCR: false, SendLF: false));

        Assert.Equal(Terminal2TextFileTransferStatus.Completed, result.Status);
        Assert.Equal(new byte[] { (byte)'A', 0x00, (byte)'$', 0x0D }, sent);
    }

    [Theory]
    [InlineData(false, false, "")]
    [InlineData(true, false, "\r")]
    [InlineData(false, true, "\n")]
    [InlineData(true, true, "\r\n")]
    public async Task Appends_cr_then_lf_according_to_options(bool sendCR, bool sendLF, string suffix)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("ending.txt", "x");
        byte[]? sent = null;
        var transfer = new Terminal2TextFileTransfer((bytes, _) =>
        {
            sent = bytes.ToArray();
            return ValueTask.CompletedTask;
        });

        var result = await transfer.RunFileAsync(path, new(sendCR, sendLF));

        Assert.Equal(Terminal2TextFileTransferStatus.Completed, result.Status);
        Assert.Equal(Encoding.UTF8.GetBytes("x" + suffix), sent);
    }

    [Fact]
    public async Task Malformed_escape_returns_line_and_offset_diagnostic_and_stops_before_that_line()
    {
        // The pinned IsHex at FrmMain.cs:1915-1923 accepts every byte and can turn malformed
        // escapes into wrong bytes. SIGNALFORGE reports that legacy defect instead of guessing.
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("bad-escape.txt", "first\nsecond $XZ\nthird");
        var sent = new List<byte[]>();
        var transfer = new Terminal2TextFileTransfer((bytes, _) =>
        {
            sent.Add(bytes.ToArray());
            return ValueTask.CompletedTask;
        });

        var result = await transfer.RunFileAsync(path, new(SendCR: false, SendLF: false));

        Assert.Equal(Terminal2TextFileTransferStatus.Failed, result.Status);
        Assert.Equal(3, result.TotalLines);
        Assert.Equal(1, result.LinesSent);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("transfer.invalid-send-text", issue.Code);
        Assert.Equal(2, issue.LineNumber);
        Assert.Equal(7, issue.CharacterOffset);
        Assert.Equal(new[] { Encoding.UTF8.GetBytes("first") }, sent);
    }

    [Fact]
    public async Task Reports_initial_and_completed_line_progress_and_applies_interline_delay()
    {
        // Pinned Terminal2 source bounds delay in FrmFileSend.cs:40-44,54-60 and sleeps after
        // every line in FrmMain.cs:2304-2322 (including the last line).
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("progress.txt", "one\ntwo");
        var progress = new List<Terminal2TextFileTransferProgress>();
        var transfer = new Terminal2TextFileTransfer((_, _) => ValueTask.CompletedTask);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await transfer.RunFileAsync(
            path,
            new(SendCR: false, SendLF: false, InterLineDelayMs: 20),
            progress.Add);

        stopwatch.Stop();
        Assert.Equal(Terminal2TextFileTransferStatus.Completed, result.Status);
        Assert.Equal(2, result.TotalLines);
        Assert.Equal(2, result.LinesSent);
        Assert.Collection(
            progress,
            initial => { Assert.Equal(0, initial.CompletedLines); Assert.Equal(2, initial.TotalLines); Assert.Null(initial.CurrentLine); },
            first => { Assert.Equal(1, first.CompletedLines); Assert.Equal("one", first.CurrentLine); },
            second => { Assert.Equal(2, second.CompletedLines); Assert.Equal("two", second.CurrentLine); });
        Assert.True(stopwatch.ElapsedMilliseconds >= 20, $"Expected two 20 ms line delays, actual {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task Pause_during_delay_holds_next_line_until_resumed()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("pause.txt", "one\ntwo");
        var firstLineSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<byte[]>();
        var transfer = new Terminal2TextFileTransfer((bytes, _) =>
        {
            sent.Add(bytes.ToArray());
            if (sent.Count == 1)
                firstLineSent.TrySetResult();
            return ValueTask.CompletedTask;
        });

        var running = transfer.RunFileAsync(
            path,
            new(SendCR: false, SendLF: false, InterLineDelayMs: 250));
        await firstLineSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transfer.Pause();
        await Task.Delay(40);

        Assert.False(running.IsCompleted);
        Assert.Single(sent);

        transfer.Resume();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(Terminal2TextFileTransferStatus.Completed, result.Status);
        Assert.Equal(2, result.LinesSent);
    }

    [Fact]
    public async Task Cancellation_during_interline_delay_returns_cancelled_and_stops()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("cancel.txt", "one\ntwo");
        var firstLineSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<byte[]>();
        var transfer = new Terminal2TextFileTransfer((bytes, _) =>
        {
            sent.Add(bytes.ToArray());
            if (sent.Count == 1)
                firstLineSent.TrySetResult();
            return ValueTask.CompletedTask;
        });

        var running = transfer.RunFileAsync(
            path,
            new(SendCR: false, SendLF: false, InterLineDelayMs: 10_000));
        await firstLineSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transfer.Cancel();

        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(Terminal2TextFileTransferStatus.Cancelled, result.Status);
        Assert.Equal(1, result.LinesSent);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Cancellation_between_lines_stops_before_the_next_send()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("cancel-between.txt", "one\ntwo");
        var sent = new List<byte[]>();
        var transfer = new Terminal2TextFileTransfer((bytes, _) =>
        {
            sent.Add(bytes.ToArray());
            return ValueTask.CompletedTask;
        });

        var result = await transfer.RunFileAsync(
            path,
            new(SendCR: false, SendLF: false),
            update =>
            {
                if (update.CompletedLines == 1)
                    transfer.Cancel();
            });

        Assert.Equal(Terminal2TextFileTransferStatus.Cancelled, result.Status);
        Assert.Equal(1, result.LinesSent);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Sender_failure_returns_line_diagnostic_and_progress_count()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("send-error.txt", "one\ntwo");
        var transfer = new Terminal2TextFileTransfer((_, _) =>
            ValueTask.FromException(new IOException("device closed")));

        var result = await transfer.RunFileAsync(path, new(SendCR: false, SendLF: false));

        Assert.Equal(Terminal2TextFileTransferStatus.Failed, result.Status);
        Assert.Equal(0, result.LinesSent);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("transfer.send-failed", issue.Code);
        Assert.Equal(1, issue.LineNumber);
        Assert.Contains("device closed", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_missing_file_with_actionable_read_error()
    {
        using var temporary = new TemporaryDirectory();
        var missingPath = Path.Combine(temporary.Path, "does-not-exist.txt");
        var transfer = new Terminal2TextFileTransfer((_, _) => ValueTask.CompletedTask);

        var result = await transfer.RunFileAsync(missingPath, new());

        Assert.Equal(Terminal2TextFileTransferStatus.Failed, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("transfer.read-failed", issue.Code);
        Assert.Contains(missingPath, issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public async Task Rejects_delay_outside_legacy_dialog_bounds(int delayMs)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Write("delay.txt", "line");
        var transfer = new Terminal2TextFileTransfer((_, _) => ValueTask.CompletedTask);

        var result = await transfer.RunFileAsync(path, new(InterLineDelayMs: delayMs));

        Assert.Equal(Terminal2TextFileTransferStatus.Failed, result.Status);
        Assert.Equal("transfer.delay-out-of-range", Assert.Single(result.Issues).Code);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SignalForge-Terminal2-transfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string contents)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
