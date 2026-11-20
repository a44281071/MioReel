using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using MioReel.Desktop.Views;

namespace MioReel.Desktop;

[Activity(
    Label = "妙妙短剧 TV",
    MainLauncher = true,
    Exported = true,
    Theme = "@style/MioReelTvTheme",
    ScreenOrientation = ScreenOrientation.Landscape,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.UiMode
        | ConfigChanges.Density
        | ConfigChanges.Keyboard
        | ConfigChanges.KeyboardHidden)]
[IntentFilter(
    new[] { Intent.ActionMain },
    Categories = new[] { Intent.CategoryLauncher, "android.intent.category.LEANBACK_LAUNCHER" })]
public class MainActivity : AvaloniaMainActivity
{
    public static MainActivity? Current { get; private set; }

    /// <summary>Raised for hardware/remote keys before the Avalonia view sees them.</summary>
    public static event Func<KeyEvent?, bool>? KeyDispatched;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);

        Window?.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);
        ApplyImmersiveMode();

        BackRequested += (_, e) =>
        {
            if (AndroidMainView.Current?.HandleBack() == true)
                e.Handled = true;
        };
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplyImmersiveMode();
        AndroidMainView.Current?.OnHostResumed();
    }

    protected override void OnPause()
    {
        AndroidMainView.Current?.OnHostPaused();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this))
            Current = null;
        base.OnDestroy();
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is { Action: KeyEventActions.Down } && KeyDispatched?.Invoke(e) == true)
            return true;

        return base.DispatchKeyEvent(e);
    }

    private void ApplyImmersiveMode()
    {
        var window = Window;
        if (window is null)
            return;

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var controller = window.InsetsController;
            if (controller is not null)
            {
                controller.Hide(WindowInsets.Type.SystemBars());
                controller.SystemBarsBehavior =
                    (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }

            return;
        }

        var decor = window.DecorView;
        if (decor is null)
            return;

        decor.SystemUiFlags =
            SystemUiFlags.LayoutStable
            | SystemUiFlags.LayoutHideNavigation
            | SystemUiFlags.LayoutFullscreen
            | SystemUiFlags.HideNavigation
            | SystemUiFlags.Fullscreen
            | SystemUiFlags.ImmersiveSticky;
    }
}