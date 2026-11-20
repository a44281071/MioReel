using Avalonia;
#if !ANDROID
using FrameFlux.Avalonia;
using MioReel.Desktop.Services;
#endif

namespace MioReel.Desktop;

public static class Program
{
#if !ANDROID
    private const string SingleInstanceAppId = "MioReel.Desktop";

    internal static SingleInstanceGuard? InstanceGuard { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        using var guard = SingleInstanceGuard.TryAcquire(SingleInstanceAppId);
        if (guard is null)
        {
            SingleInstanceGuard.SignalExistingInstance(SingleInstanceAppId);
            return;
        }

        InstanceGuard = guard;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            InstanceGuard = null;
        }
    }
#endif

    public static IServiceProvider Services { get; internal set; } = null!;

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();

#if !ANDROID
        // Desktop hosts detect the platform here; Android builds get their
        // platform backend from AvaloniaAndroidApplication.CustomizeAppBuilder.
        builder = builder.UsePlatformDetect();

#if FF_WINDOWS
        if (OperatingSystem.IsWindows())
            builder = builder.UseFrameFluxWindows();
#endif
#if FF_LINUX
        if (OperatingSystem.IsLinux())
            builder = builder.UseFrameFluxLinux();
#endif
#endif

        return AppBootstrapper.Configure(builder);
    }
}