/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.Security;
using System.Text;

namespace SignalForge.Core.Compatibility.Terminal2;

/// <summary>
/// Local collection of Terminal2-compatible <c>.profile</c> files. Each public
/// operation validates the profile stem and confines the resulting path to the
/// configured directory.
/// </summary>
public sealed class Terminal2ProfileCollection
{
    private const string ProfileExtension = ".profile";
    private const int MaximumFileNameLength = 255;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string _directoryPath;

    public Terminal2ProfileCollection(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _directoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
    }

    /// <summary>The canonical directory configured for this collection.</summary>
    public string DirectoryPath => _directoryPath;

    /// <summary>
    /// Lists top-level, non-link files with Terminal2's case-sensitive
    /// <c>.profile</c> extension, sorted deterministically by profile name.
    /// A missing collection directory is an empty collection.
    /// </summary>
    public Terminal2ValidationResult<IReadOnlyList<string>> List()
    {
        FileAttributes directoryAttributes;
        try
        {
            directoryAttributes = File.GetAttributes(_directoryPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Terminal2ValidationResult<IReadOnlyList<string>>.Success(Array.Empty<string>());
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return Failure<IReadOnlyList<string>>(
                "profile.list-failed",
                $"Could not access profile collection directory '{_directoryPath}': {exception.Message}");
        }

        if ((directoryAttributes & FileAttributes.Directory) == 0)
            return Failure<IReadOnlyList<string>>("profile.directory-not-directory", $"The profile collection path '{_directoryPath}' is a file.");

        var names = new List<string>();
        var issues = new List<Terminal2ValidationIssue>();
        try
        {
            foreach (var entryPath in Directory.EnumerateFiles(_directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(Path.GetExtension(entryPath), ProfileExtension, StringComparison.Ordinal))
                    continue;

                if (IsLinkOrReparsePoint(entryPath))
                {
                    issues.Add(new Terminal2ValidationIssue(
                        "profile.symlink-skipped",
                        $"Skipped profile file '{Path.GetFileName(entryPath)}' because it is a symbolic link or reparse point.",
                        Terminal2ValidationSeverity.Warning));
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(entryPath);
                if (!TryGetProfilePath(name, out var canonicalPath, out var nameIssue))
                {
                    issues.Add(nameIssue! with { Severity = Terminal2ValidationSeverity.Warning });
                    continue;
                }

                if (!PathComparer.Equals(Path.GetFullPath(entryPath), canonicalPath))
                {
                    issues.Add(new Terminal2ValidationIssue(
                        "profile.path-unsafe",
                        $"Skipped profile file '{Path.GetFileName(entryPath)}' because its canonical path is outside the collection directory.",
                        Terminal2ValidationSeverity.Warning));
                    continue;
                }

                names.Add(name);
            }

            var sortedNames = names
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToArray();
            return new Terminal2ValidationResult<IReadOnlyList<string>>(sortedNames, issues);
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return Failure<IReadOnlyList<string>>(
                "profile.list-failed",
                $"Could not list profiles in '{_directoryPath}': {exception.Message}");
        }
    }

    /// <summary>Loads a profile by its file-name stem; the filename overrides embedded Name.</summary>
    public Terminal2ValidationResult<Terminal2Profile> Load(string name)
    {
        if (!TryGetProfilePath(name, out var path, out var issue))
            return new Terminal2ValidationResult<Terminal2Profile>(null, new[] { issue! });

        try
        {
            if (IsLinkOrReparsePoint(path))
                return Failure<Terminal2Profile>("profile.symlink-not-allowed", $"Profile '{name}' is a symbolic link or reparse point and cannot be loaded.");
            if (!File.Exists(path))
                return Failure<Terminal2Profile>("profile.not-found", $"Profile '{name}' was not found in '{_directoryPath}'.");

            var contents = File.ReadAllText(path);
            return Terminal2ProfileImporter.Import(path, contents);
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return Failure<Terminal2Profile>("profile.load-failed", $"Could not load profile '{name}': {exception.Message}");
        }
    }

    /// <summary>Lists profiles whose names contain the query, ignoring case. An empty query lists all profiles.</summary>
    public Terminal2ValidationResult<IReadOnlyList<string>> Search(string? query)
    {
        var listed = List();
        if (listed.Value is null)
            return new Terminal2ValidationResult<IReadOnlyList<string>>(null, listed.Issues);

        var normalizedQuery = query?.Trim();
        var matches = string.IsNullOrEmpty(normalizedQuery)
            ? listed.Value
            : listed.Value.Where(name => name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new Terminal2ValidationResult<IReadOnlyList<string>>(matches, listed.Issues);
    }

    /// <summary>
    /// Validates and loads a library profile, changes the detached loaded profile's name,
    /// then serializes it into a new file without replacing an existing destination.
    /// The caller's workspace profile is never involved.
    /// </summary>
    public Terminal2ValidationResult<Terminal2Profile> Copy(string sourceName, string copyName)
    {
        if (!TryGetProfilePath(sourceName, out var sourcePath, out var sourceIssue))
            return new Terminal2ValidationResult<Terminal2Profile>(null, new[] { sourceIssue! });
        if (!TryGetProfilePath(copyName, out var copyPath, out var copyIssue))
            return new Terminal2ValidationResult<Terminal2Profile>(null, new[] { copyIssue! });
        if (PathComparer.Equals(sourcePath, copyPath))
            return Failure<Terminal2Profile>("profile.copy-same-name", "Choose a different name for the copied profile.");

        try
        {
            if (IsLinkOrReparsePoint(copyPath) || File.Exists(copyPath) || Directory.Exists(copyPath))
                return Failure<Terminal2Profile>("profile.already-exists", $"A filesystem entry already exists for profile '{copyName}'.");
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return Failure<Terminal2Profile>("profile.copy-check-failed", $"Could not check destination profile '{copyName}': {exception.Message}");
        }

        var loaded = Load(sourceName);
        if (loaded.Value is not { } profile || loaded.HasErrors)
            return new Terminal2ValidationResult<Terminal2Profile>(null, loaded.Issues);

        profile.Name = copyName;
        return SaveCore(profile, overwriteExisting: false, loaded.Issues);
    }

    /// <summary>
    /// Serializes and atomically saves a profile using <see cref="Terminal2ProfileSerializer"/>.
    /// Existing files are replaced only after the complete temporary file is written.
    /// </summary>
    public Terminal2ValidationResult<Terminal2Profile> Save(Terminal2Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return SaveCore(profile, overwriteExisting: true);
    }

    /// <summary>Creates a profile only when its destination does not already exist.</summary>
    public Terminal2ValidationResult<Terminal2Profile> Create(Terminal2Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return SaveCore(profile, overwriteExisting: false);
    }

    private Terminal2ValidationResult<Terminal2Profile> SaveCore(
        Terminal2Profile profile,
        bool overwriteExisting,
        IEnumerable<Terminal2ValidationIssue>? priorIssues = null)
    {
        if (!TryGetProfilePath(profile.Name, out var path, out var issue))
            return new Terminal2ValidationResult<Terminal2Profile>(null, (priorIssues ?? Array.Empty<Terminal2ValidationIssue>()).Append(issue!));

        var serialized = Terminal2ProfileSerializer.Serialize(profile);
        var issues = (priorIssues ?? Array.Empty<Terminal2ValidationIssue>()).Concat(serialized.Issues).ToArray();
        if (!serialized.IsValid)
            return new Terminal2ValidationResult<Terminal2Profile>(null, issues);

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_directoryPath);
            if (IsLinkOrReparsePoint(path))
                return new Terminal2ValidationResult<Terminal2Profile>(null, issues.Append(new Terminal2ValidationIssue(
                    "profile.symlink-not-allowed",
                    $"Profile '{profile.Name}' is a symbolic link or reparse point and cannot be replaced.")));

            temporaryPath = Path.Combine(_directoryPath, $".terminal2-profile-{Guid.NewGuid():N}.tmp");
            var payload = Utf8WithoutBom.GetBytes(serialized.Value!);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            // Same-directory rename keeps replacement atomic on supported local filesystems.
            File.Move(temporaryPath, path, overwrite: overwriteExisting);
            temporaryPath = null;
            return new Terminal2ValidationResult<Terminal2Profile>(profile, issues);
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            if (!overwriteExisting && (File.Exists(path) || Directory.Exists(path)))
                return new Terminal2ValidationResult<Terminal2Profile>(null, issues.Append(new Terminal2ValidationIssue(
                    "profile.already-exists",
                    $"A filesystem entry already exists for profile '{profile.Name}'.")));

            return WithAdditionalIssue<Terminal2Profile>(
                issues,
                "profile.save-failed",
                $"Could not save profile '{profile.Name}' in '{_directoryPath}': {exception.Message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (IsExpectedFileSystemException(exception))
                {
                    // Cleanup is best effort after the primary operation has failed.
                }
            }
        }
    }

    /// <summary>
    /// Renames a profile file without rewriting its contents. On load, as in
    /// Terminal2, the new filename stem takes precedence over the stored Name.
    /// </summary>
    public Terminal2ValidationResult<string> Rename(string oldName, string newName)
    {
        if (!TryGetProfilePath(oldName, out var oldPath, out var oldIssue))
            return new Terminal2ValidationResult<string>(null, new[] { oldIssue! });
        if (!TryGetProfilePath(newName, out var newPath, out var newIssue))
            return new Terminal2ValidationResult<string>(null, new[] { newIssue! });

        try
        {
            if (IsLinkOrReparsePoint(oldPath))
                return Failure<string>("profile.symlink-not-allowed", $"Profile '{oldName}' is a symbolic link or reparse point and cannot be renamed.");
            if (!File.Exists(oldPath))
                return Failure<string>("profile.not-found", $"Profile '{oldName}' was not found in '{_directoryPath}'.");
            if (IsLinkOrReparsePoint(newPath) || File.Exists(newPath) || Directory.Exists(newPath))
                return Failure<string>("profile.already-exists", $"A filesystem entry already exists for profile '{newName}'.");

            File.Move(oldPath, newPath);
            return Terminal2ValidationResult<string>.Success(newName);
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return Failure<string>("profile.rename-failed", $"Could not rename profile '{oldName}' to '{newName}': {exception.Message}");
        }
    }

    /// <summary>Deletes one profile file; directories and symbolic links are never followed.</summary>
    public Terminal2ValidationResult<bool?> Delete(string name)
    {
        if (!TryGetProfilePath(name, out var path, out var issue))
            return new Terminal2ValidationResult<bool?>(null, new[] { issue! });

        try
        {
            if (IsLinkOrReparsePoint(path))
                return Failure<bool?>("profile.symlink-not-allowed", $"Profile '{name}' is a symbolic link or reparse point and cannot be deleted through this collection.");
            if (!File.Exists(path))
                return Failure<bool?>("profile.not-found", $"Profile '{name}' was not found in '{_directoryPath}'.");

            File.Delete(path);
            return Terminal2ValidationResult<bool?>.Success(true);
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return Failure<bool?>("profile.delete-failed", $"Could not delete profile '{name}': {exception.Message}");
        }
    }

    private bool TryGetProfilePath(string? name, out string path, out Terminal2ValidationIssue? issue)
    {
        path = string.Empty;
        issue = null;
        if (!TryValidateProfileName(name, out var message))
        {
            issue = new Terminal2ValidationIssue("profile.invalid-name", message, Field: nameof(name));
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(_directoryPath, name + ProfileExtension));
            var relative = Path.GetRelativePath(_directoryPath, candidate);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison))
            {
                issue = new Terminal2ValidationIssue(
                    "profile.path-unsafe",
                    $"Profile name '{name}' resolves outside the configured collection directory.",
                    Field: nameof(name));
                return false;
            }

            var parent = Path.GetDirectoryName(candidate);
            if (parent is null || !PathComparer.Equals(parent, _directoryPath))
            {
                issue = new Terminal2ValidationIssue(
                    "profile.path-unsafe",
                    $"Profile name '{name}' does not resolve directly inside the configured collection directory.",
                    Field: nameof(name));
                return false;
            }

            path = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issue = new Terminal2ValidationIssue(
                "profile.invalid-name",
                $"Profile name '{name}' cannot be used as a file name: {exception.Message}",
                Field: nameof(name));
            return false;
        }
    }

    private static bool TryValidateProfileName(string? name, out string message)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            message = "Profile name must contain a non-whitespace character.";
            return false;
        }

        if (name is "." or ".." || name != name.TrimEnd(' ', '.'))
        {
            message = "Profile name cannot be '.' or '..', or end with a space or period.";
            return false;
        }

        if (name.EndsWith(ProfileExtension, StringComparison.OrdinalIgnoreCase))
        {
            message = $"Provide a profile name without the '{ProfileExtension}' extension.";
            return false;
        }

        if (name.Length + ProfileExtension.Length > MaximumFileNameLength)
        {
            message = $"Profile name is too long; the complete file name must be at most {MaximumFileNameLength} characters.";
            return false;
        }

        const string windowsInvalidCharacters = "<>:\"/\\|?*";
        if (name.Any(character => char.IsControl(character) || windowsInvalidCharacters.Contains(character)) ||
            Path.GetFileName(name) != name || Path.IsPathRooted(name))
        {
            message = "Profile name cannot contain path separators, control characters, or characters that are invalid in Windows file names.";
            return false;
        }

        var deviceName = name.Split('.')[0].TrimEnd(' ', '.');
        if (IsReservedWindowsDeviceName(deviceName))
        {
            message = $"'{name}' is reserved as a Windows device name.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool IsReservedWindowsDeviceName(string deviceName)
    {
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        if (deviceName.Length == 4 &&
            (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            deviceName[3] is >= '1' and <= '9')
            return true;

        return false;
    }

    private static bool IsLinkOrReparsePoint(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.LinkTarget is not null || (fileInfo.Exists && (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0))
            return true;

        var directoryInfo = new DirectoryInfo(path);
        return directoryInfo.LinkTarget is not null ||
            (directoryInfo.Exists && (directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static bool IsExpectedFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
            NotSupportedException or PathTooLongException;

    private static Terminal2ValidationResult<T> Failure<T>(string code, string message) =>
        Terminal2ValidationResult<T>.Failure(code, message);

    private static Terminal2ValidationResult<T> WithAdditionalIssue<T>(
        IReadOnlyList<Terminal2ValidationIssue> currentIssues,
        string code,
        string message) =>
        new(default, currentIssues.Append(new Terminal2ValidationIssue(code, message)));

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
