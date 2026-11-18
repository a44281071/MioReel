using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;

namespace MioReel.Desktop.Services.Hotkeys;

/// <summary>
/// Linux X11 / XWayland 系统全局热键监听（基于 libX11 XGrabKey，带 BadAccess 容错）。
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxX11GlobalHotkeyService : IGlobalHotkeyService
{
    private const int KeyPressEventType = 2;
    private const int GrabModeAsync = 1;
    private const uint ShiftMask = 1 << 0;
    private const uint LockMask = 1 << 1;
    private const uint ControlMask = 1 << 2;
    private const uint Mod1Mask = 1 << 3; // Alt
    private const uint Mod2Mask = 1 << 4; // NumLock
    private const uint Mod4Mask = 1 << 6; // Super / Meta
    private const uint AnyRelevantMask = ShiftMask | ControlMask | Mod1Mask | Mod4Mask;

    private static readonly uint[] LockCombinations = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

    private readonly object _gate = new();
    private readonly List<GrabbedKey> _grabbedKeys = [];
    private readonly bool _isWayland;
    private Dictionary<HotkeyAction, HotkeyBinding> _pendingBindings = [];
    private Dictionary<HotkeyAction, HotkeyRegistrationStatus> _statuses = [];
    private volatile bool _pendingEnabled = true;
    private volatile bool _rebindRequested;
    private volatile bool _stopRequested;
    private Thread? _thread;
    private bool _disposed;

    private static volatile bool s_x11GrabErrorOccurred;
    private static readonly XErrorHandlerDelegate s_errorHandler = OnX11Error;

    public LinuxX11GlobalHotkeyService()
    {
        var hasDisplay = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
        _isWayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        if (!hasDisplay)
        {
            IsSupported = false;
            PlatformNotice = _isWayland
                ? "当前为纯 Wayland 会话且未检测到 XWayland (DISPLAY)，暂无法全局抓取热键，窗口内快捷键仍可用。"
                : "未检测到 X11 显示服务 (DISPLAY)，系统全局热键不可用。";
            return;
        }

        IsSupported = true;
        PlatformNotice = _isWayland
            ? "检测到 Wayland 会话：全局热键通过 XWayland 生效，原生 Wayland 窗口聚焦时可能受系统安全策略限制。"
            : null;

        _thread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "MioReel.X11Hotkey"
        };
        _thread.Start();
    }

    public bool IsSupported { get; private set; }
    public string BackendName => _isWayland ? "Linux X11 (XWayland)" : "Linux X11 XGrabKey";
    public string? PlatformNotice { get; private set; }

    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationStatus> Statuses
    {
        get { lock (_gate) return new Dictionary<HotkeyAction, HotkeyRegistrationStatus>(_statuses); }
    }

    public event Action<HotkeyAction>? HotkeyTriggered;
    public event Action? StatusChanged;

    public void ApplyBindings(bool enabled, IReadOnlyDictionary<HotkeyAction, HotkeyBinding> bindings)
    {
        if (_disposed || !IsSupported) return;
        lock (_gate)
        {
            _pendingEnabled = enabled;
            _pendingBindings = new Dictionary<HotkeyAction, HotkeyBinding>(bindings);
        }
        _rebindRequested = true;
    }

    private void EventLoop()
    {
        IntPtr display = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
            {
                IsSupported = false;
                PlatformNotice = "无法连接到 X11 显示服务器，全局热键不可用。";
                StatusChanged?.Invoke();
                return;
            }

            XSetErrorHandler(s_errorHandler);
            var root = XDefaultRootWindow(display);
            _rebindRequested = true;

            while (!_stopRequested)
            {
                if (_rebindRequested)
                {
                    _rebindRequested = false;
                    RebindOnLoopThread(display, root);
                }

                while (XPending(display) > 0 && !_stopRequested)
                {
                    XNextEvent(display, out var ev);
                    if (ev.type == KeyPressEventType)
                    {
                        var pressedMods = ev.state & AnyRelevantMask;
                        foreach (var grabbed in _grabbedKeys)
                        {
                            if (grabbed.KeyCode == (int)ev.keycode && grabbed.ModifiersMask == pressedMods)
                            {
                                HotkeyTriggered?.Invoke(grabbed.Action);
                                break;
                            }
                        }
                    }
                }

                Thread.Sleep(25);
            }

            UngrabAll(display, root);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            IsSupported = false;
            PlatformNotice = "系统缺少 libX11.so.6 运行库，全局热键不可用，窗口内快捷键仍可正常使用。";
            StatusChanged?.Invoke();
        }
        finally
        {
            if (display != IntPtr.Zero)
                XCloseDisplay(display);
        }
    }

    private void RebindOnLoopThread(IntPtr display, IntPtr root)
    {
        UngrabAll(display, root);

        bool enabled;
        Dictionary<HotkeyAction, HotkeyBinding> snapshot;
        lock (_gate)
        {
            enabled = _pendingEnabled;
            snapshot = new Dictionary<HotkeyAction, HotkeyBinding>(_pendingBindings);
        }

        var nextStatuses = new Dictionary<HotkeyAction, HotkeyRegistrationStatus>();
        if (enabled)
        {
            foreach (var (action, binding) in snapshot)
            {
                if (binding.IsEmpty)
                {
                    nextStatuses[action] = new HotkeyRegistrationStatus(action, false);
                    continue;
                }

                if (!KeyNameMapper.TryToX11KeySymName(binding.NormalizedKeyName, out var symName))
                {
                    nextStatuses[action] = new HotkeyRegistrationStatus(action, false, $"按键「{binding.NormalizedKeyName}」暂不支持 X11 全局映射");
                    continue;
                }

                var keysym = XStringToKeysym(symName);
                var keycode = keysym != IntPtr.Zero ? XKeysymToKeycode(display, keysym) : 0;
                if (keycode == 0)
                {
                    nextStatuses[action] = new HotkeyRegistrationStatus(action, false, $"当前键盘布局未映射按键「{binding.NormalizedKeyName}」");
                    continue;
                }

                var modMask = MapModifiers(binding.Modifiers);
                s_x11GrabErrorOccurred = false;
                foreach (var lockCombo in LockCombinations)
                    XGrabKey(display, keycode, modMask | lockCombo, root, false, GrabModeAsync, GrabModeAsync);
                XSync(display, false);

                if (s_x11GrabErrorOccurred)
                {
                    foreach (var lockCombo in LockCombinations)
                        XUngrabKey(display, keycode, modMask | lockCombo, root);
                    nextStatuses[action] = new HotkeyRegistrationStatus(action, false, "该全局快捷键已被桌面环境或其他程序占用");
                }
                else
                {
                    _grabbedKeys.Add(new GrabbedKey(action, keycode, modMask));
                    nextStatuses[action] = new HotkeyRegistrationStatus(action, true);
                }
            }
        }

        XFlush(display);
        lock (_gate)
        {
            _statuses = nextStatuses;
        }
        StatusChanged?.Invoke();
    }

    private void UngrabAll(IntPtr display, IntPtr root)
    {
        foreach (var grabbed in _grabbedKeys)
        {
            foreach (var lockCombo in LockCombinations)
                XUngrabKey(display, grabbed.KeyCode, grabbed.ModifiersMask | lockCombo, root);
        }
        _grabbedKeys.Clear();
    }

    private static uint MapModifiers(HotkeyModifiers modifiers)
    {
        uint mask = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) mask |= ControlMask;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) mask |= Mod1Mask;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) mask |= ShiftMask;
        if (modifiers.HasFlag(HotkeyModifiers.Meta)) mask |= Mod4Mask;
        return mask;
    }

    private static int OnX11Error(IntPtr display, IntPtr errorEvent)
    {
        s_x11GrabErrorOccurred = true;
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopRequested = true;
        _thread?.Join(TimeSpan.FromMilliseconds(500));
    }

    private sealed record GrabbedKey(HotkeyAction Action, int KeyCode, uint ModifiersMask);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandlerDelegate(IntPtr display, IntPtr errorEvent);

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XKeyEventUnion
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(80)] public uint state;
        [FieldOffset(84)] public uint keycode;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XStringToKeysym(string name);

    [DllImport("libX11.so.6")]
    private static extern int XKeysymToKeycode(IntPtr display, IntPtr keysym);

    [DllImport("libX11.so.6")]
    private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, bool ownerEvents, int pointerMode, int keyboardMode);

    [DllImport("libX11.so.6")]
    private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);

    [DllImport("libX11.so.6")]
    private static extern int XPending(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XNextEvent(IntPtr display, out XKeyEventUnion eventReturn);

    [DllImport("libX11.so.6")]
    private static extern int XSync(IntPtr display, bool discard);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XSetErrorHandler(XErrorHandlerDelegate handler);
}
