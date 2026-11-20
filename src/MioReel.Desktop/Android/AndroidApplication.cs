using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace MioReel.Desktop;

[Application]
public class AndroidApplication : AvaloniaAndroidApplication<App>
{
    protected AndroidApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => AppBootstrapper.Configure(base.CustomizeAppBuilder(builder));
}