using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
#if DEBUG
using AvaDevTools;
#endif
using Microsoft.Extensions.DependencyInjection;
using MioReel.Desktop.ViewModels;
using MioReel.Desktop.Views;

namespace MioReel.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachAvaDevTools(new DevToolsOptions
        {
            McpServer = true,
            McpAllowInput = true
        });
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Program.Services.GetRequiredService<MainWindowViewModel>();
            RequestedThemeVariant = vm.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
            vm.ThemeChanged += dark =>
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

            var window = Program.Services.GetRequiredService<MainWindow>();
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => (Program.Services as IDisposable)?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
