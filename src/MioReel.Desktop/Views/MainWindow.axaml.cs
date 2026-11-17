using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Aviora.Controls;
using FrameFlux;
using FrameFlux.FFmpeg;
using Microsoft.Extensions.DependencyInjection;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Desktop.Services;
using MioReel.Desktop.ViewModels;

namespace MioReel.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = null!;
    private readonly ISettingsService _settings = null!;
    private WindowPlacement? _normalPlacement;
    private WindowPlacement? _miniPlacement;
    private bool _displayedMini;
    private bool _isClosing;
    private bool _awaitingSourcePlayback;
    private readonly DispatcherTimer _playbackSyncTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private double _normalWidth = 1260;
    private double _normalHeight = 780;
    private bool _isTransitioningMini;
    private double _lastObservedPosSeconds = -1;
    private int _eofStalledTicks;
    private bool _eofNotifiedForCurrentUrl;
    private bool _suppressMiniDragOnce;
    private IPointer? _miniPointer;
    private long _lastDownloadRefreshTick;
    private readonly DispatcherTimer _miniControlsHideTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public MainWindow()
    {
        InitializeComponent();
    }

    [ActivatorUtilitiesConstructor]
    public MainWindow(
        MainWindowViewModel viewModel,
        ISettingsService settings,
        IDrawerHostService drawerService,
        IDialogHostService dialogService,
        ILoadingHostService loadingService,
        IToastHostService toastService)
        : this()
    {
        _viewModel = viewModel;
        _settings = settings;
        _normalPlacement = settings.Current.NormalWindow;
        _miniPlacement = settings.Current.MiniWindow;

        DrawerHost.Service = drawerService;
        DialogHost.Service = dialogService;
        LoadingHost.Service = loadingService;
        ToastHost.Service = toastService;

        FrameFluxPlayer.PlayerFactory = new FfmpegMediaPlayerFactory();
        // Media comes from our prepared loopback proxy. Avoid the backend's 3-second HTTP startup buffer.
        FrameFluxPlayer.OpenOptions = new MediaOpenOptions
        {
            Network = new MediaNetworkOptions
            {
                InitialBufferDuration = TimeSpan.FromMilliseconds(150),
                RebufferDuration = TimeSpan.FromMilliseconds(750)
            }
        };
        ApplyRenderMode(_viewModel.Player.RenderMode);
        FrameFluxPlayer.PlaybackStateChanged += OnFrameFluxPlaybackStateChanged;
        FrameFluxPlayer.PlaybackError += OnFrameFluxPlaybackError;
        FrameFluxPlayer.PropertyChanged += (_, e) =>
        {
            if (e.Property == FrameFlux.Avalonia.MediaView.EffectivePresentationModeProperty)
                _viewModel.Player.IsEffectiveNativeSurface =
                    FrameFluxPlayer.EffectivePresentationMode == MediaVideoPresentationMode.NativeSurface;
        };

        var captionMargins = new Thickness(16, 0, 16, 0);
        TitleBarContentGrid.Margin = captionMargins;
        PlayerTopBarContentGrid.Margin = captionMargins;

        _playbackSyncTimer.Tick += OnPlaybackSyncTick;
        _playbackSyncTimer.Start();
        _miniControlsHideTimer.Tick += OnMiniControlsHideTick;

        SizeChanged += OnWindowSizeChanged;
        PositionChanged += (_, _) => RememberWindowBounds();
        Opened += (_, _) =>
        {
            if (_normalPlacement is { } placement) ApplyWindowPlacement(placement, false);
            else RememberWindowBounds();
        };
        Closing += (_, _) =>
        {
            RememberWindowBounds();
            SaveWindowPlacements();
            _isClosing = true;
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnWindowTunnelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Closed += (_, _) =>
        {
            _playbackSyncTimer.Stop();
            _miniControlsHideTimer.Stop();
            _viewModel.Player.Close();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                UpdateMaximizeIcons();
        };
        _viewModel.Player.StreamDimensionsChanged += FitPlayerViewport;
        _viewModel.Player.MiniModeChanged += OnMiniModeChanged;
        _viewModel.Player.MiniPinChanged += pinned => Topmost = pinned;
        _viewModel.Player.RenderModeChanged += OnRenderModeChanged;
        _viewModel.Player.SeekRequested += ts =>
        {
            _eofStalledTicks = 0;
            _eofNotifiedForCurrentUrl = false;
            _ = FrameFluxPlayer.SeekAsync(ts);
        };
        _viewModel.Player.PropertyChanged += OnPlayerPropertyChanged;
    }

    private void OnPlaybackSyncTick(object? sender, EventArgs e)
    {
        if (Environment.TickCount64 - _lastDownloadRefreshTick >= 500)
        {
            _lastDownloadRefreshTick = Environment.TickCount64;
            _viewModel.RefreshDownloadProgress();
        }
        if (!_viewModel.Player.IsOpen)
            return;

        if (_viewModel.Player.IsMiniMode && _viewModel.Player.IsNativeSurfaceMode && !_viewModel.Player.IsAnyMenuOpen)
        {
            if (IsNativeCursorOverStage())
                ShowMiniControls();
            else
                ScheduleMiniControlsHide();
        }

        // In NativeSurface mode, child HWND consumes clicks directly without raising MainWindow PointerPressed.
        // Dismiss any open menu popup if the user clicks over the video surface itself.
        if (_viewModel.Player.IsAnyMenuOpen
            && _viewModel.Player.IsNativeSurfaceMode
            && !DockedMenuPopup.IsPointerOverPopup
            && OperatingSystem.IsWindows()
            && (GetAsyncKeyState(0x01) & 0x8000) != 0
            && IsCursorOverVisual(PlayerViewportBox)
            && !IsCursorOverVisual(DockedTransportBar))
        {
            _viewModel.Player.CloseMenu();
        }

        if (!_viewModel.Player.HasRealMediaUrl || _awaitingSourcePlayback)
            return;

        var pos = FrameFluxPlayer.Position;
        var dur = FrameFluxPlayer.Duration;
        _viewModel.Player.SyncFromPlayer(pos, dur);

        if (dur is { TotalSeconds: > 0.5 } durVal)
        {
            if (_viewModel.Player.ConsumePendingResumeSeconds() is { } resumeSec
                && resumeSec < durVal.TotalSeconds - 0.5)
            {
                _ = FrameFluxPlayer.SeekAsync(TimeSpan.FromSeconds(resumeSec));
                _lastObservedPosSeconds = resumeSec;
                _eofStalledTicks = 0;
                return;
            }

            if (!_eofNotifiedForCurrentUrl
                && _viewModel.Player.IsPlaying
                && !_viewModel.Player.IsResolvingStream
                && FrameFluxPlayer.State == MediaPlaybackState.Playing)
            {
                var posSec = pos.TotalSeconds;
                if (posSec >= durVal.TotalSeconds - 0.15 && Math.Abs(posSec - _lastObservedPosSeconds) < 0.005)
                {
                    _eofStalledTicks++;
                    if (_eofStalledTicks >= 1)
                    {
                        _eofNotifiedForCurrentUrl = true;
                        _viewModel.Player.NotifyPlaybackEnded();
                    }
                }
                else
                {
                    _eofStalledTicks = 0;
                }
                _lastObservedPosSeconds = posSec;
            }
            else
            {
                _eofStalledTicks = 0;
            }
        }
    }

    private void ApplyRenderMode(VideoRenderMode mode)
    {
        FrameFluxPlayer.PresentationMode = mode switch
        {
            VideoRenderMode.GpuComposition => MediaVideoPresentationMode.GpuComposition,
            VideoRenderMode.SoftwareBitmap => MediaVideoPresentationMode.SoftwareBitmap,
            VideoRenderMode.NativeSurface => MediaVideoPresentationMode.NativeSurface,
            _ => MediaVideoPresentationMode.Automatic
        };
    }

    private async void OnRenderModeChanged(VideoRenderMode mode)
    {
        ApplyRenderMode(mode);
        var url = _viewModel.Player.ResolvedPlayUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;

        var resumePos = FrameFluxPlayer.Position.TotalSeconds;
        await FrameFluxPlayer.StopAsync();
        FrameFluxPlayer.Source = null;

        _eofStalledTicks = 0;
        _eofNotifiedForCurrentUrl = false;
        _lastObservedPosSeconds = -1;
        FrameFluxPlayer.PlaybackRate = _viewModel.Player.PlaybackRate;
        FrameFluxPlayer.Source = MediaSource.FromUri(url);

        if (resumePos > 0.5)
        {
            await Task.Delay(250);
            if (_viewModel.Player.IsOpen && FrameFluxPlayer.Source is not null)
                await FrameFluxPlayer.SeekAsync(TimeSpan.FromSeconds(resumePos));
        }
    }

    private bool IsNativeCursorOverStage() => IsCursorOverVisual(PlayerStageWrap);

    private bool IsCursorOverVisual(Visual? visual)
    {
        if (!OperatingSystem.IsWindows() || visual is null || !visual.IsEffectivelyVisible || !IsVisible)
            return false;
        if (visual.Bounds.Width <= 0 || visual.Bounds.Height <= 0 || !GetCursorPos(out var pt))
            return false;
        var tl = visual.PointToScreen(new Point(0, 0));
        var br = visual.PointToScreen(new Point(visual.Bounds.Width, visual.Bounds.Height));
        return pt.X >= tl.X && pt.X <= br.X && pt.Y >= tl.Y && pt.Y <= br.Y;
    }

    private void OnDockedMenuPopupClosed(object? sender, EventArgs e)
    {
        _viewModel.Player.CloseMenu();
        ScheduleMiniControlsHide();
    }

    private void OnWindowTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel.Player.IsMiniMode && IsWithinPlayerStage(e.Source))
        {
            _miniPointer = e.Pointer;
            ShowMiniControls();
        }
        if (!_viewModel.Player.IsAnyMenuOpen)
            return;

        if (IsWithinMenuOrTrigger(e.Source))
            return;

        _viewModel.Player.CloseMenu();
        if (_viewModel.Player.IsMiniMode)
            _suppressMiniDragOnce = true;
    }

    private bool IsWithinMenuOrTrigger(object? source)
    {
        for (var current = source as Visual; current is not null; current = Avalonia.VisualTree.VisualExtensions.GetVisualParent(current))
        {
            if (ReferenceEquals(current, FloatingMenuPanel) || ReferenceEquals(current, DockedMenuPanel))
                return true;
            if (current is Button { Name: "RenderModeBtn" or "SpeedSelectBtn" or "QualitySelectBtn" })
                return true;
        }
        return false;
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsAnyMenuOpen) && !_viewModel.Player.IsAnyMenuOpen)
            ScheduleMiniControlsHide();

        if (e.PropertyName == nameof(PlayerViewModel.ResolvedPlayUrl))
        {
            _eofStalledTicks = 0;
            _eofNotifiedForCurrentUrl = false;
            _lastObservedPosSeconds = -1;

            var url = _viewModel.Player.ResolvedPlayUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                FrameFluxPlayer.Source = null;
                _awaitingSourcePlayback = false;
            }
            else
            {
                _awaitingSourcePlayback = true;
                FrameFluxPlayer.PlaybackRate = _viewModel.Player.PlaybackRate;
                FrameFluxPlayer.Source = MediaSource.FromUri(url);
            }
        }
        else if (e.PropertyName == nameof(PlayerViewModel.IsPlaying) && FrameFluxPlayer.Source is not null && !_awaitingSourcePlayback)
        {
            if (_viewModel.Player.IsPlaying)
                _ = FrameFluxPlayer.ResumeAsync();
            else
                _ = FrameFluxPlayer.PauseAsync();
        }
        else if (e.PropertyName == nameof(PlayerViewModel.PlaybackRate))
        {
            FrameFluxPlayer.PlaybackRate = _viewModel.Player.PlaybackRate;
        }
    }

    private void OnFrameFluxPlaybackError(object? sender, MediaPlaybackErrorEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
            _viewModel.Player.ReportPlaybackError(e.Error?.Message));
    }

    private void OnFrameFluxPlaybackStateChanged(object? sender, MediaPlaybackStateChangedEventArgs e)
    {
        if (e.NewState == MediaPlaybackState.Playing)
        {
            _awaitingSourcePlayback = false;
            _viewModel.Player.ReleaseRetiredProxySessions();
        }
        if (_awaitingSourcePlayback) return;
        if (e.NewState == MediaPlaybackState.Stopped && e.OldState == MediaPlaybackState.Playing && !_eofNotifiedForCurrentUrl)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var dur = FrameFluxPlayer.Duration?.TotalSeconds ?? _viewModel.Player.DurationSeconds;
                var pos = Math.Max(FrameFluxPlayer.Position.TotalSeconds, _lastObservedPosSeconds);
                if (!_awaitingSourcePlayback && !_eofNotifiedForCurrentUrl && dur > 0.5 && pos >= dur - 0.5)
                {
                    _eofNotifiedForCurrentUrl = true;
                    _viewModel.Player.NotifyPlaybackEnded();
                }
            });
        }
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        if (!_viewModel.Player.IsMiniMode && !_isTransitioningMini && w >= 600)
        {
            _normalWidth = w;
            _normalHeight = e.NewSize.Height;
        }

        _viewModel.LayoutMode = w switch
        {
            >= 920 => WindowLayoutMode.Wide,
            >= 620 => WindowLayoutMode.CompactRail,
            _ => WindowLayoutMode.Narrow
        };

        UpdatePlayerSplitLayout();
        FitPlayerViewport();
        RememberWindowBounds();
    }

    private void UpdatePlayerSplitLayout()
    {
        if (PlayerSplitGrid is null || PlayerSidePanel is null) return;

        if (_viewModel.Player.IsMiniMode)
        {
            PlayerSplitGrid.ColumnSpacing = 0;
            PlayerSplitGrid.RowSpacing = 0;
            PlayerSplitGrid.Margin = new Thickness(0);
            PlayerStageWrap.CornerRadius = new CornerRadius(0);
            PlayerStageWrap.BorderThickness = new Thickness(0);
            PlayerViewportBox.CornerRadius = new CornerRadius(0);
            return;
        }

        PlayerSplitGrid.Margin = new Thickness(16);
        PlayerStageWrap.CornerRadius = new CornerRadius(18);
        PlayerStageWrap.BorderThickness = new Thickness(1);
        PlayerViewportBox.CornerRadius = new CornerRadius(16);

        if (_viewModel.LayoutMode == WindowLayoutMode.Narrow)
        {
            PlayerSplitGrid.ColumnSpacing = 0;
            PlayerSplitGrid.RowSpacing = 14;
            Grid.SetColumn(PlayerSidePanel, 0);
            Grid.SetRow(PlayerSidePanel, 1);
            PlayerSidePanel.Width = double.NaN;
            PlayerSidePanel.Height = 220;
        }
        else
        {
            PlayerSplitGrid.ColumnSpacing = 16;
            PlayerSplitGrid.RowSpacing = 0;
            Grid.SetColumn(PlayerSidePanel, 1);
            Grid.SetRow(PlayerSidePanel, 0);
            PlayerSidePanel.Width = 310;
            PlayerSidePanel.Height = double.NaN;
        }
    }

    private void OnPlayerStageSizeChanged(object? sender, SizeChangedEventArgs e) => FitPlayerViewport();

    private void FitPlayerViewport()
    {
        if (PlayerViewportHost is null || PlayerViewportBox is null) return;

        var pad = _viewModel.Player.IsMiniMode ? 0 : (_viewModel.Player.IsNativeSurfaceMode ? 12 : 20);
        var availW = Math.Max(120, PlayerViewportHost.Bounds.Width - pad);
        var availH = Math.Max(120, PlayerViewportHost.Bounds.Height - pad);
        var sw = Math.Max(1, _viewModel.Player.StreamWidth);
        var sh = Math.Max(1, _viewModel.Player.StreamHeight);
        var ratio = (double)sw / sh;

        var fitW = availW;
        var fitH = fitW / ratio;
        if (fitH > availH)
        {
            fitH = availH;
            fitW = fitH * ratio;
        }

        PlayerViewportBox.Width = Math.Round(fitW);
        PlayerViewportBox.Height = Math.Round(fitH);
    }

    private void OnMiniModeChanged(bool isMini, bool isLandscape)
    {
        if (_isClosing) return;
        _miniControlsHideTimer.Stop();
        _miniPointer = null;
        RememberWindowBounds();
        SaveWindowPlacements();
        _isTransitioningMini = true;
        _displayedMini = isMini;
        var target = isMini
            ? _miniPlacement ?? new WindowPlacement(Position.X, Position.Y, isLandscape ? 544 : 360, isLandscape ? 306 : 640)
            : _normalPlacement ?? new WindowPlacement(Position.X, Position.Y, _normalWidth, _normalHeight);
        WindowState = WindowState.Normal;
        if (isMini)
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.BorderOnly;
            Topmost = _viewModel.Player.IsMiniPinned;
        }
        else
        {
            Topmost = false;
            WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
        }
        ApplyWindowPlacement(target, isMini);
        UpdatePlayerSplitLayout();
        FitPlayerViewport();
        Dispatcher.UIThread.Post(() =>
        {
            if (_displayedMini != isMini || _isClosing) return;
            ApplyWindowPlacement(target, isMini);
            UpdatePlayerSplitLayout();
            FitPlayerViewport();
            _isTransitioningMini = false;
            RememberWindowBounds();
        }, DispatcherPriority.Background);
    }

    private void RememberWindowBounds()
    {
        if (_isTransitioningMini || _isClosing || !IsVisible || WindowState == WindowState.Minimized) return;
        if (!_displayedMini && WindowState == WindowState.Maximized)
        {
            if (_normalPlacement is { } normal) _normalPlacement = normal with { IsMaximized = true };
            return;
        }
        var placement = new WindowPlacement(Position.X, Position.Y, Bounds.Width, Bounds.Height);
        if (_displayedMini) _miniPlacement = placement;
        else _normalPlacement = placement;
    }

    private void SaveWindowPlacements() => _settings.Update(_settings.Current with
    {
        NormalWindow = _normalPlacement,
        MiniWindow = _miniPlacement
    });

    private void ApplyWindowPlacement(WindowPlacement placement, bool mini)
    {
        var screen = Screens.ScreenFromPoint(new PixelPoint(placement.X, placement.Y)) ?? Screens.Primary;
        if (screen is not null) placement = WindowPlacementPolicy.Fit(placement, screen.WorkingArea, screen.Scaling, mini);
        Width = placement.Width;
        Height = placement.Height;
        Position = new PixelPoint(placement.X, placement.Y);
        WindowState = !mini && placement.IsMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    private static bool IsInteractiveControl(object? source)
    {
        for (var current = source as Visual; current is not null; current = Avalonia.VisualTree.VisualExtensions.GetVisualParent(current))
        {
            if (current is Button or TextBox)
                return true;
        }
        return false;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInteractiveControl(e.Source))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
            e.Handled = true;
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsInteractiveControl(e.Source) || _viewModel.Player.IsMiniMode)
            return;
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnMinimizeWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeToggleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();

    private void UpdateMaximizeIcons()
    {
        var isMax = WindowState == WindowState.Maximized;
        WinMaxIcon.IsVisible = !isMax;
        WinRestoreIcon.IsVisible = isMax;
        PlayerMaxIcon.IsVisible = !isMax;
        PlayerRestoreIcon.IsVisible = isMax;
    }

    private void OnMiniStagePointerEntered(object? sender, PointerEventArgs e)
    {
        _miniPointer = e.Pointer;
        if (_viewModel.Player.IsMiniMode)
            ShowMiniControls();
    }

    private void OnMiniStagePointerExited(object? sender, PointerEventArgs e)
    {
        _miniPointer = e.Pointer;
        ScheduleMiniControlsHide();
    }

    private void ShowMiniControls()
    {
        _miniControlsHideTimer.Stop();
        _viewModel.Player.SetMiniControlsVisible(true);
    }

    private void ScheduleMiniControlsHide()
    {
        if (_viewModel.Player.IsMiniMode && _viewModel.Player.AreMiniControlsVisible && !_miniControlsHideTimer.IsEnabled)
            _miniControlsHideTimer.Start();
    }

    private bool IsWithinPlayerStage(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual))
            if (ReferenceEquals(visual, PlayerStageWrap)) return true;
        return false;
    }

    private void OnMiniControlsHideTick(object? sender, EventArgs e)
    {
        _miniControlsHideTimer.Stop();
        if (!_viewModel.Player.IsMiniMode || _isClosing) return;

        // Button/slider capture can produce PointerExited before release. Keep the control
        // alive until its click/drag completes, even if the pointer has left the window.
        if (IsWithinPlayerStage(_miniPointer?.Captured))
        {
            ScheduleMiniControlsHide();
            return;
        }
        if (_viewModel.Player.IsAnyMenuOpen || DockedMenuPopup.IsPointerOverPopup) return;
        var inside = OperatingSystem.IsWindows() ? IsNativeCursorOverStage() : PlayerStageWrap.IsPointerOver;
        if (!inside) _viewModel.Player.SetMiniControlsVisible(false);
    }

    private void OnMiniStageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!_viewModel.Player.IsMiniMode || IsInteractiveControl(e.Source))
            return;
        _viewModel.Player.ToggleMiniControls();
        e.Handled = true;
    }

    private void OnMiniStagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_suppressMiniDragOnce)
        {
            _suppressMiniDragOnce = false;
            return;
        }
        if (!_viewModel.Player.IsMiniMode || IsInteractiveControl(e.Source))
            return;

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            _viewModel.Player.ToggleMiniControls();
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnGlobalSearchGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _viewModel.FocusSearch();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_viewModel.Player.IsOpen) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        switch (e.Key)
        {
            case Key.PageUp:
                ((System.Windows.Input.ICommand)_viewModel.Player.PreviousEpisodeCommand).Execute(null);
                e.Handled = true;
                break;
            case Key.PageDown:
                ((System.Windows.Input.ICommand)_viewModel.Player.NextEpisodeCommand).Execute(null);
                e.Handled = true;
                break;
            case Key.Space:
                _viewModel.Player.IsPlaying = !_viewModel.Player.IsPlaying;
                e.Handled = true;
                break;
            case Key.H:
                if (_viewModel.Player.IsMiniMode)
                {
                    _viewModel.Player.ToggleMiniControls();
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                if (_viewModel.Player.IsAnyMenuOpen)
                    _viewModel.Player.CloseMenu();
                else if (_viewModel.Player.IsMiniMode)
                    ((System.Windows.Input.ICommand)_viewModel.Player.ToggleMiniModeCommand).Execute(null);
                else
                    _viewModel.Player.Close();
                e.Handled = true;
                break;
            case Key.Left:
                _viewModel.Player.PositionSeconds = Math.Max(0, _viewModel.Player.PositionSeconds - 5);
                e.Handled = true;
                break;
            case Key.Right:
                _viewModel.Player.PositionSeconds = Math.Min(_viewModel.Player.DurationSeconds, _viewModel.Player.PositionSeconds + 5);
                e.Handled = true;
                break;
        }
    }
}
