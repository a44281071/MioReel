using Avalonia.Media;
using System.Collections.ObjectModel;
using MioReel.Core.Models;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace MioReel.Desktop.ViewModels;

public enum NavSection
{
    Explore,
    Search,
    History,
    Downloads,
    Settings
}

public sealed class NavItemViewModel(NavSection section, string label, string shortLabel, string pathData)
{
    public NavSection Section { get; } = section;
    public string Label { get; } = label;
    public string ShortLabel { get; } = shortLabel;
    public Geometry IconGeometry { get; } = Geometry.Parse(pathData);
    public override string ToString() => Label;
}

public enum WindowLayoutMode
{
    Wide,
    CompactRail,
    Narrow
}

public sealed class DramaCardViewModel(DramaItem drama, string heroTag, int watchedEpisode = 0)
{
    public DramaItem Drama { get; } = drama;
    public string HeroTag { get; } = heroTag;
    public int WatchedEpisode { get; } = watchedEpisode;
    public string Title => Drama.Title;
    public string CategoryLabel => Drama.CategoryLabel;
    public string TagsSummary => Drama.TagsSummary;
    public string Intro => Drama.Intro;
    public string StatusLabel => WatchedEpisode > 0 ? $"看到第{WatchedEpisode}集" : $"全{Drama.EpisodeCount}集";
}

public sealed class EpisodeOptionViewModel : ReactiveObject
{
    private bool _isCurrent;

    public EpisodeOptionViewModel(EpisodeItem episode, bool isCurrent)
    {
        Episode = episode;
        _isCurrent = isCurrent;
    }

    public EpisodeItem Episode { get; }
    public int No => Episode.No;
    public string Label => Episode.No.ToString();

    public bool IsCurrent
    {
        get => _isCurrent;
        set => this.RaiseAndSetIfChanged(ref _isCurrent, value);
    }
}

public sealed class HistoryItemViewModel(HistoryRecord record, string heroTag)
{
    public HistoryRecord Record { get; } = record;
    public DramaCardViewModel Card { get; } = new(record.Drama, heroTag, record.LastEpisodeNo);
    public string Title => Record.Drama.Title;
    public string HeroTag => Card.HeroTag;
    public string Subtitle => $"看到第 {Record.LastEpisodeNo} 集 · {Record.RelativeTimeText}";
    public int ProgressPercent => Record.ProgressPercent;
}

public sealed class DownloadItemViewModel(DownloadTaskItem task, string heroTag) : ReactiveObject
{
    public DownloadTaskItem Task { get; private set; } = task;
    public void Update(DownloadTaskItem value)
    {
        if (Task == value) return;
        Task = value;
        foreach (var property in new[] { nameof(ProgressPercent), nameof(IsCompleted), nameof(IsPaused), nameof(ToggleLabel), nameof(RightMetric), nameof(StatusDetail) })
            this.RaisePropertyChanged(property);
    }
    public DramaCardViewModel Card { get; } = new(task.Drama, heroTag);
    public string TaskId => Task.TaskId;
    public string Title => $"{Task.Drama.Title} · {Task.EpisodeRangeLabel}";
    public string HeroTag => Card.HeroTag;
    public int ProgressPercent => Task.ProgressPercent;
    public bool IsCompleted => Task.State == DownloadTaskState.Completed;
    public bool IsPaused => Task.State == DownloadTaskState.Paused;
    public string ToggleLabel => Task.State == DownloadTaskState.Failed ? "重试" : IsPaused ? "继续" : "暂停";
    public string RightMetric => IsCompleted ? Task.SizeText : $"{Task.ProgressPercent}%";
    public string StatusDetail => Task.State switch
    {
        DownloadTaskState.Completed => "已缓存到本地，支持离线观看",
        DownloadTaskState.Paused => "已暂停",
        DownloadTaskState.Failed => Task.ErrorMessage ?? "下载失败，请重试",
        _ => $"{Task.SpeedText} · 下载中"
    };
}

public sealed class ConfirmDialogViewModel : ReactiveObject
{
    public ConfirmDialogViewModel(string title, string message, Action onConfirm, Action onCancel)
    {
        Title = title;
        Message = message;
        ConfirmCommand = ReactiveCommand.Create(onConfirm);
        CancelCommand = ReactiveCommand.Create(onCancel);
    }

    public string Title { get; }
    public string Message { get; }
    public ReactiveCommand<RxVoid, RxVoid> ConfirmCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }
}

public sealed class BatchDownloadDialogViewModel : ReactiveObject
{
    private bool _selectAllEpisodes;

    public BatchDownloadDialogViewModel(
        DramaItem drama,
        int defaultQuality,
        Action<int> onConfirm,
        Action onCancel)
    {
        Drama = drama;
        QualityText = $"{defaultQuality}P";
        FirstTwentyLabel = $"前 {Math.Min(20, drama.EpisodeCount)} 集（第 1-{Math.Min(20, drama.EpisodeCount)} 集）";
        AllEpisodesLabel = $"全部 {drama.EpisodeCount} 集";
        ConfirmCommand = ReactiveCommand.Create(() =>
            onConfirm(SelectAllEpisodes ? drama.EpisodeCount : Math.Min(20, drama.EpisodeCount)));
        CancelCommand = ReactiveCommand.Create(onCancel);
    }

    public DramaItem Drama { get; }
    public string QualityText { get; }
    public string FirstTwentyLabel { get; }
    public string AllEpisodesLabel { get; }

    public bool SelectAllEpisodes
    {
        get => _selectAllEpisodes;
        set => this.RaiseAndSetIfChanged(ref _selectAllEpisodes, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> ConfirmCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }
}

public sealed class DramaCardRowViewModel(IEnumerable<DramaCardViewModel> items)
{
    public ObservableCollection<DramaCardViewModel> Items { get; } = [.. items];
}

public sealed class HistoryRowEntry
{
    public HistoryRowEntry(string headerTitle)
    {
        IsHeader = true;
        HeaderTitle = headerTitle;
        Items = [];
    }

    public HistoryRowEntry(IReadOnlyList<HistoryItemViewModel> items)
    {
        IsHeader = false;
        HeaderTitle = string.Empty;
        Items = items;
    }

    public bool IsHeader { get; }
    public bool IsCardsRow => !IsHeader;
    public string HeaderTitle { get; }
    public IReadOnlyList<HistoryItemViewModel> Items { get; }
}

public sealed class EpisodeRowViewModel(IReadOnlyList<EpisodeOptionViewModel> items)
{
    public IReadOnlyList<EpisodeOptionViewModel> Items { get; } = items;
}

public sealed class PlayerMenuOptionViewModel : ReactiveObject
{
    private bool _isSelected;

    public PlayerMenuOptionViewModel(string label, string subtitle, int intValue = 0, double doubleValue = 0)
    {
        Label = label;
        Subtitle = subtitle;
        IntValue = intValue;
        DoubleValue = doubleValue;
    }

    public string Label { get; }
    public string Subtitle { get; }
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
    public int IntValue { get; }
    public double DoubleValue { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}
