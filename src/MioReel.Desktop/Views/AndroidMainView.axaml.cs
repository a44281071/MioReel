using System.ComponentModel;
using System.Windows.Input;
using Android.Views;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MioReel.Desktop.Services;
using MioReel.Desktop.ViewModels;
using ReactiveUI.Primitives;

namespace MioReel.Desktop.Views;

/// <summary>
/// Android TV single-view shell. All interactions are D-pad friendly and the video
/// surface is the native Media3 PlayerView hosted through Avalonia's NativeControlHost.
/// </summary>
public partial class AndroidMainView : UserControl
{
    private const int StateEnded = 4;

    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _playerDetailsHideTimer;
    private string? _loadedUrl;
    private string? _reportedErrorUrl;
    private bool _endedReported;
    private bool _hostPaused;
    private readonly CatalogPrefetchGate _explorePrefetchGate = new();
    private ScrollViewer? _exploreScrollViewer;
    private bool _isCheckingExplorePrefetch;
    private bool _isExplorePrefetchQueued;
    private bool _isCatalogObserved;

    // Avalonia's compiled XAML requires an accessible parameterless constructor.
    public AndroidMainView()
        : this(Program.Services.GetRequiredService<MainWindowViewModel>())
    {
    }

    [ActivatorUtilitiesConstructor]
    public AndroidMainView(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.Player.KeepPlayingAcrossVideos = true;

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _syncTimer.Tick += (_, _) => SyncPlayback();

        _playerDetailsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _playerDetailsHideTimer.Tick += (_, _) => HidePlayerDetails();

        _viewModel.Player.PropertyChanged += OnPlayerPropertyChanged;
        _viewModel.Player.SeekRequested += OnSeekRequested;

        ExploreVirtualizedList.TemplateApplied += (_, _) => AttachExploreScrollViewer();
        ExploreVirtualizedList.Loaded += (_, _) => AttachExploreScrollViewer();
        ExploreVirtualizedList.SizeChanged += (_, _) => UpdateExploreColumns();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Down or Key.PageDown or Key.End or Key.Space)
                RequestExplorePrefetch();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        SizeChanged += (_, _) =>
        {
            UpdateExploreColumns();
            QueueExplorePrefetchCheck();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
                QueueExplorePrefetchCheck();
        };

        AttachedToVisualTree += (_, _) => OnAttached();
        DetachedFromVisualTree += (_, _) => OnDetached();
    }

    public static AndroidMainView? Current { get; private set; }

    private void OnAttached()
    {
        Current = this;
        if (!_isCatalogObserved)
        {
            _viewModel.PropertyChanged += OnCatalogStateChanged;
            _isCatalogObserved = true;
        }
        MainActivity.KeyDispatched += HandleKey;

        // A recreated activity may hand us a fresh native player surface.
        _loadedUrl = null;
        _reportedErrorUrl = null;
        _endedReported = false;

        _syncTimer.Start();

        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.Player.IsOpen)
            {
                PlayerRoot.Focus();
                if (_viewModel.Player.IsDetailsVisible)
                    FocusCurrentPlayerEpisode();
            }
            else
            {
                NavExploreButton.Focus();
            }
        });
        AttachExploreScrollViewer();
        UpdateExploreColumns();
        QueueExplorePrefetchCheck();
    }

    private void OnDetached()
    {
        _syncTimer.Stop();
        _playerDetailsHideTimer.Stop();
        if (_isCatalogObserved)
        {
            _viewModel.PropertyChanged -= OnCatalogStateChanged;
            _isCatalogObserved = false;
        }
        if (_exploreScrollViewer is not null)
        {
            _exploreScrollViewer.ScrollChanged -= OnExploreScrollChanged;
            _exploreScrollViewer = null;
        }
        MainActivity.KeyDispatched -= HandleKey;
        if (ReferenceEquals(Current, this))
            Current = null;
        MediaPlayer.ReleasePlayer();
    }

    private void AttachExploreScrollViewer()
    {
        var sv = ExploreVirtualizedList.FindDescendantOfType<ScrollViewer>();
        if (sv is null || ReferenceEquals(_exploreScrollViewer, sv))
            return;

        if (_exploreScrollViewer is not null)
            _exploreScrollViewer.ScrollChanged -= OnExploreScrollChanged;

        _exploreScrollViewer = sv;
        _exploreScrollViewer.ScrollChanged += OnExploreScrollChanged;
        QueueExplorePrefetchCheck();
    }

    private void OnExploreScrollChanged(object? sender, ScrollChangedEventArgs e)
        => RequestExplorePrefetch();

    private void UpdateExploreColumns()
    {
        var width = ExploreVirtualizedList.Bounds.Width;
        if (width <= 0)
            return;
        _viewModel.UpdateExploreColumns(width, 118, 8);
    }

    private void RequestExplorePrefetch()
    {
        _explorePrefetchGate.RequestLoad();
        QueueExplorePrefetchCheck();
    }

    private void QueueExplorePrefetchCheck()
    {
        if (_isExplorePrefetchQueued) return;
        _isExplorePrefetchQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _isExplorePrefetchQueued = false;
            _ = CheckExplorePrefetchAsync();
        }, DispatcherPriority.Background);
    }

    private async Task CheckExplorePrefetchAsync()
    {
        if (_isCheckingExplorePrefetch
            || !_viewModel.IsExploreActive
            || !IsEffectivelyVisible
            || !_viewModel.HasMoreCatalog
            || _viewModel.IsLoadingCatalog
            || _viewModel.IsLoadingMoreCatalog)
            return;

        if (_exploreScrollViewer is not { } sv || sv.Viewport.Height <= 0)
            return;

        if (!_explorePrefetchGate.TryStart(
                sv.Extent.Height,
                sv.Offset.Y,
                sv.Viewport.Height))
            return;

        _isCheckingExplorePrefetch = true;
        try
        {
            await _viewModel.LoadMoreCatalogCommand.Execute().ToTask();
        }
        finally
        {
            _isCheckingExplorePrefetch = false;
            QueueExplorePrefetchCheck();
        }
    }

    private void OnCatalogStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsLoadingCatalog)
            && _viewModel.IsLoadingCatalog)
        {
            _explorePrefetchGate.Reset();
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsLoadingMoreCatalog)
            && _viewModel is { IsLoadingMoreCatalog: true, IsLoadingCatalog: false })
        {
            _explorePrefetchGate.ConsumeRequest();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.SelectedCategory)
            or nameof(MainWindowViewModel.SelectedHomeTab)
            or nameof(MainWindowViewModel.SelectedRankingBoardId))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_exploreScrollViewer is not null)
                    _exploreScrollViewer.Offset = default;
            });
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsLoadingCatalog)
            or nameof(MainWindowViewModel.IsLoadingMoreCatalog)
            or nameof(MainWindowViewModel.HasMoreCatalog)
            or nameof(MainWindowViewModel.SelectedCategory)
            or nameof(MainWindowViewModel.SelectedHomeTab)
            or nameof(MainWindowViewModel.SelectedRankingBoardId))
        {
            if (e.PropertyName is nameof(MainWindowViewModel.IsLoadingCatalog)
                or nameof(MainWindowViewModel.HasMoreCatalog))
            {
                Dispatcher.UIThread.Post(AttachExploreScrollViewer);
            }
            QueueExplorePrefetchCheck();
        }
    }

    public bool HandleBack()
    {
        if (_viewModel.Player.IsOpen)
        {
            if (_viewModel.Player.IsDetailsVisible)
                HidePlayerDetails();
            else
                ClosePlayer();
            return true;
        }

        // Leave the search box (and dismiss the IME) before letting Back exit the app.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
        {
            NavExploreButton.Focus();
            return true;
        }

        return false;
    }

    public void OnHostPaused()
    {
        _hostPaused = true;
        MediaPlayer.SetPlaying(false);
    }

    public void OnHostResumed()
    {
        if (!_hostPaused)
            return;

        _hostPaused = false;
        if (_viewModel.Player.IsOpen && _viewModel.Player.IsPlaying)
            MediaPlayer.SetPlaying(true);
    }

    private bool HandleKey(KeyEvent? e)
    {
        if (e is null || !_viewModel.Player.IsOpen)
            return false;

        switch (e.KeyCode)
        {
            case Keycode.MediaPlayPause:
            case Keycode.MediaPlay:
            case Keycode.MediaPause:
            case Keycode.Headsethook:
                ResetPlayerDetailsIdleTimer();
                _viewModel.Player.IsPlaying = !_viewModel.Player.IsPlaying;
                return true;

            case Keycode.MediaNext:
            case Keycode.ChannelUp:
                ResetPlayerDetailsIdleTimer();
                ((ICommand)_viewModel.Player.NextEpisodeCommand).Execute(null);
                return true;

            case Keycode.MediaPrevious:
            case Keycode.ChannelDown:
                ResetPlayerDetailsIdleTimer();
                ((ICommand)_viewModel.Player.PreviousEpisodeCommand).Execute(null);
                return true;

            case Keycode.MediaFastForward:
                ResetPlayerDetailsIdleTimer();
                MediaPlayer.SeekBy(10_000);
                return true;

            case Keycode.MediaRewind:
                ResetPlayerDetailsIdleTimer();
                MediaPlayer.SeekBy(-10_000);
                return true;

            case Keycode.MediaStop:
                ClosePlayer();
                return true;
        }

        if (_viewModel.Player.IsDetailsVisible)
        {
            // 详情面板打开时，左右键交给 Avalonia 在面板内移动焦点，
            // 不再触发快退/快进；上下键也用于面板内导航。
            if (e.KeyCode is Keycode.DpadUp or Keycode.DpadDown
                or Keycode.DpadLeft or Keycode.DpadRight
                or Keycode.DpadCenter or Keycode.Enter)
            {
                ResetPlayerDetailsIdleTimer();
            }
            return false;
        }

        switch (e.KeyCode)
        {
            case Keycode.DpadRight:
                MediaPlayer.SeekBy(10_000);
                return true;

            case Keycode.DpadLeft:
                MediaPlayer.SeekBy(-10_000);
                return true;

            case Keycode.DpadUp:
            case Keycode.DpadDown:
                // 上下按键暂时留空，避免误触。
                return true;

            case Keycode.DpadCenter:
            case Keycode.Enter:
                ShowPlayerDetails();
                return true;

            default:
                return false;
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsOpen):
                if (_viewModel.Player.IsOpen)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        PlayerRoot.Focus();
                        if (_viewModel.Player.IsDetailsVisible)
                            FocusCurrentPlayerEpisode();
                    });
                }
                else
                {
                    _playerDetailsHideTimer.Stop();
                    MediaPlayer.Stop();
                    _loadedUrl = null;
                    _endedReported = false;
                    Dispatcher.UIThread.Post(() => NavExploreButton.Focus());
                }
                break;

            case nameof(PlayerViewModel.IsDetailsVisible):
                if (_viewModel.Player.IsDetailsVisible)
                {
                    ResetPlayerDetailsIdleTimer();
                    Dispatcher.UIThread.Post(FocusCurrentPlayerEpisode);
                }
                else
                {
                    _playerDetailsHideTimer.Stop();
                    Dispatcher.UIThread.Post(() => PlayerRoot.Focus());
                }
                break;

            case nameof(PlayerViewModel.CurrentDrama):
                // “跳到下一个推荐视频”只替换详情内容，不关闭面板：
                // 把焦点留在同一个按钮上，方便连续切换多部推荐剧。
                if (_viewModel.Player.IsOpen && _viewModel.Player.IsDetailsVisible)
                {
                    ResetPlayerDetailsIdleTimer();
                    Dispatcher.UIThread.Post(
                        () => SkipToNextRecommendationButton.Focus(),
                        DispatcherPriority.Background);
                }
                break;

            case nameof(PlayerViewModel.ResolvedPlayUrl):
                _loadedUrl = null;
                _reportedErrorUrl = null;
                _endedReported = false;
                TryLoadCurrentUrl();
                break;

            case nameof(PlayerViewModel.IsPlaying):
                MediaPlayer.SetPlaying(_viewModel.Player.IsPlaying);
                break;

            case nameof(PlayerViewModel.PlaybackRate):
                MediaPlayer.SetPlaybackSpeed(_viewModel.Player.PlaybackRate);
                break;

            case nameof(PlayerViewModel.Volume):
            case nameof(PlayerViewModel.IsMuted):
                MediaPlayer.SetVolume(EffectiveVolume);
                break;

            case nameof(PlayerViewModel.HasRealMediaUrl):
                if (!_viewModel.Player.HasRealMediaUrl)
                {
                    MediaPlayer.Stop();
                    _loadedUrl = null;
                }
                break;

            case nameof(PlayerViewModel.HasStreamError):
                if (_viewModel.Player.HasStreamError)
                {
                    MediaPlayer.Stop();
                    _loadedUrl = null;
                    _viewModel.Player.IsPlaying = false;
                }
                break;
        }
    }

    private double EffectiveVolume => _viewModel.Player.IsMuted ? 0.0 : _viewModel.Player.Volume;

    private void TryLoadCurrentUrl()
    {
        if (_viewModel.Player.HasStreamError)
            return;

        var url = _viewModel.Player.ResolvedPlayUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            MediaPlayer.Stop();
            _loadedUrl = null;
            return;
        }

        if (!MediaPlayer.IsReady)
            return;

        if (string.Equals(_loadedUrl, url, StringComparison.Ordinal))
            return;

        var resumeSeconds = _viewModel.Player.ConsumePendingResumeSeconds() ?? 0;
        MediaPlayer.Load(
            url,
            (long)(resumeSeconds * 1000),
            _viewModel.Player.IsPlaying && !_hostPaused,
            _viewModel.Player.PlaybackRate,
            EffectiveVolume);

        _loadedUrl = url;
        _endedReported = false;
        _reportedErrorUrl = null;

        if (resumeSeconds > 0.5)
            _viewModel.Player.NotifyDanmakuSeeked(resumeSeconds);
    }

    private void SyncPlayback()
    {
        if (!_viewModel.Player.IsOpen)
            return;

        if (!MediaPlayer.IsReady)
            return;

        if (_loadedUrl is null)
        {
            TryLoadCurrentUrl();
            return;
        }

        var durationMs = MediaPlayer.DurationMs;
        _viewModel.Player.SyncFromPlayer(
            TimeSpan.FromMilliseconds(MediaPlayer.PositionMs),
            durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : null);

        if (MediaPlayer.PlaybackState == StateEnded && !_endedReported)
        {
            _endedReported = true;
            _viewModel.Player.NotifyPlaybackEnded();
        }

        var error = MediaPlayer.PlayerErrorMessage;
        if (!string.IsNullOrWhiteSpace(error)
            && !string.Equals(_reportedErrorUrl, _loadedUrl, StringComparison.Ordinal))
        {
            _reportedErrorUrl = _loadedUrl;
            _viewModel.Player.ReportPlaybackError(error);
        }
    }

    private void OnSeekRequested(TimeSpan position)
        => MediaPlayer.SeekTo((long)(position.TotalSeconds * 1000));

    private void ShowPlayerDetails()
    {
        if (!_viewModel.Player.IsOpen) return;
        _viewModel.Player.ShowDetails();
        ResetPlayerDetailsIdleTimer();
        Dispatcher.UIThread.Post(FocusCurrentPlayerEpisode, DispatcherPriority.Background);
    }

    private void HidePlayerDetails()
    {
        _playerDetailsHideTimer.Stop();
        _viewModel.Player.HideDetails();
        PlayerRoot.Focus();
    }

    private void ResetPlayerDetailsIdleTimer()
    {
        _playerDetailsHideTimer.Stop();
        if (_viewModel.Player.IsOpen && _viewModel.Player.IsDetailsVisible)
            _playerDetailsHideTimer.Start();
    }

    private void FocusCurrentPlayerEpisode()
    {
        if (!_viewModel.Player.IsOpen || !_viewModel.Player.IsDetailsVisible)
            return;

        var buttons = PlayerDetailsPanel.GetVisualDescendants().OfType<Avalonia.Controls.Button>().ToList();
        var target = buttons.FirstOrDefault(x => x.DataContext is EpisodeOptionViewModel { IsCurrent: true })
            ?? buttons.FirstOrDefault();
        target?.Focus();
    }

    private void OnHidePlayerDetailsClick(object? sender, RoutedEventArgs e) => HidePlayerDetails();

    private void ClosePlayer()
    {
        MediaPlayer.Stop();
        _loadedUrl = null;
        _viewModel.Player.Close();
        _viewModel.Player.ReleaseRetiredProxySessions();
    }

    }