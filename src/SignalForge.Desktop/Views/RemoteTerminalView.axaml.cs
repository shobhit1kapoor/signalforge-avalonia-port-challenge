using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;
using RoyalTerminal.Terminal.Transport.Ssh;

namespace SignalForge.Desktop.Views;

public partial class RemoteTerminalView : UserControl
{
    private const string SessionPasswordId = "signalforge-remote-session-password";
    private readonly RuntimeSshCredentialProvider _credentials = new();
    private readonly TerminalControl _terminal;
    private CancellationTokenSource? _connectCancellation;
    private bool _connecting;

    public RemoteTerminalView()
    {
        InitializeComponent();
        TransportPicker.SelectionChanged += OnTransportChanged;
        ApplyTransportVisibility();
        _terminal = new TerminalControl(
            new TerminalSessionService(),
            new DefaultTerminalInputAdapter(),
            new DefaultTerminalSelectionService(),
            new DefaultTerminalScrollService(),
            new DefaultVtProcessorFactory(),
            new DefaultPtyFactory(),
            _credentials,
            new KnownHostsSshHostKeyValidator(),
            transportFactory: null)
        {
            Columns = 100,
            Rows = 27,
            ScrollbackLimit = 10000,
            TerminalFontSize = 13,
            DefaultForeground = Avalonia.Media.Color.Parse("#D8E1EA"),
            DefaultBackground = Avalonia.Media.Color.Parse("#090C11"),
        };

        TerminalHost.Children.Add(_terminal);
    }

    private void OnTransportChanged(object? sender, SelectionChangedEventArgs e)
        => ApplyTransportVisibility();

    private void ApplyTransportVisibility()
    {
        var isSsh = TransportPicker.SelectedIndex == 0;
        UsernameInput.IsVisible = isSsh;
        PasswordInput.IsVisible = isSsh;
        HostFingerprintInput.IsVisible = isSsh;
        PortInput.Text = isSsh ? "22" : "23";
    }

    public void StopSession()
    {
        CancelPendingConnect();
        _terminal.StopPty();
        _credentials.Clear();
        PasswordInput.Text = string.Empty;
    }

    private async void OnConnectClicked(object? sender, RoutedEventArgs e)
    {
        if (_connecting || _terminal.HasActiveSession)
        {
            return;
        }

        var host = HostInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            SetStatus("Enter a host name or IP address.", isError: true);
            return;
        }

        if (!int.TryParse(PortInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            SetStatus("Port must be between 1 and 65535.", isError: true);
            return;
        }

        var isSsh = TransportPicker.SelectedIndex == 0;
        var username = UsernameInput.Text?.Trim() ?? string.Empty;
        var password = PasswordInput.Text ?? string.Empty;
        if (isSsh && string.IsNullOrWhiteSpace(username))
        {
            SetStatus("Enter an SSH username.", isError: true);
            return;
        }

        if (isSsh && password.Length == 0)
        {
            SetStatus("Enter the SSH password. Passwords are held in memory for this session only.", isError: true);
            return;
        }

        var connectCancellation = new CancellationTokenSource();
        _connectCancellation = connectCancellation;
        SetBusy(true);
        try
        {
            if (isSsh)
            {
                _credentials.SetPassword(password);
                var authentication = new SshAuthenticationOptions(
                    UsePassword: true,
                    PasswordSecretId: SessionPasswordId,
                    PrivateKeySecretIds: Array.Empty<string>(),
                    UseAgent: false);
                var options = new SshTransportOptions(
                    Endpoint: new SshEndpointOptions(host, port, username),
                    RequestPty: true,
                    TerminalType: "xterm-256color",
                    InitialCommand: null,
                    Authentication: authentication,
                    Dimensions: new TerminalSessionDimensions(100, 27, 1200, 760));

                var fingerprint = HostFingerprintInput.Text?.Trim();
                if (!string.IsNullOrEmpty(fingerprint))
                {
                    options = options with { ExpectedHostKeyFingerprintSha256 = fingerprint };
                }

                await _terminal.StartSshAsync(options, connectCancellation.Token);
                connectCancellation.Token.ThrowIfCancellationRequested();
                PasswordInput.Text = string.Empty;
                SetStatus($"SSH connected to {host}:{port} as {username}.");
            }
            else
            {
                var options = new TelnetTransportOptions(
                    Host: host,
                    Port: port,
                    TerminalType: "xterm-256color",
                    Dimensions: new TerminalSessionDimensions(100, 27, 1200, 760));
                await _terminal.StartTelnetAsync(options, connectCancellation.Token);
                connectCancellation.Token.ThrowIfCancellationRequested();
                SetStatus($"Telnet connected to {host}:{port}.");
            }

            ConnectButton.IsEnabled = false;
            DisconnectButton.IsEnabled = _terminal.HasActiveSession;
        }
        catch (OperationCanceledException)
        {
            _credentials.Clear();
            PasswordInput.Text = string.Empty;
            SetStatus("Remote connection cancelled.");
        }
        catch (Exception ex)
        {
            _credentials.Clear();
            PasswordInput.Text = string.Empty;
            if (_terminal.HasActiveSession)
            {
                _terminal.StopPty();
            }

            SetStatus($"Connection failed: {ex.Message}", isError: true);
        }
        finally
        {
            try
            {
                SetBusy(false);
                DisconnectButton.IsEnabled = _terminal.HasActiveSession;
            }
            finally
            {
                if (ReferenceEquals(_connectCancellation, connectCancellation))
                    _connectCancellation = null;
                connectCancellation.Dispose();
            }
        }
    }

    private void OnDisconnectClicked(object? sender, RoutedEventArgs e)
    {
        if (_connecting)
        {
            CancelPendingConnect();
            SetStatus("Cancelling remote connection…");
            return;
        }

        StopSession();
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        SetStatus("Remote session disconnected.");
    }

    private void CancelPendingConnect()
    {
        try
        {
            _connectCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The connect task owns disposal and may have completed while shutdown was requested.
        }
        catch (AggregateException)
        {
            // Cancellation is already signaled; continue stopping the terminal even if a callback failed.
        }
    }

    private void SetBusy(bool value)
    {
        _connecting = value;
        TransportPicker.IsEnabled = !value && !_terminal.HasActiveSession;
        HostInput.IsEnabled = !value && !_terminal.HasActiveSession;
        PortInput.IsEnabled = !value && !_terminal.HasActiveSession;
        UsernameInput.IsEnabled = !value && !_terminal.HasActiveSession;
        PasswordInput.IsEnabled = !value && !_terminal.HasActiveSession;
        HostFingerprintInput.IsEnabled = !value && !_terminal.HasActiveSession;
        ConnectButton.IsEnabled = !value && !_terminal.HasActiveSession;
        DisconnectButton.Content = value ? "CANCEL" : "DISCONNECT";
        DisconnectButton.IsEnabled = value || _terminal.HasActiveSession;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(isError ? "#F28B82" : "#AAB6C3"));
    }

    private sealed class RuntimeSshCredentialProvider : ISshCredentialProvider
    {
        private string? _password;

        public void SetPassword(string password) => _password = password;

        public void Clear() => _password = null;

        public ValueTask<SshResolvedCredentials> ResolveAsync(
            SshCredentialRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var password = request.Authentication.PasswordSecretId == SessionPasswordId ? _password : null;
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("The session password is no longer available. Re-enter it and connect again.");
            }

            return ValueTask.FromResult(new SshResolvedCredentials(
                Password: password,
                PrivateKeyPemOrPath: Array.Empty<string>(),
                UseAgent: false));
        }
    }
}
