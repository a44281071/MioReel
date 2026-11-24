using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Core.Serialization;

namespace MioReel.Infrastructure.Services;

/// <summary>
/// 纯真实请求对接 media-gateway 的视频源服务，所有网络、超时、协议与业务错误均转换为可读的 GatewayException。
/// </summary>
public sealed class GatewayDramaCatalogService : IDramaCatalogService
{
    private const string CatalogRecommendScope = "__catalog_recommend__";
    private const string CatalogHotBoardPrefix = "catalog-hot:";

    private static readonly (string Primary, string Secondary)[] AccentPalettes =
    [
        ("#BE123C", "#F59E0B"),
        ("#7C2D12", "#D97706"),
        ("#312E81", "#EC4899"),
        ("#DB2777", "#FB7185"),
        ("#6D28D9", "#F43F5E"),
        ("#065F46", "#10B981"),
        ("#1D4ED8", "#06B6D4"),
        ("#0F172A", "#38BDF8")
    ];

    // Older gateway deployments expose recommendation/ranking via catalog sort
    // instead of the dedicated /feed and /rankings routes.
    private static readonly RankingBoardItem[] CatalogFallbackRankingBoards =
    [
        new($"{CatalogHotBoardPrefix}all", "全站热播", "全站短剧热播榜", "hongguo"),
        new($"{CatalogHotBoardPrefix}real-drama", "真人剧热播", "真人短剧热播榜", "hongguo"),
        new($"{CatalogHotBoardPrefix}comic-drama", "漫剧热播", "漫剧热播榜", "hongguo"),
        new($"{CatalogHotBoardPrefix}ai-drama", "AI 剧热播", "AI 短剧热播榜", "hongguo")
    ];

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    public GatewayDramaCatalogService(HttpClient http, ISettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public bool IsLastRequestLive { get; private set; }

    public async Task<CatalogPageResult> GetCatalogPageAsync(
        DramaCategory category,
        int page = 1,
        int limit = 40,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/catalog?category={ToCategorySlug(category)}&page={Math.Max(1, page)}&limit={Math.Clamp(limit, 1, 100)}";
        var data = await SendEnvelopeAsync(
            HttpMethod.Get,
            path,
            MioReelJsonContext.Default.GatewayEnvelopeGatewayCatalogData,
            content: null,
            ct);

        var mapped = data.List
            .Select(item => MapCatalogItem(item, category))
            .ToList();
        var hasMore = mapped.Count > 0 && data.PageCount > page;
        IsLastRequestLive = true;
        return new CatalogPageResult(mapped, hasMore, IsLiveGateway: true);
    }

    public async Task<IReadOnlyList<DramaItem>> GetCatalogAsync(DramaCategory category, CancellationToken ct = default)
    {
        var page = await GetCatalogPageAsync(category, 1, 100, ct);
        return page.Items;
    }

    public async Task<FeedPageResult> GetFeedPageAsync(
        string scope,
        int page = 1,
        int limit = 40,
        CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safeLimit = Math.Clamp(limit, 1, 100);
        var trimmedScope = scope?.Trim() ?? string.Empty;
        if (string.Equals(trimmedScope, CatalogRecommendScope, StringComparison.Ordinal))
            return await GetCatalogRecommendFallbackAsync(safePage, safeLimit, ct);

        if (safePage > 1 && string.IsNullOrEmpty(trimmedScope))
        {
            throw new GatewayException(
                "推荐流游标已失效，请刷新推荐后重试",
                failureKind: GatewayFailureKind.GatewayError);
        }

        var path = $"/api/v1/feed?page={safePage}&limit={safeLimit}";
        if (!string.IsNullOrEmpty(trimmedScope))
            path += $"&scope={Uri.EscapeDataString(trimmedScope)}";

        try
        {
            var data = await SendEnvelopeAsync(
                HttpMethod.Get,
                path,
                MioReelJsonContext.Default.GatewayEnvelopeGatewayFeedData,
                content: null,
                ct);

            var mapped = (data.List ?? [])
                .Select(item => MapCatalogItem(item, DramaCategory.All))
                .ToList();
            IsLastRequestLive = true;
            return new FeedPageResult(
                mapped,
                HasMore: data.HasMore && mapped.Count > 0,
                Scope: string.IsNullOrWhiteSpace(data.Scope) ? trimmedScope : data.Scope,
                IsLiveGateway: true);
        }
        catch (GatewayException ex) when (ex.HttpStatus == (int)HttpStatusCode.NotFound)
        {
            return await GetCatalogRecommendFallbackAsync(safePage, safeLimit, ct);
        }
    }

    public async Task<IReadOnlyList<RankingBoardItem>> GetRankingBoardsAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await SendEnvelopeAsync(
                HttpMethod.Get,
                "/api/v1/rankings/boards",
                MioReelJsonContext.Default.GatewayEnvelopeGatewayRankingBoardsData,
                content: null,
                ct);

            IsLastRequestLive = true;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var boards = (data.Items ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
                .Where(item => seen.Add(item.Id.Trim()))
                .Select(item => new RankingBoardItem(
                    Id: item.Id.Trim(),
                    Name: item.Name.Trim(),
                    Description: item.Description ?? string.Empty,
                    Source: string.IsNullOrWhiteSpace(item.Source) ? "hongguo" : item.Source.Trim()))
                .ToList();
            return boards.Count > 0 ? boards : CatalogFallbackRankingBoards;
        }
        catch (GatewayException ex) when (ex.HttpStatus == (int)HttpStatusCode.NotFound)
        {
            // Older gateways expose the same content through catalog?sort=hot.
            IsLastRequestLive = true;
            return CatalogFallbackRankingBoards;
        }
    }

    public async Task<CatalogPageResult> GetRankingPageAsync(
        string boardId,
        int page = 1,
        int limit = 40,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(boardId))
        {
            throw new GatewayException(
                "榜单标识无效，请刷新榜单列表后重试",
                failureKind: GatewayFailureKind.GatewayError);
        }

        var trimmedBoardId = boardId.Trim();
        var safePage = Math.Max(1, page);
        var safeLimit = Math.Clamp(limit, 1, 100);
        if (trimmedBoardId.StartsWith(CatalogHotBoardPrefix, StringComparison.Ordinal))
        {
            var categorySlug = trimmedBoardId[CatalogHotBoardPrefix.Length..];
            return await GetSortedCatalogPageAsync(
                categorySlug,
                "hot",
                safePage,
                safeLimit,
                CategoryFromSlug(categorySlug),
                ct);
        }

        var path = $"/api/v1/rankings?board={Uri.EscapeDataString(trimmedBoardId)}&page={safePage}&limit={safeLimit}";
        var data = await SendEnvelopeAsync(
            HttpMethod.Get,
            path,
            MioReelJsonContext.Default.GatewayEnvelopeGatewayCatalogData,
            content: null,
            ct);

        var mapped = (data.List ?? [])
            .Select(item => MapCatalogItem(item, DramaCategory.All))
            .ToList();
        var hasMore = mapped.Count > 0 && data.PageCount > safePage;
        IsLastRequestLive = true;
        return new CatalogPageResult(mapped, hasMore, IsLiveGateway: true);
    }

    public async Task<IReadOnlyList<DramaItem>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var collected = new List<DramaItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var p = 1; p <= 3; p++)
            {
                var page = await GetCatalogPageAsync(DramaCategory.All, p, 100, ct);
                foreach (var item in page.Items)
                {
                    if (seen.Add(item.SeriesId))
                        collected.Add(item);
                }
                if (!page.HasMore || collected.Count >= 40)
                    break;
            }
            return collected;
        }

        var trimmedQuery = Uri.EscapeDataString(query.Trim());
        var first = await SendEnvelopeAsync(
            HttpMethod.Get,
            $"/api/v1/search?q={trimmedQuery}&page=1",
            MioReelJsonContext.Default.GatewayEnvelopeGatewaySearchData,
            content: null,
            ct);

        var hits = new List<DramaItem>();
        var hitIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in first.List)
        {
            if (hitIds.Add(item.SeriesId))
                hits.Add(MapCatalogItem(item, DramaCategory.All));
        }

        if (first.HasMore && hits.Count < 30)
        {
            var second = await SendEnvelopeAsync(
                HttpMethod.Get,
                $"/api/v1/search?q={trimmedQuery}&page=2",
                MioReelJsonContext.Default.GatewayEnvelopeGatewaySearchData,
                content: null,
                ct);
            foreach (var item in second.List)
            {
                if (hitIds.Add(item.SeriesId))
                    hits.Add(MapCatalogItem(item, DramaCategory.All));
            }
        }

        IsLastRequestLive = true;
        return hits;
    }

    public async Task<IReadOnlyList<EpisodeItem>> GetEpisodesAsync(string seriesId, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(
            new GatewaySeriesRequest(seriesId),
            MioReelJsonContext.Default.GatewaySeriesRequest);

        var data = await SendEnvelopeAsync(
            HttpMethod.Post,
            "/api/v1/series",
            MioReelJsonContext.Default.GatewayEnvelopeGatewaySeriesData,
            content,
            ct);

        IsLastRequestLive = true;
        return data.Episodes
            .Select(ep => new EpisodeItem(ep.No, ep.Vid, ep.Title, ep.Available))
            .ToList();
    }

    public async Task<ResolvedStreamInfo> ResolveEpisodeAsync(
        string seriesId,
        int episodeNo,
        int quality = 1080,
        CancellationToken ct = default)
    {
        using var content = JsonContent.Create(
            new GatewayResolveRequest(SeriesId: seriesId, EpisodeNo: episodeNo, Quality: quality),
            MioReelJsonContext.Default.GatewayResolveRequest);

        var data = await SendEnvelopeAsync(
            HttpMethod.Post,
            "/api/v1/resolve",
            MioReelJsonContext.Default.GatewayEnvelopeGatewayResolveData,
            content,
            ct);

        IsLastRequestLive = true;
        var variants = data.Variants?
            .Where(v => !string.IsNullOrWhiteSpace(v.Url))
            .Select(v => new StreamVariantInfo(v.Url, v.Quality, v.Width, v.Height, v.Codec, v.DecryptionKey))
            .ToList();

        return new ResolvedStreamInfo(
            SeriesId: data.SeriesId,
            EpisodeNo: data.EpisodeNo,
            PlayUrl: data.PlayUrl,
            PlayHeaders: data.PlayHeaders ?? new Dictionary<string, string>(),
            DecryptionKey: data.DecryptionKey,
            Encryption: data.Encryption,
            Quality: data.Quality,
            Width: data.Width,
            Height: data.Height,
            Codec: data.Codec,
            ExpiresAtUnixMs: data.ExpiresAt,
            Variants: variants,
            DanmakuId: string.IsNullOrWhiteSpace(data.DanmakuId) ? null : data.DanmakuId.Trim());
    }

    public async Task<DanmakuChunk> GetDanmakuAsync(
        string danmakuId,
        long startMs = 0,
        long durationMs = 300_000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(danmakuId))
            return new DanmakuChunk(string.Empty, 0, 30_000, []);

        var trimmedId = danmakuId.Trim();
        var safeStartMs = Math.Max(0L, startMs);
        var safeDurationMs = Math.Clamp(Math.Max(durationMs, safeStartMs + 30_000L), 30_000L, 86_400_000L);
        var path = $"/api/v1/danmaku?danmaku_id={Uri.EscapeDataString(trimmedId)}&start_ms={safeStartMs}&duration_ms={safeDurationMs}";

        var data = await SendEnvelopeAsync(
            HttpMethod.Get,
            path,
            MioReelJsonContext.Default.GatewayEnvelopeGatewayDanmakuData,
            content: null,
            ct);

        IsLastRequestLive = true;
        var items = (data.Items ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => new DanmakuItem(
                Id: item.Id ?? string.Empty,
                TimeMs: Math.Max(0L, item.TimeMs),
                Text: item.Text.Trim(),
                ColorHex: string.IsNullOrWhiteSpace(item.Color) ? "#FFFFFF" : item.Color.Trim()))
            .OrderBy(item => item.TimeMs)
            .ToList();

        return new DanmakuChunk(
            EpisodeId: string.IsNullOrWhiteSpace(data.EpisodeId) ? trimmedId : data.EpisodeId,
            StartMs: data.StartMs,
            NextMs: data.NextMs > data.StartMs ? data.NextMs : data.StartMs + 30_000L,
            Items: items);
    }

    public async Task<GatewayHealthResult> TestConnectionAsync(string gatewayBaseUrl, CancellationToken ct = default)
    {
        if (!TryBuildUri(gatewayBaseUrl, "/api/v1/health", out var healthUri)
            || !TryBuildUri(gatewayBaseUrl, "/api/v1/catalog?category=all&page=1&limit=1", out var catalogUri))
        {
            return new GatewayHealthResult(
                false,
                "服务地址格式无效，请填写有效的 http:// 或 https:// 地址",
                GatewayFailureKind.InvalidConfig);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var healthReq = CreateRequest(HttpMethod.Get, healthUri);
            using var healthResp = await _http.SendAsync(healthReq, cts.Token);
            if (!healthResp.IsSuccessStatusCode)
            {
                return new GatewayHealthResult(
                    false,
                    $"服务返回 HTTP {(int)healthResp.StatusCode}，请检查网关状态",
                    ClassifyHttpFailure(healthResp.StatusCode));
            }

            await using var healthStream = await healthResp.Content.ReadAsStreamAsync(cts.Token);
            var health = await JsonSerializer.DeserializeAsync(
                healthStream,
                MioReelJsonContext.Default.GatewayHealthResponse,
                cts.Token);

            if (health is null)
                return new GatewayHealthResult(false, "健康检查响应为空", GatewayFailureKind.InvalidResponse);

            if (health.Status is not ("ok" or "degraded"))
                return new GatewayHealthResult(false, $"服务当前不可用（status: {health.Status}）", GatewayFailureKind.GatewayError);

            using var catalogReq = CreateRequest(HttpMethod.Get, catalogUri);
            using var catalogResp = await _http.SendAsync(catalogReq, cts.Token);
            if (!catalogResp.IsSuccessStatusCode)
            {
                return catalogResp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new GatewayHealthResult(
                        false,
                        "服务已连通，但鉴权失败（401），请填写令牌",
                        GatewayFailureKind.Unauthorized),
                    HttpStatusCode.Forbidden => new GatewayHealthResult(
                        false,
                        "服务已连通，但拒绝访问（403），请检查令牌",
                        GatewayFailureKind.Forbidden),
                    _ => new GatewayHealthResult(
                        false,
                        $"健康检查通过，但片库接口返回 HTTP {(int)catalogResp.StatusCode}",
                        GatewayFailureKind.GatewayError)
                };
            }

            await using var catalogStream = await catalogResp.Content.ReadAsStreamAsync(cts.Token);
            var catalogEnvelope = await JsonSerializer.DeserializeAsync(
                catalogStream,
                MioReelJsonContext.Default.GatewayEnvelopeGatewayCatalogData,
                cts.Token);

            if (catalogEnvelope is null)
                return new GatewayHealthResult(false, "片库验证响应为空", GatewayFailureKind.InvalidResponse);

            if (catalogEnvelope.Code != 0)
            {
                return new GatewayHealthResult(
                    false,
                    FormatGatewayError(catalogEnvelope.Code, catalogEnvelope.Message),
                    ClassifyGatewayCode(catalogEnvelope.Code));
            }

            return new GatewayHealthResult(
                true,
                health.Status == "degraded" ? "服务已连接并校验鉴权（当前处于降级状态）" : "视频源服务连接正常，鉴权已通过");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException)
        {
            return new GatewayHealthResult(false, "连接视频源服务超时，请确认服务是否已启动", GatewayFailureKind.Connection);
        }
        catch (HttpRequestException)
        {
            return new GatewayHealthResult(false, "无法连接视频源服务，请确认服务端已启动且地址正确", GatewayFailureKind.Connection);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new GatewayHealthResult(false, "服务响应格式无法解析，请确认地址是否为视频源网关", GatewayFailureKind.InvalidResponse);
        }
    }

    private async Task<FeedPageResult> GetCatalogRecommendFallbackAsync(
        int page,
        int limit,
        CancellationToken ct)
    {
        // Older gateways map recommendation pages through catalog?sort=recommend.
        var result = await GetSortedCatalogPageAsync(
            "all",
            "recommend",
            page,
            limit,
            DramaCategory.All,
            ct);
        return new FeedPageResult(result.Items, result.HasMore, CatalogRecommendScope, result.IsLiveGateway);
    }

    private async Task<CatalogPageResult> GetSortedCatalogPageAsync(
        string categorySlug,
        string sort,
        int page,
        int limit,
        DramaCategory category,
        CancellationToken ct)
    {
        var safePage = Math.Max(1, page);
        var safeLimit = Math.Clamp(limit, 1, 100);
        var path = $"/api/v1/catalog?category={Uri.EscapeDataString(categorySlug)}&sort={Uri.EscapeDataString(sort)}&page={safePage}&limit={safeLimit}";
        var data = await SendEnvelopeAsync(
            HttpMethod.Get,
            path,
            MioReelJsonContext.Default.GatewayEnvelopeGatewayCatalogData,
            content: null,
            ct);

        var mapped = (data.List ?? [])
            .Select(item => MapCatalogItem(item, category))
            .ToList();
        var hasMore = mapped.Count > 0 && data.PageCount > safePage;
        IsLastRequestLive = true;
        return new CatalogPageResult(mapped, hasMore, IsLiveGateway: true);
    }

    private static DramaCategory CategoryFromSlug(string slug) => slug switch
    {
        "real-drama" => DramaCategory.RealDrama,
        "comic-drama" => DramaCategory.ComicDrama,
        "ai-drama" => DramaCategory.AiDrama,
        _ => DramaCategory.All
    };

    private async Task<T> SendEnvelopeAsync<T>(
        HttpMethod method,
        string relativePath,
        JsonTypeInfo<GatewayEnvelope<T>> jsonType,
        HttpContent? content,
        CancellationToken ct)
    {
        if (!TryBuildUri(_settings.Current.GatewayBaseUrl, relativePath, out var uri))
        {
            IsLastRequestLive = false;
            throw new GatewayException(
                "视频源服务地址无效，请在设置中配置正确的 http/https 地址",
                failureKind: GatewayFailureKind.InvalidConfig);
        }

        try
        {
            using var req = CreateRequest(method, uri);
            if (content is not null)
                req.Content = content;

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                IsLastRequestLive = false;
                var msg = resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "视频源鉴权失败（401），请在设置中填写有效的访问令牌",
                    HttpStatusCode.Forbidden => "视频源拒绝访问（403），请检查令牌权限或 IP 白名单",
                    HttpStatusCode.NotFound => "视频源服务未提供该接口（HTTP 404），请确认网关版本",
                    _ => $"视频源服务返回异常（HTTP {(int)resp.StatusCode}）"
                };
                throw new GatewayException(
                    msg,
                    failureKind: ClassifyHttpFailure(resp.StatusCode),
                    httpStatus: (int)resp.StatusCode);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var envelope = await JsonSerializer.DeserializeAsync(stream, jsonType, ct);
            if (envelope is null)
            {
                IsLastRequestLive = false;
                throw new GatewayException("视频源返回内容为空", failureKind: GatewayFailureKind.InvalidResponse);
            }

            if (envelope.Code != 0 || envelope.Data is null)
            {
                IsLastRequestLive = false;
                throw new GatewayException(
                    FormatGatewayError(envelope.Code, envelope.Message),
                    envelope.Code,
                    envelope.RequestId,
                    failureKind: ClassifyGatewayCode(envelope.Code));
            }

            return envelope.Data;
        }
        catch (GatewayException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException)
        {
            IsLastRequestLive = false;
            throw new GatewayException("请求视频源服务超时，请确认服务已启动或网络畅通", inner: ex, failureKind: GatewayFailureKind.Connection);
        }
        catch (HttpRequestException ex)
        {
            IsLastRequestLive = false;
            throw new GatewayException("无法连接视频源服务，请确认服务端已启动", inner: ex, failureKind: GatewayFailureKind.Connection);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            IsLastRequestLive = false;
            throw new GatewayException("视频源返回数据格式异常，无法解析", inner: ex, failureKind: GatewayFailureKind.InvalidResponse);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var req = new HttpRequestMessage(method, uri);
        var token = NormalizeToken(_settings.Current.GatewayToken);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static string NormalizeToken(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? trimmed[7..].Trim()
            : trimmed;
    }

    private static GatewayFailureKind ClassifyHttpFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => GatewayFailureKind.Unauthorized,
        HttpStatusCode.Forbidden => GatewayFailureKind.Forbidden,
        _ => GatewayFailureKind.GatewayError
    };

    private static GatewayFailureKind ClassifyGatewayCode(int code) => code switch
    {
        40101 => GatewayFailureKind.Unauthorized,
        40301 => GatewayFailureKind.Forbidden,
        _ => GatewayFailureKind.GatewayError
    };

    private static bool TryBuildUri(string baseUrl, string relativePath, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root))
            return false;
        if (root.Scheme is not ("http" or "https"))
            return false;
        uri = new Uri(root, relativePath.TrimStart('/'));
        return true;
    }

    private static string FormatGatewayError(int code, string message)
    {
        var prefix = code switch
        {
            40001 => "请求参数非法",
            40101 => "访问令牌无效或缺失",
            40301 => "无权限访问该视频源",
            40401 => "剧集或分集不存在",
            40901 => "上游拒绝了本次请求",
            42201 => "当前剧集暂无兼容片源",
            42901 => "请求过于频繁，请稍后重试",
            50201 or 50301 => "上游视频源暂时不可用",
            50401 => "视频源解析超时",
            _ => $"视频源错误 ({code})"
        };
        return string.IsNullOrWhiteSpace(message) ? prefix : $"{prefix}：{message}";
    }

    private static string ToCategorySlug(DramaCategory category) => category switch
    {
        DramaCategory.RealDrama => "real-drama",
        DramaCategory.ComicDrama => "comic-drama",
        DramaCategory.AiDrama => "ai-drama",
        _ => "all"
    };

    private static DramaItem MapCatalogItem(GatewayCatalogItem item, DramaCategory category)
    {
        var paletteIndex = Math.Abs(StringComparer.Ordinal.GetHashCode(item.SeriesId)) % AccentPalettes.Length;
        var (primary, secondary) = AccentPalettes[paletteIndex];
        var tags = item.Tags is { Count: > 0 } ? item.Tags : [];
        var remark = string.IsNullOrWhiteSpace(item.Remark) && item.EpisodeCount > 0
            ? $"全{item.EpisodeCount}集"
            : item.Remark;

        return new DramaItem(
            SeriesId: item.SeriesId,
            Title: item.Title,
            CoverUrl: item.Cover,
            EpisodeCount: Math.Max(1, item.EpisodeCount),
            Remark: remark,
            Intro: string.IsNullOrWhiteSpace(item.Intro) ? "暂无剧情简介。" : item.Intro,
            Tags: tags,
            Year: item.Year,
            Category: category,
            AccentPrimaryHex: primary,
            AccentSecondaryHex: secondary);
    }
}
