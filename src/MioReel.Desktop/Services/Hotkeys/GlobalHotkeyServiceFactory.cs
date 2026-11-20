using MioReel.Core.Abstractions;
using MioReel.Core.Models;

namespace MioReel.Desktop.Services.Hotkeys;

public sealed class NullGlobalHotkeyService(string notice) : IGlobalHotkeyService
{
    public bool IsSupported => false;
    public string BackendName => "Unavailable";
    public string? PlatformNotice => notice;
    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationStatus> Statuses { get; } =
        new Dictionary<HotkeyAction, HotkeyRegistrationStatus>();

    public event Action<HotkeyAction>? HotkeyTriggered
    {
        add { }
        remove { }
    }

    public event Action? StatusChanged
    {
        add { }
        remove { }
    }

    public void ApplyBindings(bool enabled, IReadOnlyDictionary<HotkeyAction, HotkeyBinding> bindings) { }

    public void Dispose() { }
}

public static class GlobalHotkeyServiceFactory
{
    public static IGlobalHotkeyService Create()
    {
#if !ANDROID
        if (OperatingSystem.IsWindows())
            return new WindowsGlobalHotkeyService();

        if (OperatingSystem.IsLinux())
            return new LinuxX11GlobalHotkeyService();
#endif

        return new NullGlobalHotkeyService("电视端不支持系统全局热键，播放页可使用遥控器快捷键");
    }
}
