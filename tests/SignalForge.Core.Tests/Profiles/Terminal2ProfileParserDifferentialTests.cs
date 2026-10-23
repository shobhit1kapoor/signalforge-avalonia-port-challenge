using System.Globalization;
using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Tests.Profiles;

/// <summary>
/// Compares the portable importer with a small source-derived reference for
/// overlapping, well-formed fields from Terminal2 v1.6.1.4 Database.ReadProfile.
/// The reference deliberately covers the legacy parser semantics exercised
/// here; it is not a replacement for the complete pinned source.
/// </summary>
public sealed class Terminal2ProfileParserDifferentialTests
{
    [Fact]
    public void Importer_matches_pinned_terminal2_source_for_profile_connection_title_and_macro_fields()
    {
        const string text = """
            [Profile]
            Name=Embedded name
            SendCR=False
            SendLF=True
            ClearCMD=True
            SendAsIType=True
            ShowHEX=True
            IgnoreCase=False
            LogDir=.\Logs
            F3Mode=2
            F3Text=alarm
            F3Color=-65536
            F3Back=-1
            F3Macro=Inspect
            [Connect]
            Type=TCPClient
            Serial=COM8
            Baudrate=57600
            DataBits=7
            Handshaking=Xon/Xoff
            Parity=Even
            StopBits=2
            InitialRTS=True
            InitialDTR=False
            RestartOnClose=True
            TCPConnectAddress=192.0.2.5
            TCPConnectPort=502
            TCPListenPort=1502
            [Titles]
            Title47=Last title
            [191]
            Title=Last macro
            ICD=3
            ILD=17
            RepeatON=True
            Delta=250
            Repeats=8
            Text=Write-$41-$0D
            AddCR=False
            AddLF=True
            """;
        const string filenameName = "fixture-profile";

        var source = ParsePinnedSourceSubset(text, filenameName);
        var imported = Terminal2ProfileSerializer.Deserialize(text, filenameName);

        Assert.True(imported.IsValid, string.Join(Environment.NewLine, imported.Issues));
        var profile = imported.Value!;
        Assert.Equal(source.Name, profile.Name);
        Assert.Equal(source.SendCr, profile.SendCR);
        Assert.Equal(source.SendLf, profile.SendLF);
        Assert.Equal(source.ClearCommand, profile.ClearCommand);
        Assert.Equal(source.SendAsYouType, profile.SendAsYouType);
        Assert.Equal(source.ShowHex, profile.ShowHex);
        Assert.Equal(source.IgnoreCase, profile.IgnoreCase);
        Assert.Equal(source.LogDirectory, profile.LogDirectory);
        Assert.Equal(source.FilterMode, profile.Filters[3].Mode);
        Assert.Equal(source.FilterText, profile.Filters[3].Text);
        Assert.Equal(source.FilterColor, profile.Filters[3].ForegroundArgb);
        Assert.Equal(source.FilterBack, profile.Filters[3].BackgroundArgb);
        Assert.Equal(source.FilterMacro, profile.Filters[3].Macro);
        Assert.Equal(source.ConnectionType, profile.ConnectionType);
        Assert.Equal(source.SerialPort, profile.SerialPort);
        Assert.Equal(source.Baudrate, profile.Baudrate);
        Assert.Equal(source.DataBits, profile.DataBits);
        Assert.Equal(source.Handshaking, profile.Handshaking);
        Assert.Equal(source.Parity, profile.Parity);
        Assert.Equal(source.StopBits, profile.StopBits);
        Assert.Equal(source.InitialRts, profile.InitialRTS);
        Assert.Equal(source.InitialDtr, profile.InitialDTR);
        Assert.Equal(source.RestartServer, profile.RestartServer);
        Assert.Equal(source.TcpAddress, profile.TcpConnectAddress);
        Assert.Equal(source.TcpPort, profile.TcpConnectPort);
        Assert.Equal(source.ListenPort, profile.TcpListenPort);
        Assert.Equal(source.Title, profile.Titles[47]);
        Assert.Equal(source.MacroTitle, profile.Macros[191]!.Title);
        Assert.Equal(source.MacroIcd, profile.Macros[191]!.DelayBetweenCharacters);
        Assert.Equal(source.MacroIld, profile.Macros[191]!.DelayBetweenLinesMs);
        Assert.Equal(source.MacroRepeat, profile.Macros[191]!.RepeatEnabled);
        Assert.Equal(source.MacroDelta, profile.Macros[191]!.RepeatEveryMs);
        Assert.Equal(source.MacroRepeats, profile.Macros[191]!.StopAfterRepeats);
        Assert.Equal(source.MacroText, profile.Macros[191]!.Text);
        Assert.Equal(source.MacroAddCr, profile.Macros[191]!.AddCR);
        Assert.Equal(source.MacroAddLf, profile.Macros[191]!.AddLF);
    }

    private static SourceProfile ParsePinnedSourceSubset(string contents, string requestedName)
    {
        // Transcribed from the pinned Database.ReadProfile loops: section
        // switching is exact, booleans use case-sensitive Contains("True"),
        // and filename/requested name wins after the file has been read.
        var profile = new SourceProfile();
        var section = "";
        var macroIndex = -1;
        foreach (var line in contents.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line is "[Profile]" or "[Connect]" or "[Titles]")
            {
                section = line[1..^1];
                macroIndex = -1;
                continue;
            }
            if (line.Length > 2 && line[0] == '[' && line[^1] == ']' &&
                int.TryParse(line[1..^1], NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsedMacro))
            {
                section = "Macros";
                macroIndex = parsedMacro;
                continue;
            }

            if (section == "Titles" && line.StartsWith("Title", StringComparison.Ordinal))
            {
                var parts = line.Split('=');
                var index = LegacyInt(parts[0][5..]);
                if (index == 47)
                    profile.Title = parts[1];
            }
            else if (section == "Profile")
            {
                if (line.StartsWith("SendCR=", StringComparison.Ordinal)) profile.SendCr = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("SendLF=", StringComparison.Ordinal)) profile.SendLf = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("ClearCMD=", StringComparison.Ordinal)) profile.ClearCommand = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("SendAsIType=", StringComparison.Ordinal)) profile.SendAsYouType = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("ShowHEX=", StringComparison.Ordinal)) profile.ShowHex = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("IgnoreCase=", StringComparison.Ordinal)) profile.IgnoreCase = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("LogDir=", StringComparison.Ordinal)) profile.LogDirectory = line[7..];
                else if (line.StartsWith("F3Mode=", StringComparison.Ordinal)) profile.FilterMode = LegacyInt(line[7..]);
                else if (line.StartsWith("F3Text=", StringComparison.Ordinal)) profile.FilterText = line[7..];
                else if (line.StartsWith("F3Color=", StringComparison.Ordinal)) profile.FilterColor = LegacyInt(line[8..]);
                else if (line.StartsWith("F3Back=", StringComparison.Ordinal)) profile.FilterBack = LegacyInt(line[7..]);
                else if (line.StartsWith("F3Macro=", StringComparison.Ordinal)) profile.FilterMacro = line[8..];
            }
            else if (section == "Connect")
            {
                if (line == "Type=TCPClient") profile.ConnectionType = Terminal2ConnectionType.TCPClient;
                else if (line == "Type=TCPServer") profile.ConnectionType = Terminal2ConnectionType.TCPServer;
                else if (line.StartsWith("Serial=", StringComparison.Ordinal)) profile.SerialPort = line[7..];
                else if (line.StartsWith("Baudrate=", StringComparison.Ordinal)) profile.Baudrate = line[9..];
                else if (line.StartsWith("DataBits=", StringComparison.Ordinal)) profile.DataBits = line[9..];
                else if (line.StartsWith("Handshaking=", StringComparison.Ordinal)) profile.Handshaking = line[12..];
                else if (line.StartsWith("Parity=", StringComparison.Ordinal)) profile.Parity = line[7..];
                else if (line.StartsWith("StopBits=", StringComparison.Ordinal)) profile.StopBits = line[9..];
                else if (line.StartsWith("InitialRTS=", StringComparison.Ordinal)) profile.InitialRts = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("InitialDTR=", StringComparison.Ordinal)) profile.InitialDtr = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("RestartOnClose=", StringComparison.Ordinal)) profile.RestartServer = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("TCPConnectAddress=", StringComparison.Ordinal)) profile.TcpAddress = line[18..];
                else if (line.StartsWith("TCPConnectPort=", StringComparison.Ordinal)) profile.TcpPort = line[15..];
                else if (line.StartsWith("TCPListenPort=", StringComparison.Ordinal)) profile.ListenPort = line[14..];
            }
            else if (section == "Macros" && macroIndex == 191)
            {
                if (line.StartsWith("Title=", StringComparison.Ordinal)) profile.MacroTitle = line[6..];
                else if (line.StartsWith("ICD=", StringComparison.Ordinal)) profile.MacroIcd = LegacyInt(line[4..]);
                else if (line.StartsWith("ILD=", StringComparison.Ordinal)) profile.MacroIld = LegacyInt(line[4..]);
                else if (line.StartsWith("RepeatON=", StringComparison.Ordinal)) profile.MacroRepeat = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("Delta=", StringComparison.Ordinal)) profile.MacroDelta = LegacyInt(line[6..]);
                else if (line.StartsWith("Repeats=", StringComparison.Ordinal)) profile.MacroRepeats = LegacyInt(line[8..]);
                else if (line.StartsWith("Text=", StringComparison.Ordinal)) profile.MacroText = line[5..];
                else if (line.StartsWith("AddCR=", StringComparison.Ordinal)) profile.MacroAddCr = line.Contains("True", StringComparison.Ordinal);
                else if (line.StartsWith("AddLF=", StringComparison.Ordinal)) profile.MacroAddLf = line.Contains("True", StringComparison.Ordinal);
            }
        }

        profile.Name = requestedName;
        return profile;
    }

    private static int LegacyInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var result) ? result : 0;

    private sealed class SourceProfile
    {
        public string Name { get; set; } = "Default";
        public bool SendCr { get; set; } = true;
        public bool SendLf { get; set; }
        public bool ClearCommand { get; set; }
        public bool SendAsYouType { get; set; }
        public bool ShowHex { get; set; }
        public bool IgnoreCase { get; set; } = true;
        public string LogDirectory { get; set; } = (Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "") + @"\Logs";
        public int FilterMode { get; set; }
        public string FilterText { get; set; } = "";
        public int FilterColor { get; set; } = -16777216;
        public int FilterBack { get; set; } = -1;
        public string FilterMacro { get; set; } = "";
        public Terminal2ConnectionType ConnectionType { get; set; }
        public string SerialPort { get; set; } = "COM1";
        public string Baudrate { get; set; } = "115200";
        public string DataBits { get; set; } = "8";
        public string Handshaking { get; set; } = "None";
        public string Parity { get; set; } = "None";
        public string StopBits { get; set; } = "1";
        public bool InitialRts { get; set; }
        public bool InitialDtr { get; set; }
        public bool RestartServer { get; set; }
        public string TcpAddress { get; set; } = "localhost";
        public string TcpPort { get; set; } = "5000";
        public string ListenPort { get; set; } = "5000";
        public string Title { get; set; } = "";
        public string MacroTitle { get; set; } = "";
        public int MacroIcd { get; set; }
        public int MacroIld { get; set; } = 200;
        public bool MacroRepeat { get; set; }
        public int MacroDelta { get; set; }
        public int MacroRepeats { get; set; }
        public string MacroText { get; set; } = "";
        public bool MacroAddCr { get; set; } = true;
        public bool MacroAddLf { get; set; }
    }
}
