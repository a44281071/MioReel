using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MioReel.Core.Models;
using MioReel.Desktop.ViewModels;

namespace MioReel.Desktop.Controls;

/// <summary>
/// 高性能自绘弹幕层：基于 TopLevel.RequestAnimationFrame 与多轨道防重叠调度实现 60fps 平滑飘动。
/// </summary>
public sealed class DanmakuOverlay : Control
{
    private const double HorizontalGapPx = 32;
    private const double BaseCrossSeconds = 8.5;
    private const int MaxActiveBullets = 28;

    private static readonly FontFamily BulletFontFamily =
        new("Microsoft YaHei UI, PingFang SC, Noto Sans CJK SC, Sans-Serif");
    private static readonly Typeface BulletTypeface = new(BulletFontFamily, FontStyle.Normal, FontWeight.Bold);
    private static readonly IBrush OutlineBrush = new SolidColorBrush(Color.FromArgb(238, 0, 0, 0));
    private static readonly Point[] OutlineOffsets =
    [
        new(-1.3, 0),
        new(1.3, 0),
        new(0, -1.3),
        new(0, 1.3),
        new(1.2, 1.2)
    ];

    private sealed class ActiveBullet
    {
        public required DanmakuItem Item { get; init; }
        public required int Lane { get; init; }
        public required double Width { get; init; }
        public required double Height { get; init; }
        public required double SpeedPxPerSec { get; set; }
        public required double X { get; set; }
        public required double Y { get; set; }
        public required FormattedText TextVisual { get; init; }
        public required FormattedText OutlineVisual { get; init; }
    }

    private readonly List<DanmakuItem> _timeline = [];
    private readonly List<ActiveBullet> _active = [];
    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);

    private PlayerViewModel? _player;
    private int _nextSpawnIndex;
    private double _interpolatedPosMs;
    private double _lastReportedPosSeconds;
    private TimeSpan? _lastFrameTimestamp;
    private bool _frameLoopActive;

    public DanmakuOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        UseLayoutRounding = true;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        RenderOptions.SetEdgeMode(this, EdgeMode.Antialias);
    }

    public void SetPlayer(PlayerViewModel? player)
    {
        if (ReferenceEquals(_player, player))
            return;

        if (_player is not null)
        {
            _player.PropertyChanged -= OnPlayerPropertyChanged;
            _player.DanmakuTimelineCleared -= OnTimelineCleared;
            _player.DanmakuItemsLoaded -= OnItemsLoaded;
            _player.DanmakuSeeked -= OnSeeked;
        }

        _player = player;
        _active.Clear();
        _timeline.Clear();
        _nextSpawnIndex = 0;

        if (_player is not null)
        {
            _player.PropertyChanged += OnPlayerPropertyChanged;
            _player.DanmakuTimelineCleared += OnTimelineCleared;
            _player.DanmakuItemsLoaded += OnItemsLoaded;
            _player.DanmakuSeeked += OnSeeked;

            _lastReportedPosSeconds = _player.PositionSeconds;
            _interpolatedPosMs = _player.PositionSeconds * 1000.0;
            var snapshot = _player.GetLoadedDanmakuSnapshot();
            if (snapshot.Count > 0)
            {
                _timeline.AddRange(snapshot);
                AdvanceSpawnIndexTo(_interpolatedPosMs - 400);
            }
        }

        UpdateAnimationLoopState();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_player is not null)
        {
            SyncFromPlayerSnapshot();
            UpdateAnimationLoopState();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frameLoopActive = false;
        _lastFrameTimestamp = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
            UpdateAnimationLoopState();
    }

    private void SyncFromPlayerSnapshot()
    {
        if (_player is null) return;
        _timeline.Clear();
        _timeline.AddRange(_player.GetLoadedDanmakuSnapshot());
        _lastReportedPosSeconds = _player.PositionSeconds;
        _interpolatedPosMs = _player.PositionSeconds * 1000.0;
        AdvanceSpawnIndexTo(_interpolatedPosMs - 400);
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_player is null) return;
        if (e.PropertyName == nameof(PlayerViewModel.PositionSeconds))
        {
            var reportedMs = _player.PositionSeconds * 1000.0;
            _lastReportedPosSeconds = _player.PositionSeconds;
            if (Math.Abs(_interpolatedPosMs - reportedMs) > 450 || !_player.IsPlaying)
                _interpolatedPosMs = reportedMs;
        }
        else if (e.PropertyName is nameof(PlayerViewModel.IsPlaying)
                 or nameof(PlayerViewModel.IsDanmakuEnabled)
                 or nameof(PlayerViewModel.HasRealMediaUrl)
                 or nameof(PlayerViewModel.IsOpen))
        {
            if (!_player.IsDanmakuEnabled || !_player.HasRealMediaUrl || !_player.IsOpen)
            {
                _active.Clear();
                InvalidateVisual();
            }
            else if (_player.IsPlaying)
            {
                _interpolatedPosMs = _player.PositionSeconds * 1000.0;
            }
            UpdateAnimationLoopState();
        }
    }

    private void OnTimelineCleared()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _active.Clear();
            _timeline.Clear();
            _nextSpawnIndex = 0;
            _interpolatedPosMs = (_player?.PositionSeconds ?? 0) * 1000.0;
            InvalidateVisual();
        });
    }

    private void OnItemsLoaded(IReadOnlyList<DanmakuItem> items)
    {
        if (items.Count == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_player is null) return;
            var currentPosMs = _interpolatedPosMs;
            DanmakuItem? nextTarget = _nextSpawnIndex < _timeline.Count ? _timeline[_nextSpawnIndex] : null;
            _timeline.AddRange(items);
            _timeline.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

            if (nextTarget is not null)
            {
                var minTime = Math.Min(nextTarget.TimeMs, (long)(currentPosMs - 400));
                AdvanceSpawnIndexTo(minTime);
            }
            else
            {
                AdvanceSpawnIndexTo(currentPosMs - 400);
            }
        });
    }

    private void OnSeeked(double positionSeconds)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _active.Clear();
            _lastReportedPosSeconds = positionSeconds;
            _interpolatedPosMs = positionSeconds * 1000.0;
            AdvanceSpawnIndexTo(_interpolatedPosMs - 400);
            InvalidateVisual();
        });
    }

    private void AdvanceSpawnIndexTo(double minTimeMs)
    {
        var idx = 0;
        while (idx < _timeline.Count && _timeline[idx].TimeMs < minTimeMs)
            idx++;
        _nextSpawnIndex = idx;
    }

    private bool ShouldAnimate() =>
        _player is not null
        && _player.IsOpen
        && _player.HasRealMediaUrl
        && _player.IsDanmakuEnabled
        && _player.IsPlaying
        && IsEffectivelyVisible
        && TopLevel.GetTopLevel(this) is not null;

    private void UpdateAnimationLoopState()
    {
        if (!ShouldAnimate())
        {
            _frameLoopActive = false;
            _lastFrameTimestamp = null;
            return;
        }

        if (_frameLoopActive)
            return;

        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _frameLoopActive = true;
            _lastFrameTimestamp = null;
            topLevel.RequestAnimationFrame(OnAnimationFrame);
        }
    }

    private void OnAnimationFrame(TimeSpan timestamp)
    {
        if (!_frameLoopActive || !ShouldAnimate() || _player is null)
        {
            _frameLoopActive = false;
            _lastFrameTimestamp = null;
            return;
        }

        var dt = _lastFrameTimestamp is { } prev
            ? Math.Clamp((timestamp - prev).TotalSeconds, 0.0, 0.08)
            : 1.0 / 60.0;
        _lastFrameTimestamp = timestamp;

        var rate = Math.Clamp(_player.PlaybackRate, 0.75, 3.0);
        // Keep timeline spawn synced with playback speed, while capping horizontal drift speed at <=1x for readability.
        var motionRate = Math.Clamp(_player.PlaybackRate, 0.75, 1.0);
        _interpolatedPosMs += dt * 1000.0 * rate;

        // Keep clock bounded if stream stalls slightly between sync ticks.
        var maxDriftMs = Math.Max(0, _lastReportedPosSeconds * 1000.0) + 550.0;
        if (_interpolatedPosMs > maxDriftMs)
            _interpolatedPosMs = maxDriftMs;

        var width = Bounds.Width;
        var height = Bounds.Height;

        if (width > 80 && height > 60)
        {
            StepBullets(dt, width, motionRate);
            SpawnDueBullets(width, height, motionRate);
        }

        if (_active.Count > 0)
            InvalidateVisual();

        if (TopLevel.GetTopLevel(this) is { } topLevel && _frameLoopActive)
            topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void StepBullets(double dt, double viewportWidth, double rate)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var b = _active[i];
            b.X -= b.SpeedPxPerSec * rate * dt;
            if (b.X + b.Width < -16 || b.X > viewportWidth + 120)
                _active.RemoveAt(i);
        }
    }

    private void SpawnDueBullets(double viewportWidth, double viewportHeight, double rate)
    {
        if (_nextSpawnIndex >= _timeline.Count)
            return;

        // Skip stale items if initial chunk loaded after playback already moved far ahead.
        while (_nextSpawnIndex < _timeline.Count && _timeline[_nextSpawnIndex].TimeMs < _interpolatedPosMs - 1400)
            _nextSpawnIndex++;

        var isMini = _player?.IsMiniMode == true || viewportWidth < 460;
        var fontSize = isMini ? 14.0 : 19.0;
        var laneHeight = Math.Round(fontSize + (isMini ? 8.0 : 11.0));
        var topMargin = isMini ? 36.0 : 16.0;
        var maxRegionHeight = Math.Max(laneHeight * 2, viewportHeight * (isMini ? 0.66 : 0.60) - topMargin);
        var laneCount = Math.Clamp((int)(maxRegionHeight / laneHeight), 2, 10);

        while (_nextSpawnIndex < _timeline.Count)
        {
            var item = _timeline[_nextSpawnIndex];
            if (item.TimeMs > _interpolatedPosMs)
                break;

            _nextSpawnIndex++;
            if (_active.Count >= MaxActiveBullets)
                continue;

            var formatted = new FormattedText(
                item.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                BulletTypeface,
                fontSize,
                ResolveBrush(item.ColorHex));

            var textW = formatted.Width;
            var textH = formatted.Height;
            var speed = Math.Clamp((viewportWidth + textW * 0.35) / BaseCrossSeconds, 44.0, 145.0);

            var lane = PickAvailableLane(laneCount, viewportWidth, textW, speed, rate);
            if (lane < 0)
                continue;

            var outline = new FormattedText(
                item.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                BulletTypeface,
                fontSize,
                OutlineBrush);

            _active.Add(new ActiveBullet
            {
                Item = item,
                Lane = lane,
                Width = textW,
                Height = textH,
                SpeedPxPerSec = speed,
                X = viewportWidth + 6,
                Y = Math.Round(topMargin + lane * laneHeight),
                TextVisual = formatted,
                OutlineVisual = outline
            });
        }
    }

    private int PickAvailableLane(int laneCount, double viewportWidth, double newWidth, double newSpeed, double rate)
    {
        var timeForNewCross = viewportWidth / Math.Max(1.0, newSpeed * rate);
        for (var lane = 0; lane < laneCount; lane++)
        {
            ActiveBullet? rightmost = null;
            foreach (var b in _active)
            {
                if (b.Lane != lane) continue;
                if (rightmost is null || b.X + b.Width > rightmost.X + rightmost.Width)
                    rightmost = b;
            }

            if (rightmost is null)
                return lane;

            var tail = rightmost.X + rightmost.Width;
            if (tail + HorizontalGapPx > viewportWidth)
                continue;

            // Ensure the new bullet cannot catch up before the leading bullet exits the left edge.
            var leadingRemainingSec = Math.Max(0.0, tail) / Math.Max(1.0, rightmost.SpeedPxPerSec * rate);
            var newHeadCatchupSec = (viewportWidth - tail - HorizontalGapPx) / Math.Max(1.0, newSpeed * rate);
            if (newSpeed <= rightmost.SpeedPxPerSec || timeForNewCross >= leadingRemainingSec || newHeadCatchupSec >= leadingRemainingSec)
                return lane;
        }

        // If all lanes are busy, reuse the lane whose rightmost bullet has moved furthest left when still clear.
        var bestLane = -1;
        var smallestTail = double.MaxValue;
        for (var lane = 0; lane < laneCount; lane++)
        {
            var laneMaxTail = double.MinValue;
            foreach (var b in _active)
            {
                if (b.Lane == lane)
                    laneMaxTail = Math.Max(laneMaxTail, b.X + b.Width);
            }
            if (laneMaxTail < viewportWidth - 8 && laneMaxTail < smallestTail)
            {
                smallestTail = laneMaxTail;
                bestLane = lane;
            }
        }
        return bestLane;
    }

    private IBrush ResolveBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return Brushes.White;

        var key = hex.Trim();
        if (_brushCache.TryGetValue(key, out var cached))
            return cached;

        var brush = Color.TryParse(key, out var color)
            ? new SolidColorBrush(Color.FromArgb(248, color.R, color.G, color.B))
            : (IBrush)Brushes.White;
        _brushCache[key] = brush;
        return brush;
    }

    public override void Render(DrawingContext context)
    {
        if (_active.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        using var clip = context.PushClip(new Rect(Bounds.Size));
        using var opacity = context.PushOpacity(0.96);
        foreach (var b in _active)
        {
            var x = Math.Round(b.X);
            var y = Math.Round(b.Y);
            foreach (var offset in OutlineOffsets)
                context.DrawText(b.OutlineVisual, new Point(x + offset.X, y + offset.Y));
            context.DrawText(b.TextVisual, new Point(x, y));
        }
    }
}
