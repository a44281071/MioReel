using System.ComponentModel;
using System.Windows.Input;
using Android.Views;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MioReel.Desktop.ViewModels;

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
    private string? _loadedUrl;
    private string? _reportedErrorUrl;
    private bool _endedReported;
    private bool _hostPaused;

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

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _syncTimer.Tick += (_, _) => SyncPlayback();

        _viewModel.Player.PropertyChanged += OnPlayerPropertyChanged;
        _viewModel.Player.SeekRequested += OnSeekRequested;

        AttachedToVisualTree += (_, _) => OnAttached();
        DetachedFromVisualTree += (_, _) => OnDetached();
    }

    public static AndroidMainView? Current { get; private set; }

    private void OnAttached()
    {
        Current = this;
        MainActivity.KeyDispatched += HandleKey;

        // A recreated activity may hand us a fresh native player surface.
        _loadedUrl = null;
        _reportedErrorUrl = null;
        _endedReported = false;

        _syncTimer.Start();

        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.Player.IsOpen)
                PlayPauseButton.Focus();
            else
                NavExploreButton.Focus();
        });
    }

    private void OnDetached()
    {
        _syncTimer.Stop();
        MainActivity.KeyDispatched -= HandleKey;
        if (ReferenceEquals(Current, this))
            Current = null;
        MediaPlayer.ReleasePlayer();
    }

    public bool HandleBack()
    {
        if (_viewModel.Player.IsOpen)
        {
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
                _viewModel.Player.IsPlaying = !_viewModel.Player.IsPlaying;
                return true;

            case Keycode.MediaNext:
            case Keycode.ChannelUp:
                ((ICommand)_viewModel.Player.NextEpisodeCommand).Execute(null);
                return true;

            case Keycode.MediaPrevious:
            case Keycode.ChannelDown:
                ((ICommand)_viewModel.Player.PreviousEpisodeCommand).Execute(null);
                return true;

            case Keycode.MediaFastForward:
                MediaPlayer.SeekBy(10_000);
                return true;

            case Keycode.MediaRewind:
                MediaPlayer.SeekBy(-10_000);
                return true;

            case Keycode.DpadRight:
                MediaPlayer.SeekBy(10_000);
                return true;

            case Keycode.DpadLeft:
                MediaPlayer.SeekBy(-10_000);
                return true;

            case Keycode.MediaStop:
                ClosePlayer();
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
                    Dispatcher.UIThread.Post(() => PlayPauseButton.Focus());
                }
                else
                {
                    MediaPlayer.Stop();
                    _loadedUrl = null;
                    _endedReported = false;
                    Dispatcher.UIThread.Post(() => NavExploreButton.Focus());
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

    private void OnClosePlayerClick(object? sender, RoutedEventArgs e) => ClosePlayer();

    private void ClosePlayer()
    {
        MediaPlayer.Stop();
        _loadedUrl = null;
        _viewModel.Player.Close();
        _viewModel.Player.ReleaseRetiredProxySessions();
    }

    private void OnSeekBackClick(object? sender, RoutedEventArgs e) => MediaPlayer.SeekBy(-10_000);

    private void OnSeekForwardClick(object? sender, RoutedEventArgs e) => MediaPlayer.SeekBy(10_000);
}