using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Desktop.ViewModels;

public partial class Terminal2ProfileLibraryViewModel : ViewModelBase
{
    private readonly Terminal2ProfileCollection _collection;
    private readonly Func<Terminal2Profile?> _getCurrentProfile;
    private readonly Func<bool> _canSaveCurrent;
    private readonly Action<Terminal2Profile, string, bool> _loadIntoWorkspace;

    [ObservableProperty]
    private string? _selectedProfile;

    [ObservableProperty]
    private string _renameTo = "";

    [ObservableProperty]
    private string _saveAsName = "";

    [ObservableProperty]
    private string _copyToName = "";

    [ObservableProperty]
    private string _newProfileName = "";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _status = "Profiles are stored locally in the SIGNALFORGE application data directory.";

    [ObservableProperty]
    private string _editorStatus = "Select a saved profile to edit its Terminal2 connection, display, and logging options.";

    [ObservableProperty]
    private bool _editorSendCR;

    [ObservableProperty]
    private bool _editorSendLF;

    [ObservableProperty]
    private bool _editorClearCommand;

    [ObservableProperty]
    private bool _editorSendAsYouType;

    [ObservableProperty]
    private bool _editorStayOnTop;

    [ObservableProperty]
    private Terminal2ConnectionType _editorConnectionType;

    [ObservableProperty]
    private string _editorSerialPort = "";

    [ObservableProperty]
    private string _editorBaudrate = "";

    [ObservableProperty]
    private string _editorDataBits = "";

    [ObservableProperty]
    private string _editorHandshaking = "";

    [ObservableProperty]
    private string _editorParity = "";

    [ObservableProperty]
    private string _editorStopBits = "";

    [ObservableProperty]
    private bool _editorInitialRTS;

    [ObservableProperty]
    private bool _editorInitialDTR;

    [ObservableProperty]
    private string _editorTcpConnectAddress = "";

    [ObservableProperty]
    private string _editorTcpConnectPort = "";

    [ObservableProperty]
    private string _editorTcpListenPort = "";

    [ObservableProperty]
    private bool _editorRestartServer;

    [ObservableProperty]
    private bool _editorShowCR;

    [ObservableProperty]
    private bool _editorShowLF;

    [ObservableProperty]
    private bool _editorShowInputTimestamp;

    [ObservableProperty]
    private bool _editorShowOutputTimestamp;

    [ObservableProperty]
    private bool _editorShowAscii;

    [ObservableProperty]
    private bool _editorShowHex;

    [ObservableProperty]
    private bool _editorIgnoreCase;

    [ObservableProperty]
    private string _editorInputFont = "";

    [ObservableProperty]
    private string _editorOutputFont = "";

    [ObservableProperty]
    private string _editorInputBackgroundArgb = "#FFFFFFFF";

    [ObservableProperty]
    private string _editorInputForegroundArgb = "#FF000000";

    [ObservableProperty]
    private string _editorOutputBackgroundArgb = "#FFDCDCDC";

    [ObservableProperty]
    private string _editorLogDirectory = "";

    [ObservableProperty]
    private string _editorLogPrefix = "";

    [ObservableProperty]
    private string _editorMaxLogSize = "0";

    private string _suggestedCopyName = "";
    private Terminal2Profile? _editingProfile;

    public ObservableCollection<string> Profiles { get; } = [];
    public IReadOnlyList<Terminal2ConnectionType> ConnectionTypes { get; } = Enum.GetValues<Terminal2ConnectionType>();

    public bool CanLoad => !string.IsNullOrWhiteSpace(SelectedProfile);
    public bool CanEditProfile => _editingProfile is not null;
    public bool CanRename => CanLoad && !string.IsNullOrWhiteSpace(RenameTo) && RenameTo != SelectedProfile;
    public bool CanDelete => CanLoad;
    public bool CanSave => _canSaveCurrent() && _getCurrentProfile() is not null && !string.IsNullOrWhiteSpace(SaveAsName);
    public bool CanCreateBlank => !string.IsNullOrWhiteSpace(NewProfileName);
    public bool CanCopy => CanLoad && !string.IsNullOrWhiteSpace(CopyToName) &&
        !StringComparer.OrdinalIgnoreCase.Equals(CopyToName.Trim(), SelectedProfile);

    public Terminal2ProfileLibraryViewModel(
        Terminal2ProfileCollection collection,
        Func<Terminal2Profile?> getCurrentProfile,
        Func<bool> canSaveCurrent,
        Action<Terminal2Profile, string, bool> loadIntoWorkspace)
    {
        _collection = collection;
        _getCurrentProfile = getCurrentProfile;
        _canSaveCurrent = canSaveCurrent;
        _loadIntoWorkspace = loadIntoWorkspace;
        Refresh();
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(RenameTo) || RenameTo == value)
            RenameTo = value ?? "";
        if (string.IsNullOrWhiteSpace(value))
        {
            if (CopyToName == _suggestedCopyName)
            {
                CopyToName = "";
                _suggestedCopyName = "";
            }
        }
        else if (string.IsNullOrWhiteSpace(CopyToName) || CopyToName == _suggestedCopyName)
        {
            _suggestedCopyName = $"{value} Copy";
            CopyToName = _suggestedCopyName;
        }
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanCopy));
        LoadSelectedCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        CopySelectedCommand.NotifyCanExecuteChanged();
        LoadEditorProfile(value);
    }

    partial void OnRenameToChanged(string value)
    {
        OnPropertyChanged(nameof(CanRename));
        RenameSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSaveAsNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanSave));
        SaveCurrentProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnCopyToNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanCopy));
        CopySelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnNewProfileNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanCreateBlank));
        CreateBlankProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchTextChanged(string value) => RefreshProfiles();

    public void NotifyCurrentProfileChanged()
    {
        if (_getCurrentProfile() is { } profile)
            SaveAsName = profile.Name;
        OnPropertyChanged(nameof(CanSave));
        SaveCurrentProfileCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Refresh() => RefreshProfiles();

    private Terminal2ValidationResult<IReadOnlyList<string>> RefreshProfiles()
    {
        var result = _collection.Search(SearchText);
        Profiles.Clear();
        if (result.Value is not null)
            foreach (var name in result.Value)
                Profiles.Add(name);

        if (SelectedProfile is not null && !Profiles.Contains(SelectedProfile))
            SelectedProfile = null;
        var query = SearchText.Trim();
        var count = query.Length == 0
            ? $"{Profiles.Count} profile(s)"
            : $"{Profiles.Count} profile(s) match '{query}'";
        if (query.Length > 0 && Profiles.Count == 0 && result.Issues.Count == 0)
            count = $"No profile names match '{query}'";
        Status = WithIssues($"{count} · {_collection.DirectoryPath}", result.Issues);
        return result;
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private void LoadSelected()
    {
        if (SelectedProfile is not { } name)
            return;
        var result = _collection.Load(name);
        if (result.Value is not { } profile)
        {
            Status = string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
            return;
        }

        _loadIntoWorkspace(profile, name, result.HasErrors);
        SaveAsName = profile.Name;
        RenameTo = name;
        Status = result.Issues.Count == 0
            ? $"Loaded '{name}' into the Terminal2 migration workspace."
            : string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
    }

    [RelayCommand(CanExecute = nameof(CanCreateBlank))]
    private void CreateBlankProfile()
    {
        var profile = new Terminal2Profile { Name = NewProfileName.Trim() };
        var result = _collection.Create(profile);
        if (result.Value is null || result.HasErrors)
        {
            Status = result.Issues.Count == 0
                ? "The blank profile could not be created."
                : string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
            return;
        }

        SearchText = "";
        RefreshProfiles();
        SelectedProfile = profile.Name;
        RenameTo = profile.Name;
        SaveAsName = profile.Name;
        CopyToName = $"{profile.Name} Copy";
        NewProfileName = "";
        _loadIntoWorkspace(profile, profile.Name, false);
        Status = $"Created and loaded '{profile.Name}' with Terminal2-compatible default settings. Edit its macro and filter banks, then save changes to the library.";
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveCurrentProfile()
    {
        var profile = _getCurrentProfile();
        if (profile is null)
        {
            Status = "Import a Terminal2 profile before saving it to this library.";
            return;
        }

        var originalName = profile.Name;
        profile.Name = SaveAsName.Trim();
        var result = _collection.Save(profile);
        if (result.Value is null || result.HasErrors)
        {
            profile.Name = originalName;
            Status = string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
            return;
        }

        SearchText = "";
        Refresh();
        SelectedProfile = profile.Name;
        RenameTo = profile.Name;
        _loadIntoWorkspace(profile, profile.Name, false);
        Status = $"Saved '{profile.Name}' as a Terminal2-compatible .profile in the local library.";
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void RenameSelected()
    {
        if (SelectedProfile is not { } oldName)
            return;
        var result = _collection.Rename(oldName, RenameTo.Trim());
        if (result.Value is null || result.HasErrors)
        {
            Status = string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
            return;
        }

        var newName = result.Value;
        SearchText = "";
        Refresh();
        SelectedProfile = newName;
        RenameTo = newName;
        Status = $"Renamed '{oldName}' to '{newName}'. The profile contents were preserved.";
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteSelected()
    {
        if (SelectedProfile is not { } name)
            return;
        var result = _collection.Delete(name);
        if (result.Value is null || result.HasErrors)
        {
            Status = string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
            return;
        }

        SelectedProfile = null;
        Refresh();
        Status = $"Deleted '{name}' from the local profile library.";
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void CopySelected()
    {
        if (SelectedProfile is not { } sourceName)
            return;

        var copyName = CopyToName.Trim();
        var result = _collection.Copy(sourceName, copyName);
        if (result.Value is null || result.HasErrors)
        {
            Status = result.Issues.Count == 0
                ? "The profile copy could not be completed."
                : string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
            return;
        }

        SearchText = "";
        var listing = RefreshProfiles();
        SelectedProfile = copyName;
        RenameTo = copyName;
        _suggestedCopyName = $"{copyName} Copy";
        CopyToName = _suggestedCopyName;
        var messages = result.Issues.Concat(listing.Issues);
        Status = WithIssues(
            $"Copied '{sourceName}' as '{copyName}'. The currently loaded workspace profile was not changed.",
            messages);
    }

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private void SaveEditedProfile()
    {
        if (_editingProfile is not { } profile || SelectedProfile is not { } selectedName)
            return;

        if (!int.TryParse(EditorMaxLogSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxLogSize))
        {
            EditorStatus = "Max log size must be a whole number.";
            return;
        }
        if (!TryParseArgb(EditorInputBackgroundArgb, out var inputBackground))
        {
            EditorStatus = "Input background color must use #AARRGGBB format, for example #FF20242B.";
            return;
        }
        if (!TryParseArgb(EditorInputForegroundArgb, out var inputForeground))
        {
            EditorStatus = "Input text color must use #AARRGGBB format, for example #FFFFFFFF.";
            return;
        }
        if (!TryParseArgb(EditorOutputBackgroundArgb, out var outputBackground))
        {
            EditorStatus = "Output background color must use #AARRGGBB format, for example #FF20242B.";
            return;
        }

        profile.SendCR = EditorSendCR;
        profile.SendLF = EditorSendLF;
        profile.ClearCommand = EditorClearCommand;
        profile.SendAsYouType = EditorSendAsYouType;
        profile.StayOnTop = EditorStayOnTop;
        profile.ConnectionType = EditorConnectionType;
        profile.SerialPort = EditorSerialPort;
        profile.Baudrate = EditorBaudrate;
        profile.DataBits = EditorDataBits;
        profile.Handshaking = EditorHandshaking;
        profile.Parity = EditorParity;
        profile.StopBits = EditorStopBits;
        profile.InitialRTS = EditorInitialRTS;
        profile.InitialDTR = EditorInitialDTR;
        profile.TcpConnectAddress = EditorTcpConnectAddress;
        profile.TcpConnectPort = EditorTcpConnectPort;
        profile.TcpListenPort = EditorTcpListenPort;
        profile.RestartServer = EditorRestartServer;
        profile.ShowCR = EditorShowCR;
        profile.ShowLF = EditorShowLF;
        profile.ShowInputTimestamp = EditorShowInputTimestamp;
        profile.ShowOutputTimestamp = EditorShowOutputTimestamp;
        profile.ShowAscii = EditorShowAscii;
        profile.ShowHex = EditorShowHex;
        profile.IgnoreCase = EditorIgnoreCase;
        profile.InputFont = EditorInputFont;
        profile.OutputFont = EditorOutputFont;
        profile.InputBackgroundArgb = inputBackground;
        profile.InputForegroundArgb = inputForeground;
        profile.OutputBackgroundArgb = outputBackground;
        profile.LogDirectory = EditorLogDirectory;
        profile.LogPrefix = EditorLogPrefix;
        profile.MaxLogSize = maxLogSize;

        var result = _collection.Save(profile);
        if (result.Value is null || result.HasErrors)
        {
            EditorStatus = result.Issues.Count == 0
                ? "The profile could not be saved."
                : string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
            return;
        }

        _editingProfile = result.Value;
        EditorStatus = WithIssues(
            $"Saved the connection, send, display, and logging options for '{selectedName}'. The active workspace profile and connection were not changed.",
            result.Issues);
    }

    private void LoadEditorProfile(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            _editingProfile = null;
            OnPropertyChanged(nameof(CanEditProfile));
            SaveEditedProfileCommand.NotifyCanExecuteChanged();
            EditorStatus = "Select a saved profile to edit its Terminal2 connection, display, and logging options.";
            return;
        }

        var result = _collection.Load(profileName);
        if (result.Value is not { } profile)
        {
            _editingProfile = null;
            OnPropertyChanged(nameof(CanEditProfile));
            SaveEditedProfileCommand.NotifyCanExecuteChanged();
            EditorStatus = result.Issues.Count == 0
                ? $"Could not load '{profileName}' for editing."
                : string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
            return;
        }

        _editingProfile = profile;
        EditorSendCR = profile.SendCR;
        EditorSendLF = profile.SendLF;
        EditorClearCommand = profile.ClearCommand;
        EditorSendAsYouType = profile.SendAsYouType;
        EditorStayOnTop = profile.StayOnTop;
        EditorConnectionType = profile.ConnectionType;
        EditorSerialPort = profile.SerialPort;
        EditorBaudrate = profile.Baudrate;
        EditorDataBits = profile.DataBits;
        EditorHandshaking = profile.Handshaking;
        EditorParity = profile.Parity;
        EditorStopBits = profile.StopBits;
        EditorInitialRTS = profile.InitialRTS;
        EditorInitialDTR = profile.InitialDTR;
        EditorTcpConnectAddress = profile.TcpConnectAddress;
        EditorTcpConnectPort = profile.TcpConnectPort;
        EditorTcpListenPort = profile.TcpListenPort;
        EditorRestartServer = profile.RestartServer;
        EditorShowCR = profile.ShowCR;
        EditorShowLF = profile.ShowLF;
        EditorShowInputTimestamp = profile.ShowInputTimestamp;
        EditorShowOutputTimestamp = profile.ShowOutputTimestamp;
        EditorShowAscii = profile.ShowAscii;
        EditorShowHex = profile.ShowHex;
        EditorIgnoreCase = profile.IgnoreCase;
        EditorInputFont = profile.InputFont;
        EditorOutputFont = profile.OutputFont;
        EditorInputBackgroundArgb = FormatArgb(profile.InputBackgroundArgb);
        EditorInputForegroundArgb = FormatArgb(profile.InputForegroundArgb);
        EditorOutputBackgroundArgb = FormatArgb(profile.OutputBackgroundArgb);
        EditorLogDirectory = profile.LogDirectory;
        EditorLogPrefix = profile.LogPrefix;
        EditorMaxLogSize = profile.MaxLogSize.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(CanEditProfile));
        SaveEditedProfileCommand.NotifyCanExecuteChanged();
        EditorStatus = WithIssues($"Editing '{profileName}'. Save writes a Terminal2-compatible .profile and leaves the active connection unchanged.", result.Issues);
    }

    private static bool TryParseArgb(string value, out int argb)
    {
        var hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unsigned))
        {
            argb = unchecked((int)unsigned);
            return true;
        }

        argb = 0;
        return false;
    }

    private static string FormatArgb(int argb) => $"#{unchecked((uint)argb):X8}";

    private static string WithIssues(string message, IEnumerable<Terminal2ValidationIssue> issues)
    {
        var details = issues.Select(issue => issue.Message).Distinct(StringComparer.Ordinal).ToArray();
        return details.Length == 0 ? message : $"{message}{Environment.NewLine}{string.Join(Environment.NewLine, details)}";
    }
}
