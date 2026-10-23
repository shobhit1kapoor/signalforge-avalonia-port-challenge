using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SignalForge.Desktop.Services;
using SignalForge.Desktop.ViewModels;
using SignalForge.Desktop.Views;

namespace SignalForge.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILocalTerminalSessionHost, RoyalTerminalSessionHost>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider();

            desktop.MainWindow = _services.GetRequiredService<MainWindow>();
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
