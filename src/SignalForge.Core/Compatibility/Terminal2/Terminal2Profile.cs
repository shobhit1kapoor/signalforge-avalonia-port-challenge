/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

namespace SignalForge.Core.Compatibility.Terminal2;

public enum Terminal2ConnectionType
{
    Serial,
    TCPServer,
    TCPClient
}

/// <summary>
/// Portable representation of the fields read and written by Terminal2's Profile,
/// ConOptions, LoggingOptions, DisplayOptions, Embellishments, Macro and ColorFilter.
/// </summary>
public sealed class Terminal2Profile
{
    public const int MacroCapacity = 48 * 4;
    public const int TitleCapacity = 12 * 4;
    public const int FilterCapacity = 12;

    public Terminal2Profile()
    {
        Macros = Enumerable.Range(0, MacroCapacity).Select(_ => new Terminal2Macro()).ToArray();
        Titles = Enumerable.Repeat(string.Empty, TitleCapacity).ToArray();
        Filters = Enumerable.Range(0, FilterCapacity).Select(_ => new Terminal2Filter()).ToArray();
    }

    public bool Active { get; set; }
    public string Name { get; set; } = "Default";
    public bool SendCR { get; set; } = true;
    public bool SendLF { get; set; }
    public bool ClearCommand { get; set; }
    public bool SendAsYouType { get; set; }
    public bool StayOnTop { get; set; }

    public bool ShowCR { get; set; }
    public bool ShowLF { get; set; }
    public bool ShowInputTimestamp { get; set; }
    public bool ShowAscii { get; set; }
    public bool ShowHex { get; set; }

    public bool ShowOutputTimestamp { get; set; } = true;
    public string InputFont { get; set; } = "Courier New, 11.25pt";
    public string OutputFont { get; set; } = "Courier New, 8.25pt";
    public int InputBackgroundArgb { get; set; } = -1;
    public int InputForegroundArgb { get; set; } = -16777216;
    public int OutputBackgroundArgb { get; set; } = -2302756;
    public bool IgnoreCase { get; set; } = true;

    public string LogDirectory { get; set; } = (Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty) + @"\Logs";
    public string LogPrefix { get; set; } = "Default_";
    public int MaxLogSize { get; set; }
    public bool ShowLogControl { get; set; } = true;

    public Terminal2ConnectionType ConnectionType { get; set; } = Terminal2ConnectionType.Serial;
    public string SerialPort { get; set; } = "COM1";
    public string Baudrate { get; set; } = "115200";
    public string DataBits { get; set; } = "8";
    public string Handshaking { get; set; } = "None";
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "1";
    public bool InitialRTS { get; set; }
    public bool InitialDTR { get; set; }
    public string TcpConnectAddress { get; set; } = "localhost";
    public string TcpConnectPort { get; set; } = "5000";
    public string TcpListenPort { get; set; } = "5000";
    public bool RestartServer { get; set; }

    public Terminal2Filter[] Filters { get; set; }
    public string[] Titles { get; set; }
    public Terminal2Macro?[] Macros { get; set; }

    /// <summary>Unrecognized key/value lines retained so newer profile fields survive a round-trip.</summary>
    public List<Terminal2UnknownProfileEntry> UnknownEntries { get; } = new();
}

public sealed class Terminal2Filter
{
    public int Mode { get; set; }
    public string Text { get; set; } = string.Empty;
    public int ForegroundArgb { get; set; } = -16777216;
    public int BackgroundArgb { get; set; } = -1;
    public string Macro { get; set; } = string.Empty;
}

public sealed class Terminal2Macro
{
    public string Title { get; set; } = string.Empty;
    public int DelayBetweenCharacters { get; set; }
    public int DelayBetweenLinesMs { get; set; } = 200;
    public bool RepeatEnabled { get; set; }
    public int RepeatEveryMs { get; set; }
    public int StopAfterRepeats { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool AddCR { get; set; } = true;
    public bool AddLF { get; set; }
}

public sealed record Terminal2UnknownProfileEntry(string Section, string RawLine);
