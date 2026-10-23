using System.Threading;
using System.Threading.Tasks;
using RoyalTerminal.Avalonia.Controls;

namespace SignalForge.Desktop.Services;

public interface ILocalTerminalSessionHost
{
    void Attach(TerminalControl control);
    Task StartAsync(CancellationToken cancellationToken = default);
    void Stop();
}
