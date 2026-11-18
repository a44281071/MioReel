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
        if (OperatingSystem.IsWindows())
            return new WindowsGlobalHotkeyService();

        if (OperatingSystem.IsLinux())
            return new LinuxX11GlobalHotkeyService();

        return new NullGlobalHotkeyService("当前系统暂不支持内置全局热键，窗口内快捷键可正常使用");
    }
}
