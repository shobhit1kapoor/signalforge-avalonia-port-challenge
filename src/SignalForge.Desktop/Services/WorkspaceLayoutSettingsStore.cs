using System.Text.Json;

namespace SignalForge.Desktop.Services;

internal sealed class WorkspaceLayoutSettingsStore
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public WorkspaceLayoutSettingsStore()
    {
        var settingsRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(settingsRoot))
            settingsRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(settingsRoot))
            settingsRoot = Path.GetTempPath();

        _settingsPath = Path.Combine(settingsRoot, "SignalForge", "Settings", "workspace-layout.json");
    }

    public WorkspaceLayoutLoadResult Load()
    {
        try
        {
            using var stream = File.OpenRead(_settingsPath);
            var settings = JsonSerializer.Deserialize<WorkspaceLayoutSettings>(stream, _jsonOptions);
            if (settings is null || !settings.IsValid(out _))
            {
                return new(WorkspaceLayoutSettings.Defaults,
                    "Saved layout is malformed or outside supported limits; default layout is active.");
            }

            return new(settings, null);
        }
        catch (FileNotFoundException)
        {
            return new(WorkspaceLayoutSettings.Defaults, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(WorkspaceLayoutSettings.Defaults, null);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new(WorkspaceLayoutSettings.Defaults,
                "Saved layout could not be read; default layout is active.");
        }
    }

    public bool TrySave(WorkspaceLayoutSettings settings, out string? error)
    {
        if (!settings.IsValid(out var validationError))
        {
            error = validationError;
            return false;
        }

        var temporaryPath = _settingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, settings, _jsonOptions);

            File.Move(temporaryPath, _settingsPath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            error = "Layout changes could not be saved. Check that the application settings folder is writable.";
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                // A leftover temp file does not affect the last successfully saved layout.
            }
        }
    }

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or
            System.Security.SecurityException or ArgumentException or NotSupportedException;
}

internal sealed record WorkspaceLayoutSettings(
    int SchemaVersion,
    double ExplorerWidth,
    double InspectorWidth,
    double TimelineHeight,
    int SelectedFeatureTabIndex)
{
    public static WorkspaceLayoutSettings Defaults { get; } = new(
        SchemaVersion: 1,
        ExplorerWidth: 244,
        InspectorWidth: 318,
        TimelineHeight: 226,
        SelectedFeatureTabIndex: 0);

    public bool IsValid(out string error)
    {
        if (SchemaVersion != 1)
        {
            error = "Saved layout version is not supported.";
            return false;
        }

        if (!double.IsFinite(ExplorerWidth) || ExplorerWidth is < 180 or > 520 ||
            !double.IsFinite(InspectorWidth) || InspectorWidth is < 220 or > 560 ||
            !double.IsFinite(TimelineHeight) || TimelineHeight is < 145 or > 500)
        {
            error = "Saved pane sizes are outside supported limits.";
            return false;
        }

        if (SelectedFeatureTabIndex is < 0 or > 32)
        {
            error = "Saved feature tab index is outside supported limits.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

internal sealed record WorkspaceLayoutLoadResult(WorkspaceLayoutSettings Settings, string? Notice);
