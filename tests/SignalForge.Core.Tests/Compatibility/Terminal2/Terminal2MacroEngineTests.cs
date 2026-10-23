/*
 * Compatibility tests derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Protocols.Terminal2;

namespace SignalForge.Core.Tests.Compatibility.Terminal2;

public sealed class Terminal2MacroEngineTests
{
    [Fact]
    public async Task Sends_encoded_lines_with_crlf_and_source_timing()
    {
        // Source behavior: FrmMain.cs:1009-1010 splits on {0D}/{0A}; lines 1220-1249
        // append configured CR/LF and sleep after each output byte and sent line.
        var profile = new Terminal2Profile();
        profile.Macros[0] = new Terminal2Macro
        {
            Text = "A$42{0D}B{0A}",
            AddCR = true,
            AddLF = true,
            DelayBetweenCharacters = 2,
            DelayBetweenLinesMs = 7
        };
        var executor = new RecordingExecutor();

        var result = await Terminal2MacroEngine.RunAsync(profile, 0, executor);

        Assert.False(result.HasErrors);
        Assert.Equal(1, result.CompletedRuns);
        Assert.Equal(new byte[] { (byte)'A', 0x42, 0x0D, 0x0A, (byte)'B', 0x0D, 0x0A }, executor.Bytes);
        Assert.Equal(7, executor.WriteCalls.Count);
        Assert.Equal(2, executor.Delays.Count(delay => delay == TimeSpan.FromMilliseconds(7)));
        Assert.Equal(7, executor.Delays.Count(delay => delay == TimeSpan.FromMilliseconds(2)));
    }

    [Fact]
    public async Task Executes_directives_notes_and_stx_framing_as_in_source_order()
    {
        // Source behavior: FrmMain.cs:1049-1227 handles directives in order, sends
        // ## notes, applies #STX, removes # comments, and then sends the resulting bytes.
        var profile = new Terminal2Profile();
        profile.Macros[0] = new Terminal2Macro
        {
            Text = "#STARTLOG{0D}#STOPLOG{0D}#DISCONNECT{0D}#DELAY 5{0D}#BREAK 12{0D}#FREEZE{0D}#DTR{0D}#RTS{0D}## note{0D}#STX A$03",
            AddCR = true,
            DelayBetweenLinesMs = 0
        };
        var executor = new RecordingExecutor();

        var result = await Terminal2MacroEngine.RunAsync(profile, 0, executor);

        Assert.False(result.HasErrors);
        Assert.Contains(TimeSpan.FromMilliseconds(5), executor.Delays);
        Assert.Equal(
            new[]
            {
                Terminal2MacroEffectKind.StartLog,
                Terminal2MacroEffectKind.StopLog,
                Terminal2MacroEffectKind.Disconnect,
                Terminal2MacroEffectKind.Break,
                Terminal2MacroEffectKind.Freeze,
                Terminal2MacroEffectKind.ToggleDtr,
                Terminal2MacroEffectKind.ToggleRts,
                Terminal2MacroEffectKind.Note
            },
            executor.Effects.Select(effect => effect.Kind));
        Assert.Equal(12, executor.Effects.Single(effect => effect.Kind == Terminal2MacroEffectKind.Break).Argument);
        Assert.Equal("-NOTE->  note\n", executor.Effects.Single(effect => effect.Kind == Terminal2MacroEffectKind.Note).Text);

        var expectedFrame = Terminal2StxEtxCodec.EncodeText("A$03").Value!
            .Concat(new byte[] { 0x0D })
            .ToArray();
        Assert.Equal(expectedFrame, executor.Bytes);
    }

    [Fact]
    public async Task Macro_directive_calls_the_first_exact_title_match_and_repeat_bound_counts_extra_passes()
    {
        // Source behavior: FrmMain.cs:1030-1047 resolves #MACRO by title; lines 1256-1260
        // increment repeats after the first pass and stop only when count > configured bound.
        var profile = new Terminal2Profile();
        profile.Macros[0] = new Terminal2Macro
        {
            Text = "#MACRO inner{0D}x",
            AddCR = false,
            DelayBetweenLinesMs = 0,
            RepeatEnabled = true,
            StopAfterRepeats = 1
        };
        profile.Macros[1] = new Terminal2Macro
        {
            Title = "inner",
            Text = "child",
            AddCR = false,
            DelayBetweenLinesMs = 0
        };
        var executor = new RecordingExecutor();

        var result = await Terminal2MacroEngine.RunAsync(profile, 0, executor);

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.CompletedRuns);
        Assert.Equal("childxchildx", System.Text.Encoding.UTF8.GetString(executor.Bytes.ToArray()));
    }

    [Fact]
    public async Task Invalid_send_escape_is_reported_with_line_number_and_skips_only_that_line()
    {
        // Terminal2's SendDollarString is at FrmMain.cs:1936-1978. The compatibility
        // parser rejects malformed $HH instead of silently mapping bad nibbles to zero.
        var profile = new Terminal2Profile();
        profile.Macros[0] = new Terminal2Macro
        {
            Text = "bad=$G1{0D}ok",
            AddCR = false,
            DelayBetweenLinesMs = 0
        };
        var executor = new RecordingExecutor();

        var result = await Terminal2MacroEngine.RunAsync(profile, 0, executor);

        Assert.True(result.HasErrors);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("send-text.invalid-escape", issue.Code);
        Assert.Equal(1, issue.LineNumber);
        Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(executor.Bytes.ToArray()));
    }

    [Theory]
    [InlineData(60_001, 0, 0, nameof(Terminal2Macro.DelayBetweenCharacters))]
    [InlineData(0, 0, 60_001, nameof(Terminal2Macro.StopAfterRepeats))]
    [InlineData(0, 1_000_000_000, 0, nameof(Terminal2Macro.RepeatEveryMs))]
    public async Task Rejects_values_outside_the_macro_options_ui_bounds(
        int delayBetweenCharacters,
        int repeatEveryMs,
        int stopAfterRepeats,
        string expectedField)
    {
        // Source bounds are set by FrmMacroOptions.cs:178-191; repeat count is capped
        // at 60000 and character delay at 60000 milliseconds.
        var profile = new Terminal2Profile();
        profile.Macros[0] = new Terminal2Macro
        {
            Text = "x",
            AddCR = false,
            DelayBetweenLinesMs = 0,
            DelayBetweenCharacters = delayBetweenCharacters,
            RepeatEveryMs = repeatEveryMs,
            StopAfterRepeats = stopAfterRepeats
        };
        var executor = new RecordingExecutor();

        var result = await Terminal2MacroEngine.RunAsync(profile, 0, executor);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, issue => issue.Code == "macro.out-of-range");
        Assert.Contains(result.Issues, issue => issue.Field == expectedField);
        Assert.Empty(executor.Bytes);
    }

    [Fact]
    public async Task Disconnect_effect_stops_later_lines_and_repeat_passes()
    {
        // Source behavior: FrmMain.cs:1015-1026 and 1253-1267 check connection
        // state before each line and before starting another pass.
        var profile = new Terminal2Profile();
        profile.Macros[0] = new Terminal2Macro
        {
            Text = "#DISCONNECT{0D}after",
            AddCR = false,
            DelayBetweenLinesMs = 0,
            RepeatEnabled = true,
            StopAfterRepeats = 3
        };
        var executor = new RecordingExecutor { StopOnDisconnect = true };

        var result = await Terminal2MacroEngine.RunAsync(profile, 0, executor);

        Assert.Empty(executor.Bytes);
        Assert.Single(executor.Effects);
        Assert.Equal(0, result.CompletedRuns);
    }

    [Fact]
    public async Task Disconnect_effect_prevents_another_repeat_pass()
    {
        // Source behavior: FrmMain.cs:1262-1267 checks connection while waiting
        // for the next scheduled pass, then returns without waiting once offline.
        var profile = new Terminal2Profile();
        profile.Macros[0] = new Terminal2Macro
        {
            Text = "#DISCONNECT",
            AddCR = false,
            DelayBetweenLinesMs = 0,
            RepeatEnabled = true,
            StopAfterRepeats = 3,
            RepeatEveryMs = 10_000
        };
        var executor = new RecordingExecutor { StopOnDisconnect = true };

        var result = await Terminal2MacroEngine.RunAsync(profile, 0, executor);

        Assert.Equal(1, result.CompletedRuns);
        Assert.Single(executor.Effects);
        Assert.Empty(executor.Delays);
    }

    private sealed class RecordingExecutor : ITerminal2MacroExecutor
    {
        public bool CanContinue { get; set; } = true;
        public bool StopOnDisconnect { get; set; }
        public List<byte> Bytes { get; } = new();
        public List<byte[]> WriteCalls { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        public List<Terminal2MacroEffect> Effects { get; } = new();

        public ValueTask SendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = bytes.ToArray();
            WriteCalls.Add(copy);
            Bytes.AddRange(copy);
            return ValueTask.CompletedTask;
        }

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return ValueTask.CompletedTask;
        }

        public ValueTask ExecuteEffectAsync(Terminal2MacroEffect effect, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Effects.Add(effect);
            if (StopOnDisconnect && effect.Kind == Terminal2MacroEffectKind.Disconnect)
                CanContinue = false;
            return ValueTask.CompletedTask;
        }
    }
}
