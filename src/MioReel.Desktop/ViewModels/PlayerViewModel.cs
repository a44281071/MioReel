using System.Collections.ObjectModel;
using Aviora.Presentation.Toasts;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace MioReel.Desktop.ViewModels;

public sealed class PlayerViewModel : ReactiveObject
{
    private static readonly (double Rate, string Label, string Subtitle)[] SpeedPresets =
    [
        (3.0, "3.0x", "三倍极速"),
        (2.5, "2.5x", "超速追剧"),
        (2.0, "2.0x", "极速浏览"),
        (1.5, "1.5x", "高效追剧"),
        (1.25, "1.25x", "轻快节奏"),
        (1.0, "1.0x", "正常语速"),
        (0.75, "0.75x", "慢速细看")
    ];

    private static readonly (int Quality, string Label, string Subtitle)[] QualityPresets =
    [
        (1080, "1080P 超清", "高码率画质"),
        (720, "720P 高清", "均衡流畅"),
        (480, "480P 标清", "省流加载快")
    ];

    private static readonly (VideoRenderMode Mode, string Label, string ShortLabel, string Subtitle)[] RenderPresets =
    [
        (VideoRenderMode.Auto, "自动渲染", "自动", "系统自动选择方式"),
        (VideoRenderMode.GpuComposition, "GPU 纹理渲染", "GPU 纹理", "硬件纹理合成渲染"),
        (VideoRenderMode.SoftwareBitmap, "软件渲染", "软件渲染", "CPU 位图兼容绘制"),
        (VideoRenderMode.NativeSurface, "NativeSurface", "原生表面", "独立硬件原生表面")
    ];

    private readonly IDramaCatalogService _catalog;
    private readonly IMediaStreamProxyService _mediaProxy;
    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private readonly IToastService _toast;

    private CancellationTokenSource? _resolveCts;
    private CancellationTokenSource? _prefetchCts;
    private Task? _prefetchTask;
    private (string SeriesId, int EpisodeNo, int Quality)? _prefetchKey;
    private (ResolvedStreamInfo Info, Uri Uri)? _prefetched;
    private long _lastHistorySaveTick;
    private Uri? _activeProxyUri;
    private readonly List<Uri> _retiredProxyUris = [];
    private ResolvedStreamInfo? _lastResolvedInfo;
    private bool _suppressSeekEvent;
    private bool _isOpen;
    private bool _isDetailsVisible;
    private bool _isAdvancingAfterEnd;
    private CancellationTokenSource? _autoAdvanceCts;
    private const int AutoAdvancePageSize = 40;
    private const int AutoAdvanceFeedPageLimit = 3;
    private const int AutoAdvanceCatalogPageLimit = 2;
    private const int RecentAutoAdvanceCapacity = 12;
    private readonly List<string> _recentAutoAdvanceSeries = [];
    private DramaItem? _currentDrama;
    private string _heroTag = string.Empty;
    private int _currentEpisodeNo = 1;
    private bool _isResolvingStream;
    private int _streamWidth = 3;
    private int _streamHeight = 4;
    private string _aspectLabel = "准备播放...";
    private string _resolutionLabel = string.Empty;
    private bool _isPlaying = true;
    private double _playbackRate = 1.0;
    private double _volume = 1.0;
    private double _lastNonZeroVolume = 1.0;
    private bool _isMuted;
    private double _audioBoostDb;
    private readonly IDownloadService? _downloads;
    private bool _offlinePlayback;
    private int _selectedQuality = 1080;
    private int _preferredQuality = 1080;
    private VideoRenderMode _renderMode = VideoRenderMode.Auto;
    private bool _isMiniMode;
    private bool _isMiniPinned = true;
    private bool _areControlsVisible = true;
    private double _positionSeconds;
    private double _durationSeconds;
    private bool _hasRealMediaUrl;
    private string? _resolvedPlayUrl;
    private string? _streamErrorMessage;
    private double? _pendingResumeSeconds;
    private string? _activeMenuKey;
    private string? _lastClosedMenuKey;
    private long _lastMenuClosedTickMs;
    private bool _isEffectiveNativeSurface;
    private bool _isDanmakuEnabled = true;
    private CancellationTokenSource? _danmakuCts;
    private string? _activeDanmakuId;
    private readonly HashSet<long> _fetchedDanmakuChunks = [];
    private readonly HashSet<long> _inFlightDanmakuChunks = [];
    private readonly HashSet<string> _seenDanmakuItemIds = new(StringComparer.Ordinal);
    private readonly List<DanmakuItem> _loadedDanmakuItems = [];
    private long? _danmakuEndMs;

    public PlayerViewModel(
        IDramaCatalogService catalog,
        IMediaStreamProxyService mediaProxy,
        IHistoryService history,
        ISettingsService settings,
        IToastService toast,
        IDownloadService? downloads = null)
    {
        _catalog = catalog;
        _mediaProxy = mediaProxy;
        _history = history;
        _settings = settings;
        _toast = toast;
        _downloads = downloads;
        _playbackRate = settings.Current.PlaybackRate;
        _volume = settings.Current.Volume is { } savedVol && double.IsFinite(savedVol)
            ? Math.Clamp(savedVol, 0.0, 1.0)
            : 1.0;
        _lastNonZeroVolume = _volume > 0.01 ? _volume : 0.5;
        _isMuted = settings.Current.IsMuted;
        _audioBoostDb = double.IsFinite(settings.Current.AudioBoostDb)
            ? Math.Clamp(settings.Current.AudioBoostDb, 0.0, 24.0)
            : 0.0;
        _isDanmakuEnabled = settings.Current.DanmakuEnabled ?? true;
        _isMiniPinned = settings.Current.MiniWindowAlwaysOnTop;
        _selectedQuality = settings.Current.DefaultQuality > 0 ? settings.Current.DefaultQuality : 1080;
        _preferredQuality = _selectedQuality;
        _renderMode = settings.Current.RenderMode;

        InitializeMenuOptions();

        CloseCommand = ReactiveCommand.Create(Close);
        TogglePlayPauseCommand = ReactiveCommand.Create(() => { IsPlaying = !IsPlaying; });
        NextEpisodeCommand = ReactiveCommand.CreateFromTask(NextEpisodeAsync);
        SkipToNextRecommendationCommand = ReactiveCommand.CreateFromTask(SkipToNextRecommendationAsync);
        PreviousEpisodeCommand = ReactiveCommand.CreateFromTask(PreviousEpisodeAsync);
        SelectEpisodeCommand = ReactiveCommand.CreateFromTask<EpisodeOptionViewModel>(SelectEpisodeAsync);
        SelectSpeedCommand = ReactiveCommand.Create<PlayerMenuOptionViewModel>(SelectSpeed);
        SelectQualityCommand = ReactiveCommand.CreateFromTask<PlayerMenuOptionViewModel>(SelectQualityAsync);
        SelectRenderModeCommand = ReactiveCommand.Create<PlayerMenuOptionViewModel>(SelectRenderMode);
        ToggleMenuCommand = ReactiveCommand.Create<string>(ToggleMenu);
        CloseMenuCommand = ReactiveCommand.Create(CloseMenu);
        SelectMenuOptionCommand = ReactiveCommand.CreateFromTask<PlayerMenuOptionViewModel>(SelectActiveMenuOptionAsync);
        ToggleMuteCommand = ReactiveCommand.Create(ToggleMute);
        SetAudioBoostCommand = ReactiveCommand.Create<string>(raw =>
        {
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db))
                SetAudioBoost(db);
        });
        ToggleMiniModeCommand = ReactiveCommand.Create(ToggleMiniMode);
        ToggleDanmakuCommand = ReactiveCommand.Create(() => { IsDanmakuEnabled = !IsDanmakuEnabled; });
        ToggleMiniPinCommand = ReactiveCommand.Create(() =>
        {
            IsMiniPinned = !IsMiniPinned;
            MiniPinChanged?.Invoke(IsMiniPinned);
        });
        RetryResolveCommand = ReactiveCommand.CreateFromTask(RetryResolveAsync);
    }

    public event Action? StreamDimensionsChanged;
    public event Action<bool, bool>? MiniModeChanged;
    public event Action<bool>? MiniPinChanged;
    public event Action? HistoryUpdated;
    public event Action<TimeSpan>? SeekRequested;
    public event Action<VideoRenderMode>? RenderModeChanged;
    public event Action<double>? AudioBoostChanged;
    public event Action<bool>? ClosingRequested;
    public event Action? DanmakuTimelineCleared;
    public event Action<IReadOnlyList<DanmakuItem>>? DanmakuItemsLoaded;
    public event Action<double>? DanmakuSeeked;

    public ObservableCollection<EpisodeOptionViewModel> Episodes { get; } = [];
    public ObservableCollection<EpisodeRowViewModel> EpisodeRows { get; } = [];
    public ObservableCollection<PlayerMenuOptionViewModel> SpeedOptions { get; } = [];
    public ObservableCollection<PlayerMenuOptionViewModel> QualityOptions { get; } = [];
    public ObservableCollection<PlayerMenuOptionViewModel> RenderOptions { get; } = [];

    public ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> TogglePlayPauseCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> NextEpisodeCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SkipToNextRecommendationCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> PreviousEpisodeCommand { get; }
    public ReactiveCommand<EpisodeOptionViewModel, RxVoid> SelectEpisodeCommand { get; }
    public ReactiveCommand<PlayerMenuOptionViewModel, RxVoid> SelectSpeedCommand { get; }
    public ReactiveCommand<PlayerMenuOptionViewModel, RxVoid> SelectQualityCommand { get; }
    public ReactiveCommand<PlayerMenuOptionViewModel, RxVoid> SelectRenderModeCommand { get; }
    public ReactiveCommand<string, RxVoid> ToggleMenuCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CloseMenuCommand { get; }
    public ReactiveCommand<PlayerMenuOptionViewModel, RxVoid> SelectMenuOptionCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ToggleMuteCommand { get; }
    public ReactiveCommand<string, RxVoid> SetAudioBoostCommand { get; }

    public bool IsAnyMenuOpen => !string.IsNullOrEmpty(_activeMenuKey);
    public bool IsVolumeMenuOpen => _activeMenuKey == "volume";
    public bool IsStandardOptionMenuOpen => IsAnyMenuOpen && !IsVolumeMenuOpen;
    public string ActiveMenuTitle => _activeMenuKey switch
    {
        "render" => "视频渲染方式",
        "speed" => "播放倍速",
        "quality" => "清晰度",
        "volume" => "播放音量",
        _ => string.Empty
    };
    public ObservableCollection<PlayerMenuOptionViewModel> ActiveMenuOptions => _activeMenuKey switch
    {
        "render" => RenderOptions,
        "speed" => SpeedOptions,
        "quality" => QualityOptions,
        _ => SpeedOptions
    };
    public ReactiveCommand<RxVoid, RxVoid> ToggleMiniModeCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ToggleDanmakuCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ToggleMiniPinCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> RetryResolveCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value) return;
            this.RaiseAndSetIfChanged(ref _isOpen, value);
            RaiseDanmakuSurfaceProperties();
        }
    }

    /// <summary>
    /// TV 播放页右侧详情面板是否展开。桌面端沿用常驻面板，不受该状态影响。
    /// </summary>
    public bool IsDetailsVisible
    {
        get => _isDetailsVisible;
        private set
        {
            if (_isDetailsVisible == value) return;
            this.RaiseAndSetIfChanged(ref _isDetailsVisible, value);
        }
    }

    /// <summary>
    /// TV 播放页开启后，整部剧播完会继续自动寻找下一部视频，保证播放不中断。
    /// 桌面端保持原有“自动连播下一集”设置语义。
    /// </summary>
    public bool KeepPlayingAcrossVideos { get; set; }

    public DramaItem? CurrentDrama
    {
        get => _currentDrama;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentDrama, value);
            this.RaisePropertyChanged(nameof(Title));
            this.RaisePropertyChanged(nameof(Synopsis));
            this.RaisePropertyChanged(nameof(CategoryTagsText));
            this.RaisePropertyChanged(nameof(TotalEpisodesText));
            RaiseEpisodeProperties();
        }
    }

    public string HeroTag
    {
        get => _heroTag;
        private set => this.RaiseAndSetIfChanged(ref _heroTag, value);
    }

    public int CurrentEpisodeNo
    {
        get => _currentEpisodeNo;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentEpisodeNo, value);
            RaiseEpisodeProperties();
        }
    }

    public bool IsResolvingStream
    {
        get => _isResolvingStream;
        private set => this.RaiseAndSetIfChanged(ref _isResolvingStream, value);
    }

    public int StreamWidth
    {
        get => _streamWidth;
        private set => this.RaiseAndSetIfChanged(ref _streamWidth, value);
    }

    public int StreamHeight
    {
        get => _streamHeight;
        private set => this.RaiseAndSetIfChanged(ref _streamHeight, value);
    }

    public string AspectLabel
    {
        get => _aspectLabel;
        private set => this.RaiseAndSetIfChanged(ref _aspectLabel, value);
    }

    public string ResolutionLabel
    {
        get => _resolutionLabel;
        private set => this.RaiseAndSetIfChanged(ref _resolutionLabel, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            this.RaiseAndSetIfChanged(ref _isPlaying, value);
            this.RaisePropertyChanged(nameof(PlayPauseLabel));
        }
    }

    public string PlayPauseLabel => IsPlaying ? "暂停" : "播放";

    public bool IsMiniMode
    {
        get => _isMiniMode;
        private set
        {
            if (_isMiniMode == value) return;
            this.RaiseAndSetIfChanged(ref _isMiniMode, value);
            RaiseControlVisibilityProperties();
        }
    }

    public bool AreControlsVisible
    {
        get => _areControlsVisible;
        private set
        {
            if (_areControlsVisible == value) return;
            this.RaiseAndSetIfChanged(ref _areControlsVisible, value);
            RaiseControlVisibilityProperties();
        }
    }

    public bool ShowMiniTopBar => IsMiniMode && AreControlsVisible;
    public bool ShowTransportControls => AreControlsVisible;

    public bool IsEffectiveNativeSurface
    {
        get => _isEffectiveNativeSurface;
        set
        {
            if (_isEffectiveNativeSurface == value) return;
            this.RaiseAndSetIfChanged(ref _isEffectiveNativeSurface, value);
            RaiseHudSurfaceProperties();
        }
    }

    public bool IsNativeSurfaceMode => _renderMode == VideoRenderMode.NativeSurface || _isEffectiveNativeSurface;
    public bool UseFloatingHud => !IsNativeSurfaceMode && ShowTransportControls;
    public bool UseDockedHud => IsNativeSurfaceMode && ShowTransportControls;
    public bool UseFloatingMiniTopBar => !IsNativeSurfaceMode && ShowMiniTopBar;
    public bool UseDockedMiniTopBar => IsNativeSurfaceMode && ShowMiniTopBar;
    public bool UseFloatingMenu => !IsNativeSurfaceMode && IsAnyMenuOpen;
    public bool UseDockedMenu => IsNativeSurfaceMode && IsAnyMenuOpen;
    public bool UseEmbeddedDanmakuOverlay => IsOpen && HasRealMediaUrl && IsDanmakuEnabled && !IsNativeSurfaceMode;
    public bool UseNativeDanmakuOverlay => IsOpen && HasRealMediaUrl && IsDanmakuEnabled && IsNativeSurfaceMode;

    public bool IsDanmakuEnabled
    {
        get => _isDanmakuEnabled;
        set
        {
            if (_isDanmakuEnabled == value) return;
            this.RaiseAndSetIfChanged(ref _isDanmakuEnabled, value);
            _settings.Update(_settings.Current with { DanmakuEnabled = value });
            this.RaisePropertyChanged(nameof(DanmakuTooltip));
            RaiseDanmakuSurfaceProperties();
            if (value)
            {
                EnsureDanmakuBufferedForSeconds(PositionSeconds);
                DanmakuSeeked?.Invoke(PositionSeconds);
            }
        }
    }

    public int LoadedDanmakuCount => _loadedDanmakuItems.Count;
    public string? ActiveDanmakuId => _activeDanmakuId;
    public string DanmakuTooltip => IsDanmakuEnabled
        ? (LoadedDanmakuCount > 0 ? $"关闭弹幕（已加载 {LoadedDanmakuCount} 条）" : "关闭弹幕")
        : "开启弹幕";

    private void RaiseControlVisibilityProperties()
    {
        this.RaisePropertyChanged(nameof(ShowMiniTopBar));
        this.RaisePropertyChanged(nameof(ShowTransportControls));
        RaiseHudSurfaceProperties();
    }

    private void RaiseHudSurfaceProperties()
    {
        this.RaisePropertyChanged(nameof(IsNativeSurfaceMode));
        this.RaisePropertyChanged(nameof(UseFloatingHud));
        this.RaisePropertyChanged(nameof(UseDockedHud));
        this.RaisePropertyChanged(nameof(UseFloatingMiniTopBar));
        this.RaisePropertyChanged(nameof(UseDockedMiniTopBar));
        this.RaisePropertyChanged(nameof(UseFloatingMenu));
        this.RaisePropertyChanged(nameof(UseDockedMenu));
        RaiseDanmakuSurfaceProperties();
    }

    private void RaiseDanmakuSurfaceProperties()
    {
        this.RaisePropertyChanged(nameof(UseEmbeddedDanmakuOverlay));
        this.RaisePropertyChanged(nameof(UseNativeDanmakuOverlay));
    }

    public bool IsMiniPinned
    {
        get => _isMiniPinned;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isMiniPinned, value);
            this.RaisePropertyChanged(nameof(MiniPinLabel));
        }
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (Math.Abs(_positionSeconds - value) < 0.05) return;
            this.RaiseAndSetIfChanged(ref _positionSeconds, value);
            this.RaisePropertyChanged(nameof(CurrentTimeText));
            this.RaisePropertyChanged(nameof(PositionText));
            this.RaisePropertyChanged(nameof(ProgressPercent));
            if (!_suppressSeekEvent && HasRealMediaUrl)
            {
                var clamped = Math.Clamp(value, 0, Math.Max(0, DurationSeconds));
                SeekRequested?.Invoke(TimeSpan.FromSeconds(clamped));
                NotifyDanmakuSeeked(clamped);
            }
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            this.RaiseAndSetIfChanged(ref _durationSeconds, value);
            this.RaisePropertyChanged(nameof(DurationText));
            this.RaisePropertyChanged(nameof(PositionText));
            this.RaisePropertyChanged(nameof(ProgressPercent));
        }
    }

    public string PositionText => $"{CurrentTimeText} / {DurationText}";

    public double ProgressPercent => DurationSeconds > 0
        ? Math.Clamp(PositionSeconds / DurationSeconds * 100.0, 0.0, 100.0)
        : 0.0;

    public bool HasRealMediaUrl
    {
        get => _hasRealMediaUrl;
        private set
        {
            if (_hasRealMediaUrl == value) return;
            this.RaiseAndSetIfChanged(ref _hasRealMediaUrl, value);
            this.RaisePropertyChanged(nameof(ShowNativeVideo));
            RaiseDanmakuSurfaceProperties();
        }
    }

    public string? ResolvedPlayUrl
    {
        get => _resolvedPlayUrl;
        private set => this.RaiseAndSetIfChanged(ref _resolvedPlayUrl, value);
    }

    public string? StreamErrorMessage
    {
        get => _streamErrorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _streamErrorMessage, value);
            this.RaisePropertyChanged(nameof(HasStreamError));
            this.RaisePropertyChanged(nameof(ShowNativeVideo));
        }
    }

    public bool HasStreamError => !string.IsNullOrWhiteSpace(StreamErrorMessage);

    /// <summary>
    /// True when the native player surface should be visible. Errors must hide it,
    /// because native views are drawn above the Avalonia error overlay.
    /// </summary>
    public bool ShowNativeVideo => HasRealMediaUrl && !HasStreamError;

    public string Title => CurrentDrama?.Title ?? string.Empty;
    public string Synopsis => CurrentDrama?.Intro ?? string.Empty;
    public string CategoryTagsText => CurrentDrama is null ? string.Empty : $"{CurrentDrama.CategoryLabel} · {CurrentDrama.TagsSummary}";
    public string TotalEpisodesText => CurrentDrama is null ? string.Empty : $"选集（共 {CurrentDrama.EpisodeCount} 集）";
    public int TotalEpisodeCount => Math.Max(CurrentDrama?.EpisodeCount ?? 0, Episodes.Select(x => x.No).DefaultIfEmpty(0).Max());
    public string EpisodeBadgeText => $"第 {CurrentEpisodeNo} / {TotalEpisodeCount} 集";
    public string MiniTitleText => $"{Title} · {EpisodeBadgeText}";
    public bool HasPreviousEpisode => Episodes.Any(x => x.No == CurrentEpisodeNo - 1 && x.Episode.Available);
    public bool HasNextEpisode => Episodes.Any(x => x.No == CurrentEpisodeNo + 1 && x.Episode.Available);
    public string QualityPreferenceText => _selectedQuality == _preferredQuality
        ? $"当前 {_selectedQuality}P" : $"优先 {_preferredQuality}P，当前片源提供 {_selectedQuality}P";
    public string SpeedLabel => SpeedPresets.FirstOrDefault(x => Math.Abs(x.Rate - _playbackRate) < 0.01).Label ?? $"{_playbackRate:0.##}x";
    public double PlaybackRate => _playbackRate;
    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 1.0;
            if (clamped > 0.001) _lastNonZeroVolume = clamped;
            if (Math.Abs(_volume - clamped) < 0.001 && (!_isMuted || clamped <= 0.001)) return;
            _volume = clamped;
            if (clamped > 0.001 && _isMuted) _isMuted = false;
            SaveVolumeSettings();
            RaiseVolumeProperties();
        }
    }

    public double VolumePercent
    {
        get => _isMuted ? 0 : Math.Round(_volume * 100);
        set
        {
            var clamped = double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 100.0;
            var normalized = clamped / 100.0;
            if (normalized > 0.001)
            {
                _isMuted = false;
                _lastNonZeroVolume = normalized;
            }
            else
            {
                _isMuted = true;
            }
            _volume = normalized > 0.001 ? normalized : _volume;
            SaveVolumeSettings();
            RaiseVolumeProperties();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            if (!_isMuted && _volume <= 0.001)
                _volume = _lastNonZeroVolume > 0.01 ? _lastNonZeroVolume : 0.5;
            SaveVolumeSettings();
            RaiseVolumeProperties();
        }
    }

    public bool IsEffectivelyMuted => _isMuted || _volume <= 0.001;
    public bool IsVolumeLow => !IsEffectivelyMuted && _volume < 0.5;
    public bool IsVolumeHigh => !IsEffectivelyMuted && _volume >= 0.5;
    public string VolumeLabel => IsEffectivelyMuted ? "静音" : $"{(int)Math.Round(_volume * 100)}%";
    public double AudioBoostDb => _audioBoostDb;
    public bool IsAudioBoostOff => _audioBoostDb < 1.0;
    public bool IsAudioBoost6 => Math.Abs(_audioBoostDb - 6.0) < 0.5;
    public bool IsAudioBoost12 => Math.Abs(_audioBoostDb - 12.0) < 0.5;
    public bool IsAudioBoost18 => _audioBoostDb >= 17.5;
    public string AudioBoostLabel => _audioBoostDb <= 0.1 ? "关闭" : $"+{_audioBoostDb:0} dB";
    public string VolumeTooltip => _audioBoostDb > 0.1
        ? $"应用内音量 {VolumeLabel} · 增强 {AudioBoostLabel}（点击调节 / 右键静音 / 滚轮增减）"
        : $"应用内音量 {VolumeLabel}（点击调节 / 右键静音 / 滚轮增减）";
    public string QualityLabel => $"{_selectedQuality}P";
    public VideoRenderMode RenderMode => _renderMode;
    public string RenderModeLabel => RenderPresets.FirstOrDefault(x => x.Mode == _renderMode).ShortLabel ?? "自动";
    public string MiniPinLabel => IsMiniPinned ? "已置顶" : "未置顶";
    public string CurrentTimeText => TimeSpan.FromSeconds(PositionSeconds).ToString(@"mm\:ss");
    public string DurationText => TimeSpan.FromSeconds(DurationSeconds).ToString(@"mm\:ss");

    public async Task OpenAsync(
        DramaCardViewModel card,
        bool forceFirstEpisode = false,
        bool fromAutoAdvance = false)
    {
        if (!fromAutoAdvance)
        {
            CancelAutoAdvance();
            _recentAutoAdvanceSeries.Clear();
        }
        SavePlaybackProgress();
        CancelPrefetch();
        ResetDanmakuForEpisode(null, 0);
        HasRealMediaUrl = false;
        ReleaseActiveProxySession();
        _resolveCts?.Cancel();
        _resolveCts = new CancellationTokenSource();
        var ct = _resolveCts.Token;
        _preferredQuality = _settings.Current.DefaultQuality > 0 ? _settings.Current.DefaultQuality : 1080;

        HeroTag = card.HeroTag;
        CurrentDrama = card.Drama;
        var saved = forceFirstEpisode
            ? null
            : _history.GetHistory().FirstOrDefault(x => x.Drama.SeriesId == card.Drama.SeriesId);
        CurrentEpisodeNo = forceFirstEpisode
            ? 1
            : Math.Max(1, saved?.LastEpisodeNo ?? card.WatchedEpisode);
        _history.RecordWatch(card.Drama, CurrentEpisodeNo, saved?.ProgressPercent ?? 0,
            saved?.PositionSeconds ?? 0, saved?.DurationSeconds ?? 0);
        // Before stream resolves, keep cover aspect (3:4) so Hero lands cleanly without distortion.
        StreamWidth = 3;
        StreamHeight = 4;
        AspectLabel = "加载画幅...";
        ResolutionLabel = "读取流信息";
        HasRealMediaUrl = false;
        ResolvedPlayUrl = null;
        StreamErrorMessage = null;
        AreControlsVisible = true;
        IsDetailsVisible = false;
        IsPlaying = true;
        IsOpen = true;
        HistoryUpdated?.Invoke();
        StreamDimensionsChanged?.Invoke();

        Episodes.Clear();
        EpisodeRows.Clear();
        RaiseEpisodeProperties();
        try
        {
            _offlinePlayback = card.HeroTag.StartsWith("dl-", StringComparison.Ordinal);
            var eps = _offlinePlayback
                ? _downloads?.GetOfflineEpisodes(card.Drama.SeriesId) ?? []
                : await _catalog.GetEpisodesAsync(card.Drama.SeriesId, ct);
            if (_offlinePlayback && eps.Count == 0)
                throw new GatewayException("本地视频文件不存在，请重新下载");
            if (ct.IsCancellationRequested) return;
            foreach (var ep in eps)
                Episodes.Add(new EpisodeOptionViewModel(ep, ep.No == CurrentEpisodeNo));
            for (var i = 0; i < Episodes.Count; i += 5)
                EpisodeRows.Add(new EpisodeRowViewModel(Episodes.Skip(i).Take(5).ToList()));
            RaiseEpisodeProperties();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (GatewayException ex) when (!ct.IsCancellationRequested)
        {
            StreamErrorMessage = ex.UserMessage;
            _toast.ShowError(ex.UserMessage);
            return;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            StreamErrorMessage = "获取分集列表失败，请稍后重试";
            _toast.ShowError(StreamErrorMessage);
            return;
        }

        if (Episodes.Count > 0 && Episodes.All(x => x.No != CurrentEpisodeNo))
            CurrentEpisodeNo = Episodes[0].No;
        await ResolveAndApplyEpisodeAsync(CurrentEpisodeNo, ct,
            saved?.LastEpisodeNo == CurrentEpisodeNo ? saved.PositionSeconds : null);
    }

    public void Close()
    {
        SavePlaybackProgress();
        CancelAutoAdvance();
        CancelPrefetch();
        _resolveCts?.Cancel();
        ResetDanmakuForEpisode(null, 0);
        CloseMenu();
        IsResolvingStream = false;
        var wasMini = IsMiniMode;
        ClosingRequested?.Invoke(wasMini);
        HasRealMediaUrl = false;
        ResolvedPlayUrl = null;
        ReleaseActiveProxySession();
        ReleaseRetiredProxySessions();
        AreControlsVisible = true;
        IsDetailsVisible = false;
        if (wasMini)
        {
            IsMiniMode = false;
            MiniModeChanged?.Invoke(false, StreamWidth > StreamHeight);
        }

        IsOpen = false;
    }

    private async Task SelectEpisodeAsync(EpisodeOptionViewModel? item)
    {
        if (item is null || CurrentDrama is null) return;
        HideDetails();
        _resolveCts?.Cancel();
        _resolveCts = new CancellationTokenSource();
        await ResolveAndApplyEpisodeAsync(item.No, _resolveCts.Token);
    }

    private async Task NextEpisodeAsync()
    {
        if (CurrentDrama is null || !HasNextEpisode) return;
        HideDetails();
        _resolveCts?.Cancel();
        _resolveCts = new CancellationTokenSource();
        await ResolveAndApplyEpisodeAsync(CurrentEpisodeNo + 1, _resolveCts.Token);
    }

    private async Task PreviousEpisodeAsync()
    {
        if (CurrentDrama is null || !HasPreviousEpisode) return;
        HideDetails();
        _resolveCts?.Cancel();
        _resolveCts = new CancellationTokenSource();
        await ResolveAndApplyEpisodeAsync(CurrentEpisodeNo - 1, _resolveCts.Token);
    }

    /// <summary>
    /// 模拟红果“划走当前剧”：不等待本剧播完，直接从推荐流中切到下一部推荐短剧。
    /// 与自动续播共用候选筛选和分页逻辑，但失败时回到当前剧而不是重播。
    /// </summary>
    private async Task SkipToNextRecommendationAsync()
    {
        if (!IsOpen || CurrentDrama is null || _isAdvancingAfterEnd) return;

        _isAdvancingAfterEnd = true;
        var cts = new CancellationTokenSource();
        _autoAdvanceCts?.Cancel();
        _autoAdvanceCts = cts;
        try
        {
            SavePlaybackProgress();
            HideDetails();
            var current = CurrentDrama;
            var candidates = await FindNextDramaCandidatesAsync(current, cts.Token);
            if (cts.IsCancellationRequested || !IsOpen) return;

            if (candidates.Count == 0)
            {
                _toast.ShowError("暂无更多推荐视频");
                return;
            }

            if (await TryOpenNextDramaAsync(candidates, cts.Token))
                return;

            if (IsOpen && !cts.IsCancellationRequested)
            {
                // 全部候选加载失败时回到当前剧，避免停在错误页。
                await OpenAsync(
                    new DramaCardViewModel(current, HeroTag, 0),
                    fromAutoAdvance: true);
                _toast.ShowError("推荐视频加载失败，请稍后重试");
            }
        }
        catch (OperationCanceledException)
        {
            // 用户关闭播放器或重新选剧时取消切换。
        }
        catch (Exception)
        {
            if (IsOpen && !cts.IsCancellationRequested)
                _toast.ShowError("切换推荐视频失败，请稍后重试");
        }
        finally
        {
            if (ReferenceEquals(_autoAdvanceCts, cts))
                _autoAdvanceCts = null;
            cts.Dispose();
            _isAdvancingAfterEnd = false;
        }
    }

    private void RaiseEpisodeProperties()
    {
        this.RaisePropertyChanged(nameof(EpisodeBadgeText));
        this.RaisePropertyChanged(nameof(MiniTitleText));
        this.RaisePropertyChanged(nameof(HasPreviousEpisode));
        this.RaisePropertyChanged(nameof(HasNextEpisode));
    }

    private async Task RetryResolveAsync()
    {
        if (CurrentDrama is null) return;
        _resolveCts?.Cancel();
        _resolveCts = new CancellationTokenSource();
        if (Episodes.Count == 0)
        {
            await OpenAsync(new DramaCardViewModel(CurrentDrama, HeroTag, CurrentEpisodeNo));
            return;
        }
        await ResolveAndApplyEpisodeAsync(CurrentEpisodeNo, _resolveCts.Token);
    }

    private async Task ResolveAndApplyEpisodeAsync(int episodeNo, CancellationToken ct, double? resumeAtSeconds = null)
    {
        if (CurrentDrama is null) return;
        SavePlaybackProgress();
        var drama = CurrentDrama;
        var quality = _preferredQuality;
        var usePrefetch = _prefetchKey == (drama.SeriesId, episodeNo, quality);
        if (!usePrefetch) CancelPrefetch();
        StreamErrorMessage = null;

        IsResolvingStream = true;
        try
        {
            if (usePrefetch && _prefetchTask is { } preparation)
                await preparation.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            var prepared = usePrefetch ? _prefetched : null;
            _prefetched = null;
            CancelPrefetch();
            if (prepared is { } expired && !IsStreamFresh(expired.Info))
            {
                _mediaProxy.ReleaseSession(expired.Uri);
                prepared = null;
            }
            var info = prepared?.Info ?? (_offlinePlayback
                ? _downloads?.GetOfflineStream(drama.SeriesId, episodeNo) ?? throw new GatewayException("本集尚未下载或本地文件已丢失")
                : await _catalog.ResolveEpisodeAsync(
                drama.SeriesId,
                episodeNo,
                quality,
                ct));

            if (ct.IsCancellationRequested) return;
            if (prepared is null) info = SelectPreferredVariant(info, quality);

            await ActivateResolvedStreamAsync(info, ct, prepared?.Uri, resumeAtSeconds);
            ct.ThrowIfCancellationRequested();

            var previous = resumeAtSeconds is > 0
                ? _history.GetHistory().FirstOrDefault(x => x.Drama.SeriesId == drama.SeriesId && x.LastEpisodeNo == episodeNo)
                : null;
            _history.RecordWatch(drama, episodeNo, previous?.ProgressPercent ?? 0,
                resumeAtSeconds ?? 0, previous?.DurationSeconds ?? 0);
            _lastHistorySaveTick = Environment.TickCount64;
            HistoryUpdated?.Invoke();
            StartNextEpisodePrefetch();
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer selection or close.
        }
        catch (GatewayException ex) when (!ct.IsCancellationRequested)
        {
            AspectLabel = "加载失败";
            ResolutionLabel = "未就绪";
            StreamErrorMessage = ex.UserMessage;
            _toast.ShowError(ex.UserMessage);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            AspectLabel = "加载失败";
            ResolutionLabel = "未就绪";
            StreamErrorMessage = "拉取视频流失败，请检查网络连接或重试";
            _toast.ShowError(StreamErrorMessage);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Ignore failures from an obsolete request.
        }
        catch (Exception)
        {
            AspectLabel = "加载失败";
            ResolutionLabel = "未就绪";
            StreamErrorMessage = "视频流加载或解密失败，请稍后重试";
            _toast.ShowError(StreamErrorMessage);
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsResolvingStream = false;
        }
    }

    private async Task ActivateResolvedStreamAsync(ResolvedStreamInfo info, CancellationToken ct, Uri? preparedUri = null, double? resumeAtSeconds = null)
    {
        Uri? playableUri = preparedUri;
        if (!string.IsNullOrWhiteSpace(info.PlayUrl))
        {
            playableUri ??= Uri.TryCreate(info.PlayUrl, UriKind.Absolute, out var local) && local.IsFile
                ? local : await _mediaProxy.PreparePlayableUriAsync(info, ct);
            if (ct.IsCancellationRequested)
            {
                _mediaProxy.ReleaseSession(playableUri);
                return;
            }
        }

        SavePlaybackProgress();
        _lastResolvedInfo = info;
        if (info.Quality > 0) _selectedQuality = info.Quality;
        this.RaisePropertyChanged(nameof(QualityLabel));
        this.RaisePropertyChanged(nameof(QualityPreferenceText));
        RebuildQualityOptions(info);
        CurrentEpisodeNo = info.EpisodeNo;
        foreach (var ep in Episodes) ep.IsCurrent = ep.No == CurrentEpisodeNo;
        _pendingResumeSeconds = resumeAtSeconds is > 0.5 ? resumeAtSeconds : null;
        _suppressSeekEvent = true;
        PositionSeconds = 0;
        DurationSeconds = 0;
        _suppressSeekEvent = false;
        StreamWidth = Math.Max(1, info.Width);
        StreamHeight = Math.Max(1, info.Height);
        AspectLabel = info.IsLandscape ? "横屏自适应" : "竖屏自适应";
        ResolutionLabel = $"{StreamWidth}×{StreamHeight}";
        if (_activeProxyUri is { } previous) _retiredProxyUris.Add(previous);
        _activeProxyUri = playableUri;
        ResetDanmakuForEpisode(ResolveDanmakuId(info), resumeAtSeconds ?? 0);
        HasRealMediaUrl = playableUri is not null;
        ResolvedPlayUrl = playableUri?.ToString();
        IsPlaying = HasRealMediaUrl;
        StreamDimensionsChanged?.Invoke();
    }

    private void ToggleMenu(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || _activeMenuKey == key)
        {
            CloseMenu();
            return;
        }

        // Prevent light-dismiss click on the active toggle button from immediately re-opening the menu
        if (_lastClosedMenuKey == key && Environment.TickCount64 - _lastMenuClosedTickMs < 250)
        {
            _lastClosedMenuKey = null;
            return;
        }

        _lastClosedMenuKey = null;
        _activeMenuKey = key;
        RaiseMenuProperties();
    }

    public void CloseMenu()
    {
        if (_activeMenuKey is null) return;
        _lastClosedMenuKey = _activeMenuKey;
        _lastMenuClosedTickMs = Environment.TickCount64;
        _activeMenuKey = null;
        RaiseMenuProperties();
    }

    private async Task SelectActiveMenuOptionAsync(PlayerMenuOptionViewModel? option)
    {
        if (option is null) return;
        var key = _activeMenuKey;
        CloseMenu();

        switch (key)
        {
            case "render":
                SelectRenderMode(option);
                break;
            case "speed":
                SelectSpeed(option);
                break;
            case "quality":
                await SelectQualityAsync(option);
                break;
        }
    }

    private void RaiseMenuProperties()
    {
        this.RaisePropertyChanged(nameof(IsAnyMenuOpen));
        this.RaisePropertyChanged(nameof(IsVolumeMenuOpen));
        this.RaisePropertyChanged(nameof(IsStandardOptionMenuOpen));
        this.RaisePropertyChanged(nameof(ActiveMenuTitle));
        this.RaisePropertyChanged(nameof(ActiveMenuOptions));
        this.RaisePropertyChanged(nameof(UseFloatingMenu));
        this.RaisePropertyChanged(nameof(UseDockedMenu));
    }

    public void ToggleMute()
    {
        if (IsEffectivelyMuted)
        {
            _isMuted = false;
            if (_volume <= 0.001)
                _volume = _lastNonZeroVolume > 0.01 ? _lastNonZeroVolume : 0.5;
        }
        else
        {
            _isMuted = true;
        }
        SaveVolumeSettings();
        RaiseVolumeProperties();
    }

    public void StepVolume(double delta)
    {
        if (!double.IsFinite(delta) || Math.Abs(delta) < 0.0001) return;
        var baseVol = IsEffectivelyMuted && delta > 0 ? 0.0 : _volume;
        var next = Math.Clamp(Math.Round((baseVol + delta) * 100) / 100.0, 0.0, 1.0);
        if (next > 0.001)
        {
            _isMuted = false;
            _lastNonZeroVolume = next;
            _volume = next;
        }
        else
        {
            _isMuted = true;
        }
        SaveVolumeSettings();
        RaiseVolumeProperties();
    }

    private void SaveVolumeSettings() =>
        _settings.Update(_settings.Current with { Volume = _volume, IsMuted = _isMuted, AudioBoostDb = _audioBoostDb });

    public void SetAudioBoost(double db)
    {
        var clamped = double.IsFinite(db) ? Math.Clamp(db, 0.0, 24.0) : 0.0;
        if (Math.Abs(_audioBoostDb - clamped) < 0.05) return;
        _audioBoostDb = clamped;
        SaveVolumeSettings();
        this.RaisePropertyChanged(nameof(AudioBoostDb));
        this.RaisePropertyChanged(nameof(IsAudioBoostOff));
        this.RaisePropertyChanged(nameof(IsAudioBoost6));
        this.RaisePropertyChanged(nameof(IsAudioBoost12));
        this.RaisePropertyChanged(nameof(IsAudioBoost18));
        this.RaisePropertyChanged(nameof(AudioBoostLabel));
        this.RaisePropertyChanged(nameof(VolumeTooltip));
        AudioBoostChanged?.Invoke(_audioBoostDb);
    }

    private void RaiseVolumeProperties()
    {
        this.RaisePropertyChanged(nameof(Volume));
        this.RaisePropertyChanged(nameof(VolumePercent));
        this.RaisePropertyChanged(nameof(IsMuted));
        this.RaisePropertyChanged(nameof(IsEffectivelyMuted));
        this.RaisePropertyChanged(nameof(IsVolumeLow));
        this.RaisePropertyChanged(nameof(IsVolumeHigh));
        this.RaisePropertyChanged(nameof(VolumeLabel));
        this.RaisePropertyChanged(nameof(VolumeTooltip));
    }

    private void SelectSpeed(PlayerMenuOptionViewModel? option)
    {
        if (option is null) return;
        ApplySpeedRate(option.DoubleValue);
    }

    public void StepSpeed(int delta)
    {
        if (delta == 0) return;
        var currentIndex = 0;
        var bestDiff = double.MaxValue;
        for (var i = 0; i < SpeedPresets.Length; i++)
        {
            var diff = Math.Abs(SpeedPresets[i].Rate - _playbackRate);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                currentIndex = i;
            }
        }

        // SpeedPresets is sorted descending (3.0x at 0 -> 0.75x at end)
        var targetIndex = Math.Clamp(currentIndex - Math.Sign(delta), 0, SpeedPresets.Length - 1);
        ApplySpeedRate(SpeedPresets[targetIndex].Rate);
    }

    public void ResetSpeed() => ApplySpeedRate(1.0);

    public void SeekBy(double deltaSeconds)
    {
        if (!HasRealMediaUrl) return;
        PositionSeconds = Math.Clamp(PositionSeconds + deltaSeconds, 0, Math.Max(0, DurationSeconds));
    }

    private void ApplySpeedRate(double rate)
    {
        _playbackRate = rate;
        _settings.Update(_settings.Current with { PlaybackRate = _playbackRate });
        foreach (var item in SpeedOptions)
            item.IsSelected = Math.Abs(item.DoubleValue - _playbackRate) < 0.01;
        this.RaisePropertyChanged(nameof(SpeedLabel));
        this.RaisePropertyChanged(nameof(PlaybackRate));
    }

    private async Task SelectQualityAsync(PlayerMenuOptionViewModel? option)
    {
        if (option is null || option.IntValue <= 0) return;
        SavePlaybackProgress();
        CancelPrefetch();
        var targetQuality = option.IntValue;
        var resumePos = HasRealMediaUrl ? PositionSeconds : 0;

        _preferredQuality = targetQuality;
        this.RaisePropertyChanged(nameof(QualityPreferenceText));

        if (CurrentDrama is null) return;

        _resolveCts?.Cancel();
        var cts = _resolveCts = new CancellationTokenSource();

        // Reuse already-resolved variant directly if still valid before expiry
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var matchedVariant = _lastResolvedInfo?.SeriesId == CurrentDrama.SeriesId
            && _lastResolvedInfo.EpisodeNo == CurrentEpisodeNo
            && _lastResolvedInfo.ExpiresAtUnixMs > nowMs + 15_000
                ? _lastResolvedInfo.Variants?.FirstOrDefault(v => v.Quality == targetQuality)
                : null;

        if (matchedVariant is not null)
        {
            IsResolvingStream = true;
            try
            {
                var swapped = _lastResolvedInfo! with
                {
                    PlayUrl = matchedVariant.Url,
                    DecryptionKey = matchedVariant.DecryptionKey,
                    Encryption = string.IsNullOrWhiteSpace(matchedVariant.DecryptionKey) ? "none" : _lastResolvedInfo.Encryption,
                    Quality = matchedVariant.Quality,
                    Width = matchedVariant.Width > 0 ? matchedVariant.Width : _lastResolvedInfo.Width,
                    Height = matchedVariant.Height > 0 ? matchedVariant.Height : _lastResolvedInfo.Height,
                    Codec = string.IsNullOrWhiteSpace(matchedVariant.Codec) ? _lastResolvedInfo.Codec : matchedVariant.Codec
                };
                await ActivateResolvedStreamAsync(swapped, cts.Token, resumeAtSeconds: resumePos);
                if (!cts.IsCancellationRequested) StartNextEpisodePrefetch();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Fall back to fresh resolve if cached variant failed
                if (cts.IsCancellationRequested) return;
            }
            finally
            {
                if (!cts.IsCancellationRequested) IsResolvingStream = false;
            }
        }

        await ResolveAndApplyEpisodeAsync(CurrentEpisodeNo, cts.Token, resumePos);
    }

    private void SelectRenderMode(PlayerMenuOptionViewModel? option)
    {
        if (option is null) return;
        SetRenderMode((VideoRenderMode)option.IntValue);
    }

    public void SetRenderMode(VideoRenderMode mode)
    {
        if (_renderMode == mode) return;

        _renderMode = mode;
        foreach (var item in RenderOptions)
            item.IsSelected = item.IntValue == (int)mode;
        this.RaisePropertyChanged(nameof(RenderMode));
        this.RaisePropertyChanged(nameof(RenderModeLabel));
        RaiseHudSurfaceProperties();

        _settings.Update(_settings.Current with { RenderMode = mode });
        RenderModeChanged?.Invoke(mode);
    }

    public void SetControlsVisible(bool visible)
    {
        if (!visible) CloseMenu();
        AreControlsVisible = visible;
    }

    public void ShowDetails()
    {
        if (!IsOpen) return;
        CloseMenu();
        IsDetailsVisible = true;
    }

    public void HideDetails()
    {
        IsDetailsVisible = false;
    }

    public void ToggleDetails()
    {
        if (IsDetailsVisible) HideDetails();
        else ShowDetails();
    }

    public void ToggleControlsVisibility() => SetControlsVisible(!AreControlsVisible);

    public double? ConsumePendingResumeSeconds()
    {
        var val = _pendingResumeSeconds;
        _pendingResumeSeconds = null;
        return val;
    }

    private void InitializeMenuOptions()
    {
        SpeedOptions.Clear();
        foreach (var (rate, label, subtitle) in SpeedPresets)
        {
            SpeedOptions.Add(new PlayerMenuOptionViewModel(label, subtitle, doubleValue: rate)
            {
                IsSelected = Math.Abs(rate - _playbackRate) < 0.01
            });
        }

        RenderOptions.Clear();
        foreach (var (mode, label, _, subtitle) in RenderPresets)
        {
            RenderOptions.Add(new PlayerMenuOptionViewModel(label, subtitle, intValue: (int)mode)
            {
                IsSelected = mode == _renderMode
            });
        }

        RebuildQualityOptions(null);
    }

    private void RebuildQualityOptions(ResolvedStreamInfo? info)
    {
        QualityOptions.Clear();
        if (_offlinePlayback && info is not null)
        {
            QualityOptions.Add(new PlayerMenuOptionViewModel($"{info.Quality}P", "本地离线视频", intValue: info.Quality) { IsSelected = true });
            return;
        }
        if (info?.Variants is { Count: > 0 } variants)
        {
            var seen = new HashSet<int>();
            foreach (var v in variants.OrderByDescending(x => x.Quality))
            {
                if (v.Quality <= 0 || !seen.Add(v.Quality)) continue;
                var presetSub = QualityPresets.FirstOrDefault(p => p.Quality == v.Quality).Subtitle ?? "在线片源";
                var codecTag = string.IsNullOrWhiteSpace(v.Codec) ? string.Empty : $" · {v.Codec.ToUpperInvariant()}";
                var resTag = v.Width > 0 && v.Height > 0 ? $"{v.Width}×{v.Height}" : presetSub;
                QualityOptions.Add(new PlayerMenuOptionViewModel($"{v.Quality}P", $"{resTag}{codecTag}", intValue: v.Quality)
                {
                    IsSelected = v.Quality == _selectedQuality
                });
            }
        }

        if (QualityOptions.Count == 0)
        {
            foreach (var (q, label, sub) in QualityPresets)
            {
                QualityOptions.Add(new PlayerMenuOptionViewModel(label, sub, intValue: q)
                {
                    IsSelected = q == _selectedQuality
                });
            }
        }
    }

    public void SyncFromPlayer(TimeSpan position, TimeSpan? duration)
    {
        if (!IsOpen || !HasRealMediaUrl) return;
        _suppressSeekEvent = true;
        try
        {
            if (duration is { TotalSeconds: > 0 } dur)
                DurationSeconds = dur.TotalSeconds;

            var posSec = Math.Max(0, position.TotalSeconds);
            if (Math.Abs(_positionSeconds - posSec) >= 0.15)
            {
                _positionSeconds = posSec;
                this.RaisePropertyChanged(nameof(PositionSeconds));
                this.RaisePropertyChanged(nameof(CurrentTimeText));
                this.RaisePropertyChanged(nameof(PositionText));
                this.RaisePropertyChanged(nameof(ProgressPercent));
            }
        }
        finally
        {
            _suppressSeekEvent = false;
        }
        if (_pendingResumeSeconds is null && Environment.TickCount64 - _lastHistorySaveTick >= 5_000)
            SavePlaybackProgress();
        if (IsDanmakuEnabled && _activeDanmakuId is not null)
            EnsureDanmakuBufferedForSeconds(position.TotalSeconds);
    }

    public void NotifyDanmakuSeeked(double positionSeconds)
    {
        EnsureDanmakuBufferedForSeconds(positionSeconds);
        DanmakuSeeked?.Invoke(Math.Max(0, positionSeconds));
    }

    public IReadOnlyList<DanmakuItem> GetLoadedDanmakuSnapshot() => [.. _loadedDanmakuItems];

    private string? ResolveDanmakuId(ResolvedStreamInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.DanmakuId))
            return info.DanmakuId.Trim();

        var ep = Episodes.FirstOrDefault(x => x.No == info.EpisodeNo);
        var vid = ep?.Episode.Vid;
        if (!string.IsNullOrWhiteSpace(vid)
            && IsAsciiDigits(info.SeriesId)
            && IsAsciiDigits(vid)
            && vid.Length is >= 10 and <= 30)
        {
            return $"hongguo:{info.SeriesId}:{vid}";
        }

        return null;
    }

    private static bool IsAsciiDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var ch in value)
        {
            if (ch is < '0' or > '9') return false;
        }
        return true;
    }

    private void ResetDanmakuForEpisode(string? danmakuId, double initialSeconds)
    {
        _danmakuCts?.Cancel();
        _danmakuCts?.Dispose();
        _danmakuCts = danmakuId is null ? null : new CancellationTokenSource();
        _activeDanmakuId = danmakuId;
        _fetchedDanmakuChunks.Clear();
        _inFlightDanmakuChunks.Clear();
        _seenDanmakuItemIds.Clear();
        _loadedDanmakuItems.Clear();
        _danmakuEndMs = null;
        this.RaisePropertyChanged(nameof(LoadedDanmakuCount));
        this.RaisePropertyChanged(nameof(ActiveDanmakuId));
        this.RaisePropertyChanged(nameof(DanmakuTooltip));
        DanmakuTimelineCleared?.Invoke();

        if (danmakuId is not null && IsDanmakuEnabled)
            EnsureDanmakuBufferedForSeconds(initialSeconds);
    }

    private void EnsureDanmakuBufferedForSeconds(double seconds)
    {
        if (!IsDanmakuEnabled || _activeDanmakuId is not { } danmakuId || _danmakuCts is not { } cts)
            return;

        var posMs = Math.Max(0L, (long)(seconds * 1000.0));
        var chunkStartMs = (posMs / 30_000L) * 30_000L;
        ScheduleDanmakuChunkFetch(danmakuId, chunkStartMs, cts);

        // Prefetch the next 30s segment once playback enters the last 10s of the active chunk.
        if (posMs - chunkStartMs >= 20_000L)
        {
            var nextChunkMs = chunkStartMs + 30_000L;
            var knownDurMs = (long)(DurationSeconds * 1000.0);
            if (knownDurMs <= 0 || nextChunkMs < knownDurMs)
                ScheduleDanmakuChunkFetch(danmakuId, nextChunkMs, cts);
        }
    }

    private void ScheduleDanmakuChunkFetch(string danmakuId, long chunkStartMs, CancellationTokenSource cts)
    {
        if (_danmakuEndMs is { } endMs && chunkStartMs >= endMs)
            return;
        if (_fetchedDanmakuChunks.Contains(chunkStartMs) || !_inFlightDanmakuChunks.Add(chunkStartMs))
            return;

        var knownDurMs = DurationSeconds > 1.0 ? (long)Math.Ceiling(DurationSeconds * 1000.0) : 300_000L;
        var durationMs = Math.Clamp(Math.Max(knownDurMs, chunkStartMs + 30_000L), 30_000L, 86_400_000L);
        _ = FetchDanmakuChunkAsync(danmakuId, chunkStartMs, durationMs, cts.Token);
    }

    private async Task FetchDanmakuChunkAsync(string danmakuId, long startMs, long durationMs, CancellationToken ct)
    {
        try
        {
            var chunk = await _catalog.GetDanmakuAsync(danmakuId, startMs, durationMs, ct);
            if (ct.IsCancellationRequested || !string.Equals(_activeDanmakuId, danmakuId, StringComparison.Ordinal))
                return;

            _fetchedDanmakuChunks.Add(startMs);
            if (chunk.NextMs <= chunk.StartMs)
                _danmakuEndMs = chunk.StartMs + 30_000L;

            if (chunk.Items.Count > 0)
            {
                var added = new List<DanmakuItem>(chunk.Items.Count);
                foreach (var item in chunk.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.Text)) continue;
                    var dedupeKey = string.IsNullOrWhiteSpace(item.Id)
                        ? $"{item.TimeMs}:{item.Text}"
                        : item.Id;
                    if (_seenDanmakuItemIds.Add(dedupeKey))
                    {
                        _loadedDanmakuItems.Add(item);
                        added.Add(item);
                    }
                }

                if (added.Count > 0)
                {
                    _loadedDanmakuItems.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
                    this.RaisePropertyChanged(nameof(LoadedDanmakuCount));
                    this.RaisePropertyChanged(nameof(DanmakuTooltip));
                    DanmakuItemsLoaded?.Invoke(added);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by episode switch or close.
        }
        catch
        {
            // Danmaku failures must stay silent so they never disrupt video playback.
        }
        finally
        {
            _inFlightDanmakuChunks.Remove(startMs);
        }
    }

    private void SavePlaybackProgress()
    {
        if (!IsOpen || !HasRealMediaUrl || CurrentDrama is null || _pendingResumeSeconds is not null) return;
        var percent = DurationSeconds > 0 ? (int)Math.Clamp(PositionSeconds / DurationSeconds * 100, 0, 100) : 0;
        _history.RecordWatch(CurrentDrama, CurrentEpisodeNo, percent, PositionSeconds, DurationSeconds);
        _lastHistorySaveTick = Environment.TickCount64;
        HistoryUpdated?.Invoke();
    }

    private static bool IsStreamFresh(ResolvedStreamInfo info) =>
        info.ExpiresAtUnixMs <= 0 || info.ExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000;

    private void StartNextEpisodePrefetch()
    {
        CancelPrefetch();
        if (!IsOpen || CurrentDrama is null || !HasRealMediaUrl || _offlinePlayback) return;
        var next = CurrentEpisodeNo + 1;
        if (!Episodes.Any(x => x.No == next && x.Episode.Available)) return;
        _prefetchKey = (CurrentDrama.SeriesId, next, _preferredQuality);
        _prefetchCts = new CancellationTokenSource();
        _prefetchTask = PrefetchEpisodeAsync(CurrentDrama.SeriesId, next, _preferredQuality, _prefetchCts.Token);
    }

    private async Task PrefetchEpisodeAsync(string seriesId, int episodeNo, int quality, CancellationToken ct)
    {
        Uri? uri = null;
        try
        {
            var info = await _catalog.ResolveEpisodeAsync(seriesId, episodeNo, quality, ct);
            info = SelectPreferredVariant(info, quality);
            ct.ThrowIfCancellationRequested();
            if (!IsStreamFresh(info) || string.IsNullOrWhiteSpace(info.PlayUrl)) return;
            uri = await _mediaProxy.PreparePlayableUriAsync(info, ct);
            ct.ThrowIfCancellationRequested();
            _prefetched = (info, uri);
            uri = null; // Ownership transfers to the next playback or CancelPrefetch.
        }
        catch (Exception)
        {
            // Speculative loading must never interrupt current playback; switching retries normally.
        }
        finally
        {
            if (uri is not null) _mediaProxy.ReleaseSession(uri);
        }
    }

    private void CancelPrefetch()
    {
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        _prefetchCts = null;
        _prefetchKey = null;
        _prefetchTask = null;
        if (_prefetched is { } prepared) _mediaProxy.ReleaseSession(prepared.Uri);
        _prefetched = null;
    }

    public void ReportPlaybackError(string? message)
    {
        if (!IsOpen || !HasRealMediaUrl) return;
        IsPlaying = false;
        StreamErrorMessage = string.IsNullOrWhiteSpace(message) ? "播放器解码或网络异常，请重试" : $"播放异常：{message}";
    }

    public void NotifyPlaybackEnded()
    {
        if (!IsOpen || !HasRealMediaUrl || IsResolvingStream || _isAdvancingAfterEnd) return;
        if (!KeepPlayingAcrossVideos)
        {
            if (_settings.Current.AutoPlayNextEpisode && CurrentDrama is not null && HasNextEpisode)
                _ = NextEpisodeAsync();
            else
                IsPlaying = false;
            return;
        }

        SavePlaybackProgress();
        _ = AdvanceAfterPlaybackEndedAsync();
    }

    /// <summary>
    /// 当前视频播放结束后持续续播：优先下一集；整部剧播完则从推荐流中挑选
    /// 下一部未看/未看完的剧集。会继续翻推荐页，避免只看第一页就重复旧内容；
    /// 推荐接口不可用时补充普通目录，最后兜底重播当前剧。
    /// </summary>
    private async Task AdvanceAfterPlaybackEndedAsync()
    {
        if (!IsOpen || _isAdvancingAfterEnd) return;
        _isAdvancingAfterEnd = true;
        var cts = new CancellationTokenSource();
        _autoAdvanceCts?.Cancel();
        _autoAdvanceCts = cts;
        try
        {
            var current = CurrentDrama;
            if (current is null) return;

            if (HasNextEpisode)
            {
                await NextEpisodeAsync();
                return;
            }

            var candidates = await FindNextDramaCandidatesAsync(current, cts.Token);
            if (cts.IsCancellationRequested || !IsOpen) return;

            if (candidates.Count > 0 && await TryOpenNextDramaAsync(candidates, cts.Token))
                return;

            // 推荐流和目录都没有可播放结果（或全部解析失败）时也不要黑屏：
            // 回到当前剧从头播放，保持 TV 端持续播放不中断。
            if (!cts.IsCancellationRequested && IsOpen)
            {
                await OpenAsync(
                    new DramaCardViewModel(current, HeroTag, 1),
                    forceFirstEpisode: true,
                    fromAutoAdvance: true);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户关闭播放器或手动切剧时取消自动续播。
        }
        catch (Exception)
        {
            if (IsOpen && !cts.IsCancellationRequested)
                _toast.ShowError("自动续播失败，请返回后重新选择视频");
        }
        finally
        {
            if (ReferenceEquals(_autoAdvanceCts, cts))
                _autoAdvanceCts = null;
            cts.Dispose();
            _isAdvancingAfterEnd = false;
        }
    }

    private async Task<bool> TryOpenNextDramaAsync(
        IReadOnlyList<DramaItem> candidates,
        CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsOpen) return false;

            // 自动切剧从第一集开始，和红果推荐卡片的“看新剧”行为一致。
            await OpenAsync(
                new DramaCardViewModel(candidate, $"auto-next-{candidate.SeriesId}", 0),
                forceFirstEpisode: true,
                fromAutoAdvance: true);

            if (!HasStreamError && !string.IsNullOrWhiteSpace(ResolvedPlayUrl))
            {
                RememberAutoAdvanceSeries(candidate.SeriesId);
                return true;
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<DramaItem>> FindNextDramaCandidatesAsync(
        DramaItem current,
        CancellationToken ct)
    {
        var history = _history.GetHistory()
            .Where(x => x.Drama is not null)
            .GroupBy(x => x.Drama.SeriesId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var recent = new HashSet<string>(_recentAutoAdvanceSeries, StringComparer.Ordinal);
        var candidates = new List<DramaItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 新网关使用 /feed 推荐流；旧网关在服务层自动回退到 catalog?sort=recommend。
        // 第一页可能全是已看完内容，继续翻页直到找到未看过的推荐。
        await CollectFeedCandidatesAsync(candidates, seen, current, history, recent, ct);
        ct.ThrowIfCancellationRequested();

        // 推荐流没有“未看/未看完”的候选时，再补充普通目录，
        // 避免只因为第一页都是历史剧就重播已看完的剧。
        if (!candidates.Any(x => IsPreferredNextDrama(x, history, recent)))
            await CollectCatalogCandidatesAsync(candidates, seen, current, ct);
        ct.ThrowIfCancellationRequested();

        return OrderNextDramaCandidates(candidates, current, history, recent);
    }

    private async Task CollectFeedCandidatesAsync(
        List<DramaItem> candidates,
        HashSet<string> seen,
        DramaItem current,
        IReadOnlyDictionary<string, HistoryRecord> history,
        IReadOnlySet<string> recent,
        CancellationToken ct)
    {
        var scope = string.Empty;
        for (var page = 1; page <= AutoAdvanceFeedPageLimit; page++)
        {
            FeedPageResult result;
            try
            {
                result = await _catalog.GetFeedPageAsync(scope, page, AutoAdvancePageSize, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 推荐接口不可用时继续尝试普通目录。
                break;
            }

            ct.ThrowIfCancellationRequested();
            AddCandidates(candidates, seen, result.Items);

            // 已经找到完全没看过的推荐，按推荐顺序直接使用，无需继续翻页。
            if (candidates.Any(x => IsNeverWatched(x, current, history, recent)))
                break;
            if (!result.HasMore || string.IsNullOrWhiteSpace(result.Scope))
                break;
            scope = result.Scope;
        }
    }

    private async Task CollectCatalogCandidatesAsync(
        List<DramaItem> candidates,
        HashSet<string> seen,
        DramaItem current,
        CancellationToken ct)
    {
        for (var page = 1; page <= AutoAdvanceCatalogPageLimit; page++)
        {
            CatalogPageResult result;
            try
            {
                result = await _catalog.GetCatalogPageAsync(DramaCategory.All, page, 100, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            AddCandidates(candidates, seen, result.Items);
            if (!result.HasMore) break;
        }
    }

    private static void AddCandidates(
        List<DramaItem> candidates,
        HashSet<string> seen,
        IEnumerable<DramaItem> items)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.SeriesId) || !seen.Add(item.SeriesId))
                continue;
            candidates.Add(item);
        }
    }

    private static IReadOnlyList<DramaItem> OrderNextDramaCandidates(
        IReadOnlyList<DramaItem> candidates,
        DramaItem current,
        IReadOnlyDictionary<string, HistoryRecord> history,
        IReadOnlySet<string> recent)
    {
        var all = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x.SeriesId))
            .Where(x => !string.Equals(x.SeriesId, current.SeriesId, StringComparison.Ordinal))
            .DistinctBy(x => x.SeriesId, StringComparer.Ordinal)
            .ToList();
        if (all.Count == 0) return [];

        // 最近自动切过的剧先跳过，避免连续按“下一个推荐”时在两部剧之间来回跳。
        var pool = all.Where(x => !recent.Contains(x.SeriesId)).ToList();
        if (pool.Count == 0)
            pool = all; // 只有这几部可播时允许重复，保证播放不中断。

        var neverWatched = pool
            .Where(x => !history.ContainsKey(x.SeriesId))
            .ToList();
        var unfinished = pool
            .Where(x => history.TryGetValue(x.SeriesId, out var record) && !IsFullyWatched(x, record))
            .ToList();
        var watched = pool
            .Where(x => history.TryGetValue(x.SeriesId, out var record) && IsFullyWatched(x, record))
            .ToList();

        return neverWatched.Concat(unfinished).Concat(watched).ToList();
    }

    private static bool IsNeverWatched(
        DramaItem drama,
        DramaItem current,
        IReadOnlyDictionary<string, HistoryRecord> history,
        IReadOnlySet<string> recent)
    {
        return !string.Equals(drama.SeriesId, current.SeriesId, StringComparison.Ordinal)
               && !recent.Contains(drama.SeriesId)
               && !history.ContainsKey(drama.SeriesId);
    }

    private static bool IsPreferredNextDrama(
        DramaItem drama,
        IReadOnlyDictionary<string, HistoryRecord> history,
        IReadOnlySet<string> recent)
    {
        if (recent.Contains(drama.SeriesId)) return false;
        return !history.TryGetValue(drama.SeriesId, out var record)
               || !IsFullyWatched(drama, record);
    }

    private void RememberAutoAdvanceSeries(string seriesId)
    {
        if (string.IsNullOrWhiteSpace(seriesId)) return;
        _recentAutoAdvanceSeries.Remove(seriesId);
        _recentAutoAdvanceSeries.Add(seriesId);
        if (_recentAutoAdvanceSeries.Count > RecentAutoAdvanceCapacity)
            _recentAutoAdvanceSeries.RemoveAt(0);
    }

    private static bool IsFullyWatched(DramaItem drama, HistoryRecord record)
    {
        var total = Math.Max(drama.EpisodeCount, record.Drama.EpisodeCount);
        return total > 0 && record.LastEpisodeNo >= total && record.ProgressPercent >= 95;
    }

    private void CancelAutoAdvance()
    {
        // 任务自身负责释放 CTS；这里只负责打断在途的推荐请求。
        _autoAdvanceCts?.Cancel();
    }

    private void ReleaseActiveProxySession()
    {
        var prev = Interlocked.Exchange(ref _activeProxyUri, null);
        if (prev is not null)
            _mediaProxy.ReleaseSession(prev);
    }

    public void ReleaseRetiredProxySessions()
    {
        foreach (var uri in _retiredProxyUris) _mediaProxy.ReleaseSession(uri);
        _retiredProxyUris.Clear();
    }

    private static ResolvedStreamInfo SelectPreferredVariant(ResolvedStreamInfo info, int quality)
    {
        var variant = info.Variants?.FirstOrDefault(x => x.Quality == quality && !string.IsNullOrWhiteSpace(x.Url));
        if (info.Quality == quality || variant is null) return info;
        return info with
        {
            PlayUrl = variant.Url, Quality = variant.Quality, DecryptionKey = variant.DecryptionKey,
            Encryption = string.IsNullOrWhiteSpace(variant.DecryptionKey) ? "none" : "aes-128-cbc",
            Width = variant.Width > 0 ? variant.Width : info.Width,
            Height = variant.Height > 0 ? variant.Height : info.Height,
            Codec = string.IsNullOrWhiteSpace(variant.Codec) ? info.Codec : variant.Codec
        };
    }

    private void ToggleMiniMode()
    {
        CloseMenu();
        AreControlsVisible = true;
        IsMiniMode = !IsMiniMode;
        MiniModeChanged?.Invoke(IsMiniMode, StreamWidth > StreamHeight);
    }
}
