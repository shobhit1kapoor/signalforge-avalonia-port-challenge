/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.Globalization;

namespace SignalForge.Core.Compatibility.Terminal2;

/// <summary>Imports the v2 Terminal2 macro text format into a profile without file-system side effects.</summary>
public static class Terminal2MacroFileImporter
{
    private const string Header = "# Terminal macro file v2";

    public static Terminal2ValidationResult<Terminal2Profile> Import(string contents, string profileName)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(profileName);

        var normalized = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        // File.ReadAllLines, used by Terminal2, does not treat a final line terminator
        // as an additional blank macro line.
        if (normalized.EndsWith('\n'))
            lines = lines[..^1];
        if (lines.Length == 0 || !lines[0].StartsWith(Header, StringComparison.Ordinal))
        {
            return Terminal2ValidationResult<Terminal2Profile>.Failure(
                "tmf.invalid-header",
                "The file does not start with the Terminal macro file v2 header.");
        }

        var profile = CreateTerminal2NewProfile(profileName);
        var issues = new List<Terminal2ValidationIssue>();
        var macroIndex = 0;
        for (var i = 1; i < lines.Length - 1; i += 2)
        {
            if (macroIndex >= Terminal2Profile.MacroCapacity)
            {
                issues.Add(new Terminal2ValidationIssue(
                    "tmf.too-many-macros",
                    $"The TMF contains more than {Terminal2Profile.MacroCapacity} macro pairs.",
                    LineNumber: i + 1));
                break;
            }

            var title = lines[i];
            var text = lines[i + 1];
            var addCr = text.EndsWith("$0D", StringComparison.Ordinal);
            if (addCr)
                text = text[..^3];

            profile.Macros[macroIndex++] = new Terminal2Macro
            {
                Title = title,
                DelayBetweenCharacters = 0,
                DelayBetweenLinesMs = 0,
                RepeatEnabled = false,
                RepeatEveryMs = 0,
                StopAfterRepeats = 0,
                Text = text,
                AddCR = addCr,
                AddLF = false
            };
        }

        return new Terminal2ValidationResult<Terminal2Profile>(profile, issues);
    }

    /// <summary>Terminal2's NewProfile defaults, before its TMF macro pairs are appended.</summary>
    public static Terminal2Profile CreateTerminal2NewProfile(string name)
    {
        var profile = new Terminal2Profile
        {
            Name = name,
            SendCR = true,
            SendLF = false,
            ClearCommand = false,
            SendAsYouType = false,
            StayOnTop = false,
            ShowOutputTimestamp = true,
            ShowCR = false,
            ShowLF = false,
            ShowAscii = true,
            ShowHex = false,
            ShowInputTimestamp = true,
            LogDirectory = @"C:\Temp",
            LogPrefix = "Default_",
            MaxLogSize = 0,
            OutputFont = "Courier New, 8.25pt",
            InputFont = "Courier New, 11.25pt",
            InputBackgroundArgb = -1,
            InputForegroundArgb = -16777216,
            OutputBackgroundArgb = -2302756,
            IgnoreCase = true,
            ConnectionType = Terminal2ConnectionType.TCPServer,
            SerialPort = "COM1",
            Baudrate = "115200",
            DataBits = "8",
            Handshaking = "None",
            Parity = "None",
            StopBits = "1",
            InitialRTS = false,
            InitialDTR = false,
            TcpConnectAddress = "localhost",
            TcpConnectPort = "5000",
            TcpListenPort = "5000",
            RestartServer = false
        };

        var foregrounds = new[]
        {
            -29696, -16776961, -16728064, -16751616, -65281, -8355840,
            -4194112, -16744448, -16727872, -24454, -256, -16777216
        };
        for (var i = 0; i < profile.Filters.Length; i++)
        {
            profile.Filters[i].Mode = 1;
            profile.Filters[i].ForegroundArgb = foregrounds[i];
            profile.Filters[i].BackgroundArgb = i == 10 ? -65536 : i == 11 ? -16711936 : -1;
        }
        profile.Filters[10].Text = "ERROR";

        return profile;
    }
}
