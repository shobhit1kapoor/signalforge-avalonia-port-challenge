/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.Security;
using System.Text;
using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Profiles;

/// <summary>
/// Owns Terminal2's active-profile marker and the profile startup policy.
/// The marker is the extensionless file named <c>Active</c> beside the
/// <c>.profile</c> files, matching Terminal2's on-disk convention.
/// </summary>
public sealed class Terminal2ProfileLifecycle
{
    public const string DefaultProfileName = "Default";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Terminal2ProfileCollection _profiles;
    private readonly string _activeMarkerPath;

    public Terminal2ProfileLifecycle(Terminal2ProfileCollection profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
        _activeMarkerPath = Path.Combine(profiles.DirectoryPath, "Active");
    }

    /// <summary>
    /// Ensures a default profile exists, restores the saved profile when it is
    /// usable, and persists the profile chosen for this startup. Missing or
    /// unreadable active markers and missing/invalid active profiles fall back
    /// to <see cref="DefaultProfileName"/> with a warning.
    /// </summary>
    public Terminal2ValidationResult<Terminal2Profile> Initialize()
    {
        var issues = new List<Terminal2ValidationIssue>();
        var defaultProfile = _profiles.Load(DefaultProfileName);
        if (defaultProfile.Value is null && defaultProfile.Issues.Any(issue => issue.Code == "profile.not-found"))
        {
            var created = _profiles.Create(CreateDefaultProfile());
            if (!created.IsValid)
                return new Terminal2ValidationResult<Terminal2Profile>(null, created.Issues);
            defaultProfile = created;
        }

        var activeName = ReadActiveName(issues) ?? DefaultProfileName;
        var active = _profiles.Load(activeName);
        if (!active.IsValid)
        {
            if (!defaultProfile.IsValid)
                return new Terminal2ValidationResult<Terminal2Profile>(null,
                    issues.Concat(defaultProfile.Issues).Concat(active.Issues).Distinct().ToArray());

            issues.AddRange(defaultProfile.Issues);
            issues.Add(new Terminal2ValidationIssue(
                "profile.active-fallback",
                $"The saved active profile '{activeName}' could not be loaded; '{DefaultProfileName}' was selected instead.",
                Terminal2ValidationSeverity.Warning));
            active = defaultProfile;
            activeName = DefaultProfileName;
        }
        else if (!defaultProfile.IsValid)
        {
            // An unused corrupt Default.profile should not prevent restoring a
            // different valid profile, but keep the damage visible to the UI.
            issues.AddRange(defaultProfile.Issues.Select(issue => issue with
            {
                Severity = Terminal2ValidationSeverity.Warning
            }));
        }

        issues.AddRange(active.Issues);
        var markerResult = WriteActiveName(activeName);
        issues.AddRange(markerResult.Issues);
        return new Terminal2ValidationResult<Terminal2Profile>(active.Value, issues);
    }

    /// <summary>
    /// Loads a profile and makes it active only when parsing succeeded. This
    /// mirrors Terminal2's SwitchToProfile behavior while surfacing I/O and
    /// parsing failures to the caller.
    /// </summary>
    public Terminal2ValidationResult<Terminal2Profile> SetActive(string name)
    {
        var loaded = _profiles.Load(name);
        if (!loaded.IsValid)
            return loaded;

        var saved = WriteActiveName(name);
        return new Terminal2ValidationResult<Terminal2Profile>(
            saved.IsValid ? loaded.Value : null,
            loaded.Issues.Concat(saved.Issues));
    }

    private string? ReadActiveName(List<Terminal2ValidationIssue> issues)
    {
        try
        {
            if (IsLinkOrReparsePoint(_activeMarkerPath))
            {
                issues.Add(new Terminal2ValidationIssue(
                    "profile.active-marker-link",
                    "The active-profile marker is a symbolic link or reparse point and was ignored.",
                    Terminal2ValidationSeverity.Warning));
                return null;
            }

            if (!File.Exists(_activeMarkerPath))
            {
                issues.Add(new Terminal2ValidationIssue(
                    "profile.active-marker-missing",
                    "No active profile was saved; the default profile was selected.",
                    Terminal2ValidationSeverity.Warning));
                return null;
            }

            var name = File.ReadAllText(_activeMarkerPath);
            if (string.IsNullOrWhiteSpace(name))
            {
                issues.Add(new Terminal2ValidationIssue(
                    "profile.active-marker-empty",
                    "The active-profile marker is empty; the default profile was selected.",
                    Terminal2ValidationSeverity.Warning));
                return null;
            }

            return name;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            issues.Add(new Terminal2ValidationIssue(
                "profile.active-marker-read-failed",
                $"Could not read the active-profile marker; the default profile was selected. {exception.Message}",
                Terminal2ValidationSeverity.Warning));
            return null;
        }
    }

    private Terminal2ValidationResult<bool?> WriteActiveName(string name)
    {
        // Use the collection's validation path before persisting any marker
        // content, so a bad marker cannot select a profile outside its folder.
        var profile = _profiles.Load(name);
        if (!profile.IsValid)
            return new Terminal2ValidationResult<bool?>(null, profile.Issues);

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_profiles.DirectoryPath);
            if (IsLinkOrReparsePoint(_activeMarkerPath))
                return Terminal2ValidationResult<bool?>.Failure(
                    "profile.active-marker-link",
                    "The active-profile marker is a symbolic link or reparse point and cannot be replaced.");

            temporaryPath = Path.Combine(_profiles.DirectoryPath, $".terminal2-active-{Guid.NewGuid():N}.tmp");
            var payload = Utf8WithoutBom.GetBytes(name);
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

            File.Move(temporaryPath, _activeMarkerPath, overwrite: true);
            temporaryPath = null;
            return Terminal2ValidationResult<bool?>.Success(true);
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return Terminal2ValidationResult<bool?>.Failure(
                "profile.active-marker-save-failed",
                $"Could not save the active profile '{name}': {exception.Message}");
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
                    // Best effort cleanup after a failed marker write.
                }
            }
        }
    }

    private static Terminal2Profile CreateDefaultProfile() => new()
    {
        Name = DefaultProfileName,
        LogDirectory = @".\Logs",
        SerialPort = "COM3",
        ShowHex = true,
    };

    private static bool IsLinkOrReparsePoint(string path)
    {
        var file = new FileInfo(path);
        return file.LinkTarget is not null || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static bool IsExpectedFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
            NotSupportedException or PathTooLongException;
}
