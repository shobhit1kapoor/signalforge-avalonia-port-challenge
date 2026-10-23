/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.IO;

namespace SignalForge.Core.Compatibility.Terminal2;

/// <summary>Dispatches legacy text files by extension and returns typed parse diagnostics.</summary>
public static class Terminal2ProfileImporter
{
    public static Terminal2ValidationResult<Terminal2Profile> Import(string fileName, string contents)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(contents);

        var name = Path.GetFileNameWithoutExtension(fileName);
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".profile" => Terminal2ProfileSerializer.Deserialize(contents, name),
            ".tmf" => Terminal2MacroFileImporter.Import(contents, name),
            _ => Terminal2ValidationResult<Terminal2Profile>.Failure(
                "profile.unsupported-extension",
                $"'{Path.GetExtension(fileName)}' is not a supported Terminal2 profile file type.")
        };
    }
}
