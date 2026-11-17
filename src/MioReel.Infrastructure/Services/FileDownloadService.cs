using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Core.Serialization;

namespace MioReel.Infrastructure.Services;

/// <summary>串行离线下载队列。只有完整写入并关闭的文件才会进入离线片单。</summary>
public sealed class FileDownloadService : IDownloadService, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IDramaCatalogService _catalog;
    private readonly IMediaStreamProxyService _proxy;
    private readonly ISettingsService _settings;
    private readonly string _manifestPath;
    private readonly List<DownloadTaskItem> _tasks = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Task _worker;
    private CancellationTokenSource? _active;
    private string? _activeId;
    private int _disposed;

    public FileDownloadService(IDramaCatalogService catalog, IMediaStreamProxyService proxy, ISettingsService settings)
        : this(catalog, proxy, settings, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MioReel", "downloads.json")) { }

    public FileDownloadService(IDramaCatalogService catalog, IMediaStreamProxyService proxy, ISettingsService settings, string manifestPath)
    {
        _catalog = catalog;
        _proxy = proxy;
        _settings = settings;
        _manifestPath = manifestPath;
        if (File.Exists(manifestPath))
        {
            // Never overwrite a damaged manifest with an apparently empty queue.
            try
            {
                var saved = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), MioReelJsonContext.Default.ListDownloadTaskItem);
                if (saved is not null)
                    _tasks.AddRange(saved.Select(x => x.State == DownloadTaskState.Downloading
                        ? x with { State = DownloadTaskState.Paused, SpeedText = "", ErrorMessage = null } : x));
            }
            catch (JsonException) { File.Copy(manifestPath, manifestPath + $".corrupt-{DateTime.UtcNow.Ticks}"); }
        }
        _worker = Task.Run(RunAsync);
    }

    public IReadOnlyList<DownloadTaskItem> GetDownloads() { lock (_gate) return _tasks.ToArray(); }

    public DownloadTaskItem Enqueue(DramaItem drama, int episodeCount)
    {
        if (episodeCount < 1 || drama.EpisodeCount < 1) throw new ArgumentException("没有可下载的剧集");
        episodeCount = Math.Min(episodeCount, drama.EpisodeCount);
        var root = _settings.Current.DownloadDirectory;
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("请选择完整的下载目录路径");
        var id = Guid.NewGuid().ToString("N");
        // A generated directory avoids path traversal, invalid title characters and name collisions.
        var seriesKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(drama.SeriesId)))[..16];
        var directory = Path.Combine(Path.GetFullPath(root), seriesKey, id);
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, ".write-test");
        File.WriteAllText(probe, "");
        File.Delete(probe);
        lock (_gate)
        {
            var existing = _tasks.FirstOrDefault(x => x.Drama.SeriesId == drama.SeriesId && x.Quality == _settings.Current.DefaultQuality
                && x.EpisodeNumbers?.Length == episodeCount && x.State != DownloadTaskState.Failed
                && (x.State != DownloadTaskState.Completed || x.Files?.Count(IsAvailable) == episodeCount));
            if (existing is not null) return existing;
            var task = new DownloadTaskItem(id, drama, $"第 1-{episodeCount} 集", 0, "等待下载", "0 MB",
                DownloadTaskState.Downloading, directory, _settings.Current.DefaultQuality, Enumerable.Range(1, episodeCount).ToArray(), []);
            _tasks.Insert(0, task);
            try { SaveLocked(); }
            catch { _tasks.Remove(task); throw; }
            _signal.Release();
            return task;
        }
    }

    public void TogglePause(string taskId)
    {
        lock (_gate)
        {
            var i = _tasks.FindIndex(x => x.TaskId == taskId);
            if (i < 0 || _tasks[i].State == DownloadTaskState.Completed) return;
            var pause = _tasks[i].State == DownloadTaskState.Downloading;
            _tasks[i] = _tasks[i] with { State = pause ? DownloadTaskState.Paused : DownloadTaskState.Downloading,
                ErrorMessage = null, SpeedText = pause ? "" : "等待下载" };
            if (pause && _activeId == taskId) _active?.Cancel();
            SaveLocked();
            _signal.Release();
        }
    }

    public void Remove(string taskId)
    {
        lock (_gate)
        {
            if (_activeId == taskId) _active?.Cancel();
            _tasks.RemoveAll(x => x.TaskId == taskId);
            SaveLocked();
        }
    }

    private static bool IsAvailable(OfflineEpisode file) => File.Exists(file.FilePath) && new FileInfo(file.FilePath).Length == file.Length && file.Length > 0;

    public IReadOnlyList<EpisodeItem> GetOfflineEpisodes(string seriesId) => GetDownloads()
        .Where(x => x.Drama.SeriesId == seriesId).SelectMany(x => x.Files ?? []).Where(IsAvailable)
        .DistinctBy(x => x.EpisodeNo).OrderBy(x => x.EpisodeNo)
        .Select(x => new EpisodeItem(x.EpisodeNo, "", $"第 {x.EpisodeNo} 集", true)).ToArray();

    public ResolvedStreamInfo? GetOfflineStream(string seriesId, int episodeNo)
    {
        var file = GetDownloads().Where(x => x.Drama.SeriesId == seriesId).SelectMany(x => x.Files ?? [])
            .Where(x => x.EpisodeNo == episodeNo && IsAvailable(x)).OrderByDescending(x => x.Quality).FirstOrDefault();
        return file is null ? null : new ResolvedStreamInfo(seriesId, episodeNo, new Uri(file.FilePath).AbsoluteUri,
            new Dictionary<string, string>(), "", "none", file.Quality, file.Width, file.Height, file.Codec, 0);
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token);
                while (true)
                {
                    _shutdown.Token.ThrowIfCancellationRequested();
                    DownloadTaskItem? task;
                    CancellationToken token;
                    lock (_gate)
                    {
                        task = _tasks.LastOrDefault(x => x.State == DownloadTaskState.Downloading);
                        if (task is null) break;
                        _activeId = task.TaskId;
                        _active = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                        token = _active.Token;
                    }
                    try { await DownloadAsync(task, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception) when (token.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        Update(task.TaskId, x => x with { State = DownloadTaskState.Failed, SpeedText = "",
                            ErrorMessage = ex is GatewayException gateway ? gateway.UserMessage : $"下载失败：{ex.Message}" }, persist: true);
                    }
                    finally
                    {
                        lock (_gate) { _active?.Dispose(); _active = null; _activeId = null; }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task DownloadAsync(DownloadTaskItem task, CancellationToken ct)
    {
        var episodes = task.EpisodeNumbers ?? throw new InvalidDataException("下载任务缺少集数信息");
        var files = (task.Files ?? []).Where(IsAvailable).ToList();
        Directory.CreateDirectory(task.DirectoryPath);
        foreach (var number in episodes)
        {
            ct.ThrowIfCancellationRequested();
            if (files.Any(x => x.EpisodeNo == number)) continue;
            var info = await _catalog.ResolveEpisodeAsync(task.Drama.SeriesId, number, task.Quality, ct);
            var variant = info.Variants?.FirstOrDefault(x => x.Quality == task.Quality);
            if (variant is not null && info.Quality != task.Quality)
                info = info with { PlayUrl = variant.Url, Quality = variant.Quality, Width = variant.Width, Height = variant.Height,
                    Codec = variant.Codec, DecryptionKey = variant.DecryptionKey, Encryption = string.IsNullOrEmpty(variant.DecryptionKey) ? "none" : "aes-128-cbc" };
            var uri = await _proxy.PreparePlayableUriAsync(info, ct);
            var path = Path.Combine(task.DirectoryPath, $"episode-{number:D4}.mp4");
            try
            {
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;
                if (length is not > 0) throw new InvalidDataException("视频响应缺少有效长度");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                long received = 0;
                var clock = Stopwatch.StartNew();
                var lastReport = -1L;
                await using (var output = new FileStream(path + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    var buffer = new byte[128 * 1024];
                    int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                        received += count;
                        if (clock.ElapsedMilliseconds - lastReport < 300) continue;
                        lastReport = clock.ElapsedMilliseconds;
                        var percent = (int)((files.Count + Math.Min(0.999, (double)received / length.Value)) * 100 / episodes.Length);
                        Update(task.TaskId, x => x with { ProgressPercent = Math.Min(99, percent),
                            SpeedText = $"{received / Math.Max(0.1, clock.Elapsed.TotalSeconds) / 1048576:0.0} MB/s",
                            SizeText = $"{(files.Sum(f => f.Length) + received) / 1048576.0:0.0} MB" });
                    }
                    await output.FlushAsync(ct);
                }
                if (received != length) throw new IOException("视频传输不完整，请重试");
                ct.ThrowIfCancellationRequested();
                File.Move(path + ".part", path, overwrite: true);
                files.Add(new OfflineEpisode(number, path, received, info.Quality, info.Width, info.Height, info.Codec));
                Update(task.TaskId, x => x with { Files = files.ToArray(), ProgressPercent = files.Count * 100 / episodes.Length,
                    SizeText = $"{files.Sum(f => f.Length) / 1048576.0:0.0} MB" }, persist: true);
            }
            finally { _proxy.ReleaseSession(uri); }
        }
        ct.ThrowIfCancellationRequested();
        Update(task.TaskId, x => x with { State = DownloadTaskState.Completed, ProgressPercent = 100, SpeedText = "", ErrorMessage = null }, persist: true);
    }

    private void Update(string id, Func<DownloadTaskItem, DownloadTaskItem> update, bool persist = false)
    {
        lock (_gate)
        {
            var i = _tasks.FindIndex(x => x.TaskId == id);
            if (i < 0 || _tasks[i].State != DownloadTaskState.Downloading) return;
            _tasks[i] = update(_tasks[i]);
            if (persist)
            {
                try { SaveLocked(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep the worker usable when the disk fills or the manifest becomes unwritable.
                    _tasks[i] = _tasks[i] with { State = DownloadTaskState.Failed, SpeedText = "", ErrorMessage = "无法保存下载记录，请检查磁盘空间和目录权限后重试" };
                    if (_activeId == id) _active?.Cancel();
                }
            }
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_manifestPath))!);
        File.WriteAllText(_manifestPath + ".tmp", JsonSerializer.Serialize(_tasks, MioReelJsonContext.Default.ListDownloadTaskItem));
        File.Move(_manifestPath + ".tmp", _manifestPath, overwrite: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        lock (_gate) _active?.Cancel();
        // Let cancellation unwind network/file operations before disposing shared resources.
        _ = _worker.ContinueWith(_ => { _http.Dispose(); _signal.Dispose(); _shutdown.Dispose(); }, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _worker.ConfigureAwait(false);
    }
}
