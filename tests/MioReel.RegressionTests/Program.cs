using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ReactiveUI.Primitives;
using Aviora.Presentation.Toasts;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Desktop.ViewModels;
using MioReel.Infrastructure.Services;
using MioReel.Infrastructure.Services.MediaProxy;

var root = Path.Combine(Path.GetTempPath(), "MioReel-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var drama = new DramaItem("series", "测试短剧", "", 3, "", "", ["测试"], "2026", DramaCategory.RealDrama, "#000000", "#FFFFFF");
    var path = Path.Combine(root, "history.json");
    var history = new FileHistoryService(path);
    history.RecordWatch(drama, 2, 35, 42, 120);
    history = new FileHistoryService(path);
    Check(history.GetHistory().Single() is { LastEpisodeNo: 2, PositionSeconds: 42, ProgressPercent: 35 }, "历史重启后保留集数、进度和位置");
    history.RecordWatch(drama, 2, 50, 60, 120);
    Check(new FileHistoryService(path).GetHistory().Count == 1, "同一短剧更新记录而非重复添加");
    history.Remove(drama.SeriesId);
    Check(new FileHistoryService(path).GetHistory().Count == 0, "删除记录持久化");
    history.RecordWatch(drama, 1, 0);
    history.Clear();
    Check(new FileHistoryService(path).GetHistory().Count == 0, "清空记录持久化");
    File.WriteAllText(path, "{broken");
    Check(new FileHistoryService(path).GetHistory().Count == 0, "损坏历史文件允许正常启动");

    history = new FileHistoryService(path);
    history.RecordWatch(drama, 1, 25, 30, 120);
    var catalog = new FakeCatalog();
    var proxy = new FakeProxy();
    var player = new PlayerViewModel(catalog, proxy, history, new InMemorySettingsService(), DispatchProxy.Create<IToastService, NoopProxy>());
    await player.OpenAsync(new DramaCardViewModel(drama, "test"));
    Check(player.ConsumePendingResumeSeconds() == 30, "从真实历史位置断点续播");
    Check(catalog.Resolved.SequenceEqual([1, 2]) && proxy.Prepared.Count == 2, "当前集加载后提前解析并准备下一集媒体");
    player.SyncFromPlayer(TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(120));
    var nextUri = proxy.Prepared[1];
    var sourceChanges = new List<string?>();
    player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlayerViewModel.ResolvedPlayUrl)) sourceChanges.Add(player.ResolvedPlayUrl); };
    await player.NextEpisodeCommand.Execute().ToTask();
    Check(player.ResolvedPlayUrl == nextUri.ToString() && catalog.Resolved.Count(x => x == 2) == 1, "切集复用预加载会话且不重复解析");
    Check(catalog.Resolved.SequenceEqual([1, 2, 3]), "切集后继续预加载下一集");
    Check(sourceChanges.SequenceEqual([nextUri.ToString()]), "切集直接替换播放源，中途不清空播放器");
    Check(!proxy.Released.Contains(proxy.Prepared[0]), "下一集开始播放前保留旧代理会话");
    player.ReleaseRetiredProxySessions();
    Check(proxy.Released.Contains(proxy.Prepared[0]), "下一集开始播放后释放旧会话");
    player.SyncFromPlayer(TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(120));
    player.Close();
    var saved = new FileHistoryService(path).GetHistory().Single();
    Check(saved is { LastEpisodeNo: 2, PositionSeconds: 70, ProgressPercent: 58 }, "关闭时保存实际播放进度");
    Check(proxy.Released.Count == proxy.Prepared.Count, "关闭时释放当前集及预加载会话");

    var qualitySettings = new InMemorySettingsService();
    qualitySettings.Update(qualitySettings.Current with { DefaultQuality = 720 });
    catalog = new FakeCatalog
    {
        Transform = info => info with
        {
            Quality = 720,
            Variants = info.EpisodeNo == 2
                ? [new StreamVariantInfo("https://example.invalid/1080.mp4", 1080, 1080, 1920, "h264", "")]
                : null
        }
    };
    proxy = new FakeProxy();
    player = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), qualitySettings, DispatchProxy.Create<IToastService, NoopProxy>());
    qualitySettings.Update(qualitySettings.Current with { DefaultQuality = 1080 });
    await player.OpenAsync(new DramaCardViewModel(drama, "preferred"));
    Check(catalog.RequestedQualities.SequenceEqual([1080, 1080]), "播放读取最新默认画质，单集降级不会覆盖后续1080P偏好");
    Check(player.QualityLabel == "720P" && player.QualityPreferenceText.Contains("1080P"), "缺少1080P时显示真实画质并保留优先级说明");
    Check(proxy.Streams[1].Quality == 1080 && proxy.Streams[1].PlayUrl.EndsWith("1080.mp4"), "预加载使用可用的1080P变体");
    Check(!player.HasPreviousEpisode && player.HasNextEpisode && player.EpisodeBadgeText == "第 1 / 3 集", "首集导航边界及标题总集数正确");
    await player.PreviousEpisodeCommand.Execute().ToTask();
    Check(player.CurrentEpisodeNo == 1, "首集不会跳转到第零集");
    await player.NextEpisodeCommand.Execute().ToTask();
    Check(player.QualityLabel == "1080P" && player.HasPreviousEpisode, "下一集恢复优先1080P且允许返回上一集");
    await player.PreviousEpisodeCommand.Execute().ToTask();
    Check(player.CurrentEpisodeNo == 1 && player.MiniTitleText.Contains("1 / 3"), "上一集跳转及小窗标题总集数正确");
    player.Close();

    catalog = new FakeCatalog { FailEpisode = 2 };
    proxy = new FakeProxy();
    player = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), new InMemorySettingsService(), DispatchProxy.Create<IToastService, NoopProxy>());
    await player.OpenAsync(new DramaCardViewModel(drama, "failure"));
    Check(player.HasRealMediaUrl && !player.HasStreamError, "预加载失败不影响当前播放");
    catalog.FailEpisode = 0;
    await player.NextEpisodeCommand.Execute().ToTask();
    Check(player.CurrentEpisodeNo == 2 && player.HasRealMediaUrl && catalog.Resolved.Count(x => x == 2) == 2, "预加载失败后切集正常重试");
    await player.NextEpisodeCommand.Execute().ToTask();
    Check(catalog.Resolved.All(x => x <= 3), "最后一集不预加载越界集数");
    player.Close();

    catalog = new FakeCatalog();
    proxy = new FakeProxy();
    player = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), new InMemorySettingsService(), DispatchProxy.Create<IToastService, NoopProxy>());
    await player.OpenAsync(new DramaCardViewModel(drama, "quality"));
    var oldPrefetch = proxy.Prepared[1];
    await player.SelectQualityCommand.Execute(new PlayerMenuOptionViewModel("720P", "", intValue: 720)).ToTask();
    Check(proxy.Released.Contains(oldPrefetch) && catalog.RequestedQualities.TakeLast(2).All(x => x == 720), "切换清晰度释放旧预加载并按新画质重新缓存");
    var qualityPrefetch = proxy.Prepared[^1];
    await player.NextEpisodeCommand.Execute().ToTask();
    Check(player.ResolvedPlayUrl == qualityPrefetch.ToString(), "下一集复用新画质缓存");
    player.Close();

    catalog = new FakeCatalog();
    proxy = new FakeProxy();
    player = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), new InMemorySettingsService(), DispatchProxy.Create<IToastService, NoopProxy>());
    await player.OpenAsync(new DramaCardViewModel(drama, "skip"));
    var skipped = proxy.Prepared[1];
    await player.SelectEpisodeCommand.Execute(player.Episodes.Single(x => x.No == 3)).ToTask();
    Check(proxy.Released.Contains(skipped) && player.CurrentEpisodeNo == 3, "跳集时丢弃不匹配的下一集缓存");

    Check(player.ShowTransportControls && !player.ShowMiniTopBar, "正常窗口打开后默认显示播放功能栏");
    await player.ToggleMenuCommand.Execute("speed").ToTask();
    Check(player.IsAnyMenuOpen && player.UseFloatingMenu, "正常窗口打开倍速菜单");
    player.SetControlsVisible(false);
    Check(!player.AreControlsVisible && !player.ShowTransportControls && !player.UseFloatingHud && !player.IsAnyMenuOpen, "正常窗口隐藏功能栏时同步收起悬浮条与菜单");
    player.SetControlsVisible(true);
    Check(player.AreControlsVisible && player.ShowTransportControls && player.UseFloatingHud, "正常窗口重新显示功能栏");
    await player.ToggleMiniModeCommand.Execute().ToTask();
    Check(player.IsMiniMode && player.ShowMiniTopBar && player.ShowTransportControls, "切换小窗后默认显示顶栏与控制栏");
    player.ToggleControlsVisibility();
    Check(!player.ShowMiniTopBar && !player.ShowTransportControls, "小窗允许切换隐藏全部功能栏");
    player.Close();

    catalog = new FakeCatalog { DelayEpisode = 2 };
    proxy = new FakeProxy();
    player = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), new InMemorySettingsService(), DispatchProxy.Create<IToastService, NoopProxy>());
    await player.OpenAsync(new DramaCardViewModel(drama, "cancel"));
    player.Close();
    catalog.Delayed.TrySetResult();
    await Task.Delay(50);
    Check(proxy.Prepared.Count == 1 && proxy.Released.Count == 1 && !player.IsOpen, "关闭时取消在途预加载且不激活陈旧媒体");

    await CatalogRegressionTests.RunAsync();
    await DownloadRegressionTests.RunAsync(root);
    HotkeyRegressionTests.Run(root);

    var instanceId = "MioReel.Test." + Guid.NewGuid().ToString("N");
    using (var primary = MioReel.Desktop.Services.SingleInstanceGuard.TryAcquire(instanceId))
    {
        Check(primary is not null, "首次启动成功获取单实例互斥锁");
        var secondAcquired = await Task.Run(() =>
        {
            using var secondary = MioReel.Desktop.Services.SingleInstanceGuard.TryAcquire(instanceId);
            return secondary is not null;
        });
        Check(!secondAcquired, "重复启动时进程互斥阻止第二个实例进入");

        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary!.StartListening(() => activated.TrySetResult());
        MioReel.Desktop.Services.SingleInstanceGuard.SignalExistingInstance(instanceId);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(activated.Task.IsCompletedSuccessfully, "重复启动可唤醒已存在的主实例");
    }
    using (var reacquired = MioReel.Desktop.Services.SingleInstanceGuard.TryAcquire(instanceId))
    {
        Check(reacquired is not null, "主实例退出释放锁后允许再次启动");
    }

    await using var upstream = new RangeServer();
    using var mediaProxy = new LocalMediaProxyService();
    var info = new ResolvedStreamInfo("range", 1, upstream.Url, new Dictionary<string, string>(), "", "none", 1080, 720, 1280, "h264", 0);
    var playable = await mediaProxy.PreparePlayableUriAsync(info);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    Check(upstream.RequestCount == 2, "媒体准备阶段已读取开头和尾部数据");
    var opening = await ReadRange(http, playable, 0, 65535);
    Check(opening.SequenceEqual(upstream.Bytes.Take(65536)) && upstream.RequestCount == 2, "播放开头直接命中预缓存");
    var tail = await ReadRange(http, playable, upstream.Bytes.Length - 65536, upstream.Bytes.Length - 1);
    Check(tail.SequenceEqual(upstream.Bytes.TakeLast(65536)) && upstream.RequestCount == 2, "尾部探测直接命中预缓存且返回正确 Range 数据");
    _ = await ReadRange(http, playable, 0, 65535);
    Check(upstream.RequestCount == 2, "尾部探测后开头缓存仍可复用");
    mediaProxy.ReleaseSession(playable);
    using var released = await http.GetAsync(playable);
    Check(released.StatusCode == HttpStatusCode.NotFound, "释放后的媒体会话不可继续访问");
    Console.WriteLine("所有回归检查通过");
}
finally
{
    foreach (var file in Directory.GetFiles(root)) File.Delete(file);
    Directory.Delete(root);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

static async Task<byte[]> ReadRange(HttpClient http, Uri uri, long start, long end)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
    request.Headers.Range = new(start, end);
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync();
}

public class NoopProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.ReturnType == typeof(void) ? null : targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
}

sealed class FakeCatalog : IDramaCatalogService
{
    public List<int> Resolved { get; } = [];
    public List<int> RequestedQualities { get; } = [];
    public Func<ResolvedStreamInfo, ResolvedStreamInfo>? Transform { get; init; }
    public int FailEpisode { get; set; }
    public int DelayEpisode { get; set; }
    public TaskCompletionSource Delayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsLastRequestLive => true;
    public Task<IReadOnlyList<EpisodeItem>> GetEpisodesAsync(string seriesId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EpisodeItem>>([new(1, "1", "1", true), new(2, "2", "2", true), new(3, "3", "3", true)]);
    public async Task<ResolvedStreamInfo> ResolveEpisodeAsync(string seriesId, int episodeNo, int quality = 1080, CancellationToken ct = default)
    {
        Resolved.Add(episodeNo);
        RequestedQualities.Add(quality);
        if (episodeNo == DelayEpisode) await Delayed.Task.WaitAsync(ct);
        if (episodeNo == FailEpisode) throw new HttpRequestException("测试网络失败");
        var info = new ResolvedStreamInfo(seriesId, episodeNo, "https://example.invalid/video.mp4", new Dictionary<string, string>(), "", "none", quality, 720, 1280, "h264", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
        return Transform?.Invoke(info) ?? info;
    }
    public Task<CatalogPageResult> GetCatalogPageAsync(DramaCategory category, int page = 1, int limit = 40, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<DramaItem>> GetCatalogAsync(DramaCategory category, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<DramaItem>> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<GatewayHealthResult> TestConnectionAsync(string gatewayBaseUrl, CancellationToken ct = default) => throw new NotSupportedException();
}

sealed class FakeProxy : IMediaStreamProxyService
{
    public List<Uri> Prepared { get; } = [];
    public List<ResolvedStreamInfo> Streams { get; } = [];
    public HashSet<Uri> Released { get; } = [];
    public Task<Uri> PreparePlayableUriAsync(ResolvedStreamInfo stream, CancellationToken ct = default)
    {
        var uri = new Uri($"http://127.0.0.1/stream/{Prepared.Count}.mp4");
        Prepared.Add(uri);
        Streams.Add(stream);
        return Task.FromResult(uri);
    }
    public void ReleaseSession(Uri? uri) { if (uri is not null) Released.Add(uri); }
    public void Dispose() { }
}

sealed class RangeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _requests;
    public byte[] Bytes { get; } = Enumerable.Range(0, 4 * 1024 * 1024).Select(x => (byte)(x % 251)).ToArray();
    public int RequestCount => Volatile.Read(ref _requests);
    public string Url { get; }
    public RangeServer()
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/video.mp4";
        _loop = RunAsync();
    }
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                long start = 0, end = Bytes.Length - 1;
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                {
                    if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = line["Range: bytes=".Length..].Split('-');
                    start = long.Parse(parts[0]);
                    end = Math.Min(long.Parse(parts[1]), Bytes.Length - 1);
                }
                Interlocked.Increment(ref _requests);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{Bytes.Length}\r\nContent-Length: {end - start + 1}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(Bytes.AsMemory((int)start, (int)(end - start + 1)), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}
