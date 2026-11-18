using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;

namespace MioReel.Desktop.Services.Hotkeys;

/// <summary>
/// Windows 平台系统级全局热键监听（基于独立消息线程 + RegisterHotKey + MOD_NOREPEAT）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const uint WmHotKey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint WmAppRebind = 0x8001;
    private const uint PmNoRemove = 0x0000;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _queueReady = new(false);
    private readonly Thread _thread;
    private readonly Dictionary<int, HotkeyAction> _activeIdToAction = [];
    private Dictionary<HotkeyAction, HotkeyBinding> _pendingBindings = [];
    private Dictionary<HotkeyAction, HotkeyRegistrationStatus> _statuses = [];
    private bool _pendingEnabled = true;
    private uint _threadId;
    private bool _disposed;

    public WindowsGlobalHotkeyService()
    {
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "MioReel.WinHotkey"
        };
        _thread.Start();
    }

    public bool IsSupported => true;
    public string BackendName => "Windows RegisterHotKey";
    public string? PlatformNotice => null;

    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationStatus> Statuses
    {
        get { lock (_gate) return new Dictionary<HotkeyAction, HotkeyRegistrationStatus>(_statuses); }
    }

    public event Action<HotkeyAction>? HotkeyTriggered;
    public event Action? StatusChanged;

    public void ApplyBindings(bool enabled, IReadOnlyDictionary<HotkeyAction, HotkeyBinding> bindings)
    {
        if (_disposed) return;

        lock (_gate)
        {
            _pendingEnabled = enabled;
            _pendingBindings = new Dictionary<HotkeyAction, HotkeyBinding>(bindings);
        }

        if (_queueReady.Wait(TimeSpan.FromSeconds(2)) && _threadId != 0)
            PostThreadMessageW(_threadId, WmAppRebind, IntPtr.Zero, IntPtr.Zero);
    }

    private void MessageLoop()
    {
        // Force creation of the thread message queue before releasing waiters.
        PeekMessageW(out _, IntPtr.Zero, 0, 0, PmNoRemove);
        _threadId = GetCurrentThreadId();
        _queueReady.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmAppRebind)
            {
                RebindOnLoopThread();
                continue;
            }

            if (msg.message == WmHotKey)
            {
                var id = (int)msg.wParam;
                if (_activeIdToAction.TryGetValue(id, out var action))
                    HotkeyTriggered?.Invoke(action);
            }
        }

        UnregisterAllOnLoopThread();
    }

    private void RebindOnLoopThread()
    {
        UnregisterAllOnLoopThread();

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
            var nextId = 1;
            foreach (var (action, binding) in snapshot)
            {
                if (binding.IsEmpty)
                {
                    nextStatuses[action] = new HotkeyRegistrationStatus(action, false);
                    continue;
                }

                if (!KeyNameMapper.TryToWin32VirtualKey(binding.NormalizedKeyName, out var vk))
                {
                    nextStatuses[action] = new HotkeyRegistrationStatus(action, false, $"按键「{binding.NormalizedKeyName}」不支持系统全局注册");
                    continue;
                }

                var fsMods = MapModifiers(binding.Modifiers) | ModNoRepeat;
                var id = nextId++;
                if (RegisterHotKey(IntPtr.Zero, id, fsMods, vk))
                {
                    _activeIdToAction[id] = action;
                    nextStatuses[action] = new HotkeyRegistrationStatus(action, true);
                }
                else
                {
                    var err = Marshal.GetLastWin32Error();
                    var msg = err == ErrorHotkeyAlreadyRegistered
                        ? "该全局快捷键已被其他程序占用"
                        : $"系统热键注册失败 (错误码 {err})";
                    nextStatuses[action] = new HotkeyRegistrationStatus(action, false, msg);
                }
            }
        }

        lock (_gate)
        {
            _statuses = nextStatuses;
        }

        StatusChanged?.Invoke();
    }

    private void UnregisterAllOnLoopThread()
    {
        foreach (var id in _activeIdToAction.Keys)
            UnregisterHotKey(IntPtr.Zero, id);
        _activeIdToAction.Clear();
    }

    private static uint MapModifiers(HotkeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) result |= ModAlt;
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) result |= ModControl;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) result |= ModShift;
        if (modifiers.HasFlag(HotkeyModifiers.Meta)) result |= ModWin;
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_queueReady.IsSet && _threadId != 0)
            PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);

        _thread.Join(TimeSpan.FromMilliseconds(500));
        _queueReady.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinMsg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out WinMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out WinMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
