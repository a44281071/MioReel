using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MioReel.Desktop.Controls;

/// <summary>
/// NativeSurface 模式下的鼠标穿透透明叠加窗口：在原生子 HWND 之上绘制弹幕而不拦截点击、拖拽与滚轮事件。
/// </summary>
public sealed class DanmakuNativeOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;

    public DanmakuNativeOverlayWindow()
    {
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        CanResize = false;
        Focusable = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        Content = Overlay;
    }

    public DanmakuOverlay Overlay { get; } = new();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
        {
            var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate));
        }
    }

    public void SyncBounds(PixelPoint screenOrigin, double dipWidth, double dipHeight, bool topmost)
    {
        if (Topmost != topmost)
            Topmost = topmost;
        if (Position != screenOrigin)
            Position = screenOrigin;
        if (Math.Abs(Width - dipWidth) >= 0.5)
            Width = dipWidth;
        if (Math.Abs(Height - dipHeight) >= 0.5)
            Height = dipHeight;
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
