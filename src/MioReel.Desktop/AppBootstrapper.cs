using Avalonia;
using Aviora.Controls;
using Aviora.Presentation.Dialogs;
using Aviora.Presentation.Drawers;
using Aviora.Presentation.Loadings;
using Aviora.Presentation.Toasts;
using Microsoft.Extensions.DependencyInjection;
using MioReel.Core.Abstractions;
using MioReel.Desktop.Services.Hotkeys;
using MioReel.Desktop.ViewModels;
using MioReel.Desktop.Views;
using MioReel.Infrastructure;
using ReactiveUI.Avalonia.Splat;

namespace MioReel.Desktop;

/// <summary>
/// Shared Avalonia/DI configuration used by the desktop host and the Android TV host.
/// </summary>
internal static class AppBootstrapper
{
    public static AppBuilder Configure(AppBuilder builder)
    {
        return builder
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUIWithMicrosoftDependencyResolver(
                services =>
                {
                    services.AddMioReelInfrastructure();

                    // Aviora Presentation Services (same singleton instance serves both VM and Host interfaces)
                    services.AddSingleton<DialogService>();
                    services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
                    services.AddSingleton<IDialogHostService>(sp => sp.GetRequiredService<DialogService>());

                    services.AddSingleton<DrawerService>();
                    services.AddSingleton<IDrawerService>(sp => sp.GetRequiredService<DrawerService>());
                    services.AddSingleton<IDrawerHostService>(sp => sp.GetRequiredService<DrawerService>());

                    services.AddSingleton<LoadingService>();
                    services.AddSingleton<ILoadingService>(sp => sp.GetRequiredService<LoadingService>());
                    services.AddSingleton<ILoadingHostService>(sp => sp.GetRequiredService<LoadingService>());

                    services.AddSingleton<ToastService>();
                    services.AddSingleton<IToastService>(sp => sp.GetRequiredService<ToastService>());
                    services.AddSingleton<IToastHostService>(sp => sp.GetRequiredService<ToastService>());

                    services.AddSingleton<IGlobalHotkeyService>(_ => GlobalHotkeyServiceFactory.Create());
                    services.AddSingleton<PlayerViewModel>();
                    services.AddSingleton<MainWindowViewModel>();

#if ANDROID
                    services.AddSingleton<AndroidMainView>();
#else
                    services.AddSingleton<MainWindow>();
#endif
                },
                withResolver: sp => Program.Services = sp!);
    }
}