using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RoyalTerminal.Avalonia.Controls;
using SignalForge.Desktop.Services;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;
    private DockWorkspaceHost? _dockHost;
    private bool _started;
    private CommandPaletteViewModel? _commandPalette;
    private RemoteTerminalView? _remoteTerminal;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = null;
    }

    public MainWindow(MainViewModel viewModel, ILocalTerminalSessionHost terminalHost)
        : this()
    {
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        _viewModel = viewModel;
        DataContext = viewModel;
        _dockHost = this.FindControl<DockWorkspaceHost>("DockWorkspaceHost");
        _dockHost?.InitializeWorkspace(viewModel, terminalHost);
        ConfigureCommandPalette(viewModel);
        SetLayoutNotice(_dockHost?.Status);
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnResetLayoutClicked(object? sender, RoutedEventArgs e)
    {
        _dockHost?.ResetLayout();
        SetLayoutNotice(_dockHost?.Status ?? "Default dock layout restored and saved.");
    }

    private void OnRestartPtyClicked(object? sender, RoutedEventArgs e)
    {
        if (_dockHost is null || _viewModel is null)
            return;

        const int localShellTabIndex = 8;
        if (_dockHost.SelectedFeatureTabIndex != localShellTabIndex)
        {
            _dockHost.SelectFeatureTab(localShellTabIndex);
            return;
        }

        _viewModel.RestartLocalSessionCommand.Execute(null);
    }

    private void SetLayoutNotice(string? message)
    {
        var notice = this.FindControl<TextBlock>("LayoutNotice");
        if (notice is null)
        {
            return;
        }

        notice.Text = message ?? string.Empty;
        notice.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_started)
        {
            return;
        }

        _started = true;
        _remoteTerminal = _dockHost?.GetRemoteTerminal();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _dockHost?.SaveNow();
        if (_viewModel is not null)
        {
            _remoteTerminal?.StopSession();
            await _viewModel.ShutdownAsync();
        }
    }

    private void ConfigureCommandPalette(MainViewModel viewModel)
    {
        var overlay = this.FindControl<CommandPaletteView>("CommandPaletteOverlay");
        if (overlay is null)
        {
            return;
        }

        var commands = new List<CommandPaletteItem>
        {
            new("tab.byte-session", "Open byte workbench", "Workspace", () => SelectWorkspaceTab(0),
                "Connect serial or TCP and inspect the live byte stream.", keywords: ["transport", "serial", "tcp"]),
            new("tab.protocol-testing", "Open protocol test engine", "Workspace", () => SelectWorkspaceTab(1),
                "Run send/expect sequences against the simulator or an active session.", keywords: ["test", "assertions", "automation"]),
            new("tab.user-schemas", "Open schema studio", "Workspace", () => SelectWorkspaceTab(2),
                "Edit and decode declarative protocol schemas.", keywords: ["schema", "decoder"]),
            new("tab.trace-compare", "Open trace comparison", "Workspace", () => SelectWorkspaceTab(3),
                "Compare recorded good and failed sessions.", keywords: ["forensics", "compare"]),
            new("tab.terminal2", "Open Terminal2 migration", "Workspace", () => SelectWorkspaceTab(5),
                "Import or export legacy profiles and send configured text or macros.", keywords: ["legacy", "profile", "macro"]),
            new("tab.filters", "Open Terminal2 filters", "Workspace", () => SelectWorkspaceTab(6),
                "Edit ordered RX text filters and inspect their match counts.", keywords: ["highlight", "filter"]),
            new("tab.profile-library", "Open profile library", "Workspace", () => SelectWorkspaceTab(7),
                "Browse and manage locally stored Terminal2 profiles.", keywords: ["legacy", "profile"]),
            new("tab.receive-history", "Open Terminal2 RX line history", "Workspace", () => SelectWorkspaceTab(13),
                "Search, freeze, and stop on complete received text lines while byte capture continues.", keywords: ["legacy", "receive", "search", "freeze"]),
            new("tab.macro-editor", "Open macro editor", "Workspace", () => SelectWorkspaceTab(4),
                "Edit Terminal2 macro slots.", keywords: ["legacy", "profile"]),
            new("tab.local-shell", "Open local shell", "Workspace", () => SelectWorkspaceTab(8),
                "Use the independent RoyalTerminal local PTY.", keywords: ["terminal", "pty", "shell"]),
            new("tab.remote-shell", "Open SSH or Telnet terminal", "Workspace", () => SelectWorkspaceTab(12),
                "Connect a remote terminal using SSH or Telnet.", keywords: ["remote", "ssh", "telnet"]),
            new("tab.file-transfer", "Open text file transfer", "Workspace", () => SelectWorkspaceTab(9),
                "Transfer text files through an active serial session.", keywords: ["serial", "upload", "download"]),
            new("tab.xmodem", "Open XMODEM transfer", "Workspace", () => SelectWorkspaceTab(10),
                "Send a file using the XMODEM protocol over serial.", keywords: ["serial", "transfer"]),
            new("tab.binary-tools", "Open binary tools", "Workspace", () => SelectWorkspaceTab(11),
                "Inspect bytes, frame boundaries, and binary structures.", keywords: ["hex", "binary", "xray"]),
            new("tab.next", "Next workspace view", "Workspace", () => SelectAdjacentWorkspaceTab(1),
                "Move to the next view · Ctrl+Tab or Ctrl+PageDown (⌘ on macOS).", keywords: ["navigate", "tabs"]),
            new("tab.previous", "Previous workspace view", "Workspace", () => SelectAdjacentWorkspaceTab(-1),
                "Move to the previous view · Ctrl+Shift+Tab or Ctrl+PageUp (⌘ on macOS).", keywords: ["navigate", "tabs"]),
            new("layout.reset", "Reset workspace layout", "Workspace", () => OnResetLayoutClicked(this, new RoutedEventArgs()),
                "Restore the default pane sizes and selected view · Ctrl+Shift+R (⌘ on macOS).", keywords: ["panes", "default"]),
            new("connect", "Connect byte transport", "Transport", viewModel.ConnectCommand,
                "Open the selected serial, TCP client/server, or simulator transport.", keywords: ["serial", "tcp"]),
            new("disconnect", "Disconnect byte transport", "Transport", viewModel.DisconnectCommand,
                "Stop the active byte transport and finalize its trace.", keywords: ["close", "stop"]),
            new("send.hex", "Send hexadecimal bytes", "Transport", viewModel.SendHexCommand,
                "Send the bytes entered in the byte workbench.", keywords: ["write", "transmit", "tx"]),
            new("send.modbus", "Send Modbus read example", "Protocol", viewModel.SendModbusReadExampleCommand,
                "Send a read-holding-registers request through the active transport.", keywords: ["rtu", "tcp", "register"]),
            new("trace.load", "Load trace", "Trace", viewModel.LoadTraceCommand,
                "Load a SignalForge JSONL trace from the configured path.", keywords: ["open", "capture"]),
            new("trace.replay", "Replay loaded trace", "Trace", viewModel.ReplayTraceCommand,
                "Replay receive bytes and verify sends against a loaded trace.", keywords: ["time machine", "offline"]),
            new("trace.pause", "Pause trace replay", "Trace", viewModel.PauseReplayCommand,
                "Pause active trace replay.", keywords: ["stop", "hold"]),
            new("trace.resume", "Resume trace replay", "Trace", viewModel.ResumeReplayCommand,
                "Resume paused trace replay.", keywords: ["continue"]),
            new("profile.import", "Import Terminal2 profile", "Terminal2", viewModel.ImportTerminal2ProfileCommand,
                "Load a compatible .profile or .tmf file.", keywords: ["legacy", "tmf"]),
            new("profile.export", "Export Terminal2 profile", "Terminal2", viewModel.ExportTerminal2ProfileCommand,
                "Write the current profile in compatible .profile format.", keywords: ["legacy"]),
            new("macro.run", "Run selected Terminal2 macro", "Terminal2", viewModel.RunTerminal2MacroCommand,
                "Execute the selected macro over the active byte transport.", keywords: ["legacy", "automation"]),
            new("text.send", "Send configured Terminal2 text", "Terminal2", viewModel.SendTerminal2TextCommand,
                "Send text with the imported Terminal2 line-ending settings.", keywords: ["legacy", "cr", "lf"]),
        };

        _commandPalette = new CommandPaletteViewModel(commands);
        overlay.DataContext = _commandPalette;
    }

    private void SelectWorkspaceTab(int tabIndex)
    {
        _dockHost?.SelectFeatureTab(tabIndex);
    }

    private void SelectAdjacentWorkspaceTab(int direction)
    {
        _dockHost?.SelectAdjacentFeatureTab(direction);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var textEntryFocused = IsTextEntrySource(e.Source);
        if ((!textEntryFocused || IsTerminal2SendTextSource(e.Source))
            && Terminal2MacroHotkeyResolver.TryResolve(
                e.Key,
                e.KeyModifiers,
                _viewModel?.Terminal2MacroHotkeyBank ?? 1,
                out var macroIndex)
            && _viewModel is not null)
        {
            _ = _viewModel.RunTerminal2MacroHotkeyAsync(macroIndex);
            e.Handled = true;
            return;
        }

        var action = ResolveWorkspaceShortcut(
            e.Key,
            e.KeyModifiers,
            textEntryFocused,
            OperatingSystem.IsMacOS(),
            out var tabIndex);
        if (action is null)
        {
            return;
        }

        switch (action.Value)
        {
            case WorkspaceShortcutAction.ToggleCommandPalette:
                if (_commandPalette is null)
                    return;
                if (_commandPalette.IsOpen)
                    _commandPalette.Close();
                else
                    _commandPalette.Open();
                break;
            case WorkspaceShortcutAction.NextTab:
                SelectAdjacentWorkspaceTab(1);
                break;
            case WorkspaceShortcutAction.PreviousTab:
                SelectAdjacentWorkspaceTab(-1);
                break;
            case WorkspaceShortcutAction.SelectTab:
                SelectWorkspaceTab(tabIndex);
                break;
            case WorkspaceShortcutAction.ResetLayout:
                OnResetLayoutClicked(this, new RoutedEventArgs());
                break;
        }

        e.Handled = true;
    }

    private static WorkspaceShortcutAction? ResolveWorkspaceShortcut(
        Key key,
        KeyModifiers modifiers,
        bool textEntryFocused,
        bool isMacOS,
        out int tabIndex)
    {
        tabIndex = -1;
        var primary = isMacOS ? KeyModifiers.Meta : KeyModifiers.Control;
        var relevantModifiers = modifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);

        if (key == Key.P && relevantModifiers == (primary | KeyModifiers.Shift))
            return WorkspaceShortcutAction.ToggleCommandPalette;

        // Let editors, terminals and selection controls keep navigation and edit keys.
        if (textEntryFocused)
            return null;

        if (relevantModifiers == primary)
        {
            if (key == Key.Tab || key == Key.PageDown)
                return WorkspaceShortcutAction.NextTab;
            if (key == Key.PageUp)
                return WorkspaceShortcutAction.PreviousTab;
            if (TryGetTabNumber(key, out tabIndex))
                return WorkspaceShortcutAction.SelectTab;
        }

        if (relevantModifiers == (primary | KeyModifiers.Shift))
        {
            if (key == Key.Tab)
                return WorkspaceShortcutAction.PreviousTab;
            if (key == Key.R)
                return WorkspaceShortcutAction.ResetLayout;
        }

        return null;
    }

    private static bool TryGetTabNumber(Key key, out int tabIndex)
    {
        var number = key switch
        {
            >= Key.D1 and <= Key.D9 => (int)key - (int)Key.D1 + 1,
            >= Key.NumPad1 and <= Key.NumPad9 => (int)key - (int)Key.NumPad1 + 1,
            _ => 0,
        };
        tabIndex = number - 1;
        return number > 0;
    }

    private static bool IsTextEntrySource(object? source)
    {
        for (var control = source as Control; control is not null; control = control.Parent as Control)
        {
            if (control is TextBox or ComboBox or NumericUpDown or TerminalControl or RemoteTerminalView)
                return true;
        }
        return false;
    }

    private static bool IsTerminal2SendTextSource(object? source)
    {
        for (var control = source as Control; control is not null; control = control.Parent as Control)
        {
            if (string.Equals(control.Name, "Terminal2SendTextBox", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private enum WorkspaceShortcutAction
    {
        ToggleCommandPalette,
        NextTab,
        PreviousTab,
        SelectTab,
        ResetLayout,
    }

    private void OnOpenCommandPaletteClicked(object? sender, RoutedEventArgs e) => _commandPalette?.Open();
}
