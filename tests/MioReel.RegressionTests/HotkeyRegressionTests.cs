using MioReel.Core.Models;
using MioReel.Desktop.Services.Hotkeys;
using MioReel.Infrastructure.Services;

internal static class HotkeyRegressionTests
{
    public static void Run(string root)
    {
        var defaults = HotkeySettings.CreateDefaults();
        Check(defaults.GlobalHotkeysEnabled, "默认开启系统全局热键");
        Check(defaults.GetBinding(HotkeyAction.GlobalBossKey).DisplayText == "Ctrl + Alt + H", "默认老板键格式化文本正确");
        Check(defaults.GetBinding(HotkeyAction.RestoreWindow).DisplayText == "Ctrl + R", "默认还原窗体快捷键格式化文本正确");
        Check(defaults.GetBinding(HotkeyAction.SpeedUp).Matches(HotkeyModifiers.None, "Up"), "窗口倍速加速键匹配正确");

        var conflict = defaults.FindConflict(HotkeyAction.NextEpisode, new HotkeyBinding(HotkeyModifiers.None, "Left"));
        Check(conflict == HotkeyAction.SeekBackward, "能准确检测跨功能热键冲突");
        Check(defaults.FindConflict(HotkeyAction.SeekBackward, new HotkeyBinding(HotkeyModifiers.None, "Left")) is null, "编辑自身快捷键不误报冲突");

        var updated = defaults.WithBinding(HotkeyAction.GlobalBossKey, new HotkeyBinding(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, "B"));
        var settingsPath = Path.Combine(root, "hotkey-settings.json");
        var settingsService = new FileSettingsService(settingsPath);
        settingsService.Update(settingsService.Current with { Hotkeys = updated });

        var reloaded = new FileSettingsService(settingsPath).Current;
        Check(reloaded.Hotkeys is not null, "settings.json 成功持久化 Hotkeys 字段");
        Check(reloaded.Hotkeys!.GetBinding(HotkeyAction.GlobalBossKey).DisplayText == "Ctrl + Shift + B", "重启后保留自定义全局热键");
        Check(reloaded.Hotkeys.GetBinding(HotkeyAction.PlayPause).NormalizedKeyName == "Space", "其余窗口快捷键保持默认");

        // Backward-compatibility check when upgrading legacy settings without hotkeys section
        File.WriteAllText(settingsPath, "{\"theme\":0,\"miniWindowAlwaysOnTop\":true,\"defaultQuality\":1080,\"autoPlayNextEpisode\":true}");
        var upgraded = new FileSettingsService(settingsPath).Current;
        Check(upgraded.Hotkeys is not null && upgraded.Hotkeys.GetBinding(HotkeyAction.NextEpisode).NormalizedKeyName == "PageDown", "旧版设置文件自动补全默认热键");

        Check(KeyNameMapper.TryToWin32VirtualKey("H", out var vkH) && vkH == 0x48, "Win32 虚拟键映射正确");
        Check(KeyNameMapper.TryToX11KeySymName("PageUp", out var symPgUp) && symPgUp == "Prior", "Linux X11 KeySym 映射正确");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
