/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.Globalization;
using System.Text;

namespace SignalForge.Core.Compatibility.Terminal2;

/// <summary>Reads and writes Terminal2's INI-like .profile text format.</summary>
public static class Terminal2ProfileSerializer
{
    private const string ProfileSection = "Profile";
    private const string ConnectSection = "Connect";
    private const string TitlesSection = "Titles";

    public static Terminal2ValidationResult<Terminal2Profile> Deserialize(string contents, string? profileName = null)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var profile = new Terminal2Profile();
        var issues = new List<Terminal2ValidationIssue>();
        var section = string.Empty;
        int? macroIndex = null;
        var lineNumber = 0;

        using var reader = new StringReader(contents);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                macroIndex = null;

                if (section is ProfileSection or ConnectSection or TitlesSection)
                    continue;

                if (int.TryParse(section, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIndex))
                {
                    if (parsedIndex is >= 0 and < Terminal2Profile.MacroCapacity)
                    {
                        macroIndex = parsedIndex;
                        profile.Macros[parsedIndex] ??= new Terminal2Macro();
                    }
                    else
                    {
                        issues.Add(new Terminal2ValidationIssue(
                            "profile.macro-index-out-of-range",
                            $"Macro section [{section}] is outside the supported range 0–{Terminal2Profile.MacroCapacity - 1}.",
                            LineNumber: lineNumber));
                    }

                    continue;
                }

                issues.Add(new Terminal2ValidationIssue(
                    "profile.unknown-section",
                    $"Unknown profile section [{section}] was retained for export.",
                    Terminal2ValidationSeverity.Warning,
                    LineNumber: lineNumber));
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                PreserveUnknown(profile, section, line, issues, lineNumber);
                continue;
            }

            var key = line[..equals];
            var value = line[(equals + 1)..];
            switch (section)
            {
                case ProfileSection:
                    ReadProfileField(profile, key, value, issues, lineNumber);
                    break;
                case ConnectSection:
                    ReadConnectionField(profile, key, value, issues, lineNumber);
                    break;
                case TitlesSection:
                    ReadTitleField(profile, key, value, issues, lineNumber);
                    break;
                default:
                    if (macroIndex is { } index)
                    {
                        if (!ReadMacroField(profile.Macros[index]!, key, value, issues, lineNumber, index))
                            PreserveUnknown(profile, section, line, issues, lineNumber);
                    }
                    else
                        PreserveUnknown(profile, section, line, issues, lineNumber);
                    break;
            }
        }

        if (profileName is not null)
            profile.Name = profileName;

        return new Terminal2ValidationResult<Terminal2Profile>(profile, issues);
    }

    public static Terminal2ValidationResult<string> Serialize(Terminal2Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var issues = ValidateForExport(profile);
        if (issues.Any(issue => issue.Severity == Terminal2ValidationSeverity.Error))
            return new Terminal2ValidationResult<string>(null, issues);

        var output = new StringBuilder();
        output.Append("# Terminal2 compatible profile\n");
        output.Append("# Derived from Terminal2 by Michael Heyns; Copyright (C) 2021-2024 Michael Heyns\n");
        output.Append("# Licensed under GNU GPL-3.0-or-later; see LICENSE and external/Terminal2/LICENSE\n\n");

        output.Append("[Profile]\n");
        Write(output, "Name", profile.Name);
        Write(output, "SendCR", profile.SendCR);
        Write(output, "SendLF", profile.SendLF);
        Write(output, "ClearCMD", profile.ClearCommand);
        Write(output, "SendAsIType", profile.SendAsYouType);
        Write(output, "OnTop", profile.StayOnTop);
        Write(output, "TimeOutput", profile.ShowOutputTimestamp);
        Write(output, "ShowCR", profile.ShowCR);
        Write(output, "ShowLF", profile.ShowLF);
        Write(output, "ShowASCII", profile.ShowAscii);
        Write(output, "ShowHEX", profile.ShowHex);
        Write(output, "TimeInput", profile.ShowInputTimestamp);
        Write(output, "LogDir", profile.LogDirectory);
        Write(output, "LogPrefix", profile.LogPrefix);
        Write(output, "MaxLogSize", profile.MaxLogSize);
        Write(output, "OutFont", profile.OutputFont);
        Write(output, "InFont", profile.InputFont);
        output.Append("# Input panel colours\n");
        Write(output, "InBackColor", profile.InputBackgroundArgb);
        Write(output, "InTextColor", profile.InputForegroundArgb);
        output.Append("# Output panel colours\n");
        Write(output, "OutBackColor", profile.OutputBackgroundArgb);
        Write(output, "IgnoreCase", profile.IgnoreCase);
        output.Append("# Color filters\n");
        for (var i = 0; i < profile.Filters.Length; i++)
        {
            var filter = profile.Filters[i];
            Write(output, $"F{i}Mode", filter.Mode);
            Write(output, $"F{i}Text", filter.Text);
            Write(output, $"F{i}Color", filter.ForegroundArgb);
            Write(output, $"F{i}Back", filter.BackgroundArgb);
            Write(output, $"F{i}Macro", filter.Macro);
        }
        AppendUnknown(output, profile, ProfileSection);
        output.Append('\n');

        output.Append("[Connect]\n");
        Write(output, "Type", profile.ConnectionType.ToString());
        Write(output, "Serial", profile.SerialPort);
        Write(output, "Baudrate", profile.Baudrate);
        Write(output, "DataBits", profile.DataBits);
        Write(output, "Handshaking", profile.Handshaking);
        Write(output, "Parity", profile.Parity);
        Write(output, "StopBits", profile.StopBits);
        Write(output, "InitialRTS", profile.InitialRTS);
        Write(output, "InitialDTR", profile.InitialDTR);
        Write(output, "TCPConnectAddress", profile.TcpConnectAddress);
        Write(output, "TCPConnectPort", profile.TcpConnectPort);
        Write(output, "TCPListenPort", profile.TcpListenPort);
        Write(output, "RestartOnClose", profile.RestartServer);
        AppendUnknown(output, profile, ConnectSection);
        output.Append('\n');

        output.Append("[Titles]\n");
        for (var i = 0; i < profile.Titles.Length; i++)
        {
            if (profile.Titles[i].Length > 0)
                Write(output, $"Title{i}", profile.Titles[i]);
        }
        AppendUnknown(output, profile, TitlesSection);
        output.Append('\n');

        for (var i = 0; i < profile.Macros.Length; i++)
        {
            if (profile.Macros[i] is not { } macro || !ShouldWriteMacro(macro))
                continue;

            output.Append('[').Append(i.ToString(CultureInfo.InvariantCulture)).Append("]\n");
            Write(output, "Title", macro.Title);
            Write(output, "ICD", macro.DelayBetweenCharacters);
            Write(output, "ILD", macro.DelayBetweenLinesMs);
            Write(output, "RepeatON", macro.RepeatEnabled);
            Write(output, "Delta", macro.RepeatEveryMs);
            Write(output, "Repeats", macro.StopAfterRepeats);
            Write(output, "Text", macro.Text);
            Write(output, "AddCR", macro.AddCR);
            Write(output, "AddLF", macro.AddLF);
            AppendUnknown(output, profile, i.ToString(CultureInfo.InvariantCulture));
            output.Append('\n');
        }

        for (var i = 0; i < profile.Macros.Length; i++)
        {
            var section = i.ToString(CultureInfo.InvariantCulture);
            if (profile.Macros[i] is not null && ShouldWriteMacro(profile.Macros[i]!)
                || !profile.UnknownEntries.Any(entry => string.Equals(entry.Section, section, StringComparison.Ordinal)))
                continue;

            output.Append('[').Append(section).Append("]\n");
            AppendUnknown(output, profile, section);
            output.Append('\n');
        }

        foreach (var unknownSection in profile.UnknownEntries
                     .Select(entry => entry.Section)
                     .Where(IsUnknownSection)
                     .Distinct(StringComparer.Ordinal))
        {
            output.Append('[').Append(unknownSection).Append("]\n");
            AppendUnknown(output, profile, unknownSection);
            output.Append('\n');
        }

        return new Terminal2ValidationResult<string>(output.ToString(), issues);
    }

    private static void ReadProfileField(
        Terminal2Profile profile,
        string key,
        string value,
        List<Terminal2ValidationIssue> issues,
        int line)
    {
        switch (key)
        {
            case "Name": profile.Name = value; return;
            case "SendCR": ReadBoolean(value, v => profile.SendCR = v, issues, line, key); return;
            case "SendLF": ReadBoolean(value, v => profile.SendLF = v, issues, line, key); return;
            case "ClearCMD": ReadBoolean(value, v => profile.ClearCommand = v, issues, line, key); return;
            case "SendAsIType": ReadBoolean(value, v => profile.SendAsYouType = v, issues, line, key); return;
            case "OnTop": ReadBoolean(value, v => profile.StayOnTop = v, issues, line, key); return;
            case "TimeOutput": ReadBoolean(value, v => profile.ShowOutputTimestamp = v, issues, line, key); return;
            case "ShowCR": ReadBoolean(value, v => profile.ShowCR = v, issues, line, key); return;
            case "ShowLF": ReadBoolean(value, v => profile.ShowLF = v, issues, line, key); return;
            case "ShowASCII": ReadBoolean(value, v => profile.ShowAscii = v, issues, line, key); return;
            case "ShowHEX": ReadBoolean(value, v => profile.ShowHex = v, issues, line, key); return;
            case "TimeInput": ReadBoolean(value, v => profile.ShowInputTimestamp = v, issues, line, key); return;
            case "LogDir": profile.LogDirectory = value; return;
            case "LogPrefix": profile.LogPrefix = value; return;
            case "OutFont": if (!value.EndsWith("(none)", StringComparison.Ordinal)) profile.OutputFont = value; return;
            case "InFont": if (!value.EndsWith("(none)", StringComparison.Ordinal)) profile.InputFont = value; return;
            case "IgnoreCase": ReadBoolean(value, v => profile.IgnoreCase = v, issues, line, key); return;
            case "InBackColor": ReadInteger(value, v => profile.InputBackgroundArgb = v, issues, line, key); return;
            case "InTextColor": ReadInteger(value, v => profile.InputForegroundArgb = v, issues, line, key); return;
            case "OutBackColor": ReadInteger(value, v => profile.OutputBackgroundArgb = v, issues, line, key); return;
            case "MaxLogSize": ReadInteger(value, v => profile.MaxLogSize = v, issues, line, key); return;
        }

        if (TryFilterKey(key, out var filterIndex, out var filterField))
        {
            var filter = profile.Filters[filterIndex];
            switch (filterField)
            {
                case "Mode": ReadInteger(value, v => filter.Mode = v, issues, line, key); return;
                case "Text": filter.Text = value; return;
                case "Color": ReadInteger(value, v => filter.ForegroundArgb = v, issues, line, key); return;
                case "Back": ReadInteger(value, v => filter.BackgroundArgb = v, issues, line, key); return;
                case "Macro": filter.Macro = value; return;
            }
        }

        PreserveUnknown(profile, ProfileSection, $"{key}={value}", issues, line);
    }

    private static void ReadConnectionField(
        Terminal2Profile profile,
        string key,
        string value,
        List<Terminal2ValidationIssue> issues,
        int line)
    {
        switch (key)
        {
            case "Type":
                if (Enum.TryParse<Terminal2ConnectionType>(value, ignoreCase: false, out var type)
                    && Enum.IsDefined(type))
                    profile.ConnectionType = type;
                else
                    issues.Add(new Terminal2ValidationIssue("profile.connection-type", $"Unsupported connection type '{value}'.", LineNumber: line, Field: key));
                return;
            case "Serial": profile.SerialPort = value; return;
            case "Baudrate": profile.Baudrate = value; return;
            case "DataBits": profile.DataBits = value; return;
            case "Handshaking": profile.Handshaking = value; return;
            case "Parity": profile.Parity = value; return;
            case "StopBits": profile.StopBits = value; return;
            case "InitialRTS": ReadBoolean(value, v => profile.InitialRTS = v, issues, line, key); return;
            case "InitialDTR": ReadBoolean(value, v => profile.InitialDTR = v, issues, line, key); return;
            case "TCPConnectAddress": profile.TcpConnectAddress = value; return;
            case "TCPConnectPort": profile.TcpConnectPort = value; return;
            case "TCPListenPort": profile.TcpListenPort = value; return;
            case "RestartOnClose": ReadBoolean(value, v => profile.RestartServer = v, issues, line, key); return;
            default:
                PreserveUnknown(profile, ConnectSection, $"{key}={value}", issues, line);
                return;
        }
    }

    private static void ReadTitleField(
        Terminal2Profile profile,
        string key,
        string value,
        List<Terminal2ValidationIssue> issues,
        int line)
    {
        if (key.StartsWith("Title", StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            if (index is >= 0 and < Terminal2Profile.TitleCapacity)
                profile.Titles[index] = value;
            else
                issues.Add(new Terminal2ValidationIssue("profile.title-index-out-of-range", $"Title index {index} is outside the supported range.", LineNumber: line, Field: key));
            return;
        }

        PreserveUnknown(profile, TitlesSection, $"{key}={value}", issues, line);
    }

    private static bool ReadMacroField(
        Terminal2Macro macro,
        string key,
        string value,
        List<Terminal2ValidationIssue> issues,
        int line,
        int index)
    {
        switch (key)
        {
            case "Title": macro.Title = value; return true;
            case "ICD": ReadInteger(value, v => macro.DelayBetweenCharacters = v, issues, line, $"[{index}].{key}"); return true;
            case "ILD": ReadInteger(value, v => macro.DelayBetweenLinesMs = v, issues, line, $"[{index}].{key}"); return true;
            case "RepeatON": ReadBoolean(value, v => macro.RepeatEnabled = v, issues, line, $"[{index}].{key}"); return true;
            case "Delta": ReadInteger(value, v => macro.RepeatEveryMs = v, issues, line, $"[{index}].{key}"); return true;
            case "Repeats": ReadInteger(value, v => macro.StopAfterRepeats = v, issues, line, $"[{index}].{key}"); return true;
            case "Text": macro.Text = value; return true;
            case "AddCR": ReadBoolean(value, v => macro.AddCR = v, issues, line, $"[{index}].{key}"); return true;
            case "AddLF": ReadBoolean(value, v => macro.AddLF = v, issues, line, $"[{index}].{key}"); return true;
            default: return false;
        }
    }

    private static bool TryFilterKey(string key, out int index, out string field)
    {
        index = -1;
        field = string.Empty;
        if (key.Length < 3 || key[0] != 'F')
            return false;

        var fieldStart = key.IndexOfAny("ModeTextColorBackMacro".ToCharArray(), 2);
        if (fieldStart < 2 || !int.TryParse(key.AsSpan(1, fieldStart - 1), NumberStyles.None, CultureInfo.InvariantCulture, out index)
            || index is < 0 or >= Terminal2Profile.FilterCapacity)
            return false;

        field = key[fieldStart..];
        return field is "Mode" or "Text" or "Color" or "Back" or "Macro";
    }

    private static void ReadBoolean(string value, Action<bool> setter, List<Terminal2ValidationIssue> issues, int line, string field)
    {
        if (bool.TryParse(value, out var parsed))
            setter(parsed);
        else
            issues.Add(new Terminal2ValidationIssue("profile.invalid-boolean", $"'{value}' is not a valid boolean.", LineNumber: line, Field: field));
    }

    private static void ReadInteger(string value, Action<int> setter, List<Terminal2ValidationIssue> issues, int line, string field)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            setter(parsed);
        else
            issues.Add(new Terminal2ValidationIssue("profile.invalid-integer", $"'{value}' is not a valid integer.", LineNumber: line, Field: field));
    }

    private static void PreserveUnknown(
        Terminal2Profile profile,
        string section,
        string rawLine,
        List<Terminal2ValidationIssue> issues,
        int line)
    {
        profile.UnknownEntries.Add(new Terminal2UnknownProfileEntry(section, rawLine));
        issues.Add(new Terminal2ValidationIssue(
            "profile.unknown-entry",
            $"Unknown entry in section [{section}] was retained for export.",
            Terminal2ValidationSeverity.Warning,
            LineNumber: line));
    }

    private static List<Terminal2ValidationIssue> ValidateForExport(Terminal2Profile profile)
    {
        var issues = new List<Terminal2ValidationIssue>();
        if (profile.Filters is null || profile.Titles is null || profile.Macros is null)
        {
            issues.Add(new Terminal2ValidationIssue("profile.null-slots", "Filter, title, and macro slot arrays cannot be null."));
            return issues;
        }
        if (profile.Filters.Length != Terminal2Profile.FilterCapacity)
            issues.Add(new Terminal2ValidationIssue("profile.filter-count", $"Exactly {Terminal2Profile.FilterCapacity} filters are required."));
        if (profile.Titles.Length != Terminal2Profile.TitleCapacity)
            issues.Add(new Terminal2ValidationIssue("profile.title-count", $"Exactly {Terminal2Profile.TitleCapacity} titles are required."));
        if (profile.Macros.Length != Terminal2Profile.MacroCapacity)
            issues.Add(new Terminal2ValidationIssue("profile.macro-count", $"Exactly {Terminal2Profile.MacroCapacity} macro slots are required."));

        ValidateText(issues, "Name", profile.Name);
        ValidateText(issues, "LogDir", profile.LogDirectory);
        ValidateText(issues, "LogPrefix", profile.LogPrefix);
        ValidateText(issues, "OutFont", profile.OutputFont);
        ValidateText(issues, "InFont", profile.InputFont);
        ValidateText(issues, "Serial", profile.SerialPort);
        ValidateText(issues, "Baudrate", profile.Baudrate);
        ValidateText(issues, "DataBits", profile.DataBits);
        ValidateText(issues, "Handshaking", profile.Handshaking);
        ValidateText(issues, "Parity", profile.Parity);
        ValidateText(issues, "StopBits", profile.StopBits);
        ValidateText(issues, "TCPConnectAddress", profile.TcpConnectAddress);
        ValidateText(issues, "TCPConnectPort", profile.TcpConnectPort);
        ValidateText(issues, "TCPListenPort", profile.TcpListenPort);

        foreach (var title in profile.Titles)
            ValidateText(issues, "Title", title);
        foreach (var filter in profile.Filters)
        {
            if (filter is null)
            {
                issues.Add(new Terminal2ValidationIssue("profile.null-filter", "Filter slots cannot be null."));
                continue;
            }
            ValidateText(issues, "Filter.Text", filter.Text);
            ValidateText(issues, "Filter.Macro", filter.Macro);
        }
        for (var i = 0; i < profile.Macros.Length; i++)
        {
            if (profile.Macros[i] is not { } macro)
                continue;
            ValidateText(issues, $"[{i}].Title", macro.Title);
            ValidateText(issues, $"[{i}].Text", macro.Text);
        }
        foreach (var entry in profile.UnknownEntries)
        {
            ValidateText(issues, "Unknown.Section", entry.Section);
            ValidateText(issues, "Unknown.Line", entry.RawLine);
            if (entry.RawLine.StartsWith('['))
                issues.Add(new Terminal2ValidationIssue("profile.unsafe-unknown-entry", "Unknown entries cannot define new sections."));
        }

        return issues;
    }

    private static void ValidateText(List<Terminal2ValidationIssue> issues, string field, string? value)
    {
        if (value is null)
        {
            issues.Add(new Terminal2ValidationIssue("profile.null-value", $"Field {field} cannot be null.", Field: field));
            return;
        }
        if (value.Contains('\r') || value.Contains('\n'))
            issues.Add(new Terminal2ValidationIssue("profile.multiline-value", $"Field {field} contains a line break that the legacy format cannot represent safely.", Field: field));
    }

    private static bool ShouldWriteMacro(Terminal2Macro macro) =>
        macro.Title.Length > 0
        || macro.Text.Length > 0
        || macro.DelayBetweenCharacters != 0
        || macro.DelayBetweenLinesMs != 200
        || macro.RepeatEnabled
        || macro.RepeatEveryMs != 0
        || macro.StopAfterRepeats != 0
        || !macro.AddCR
        || macro.AddLF;

    private static bool IsUnknownSection(string section)
    {
        if (section is ProfileSection or ConnectSection or TitlesSection)
            return false;
        if (!int.TryParse(section, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            return true;
        return index < 0 || index >= Terminal2Profile.MacroCapacity;
    }

    private static void AppendUnknown(StringBuilder output, Terminal2Profile profile, string section)
    {
        foreach (var entry in profile.UnknownEntries.Where(entry => string.Equals(entry.Section, section, StringComparison.Ordinal)))
            output.Append(entry.RawLine).Append('\n');
    }

    private static void Write(StringBuilder output, string key, string value) => output.Append(key).Append('=').Append(value).Append('\n');
    private static void Write(StringBuilder output, string key, bool value) => Write(output, key, value.ToString(CultureInfo.InvariantCulture));
    private static void Write(StringBuilder output, string key, int value) => Write(output, key, value.ToString(CultureInfo.InvariantCulture));
}
