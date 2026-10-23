/*
 * Compatibility tests derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Tests.Compatibility.Terminal2;

public sealed class Terminal2ProfileCompatibilityTests
{
    [Fact]
    public void Profile_round_trip_preserves_legacy_fields_and_all_slots()
    {
        var profile = new Terminal2Profile
        {
            Name = "Bench A",
            SendCR = false,
            SendLF = true,
            ClearCommand = true,
            SendAsYouType = true,
            StayOnTop = true,
            ShowCR = true,
            ShowLF = true,
            ShowInputTimestamp = true,
            ShowAscii = true,
            ShowHex = true,
            ShowOutputTimestamp = false,
            InputFont = "Consolas, 10pt",
            OutputFont = "Consolas, 9pt",
            InputBackgroundArgb = -1234567,
            InputForegroundArgb = -7654321,
            OutputBackgroundArgb = -2345678,
            IgnoreCase = false,
            LogDirectory = @"D:\SignalForge Logs",
            LogPrefix = "Bench_",
            MaxLogSize = 42,
            ConnectionType = Terminal2ConnectionType.TCPClient,
            SerialPort = "COM19",
            Baudrate = "57600",
            DataBits = "7",
            Handshaking = "Xon/Xoff",
            Parity = "Even",
            StopBits = "2",
            InitialRTS = true,
            InitialDTR = true,
            TcpConnectAddress = "192.0.2.12",
            TcpConnectPort = "502",
            TcpListenPort = "1502",
            RestartServer = true
        };
        profile.Titles[47] = "Last title";
        profile.Filters[11] = new Terminal2Filter
        {
            Mode = 0,
            Text = "custom search slot",
            ForegroundArgb = -2222222,
            BackgroundArgb = -3333333,
            Macro = "macro-11"
        };
        profile.Macros[191] = new Terminal2Macro
        {
            Title = "Last macro",
            DelayBetweenCharacters = 3,
            DelayBetweenLinesMs = 17,
            RepeatEnabled = true,
            RepeatEveryMs = 250,
            StopAfterRepeats = 8,
            Text = "Write=$41=$0D",
            AddCR = false,
            AddLF = true
        };
        profile.UnknownEntries.Add(new Terminal2UnknownProfileEntry("Profile", "FutureField=preserved"));

        var serialized = Terminal2ProfileSerializer.Serialize(profile);

        Assert.True(serialized.IsValid, string.Join(Environment.NewLine, serialized.Issues));
        Assert.Contains("F11Text=custom search slot\n", serialized.Value);
        Assert.Contains("[191]\n", serialized.Value);
        Assert.Contains("FutureField=preserved\n", serialized.Value);

        var imported = Terminal2ProfileSerializer.Deserialize(serialized.Value!);

        Assert.True(imported.IsValid, string.Join(Environment.NewLine, imported.Issues));
        var actual = imported.Value!;
        Assert.Equal(profile.Name, actual.Name);
        Assert.Equal(profile.SendCR, actual.SendCR);
        Assert.Equal(profile.SendLF, actual.SendLF);
        Assert.Equal(profile.ClearCommand, actual.ClearCommand);
        Assert.Equal(profile.SendAsYouType, actual.SendAsYouType);
        Assert.Equal(profile.StayOnTop, actual.StayOnTop);
        Assert.Equal(profile.ShowCR, actual.ShowCR);
        Assert.Equal(profile.ShowLF, actual.ShowLF);
        Assert.Equal(profile.ShowInputTimestamp, actual.ShowInputTimestamp);
        Assert.Equal(profile.ShowAscii, actual.ShowAscii);
        Assert.Equal(profile.ShowHex, actual.ShowHex);
        Assert.Equal(profile.ShowOutputTimestamp, actual.ShowOutputTimestamp);
        Assert.Equal(profile.InputFont, actual.InputFont);
        Assert.Equal(profile.OutputFont, actual.OutputFont);
        Assert.Equal(profile.InputBackgroundArgb, actual.InputBackgroundArgb);
        Assert.Equal(profile.InputForegroundArgb, actual.InputForegroundArgb);
        Assert.Equal(profile.OutputBackgroundArgb, actual.OutputBackgroundArgb);
        Assert.Equal(profile.IgnoreCase, actual.IgnoreCase);
        Assert.Equal(profile.LogDirectory, actual.LogDirectory);
        Assert.Equal(profile.LogPrefix, actual.LogPrefix);
        Assert.Equal(profile.MaxLogSize, actual.MaxLogSize);
        Assert.Equal(profile.ConnectionType, actual.ConnectionType);
        Assert.Equal(profile.SerialPort, actual.SerialPort);
        Assert.Equal(profile.Baudrate, actual.Baudrate);
        Assert.Equal(profile.DataBits, actual.DataBits);
        Assert.Equal(profile.Handshaking, actual.Handshaking);
        Assert.Equal(profile.Parity, actual.Parity);
        Assert.Equal(profile.StopBits, actual.StopBits);
        Assert.Equal(profile.InitialRTS, actual.InitialRTS);
        Assert.Equal(profile.InitialDTR, actual.InitialDTR);
        Assert.Equal(profile.TcpConnectAddress, actual.TcpConnectAddress);
        Assert.Equal(profile.TcpConnectPort, actual.TcpConnectPort);
        Assert.Equal(profile.TcpListenPort, actual.TcpListenPort);
        Assert.Equal(profile.RestartServer, actual.RestartServer);
        Assert.Equal(profile.Titles[47], actual.Titles[47]);
        Assert.Equal(profile.Filters[11].Text, actual.Filters[11].Text);
        Assert.Equal(profile.Filters[11].ForegroundArgb, actual.Filters[11].ForegroundArgb);
        Assert.Equal(profile.Filters[11].BackgroundArgb, actual.Filters[11].BackgroundArgb);
        Assert.Equal(profile.Filters[11].Macro, actual.Filters[11].Macro);
        Assert.Equal(profile.Macros[191]!.Title, actual.Macros[191]!.Title);
        Assert.Equal(profile.Macros[191]!.DelayBetweenCharacters, actual.Macros[191]!.DelayBetweenCharacters);
        Assert.Equal(profile.Macros[191]!.DelayBetweenLinesMs, actual.Macros[191]!.DelayBetweenLinesMs);
        Assert.Equal(profile.Macros[191]!.RepeatEnabled, actual.Macros[191]!.RepeatEnabled);
        Assert.Equal(profile.Macros[191]!.RepeatEveryMs, actual.Macros[191]!.RepeatEveryMs);
        Assert.Equal(profile.Macros[191]!.StopAfterRepeats, actual.Macros[191]!.StopAfterRepeats);
        Assert.Equal(profile.Macros[191]!.Text, actual.Macros[191]!.Text);
        Assert.Equal(profile.Macros[191]!.AddCR, actual.Macros[191]!.AddCR);
        Assert.Equal(profile.Macros[191]!.AddLF, actual.Macros[191]!.AddLF);
        Assert.Contains(actual.UnknownEntries, entry => entry.RawLine == "FutureField=preserved");
    }

    [Fact]
    public void Import_profile_name_overrides_embedded_name_like_terminal2()
    {
        const string text = "[Profile]\nName=inside-file\nSendCR=True\n[Connect]\nType=TCPServer\n";

        var result = Terminal2ProfileSerializer.Deserialize(text, "from-filename");

        Assert.True(result.IsValid);
        Assert.Equal("from-filename", result.Value!.Name);
        Assert.Equal(Terminal2ConnectionType.TCPServer, result.Value.ConnectionType);
    }

    [Fact]
    public void Importer_dispatches_profile_and_tmf_text_by_file_extension()
    {
        var profile = Terminal2ProfileImporter.Import("station.profile", "[Profile]\nName=embedded\n");
        var tmf = Terminal2ProfileImporter.Import("macros.tmf", "# Terminal macro file v2\nStart\n$41\n");

        Assert.True(profile.IsValid);
        Assert.Equal("station", profile.Value!.Name);
        Assert.True(tmf.IsValid);
        Assert.Equal("macros", tmf.Value!.Name);
        Assert.Equal("Start", tmf.Value.Macros[0]!.Title);
    }

    [Fact]
    public void Terminal2_generated_default_profile_imports_and_round_trips()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Terminal2", "Default.profile");
        var contents = File.ReadAllText(fixturePath);

        var imported = Terminal2ProfileImporter.Import("Default.profile", contents);

        Assert.True(imported.IsValid, string.Join(Environment.NewLine, imported.Issues));
        var original = imported.Value!;
        Assert.Equal("Default", original.Name);
        Assert.True(original.SendCR);
        Assert.False(original.SendLF);
        Assert.Equal(".\\Logs", original.LogDirectory);
        Assert.Equal(Terminal2ConnectionType.Serial, original.ConnectionType);
        Assert.Equal("COM3", original.SerialPort);
        Assert.Equal("115200", original.Baudrate);
        Assert.Equal(192, original.Macros.Length);
        Assert.Equal(48, original.Titles.Length);
        Assert.Equal(12, original.Filters.Length);

        var serialized = Terminal2ProfileSerializer.Serialize(original);

        Assert.True(serialized.IsValid, string.Join(Environment.NewLine, serialized.Issues));
        var roundTripped = Terminal2ProfileImporter.Import("Default.profile", serialized.Value!);

        Assert.True(roundTripped.IsValid, string.Join(Environment.NewLine, roundTripped.Issues));
        Assert.Equal(original.Name, roundTripped.Value!.Name);
        Assert.Equal(original.SendCR, roundTripped.Value.SendCR);
        Assert.Equal(original.SendLF, roundTripped.Value.SendLF);
        Assert.Equal(original.ConnectionType, roundTripped.Value.ConnectionType);
        Assert.Equal(original.SerialPort, roundTripped.Value.SerialPort);
        Assert.Equal(original.Baudrate, roundTripped.Value.Baudrate);
        Assert.Equal(original.OutputBackgroundArgb, roundTripped.Value.OutputBackgroundArgb);
        Assert.Equal(original.Macros.Length, roundTripped.Value.Macros.Length);
        Assert.Equal(original.Titles.Length, roundTripped.Value.Titles.Length);
        Assert.Equal(original.Filters.Length, roundTripped.Value.Filters.Length);
    }

    [Fact]
    public void Invalid_typed_profile_values_are_reported_with_line_and_field()
    {
        const string text = "[Profile]\nSendCR=maybe\nMaxLogSize=large\n[Connect]\nType=Usb\n";

        var result = Terminal2ProfileSerializer.Deserialize(text);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "profile.invalid-boolean" && issue.LineNumber == 2 && issue.Field == "SendCR");
        Assert.Contains(result.Issues, issue => issue.Code == "profile.invalid-integer" && issue.LineNumber == 3);
        Assert.Contains(result.Issues, issue => issue.Code == "profile.connection-type" && issue.LineNumber == 5);
    }

    [Fact]
    public void Export_rejects_values_that_cannot_be_represented_as_legacy_single_lines()
    {
        var profile = new Terminal2Profile();
        profile.Macros[0] = new Terminal2Macro { Title = "multi\nline" };

        var result = Terminal2ProfileSerializer.Serialize(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "profile.multiline-value");
    }
}
