using MioReel.Core.Models;

namespace MioReel.Core.Abstractions;

public interface IDramaCatalogService
{
    bool IsLastRequestLive { get; }
    Task<CatalogPageResult> GetCatalogPageAsync(DramaCategory category, int page = 1, int limit = 40, CancellationToken ct = default);
    Task<IReadOnlyList<DramaItem>> GetCatalogAsync(DramaCategory category, CancellationToken ct = default);
    Task<IReadOnlyList<DramaItem>> SearchAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<EpisodeItem>> GetEpisodesAsync(string seriesId, CancellationToken ct = default);
    Task<ResolvedStreamInfo> ResolveEpisodeAsync(string seriesId, int episodeNo, int quality = 1080, CancellationToken ct = default);
    Task<DanmakuChunk> GetDanmakuAsync(string danmakuId, long startMs = 0, long durationMs = 300_000, CancellationToken ct = default) =>
        Task.FromResult(new DanmakuChunk(danmakuId, startMs, startMs + 30_000, []));
    Task<GatewayHealthResult> TestConnectionAsync(string gatewayBaseUrl, CancellationToken ct = default);
}

public interface IHistoryService
{
    IReadOnlyList<HistoryRecord> GetHistory();
    void RecordWatch(DramaItem drama, int episodeNo, int progressPercent, double positionSeconds = 0, double durationSeconds = 0);
    void Remove(string seriesId);
    void Clear();
}

public interface IDownloadService
{
    IReadOnlyList<DownloadTaskItem> GetDownloads();
    DownloadTaskItem Enqueue(DramaItem drama, int episodeCount);
    void TogglePause(string taskId);
    void Remove(string taskId);
    IReadOnlyList<EpisodeItem> GetOfflineEpisodes(string seriesId) => [];
    ResolvedStreamInfo? GetOfflineStream(string seriesId, int episodeNo) => null;
}

public interface ISettingsService
{
    AppSettings Current { get; }
    void Update(AppSettings settings);
}

/// <summary>
/// 客户端本地视频流解密与 HTTP Range 代理服务：将加密流或需自定义请求头的 CDN 直链转换为 FrameFlux 可直接播放的本地 HTTP 地址。
/// </summary>
public interface IMediaStreamProxyService : IDisposable
{
    Task<Uri> PreparePlayableUriAsync(ResolvedStreamInfo stream, CancellationToken ct = default);
    void ReleaseSession(Uri? proxyUri);
}

/// <summary>
/// 跨平台系统全局热键监听服务（Windows RegisterHotKey / Linux X11 XGrabKey）。
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    bool IsSupported { get; }
    string BackendName { get; }
    string? PlatformNotice { get; }
    IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationStatus> Statuses { get; }

    event Action<HotkeyAction>? HotkeyTriggered;
    event Action? StatusChanged;

    void ApplyBindings(bool enabled, IReadOnlyDictionary<HotkeyAction, HotkeyBinding> bindings);
}
