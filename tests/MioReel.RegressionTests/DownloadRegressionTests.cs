using System.Reflection;
using Aviora.Presentation.Toasts;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Desktop.ViewModels;
using MioReel.Infrastructure.Services;
using MioReel.Infrastructure.Services.MediaProxy;
using ReactiveUI.Primitives;

internal static class DownloadRegressionTests
{
    public static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "download-tests");
        Directory.CreateDirectory(directory);
        try
        {
            var drama = new DramaItem("../../非法:/剧名", "测试短剧", "", 3, "", "", [], "2026", DramaCategory.RealDrama, "#000000", "#FFFFFF");
            var settingsPath = Path.Combine(directory, "settings.json");
            var settings = new FileSettingsService(settingsPath);
            settings.Update(settings.Current with { DownloadDirectory = Path.Combine(directory, "videos") });
            var toast = DispatchProxy.Create<IToastService, NoopProxy>();
            var player = new PlayerViewModel(new FakeCatalog(), new FakeProxy(), new InMemoryHistoryService(), settings, toast);
            await player.SelectSpeedCommand.Execute(player.SpeedOptions.Single(x => x.DoubleValue == 1.5)).ToTask();
            var reloaded = new FileSettingsService(settingsPath);
            player = new PlayerViewModel(new FakeCatalog(), new FakeProxy(), new InMemoryHistoryService(), reloaded, toast);
            Check(player.PlaybackRate == 1.5 && player.SpeedOptions.Single(x => x.IsSelected).DoubleValue == 1.5,
                "播放倍速写入设置且重启播放器后恢复选中状态");
            await player.OpenAsync(new DramaCardViewModel(drama, "online"));
            await player.NextEpisodeCommand.Execute().ToTask();
            Check(player.PlaybackRate == 1.5, "打开视频和切集保留已保存的倍速");
            player.Close();

            await using var upstream = new RangeServer();
            using var proxy = new LocalMediaProxyService();
            var catalog = new FakeCatalog { DelayEpisode = 2, Transform = info => info with { PlayUrl = upstream.Url } };
            var manifest = Path.Combine(directory, "downloads.json");
            DownloadTaskItem task;
            await using (var downloads = new FileDownloadService(catalog, proxy, settings, manifest))
            {
                task = downloads.Enqueue(drama, 3);
                Check(Path.GetFullPath(task.DirectoryPath).StartsWith(settings.Current.DownloadDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "下载目录隔离且非法剧目ID不会逃逸目标目录");
                Check(downloads.Enqueue(drama, 3).TaskId == task.TaskId, "重复提交相同下载复用已有任务");
                await Until(() => downloads.GetDownloads().Single().Files?.Count == 1);
                downloads.TogglePause(task.TaskId);
                Check(downloads.GetDownloads().Single().State == DownloadTaskState.Paused, "下载可以暂停且保留已完成集数");
                settings.Update(settings.Current with { DownloadDirectory = Path.Combine(directory, "new-videos") });
                Check(downloads.GetDownloads().Single().DirectoryPath == task.DirectoryPath, "切换下载目录不改变已有任务路径");
                downloads.TogglePause(task.TaskId);
                catalog.Delayed.TrySetResult();
                await Until(() => downloads.GetDownloads().Single().State == DownloadTaskState.Completed);
                var completed = downloads.GetDownloads().Single();
                Check(completed.ProgressPercent == 100 && completed.Files?.Count == 3, "继续下载后完成整组任务并记录真实进度");
                foreach (var file in completed.Files!)
                    Check(File.ReadAllBytes(file.FilePath).SequenceEqual(upstream.Bytes), $"第 {file.EpisodeNo} 集落盘内容与源数据完整一致");
                Check(catalog.Resolved.Count(x => x == 1) == 1, "继续下载复用已完成的集数");
                Check(completed.Files.All(x => !File.Exists(x.FilePath + ".part")), "只有完整文件进入已完成片单");
            }

            var retryCatalog = new FakeCatalog { FailEpisode = 1, Transform = info => info with { PlayUrl = upstream.Url } };
            await using (var downloads = new FileDownloadService(retryCatalog, proxy, settings, manifest))
            {
                Check(downloads.GetDownloads().Single().State == DownloadTaskState.Completed && downloads.GetOfflineEpisodes(drama.SeriesId).Count == 3,
                    "重启后恢复下载任务和离线分集列表");
                var offlineCatalog = new FakeCatalog { FailEpisode = 1 };
                var offlineProxy = new FakeProxy();
                player = new PlayerViewModel(offlineCatalog, offlineProxy, new InMemoryHistoryService(), settings, toast, downloads);
                await player.OpenAsync(new DramaCardViewModel(drama, "dl-offline"));
                Check(player.ResolvedPlayUrl?.StartsWith("file:") == true && offlineCatalog.Resolved.Count == 0 && offlineProxy.Prepared.Count == 0,
                    "离线观看直接打开本地文件且不解析网络片源");
                await player.NextEpisodeCommand.Execute().ToTask();
                Check(player.CurrentEpisodeNo == 2 && offlineCatalog.Resolved.Count == 0, "离线切集无需连接网关");
                player.Close();
                var info = downloads.GetOfflineStream(drama.SeriesId, 3)!;
                File.Delete(new Uri(info.PlayUrl).LocalPath);
                Check(downloads.GetOfflineStream(drama.SeriesId, 3) is null && downloads.GetOfflineEpisodes(drama.SeriesId).Count == 2,
                    "本地文件丢失时不再作为可播放离线剧集");
                var another = drama with { SeriesId = "failure", EpisodeCount = 1 };
                var failed = downloads.Enqueue(another, 1);
                Check(failed.DirectoryPath.StartsWith(settings.Current.DownloadDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "更改目录后新任务使用新下载目录");
                await Until(() => downloads.GetDownloads().Single(x => x.TaskId == failed.TaskId).State == DownloadTaskState.Failed);
                Check(downloads.GetDownloads().Single(x => x.TaskId == failed.TaskId).ErrorMessage is { Length: > 0 }, "下载失败展示错误并允许重试");
                retryCatalog.FailEpisode = 0;
                downloads.TogglePause(failed.TaskId);
                await Until(() => downloads.GetDownloads().Single(x => x.TaskId == failed.TaskId).State == DownloadTaskState.Completed);
                Check(downloads.GetDownloads().Single(x => x.TaskId == failed.TaskId).Files?.Count == 1, "失败任务重试后可以正常完成下载");
                downloads.Remove(failed.TaskId);
                downloads.Remove(task.TaskId);
                Check(downloads.GetDownloads().Count == 0 && Directory.GetFiles(task.DirectoryPath, "*.mp4").Length == 2,
                    "移除任务保留本地视频文件");
                retryCatalog.DelayEpisode = 1;
                downloads.Enqueue(drama with { SeriesId = "interrupted" }, 1);
            }
            await using (var restarted = new FileDownloadService(retryCatalog, proxy, settings, manifest))
                Check(restarted.GetDownloads().Single().State == DownloadTaskState.Paused,
                    "重启后未完成的下载恢复为暂停状态，等待用户继续");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(30, timeout.Token);
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
}
