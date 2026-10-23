using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;

namespace SignalForge.Desktop.Services;

public sealed class RoyalTerminalSessionHost : ILocalTerminalSessionHost, IDisposable
{
    private TerminalControl? _control;
    private int _startInProgress;

    public void Attach(TerminalControl control)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (ReferenceEquals(_control, control))
            return;

        _control?.StopPty();

        _control = control;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var control = _control ?? throw new InvalidOperationException("The terminal view has not been attached yet.");
        if (control.HasActiveSession || Interlocked.CompareExchange(ref _startInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                workingDirectory = Environment.CurrentDirectory;
            }

            var options = new PtyTransportOptions(
                Command: null,
                WorkingDirectory: workingDirectory,
                Environment: null,
                Dimensions: new TerminalSessionDimensions(100, 27, 1200, 760));

            await control.StartSessionAsync(options, cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _startInProgress, 0);
        }
    }

    public void Stop()
    {
        _control?.StopPty();
    }

    public void Dispose() => Stop();
}
