using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
#if DEBUG && !ANDROID
using AvaDevTools;
#endif
using Microsoft.Extensions.DependencyInjection;
using MioReel.Desktop.ViewModels;
using MioReel.Desktop.Views;

namespace MioReel.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if ANDROID
        // The Fluent theme defaults to Inter, which has no CJK glyphs. Avalonia's per-glyph
        // fallback is unreliable on Android, so use the system font family there.
        Resources["ContentControlThemeFontFamily"] = new FontFamily("$Default");
#endif
#if DEBUG && !ANDROID
        this.AttachAvaDevTools(new DevToolsOptions
        {
            McpServer = true,
            McpAllowInput = true
        });
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var vm = Program.Services.GetRequiredService<MainWindowViewModel>();
        RequestedThemeVariant = vm.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        vm.ThemeChanged += dark =>
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

#if ANDROID
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            var view = Program.Services.GetRequiredService<AndroidMainView>();
            view.DataContext = vm;
            activity.MainViewFactory = () => view;
        }
#else
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = Program.Services.GetRequiredService<MainWindow>();
            window.DataContext = vm;
            desktop.MainWindow = window;
            Program.InstanceGuard?.StartListening(() =>
                Dispatcher.UIThread.Post(window.BringToForeground));
            desktop.Exit += (_, _) => (Program.Services as IDisposable)?.Dispose();
        }
#endif

        base.OnFrameworkInitializationCompleted();
    }
}