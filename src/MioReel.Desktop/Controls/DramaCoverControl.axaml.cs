using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MioReel.Core.Models;

namespace MioReel.Desktop.Controls;

public partial class DramaCoverControl : UserControl
{
    public static readonly StyledProperty<DramaItem?> DramaProperty =
        AvaloniaProperty.Register<DramaCoverControl, DramaItem?>(nameof(Drama));

    public static readonly StyledProperty<bool> ShowTextOverlayProperty =
        AvaloniaProperty.Register<DramaCoverControl, bool>(nameof(ShowTextOverlay), true);

    public static readonly StyledProperty<bool> ShowBadgesProperty =
        AvaloniaProperty.Register<DramaCoverControl, bool>(nameof(ShowBadges), true);

    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<DramaCoverControl, double>(nameof(BlurRadius), 0d);

    static DramaCoverControl()
    {
        DramaProperty.Changed.AddClassHandler<DramaCoverControl>((ctrl, _) => ctrl.SyncDrama());
        ShowTextOverlayProperty.Changed.AddClassHandler<DramaCoverControl>((ctrl, _) => ctrl.SyncOverlay());
        ShowBadgesProperty.Changed.AddClassHandler<DramaCoverControl>((ctrl, _) => ctrl.UpdateBadgeVisibility(ctrl.Bounds.Width));
        CornerRadiusProperty.Changed.AddClassHandler<DramaCoverControl>((ctrl, _) => ctrl.SyncCornerRadius());
        BlurRadiusProperty.Changed.AddClassHandler<DramaCoverControl>((ctrl, _) => ctrl.SyncBlurEffect());
    }

    public DramaCoverControl()
    {
        InitializeComponent();
        SizeChanged += (_, e) => UpdateBadgeVisibility(e.NewSize.Width);
        SyncCornerRadius();
        SyncOverlay();
        SyncBlurEffect();
        SyncDrama();
    }

    public DramaItem? Drama
    {
        get => GetValue(DramaProperty);
        set => SetValue(DramaProperty, value);
    }

    public bool ShowTextOverlay
    {
        get => GetValue(ShowTextOverlayProperty);
        set => SetValue(ShowTextOverlayProperty, value);
    }

    public bool ShowBadges
    {
        get => GetValue(ShowBadgesProperty);
        set => SetValue(ShowBadgesProperty, value);
    }

    public double BlurRadius
    {
        get => GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    private void SyncBlurEffect()
    {
        if (BackdropLayer is null || RootBorder is null) return;
        var radius = Math.Max(0, BlurRadius);
        if (radius > 0)
        {
            BackdropLayer.Effect = new BlurEffect { Radius = radius };
            BackdropLayer.Margin = new Thickness(-radius);
            RootBorder.BorderThickness = new Thickness(0);
            if (CenterWatermark is not null)
                CenterWatermark.IsVisible = false;
        }
        else
        {
            BackdropLayer.Effect = null;
            BackdropLayer.Margin = new Thickness(0);
            RootBorder.BorderThickness = new Thickness(1);
            if (CenterWatermark is not null && CoverImage is not null)
                CenterWatermark.IsVisible = !CoverImage.IsVisible;
        }
    }

    private void UpdateBadgeVisibility(double width)
    {
        if (BadgeRow is null) return;
        BadgeRow.IsVisible = ShowBadges && (width <= 0 || width >= 88);
    }

    private void SyncCornerRadius()
    {
        if (RootBorder is not null)
            RootBorder.CornerRadius = CornerRadius;
    }

    private void SyncOverlay()
    {
        if (BottomScrim is not null)
            BottomScrim.IsVisible = ShowTextOverlay;
    }

    private void SyncDrama()
    {
        var drama = Drama;
        if (drama is null || RootBorder is null)
            return;

        var c1 = Color.TryParse(drama.AccentPrimaryHex, out var p1) ? p1 : Color.Parse("#BE123C");
        var c2 = Color.TryParse(drama.AccentSecondaryHex, out var p2) ? p2 : Color.Parse("#F59E0B");

        RootBorder.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(230, c1.R, c1.G, c1.B), 0),
                new GradientStop(Color.Parse("#11131D"), 0.56),
                new GradientStop(Color.FromArgb(215, c2.R, c2.G, c2.B), 1.0)
            ]
        };

        GlowTop.Background = new RadialGradientBrush
        {
            Center = new RelativePoint(0.82, 0.18, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.82, 0.18, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.65, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(115, c2.R, c2.G, c2.B), 0),
                new GradientStop(Color.FromArgb(0, c2.R, c2.G, c2.B), 1)
            ]
        };

        GlowBottom.Background = new RadialGradientBrush
        {
            Center = new RelativePoint(0.18, 0.85, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.18, 0.85, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.80, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.65, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(110, c1.R, c1.G, c1.B), 0),
                new GradientStop(Color.FromArgb(0, c1.R, c1.G, c1.B), 1)
            ]
        };

        CategoryText.Text = drama.CategoryLabel;
        EpisodeText.Text = $"{drama.EpisodeCount}集";
        var shortTitle = drama.Title.Length > 6 ? drama.Title[..6] : drama.Title;
        CenterWatermark.Text = shortTitle.TrimEnd('，', '：', '、', '·', ' ');
        TagsTextBlock.Text = drama.TagsSummary;
        TitleTextBlock.Text = drama.Title;

        SyncCoverImage(drama);
    }

    private void SyncCoverImage(DramaItem drama)
    {
        if (CoverImage is null) return;

        var url = drama.CoverUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            CoverImage.Source = null;
            CoverImage.IsVisible = false;
            CenterWatermark.IsVisible = BlurRadius <= 0;
            return;
        }

        if (CoverImageCache.TryGetCached(url, out var cached) && cached is not null)
        {
            CoverImage.Source = cached;
            CoverImage.IsVisible = true;
            CenterWatermark.IsVisible = false;
            return;
        }

        CoverImage.Source = null;
        CoverImage.IsVisible = false;
        CenterWatermark.IsVisible = BlurRadius <= 0;
        _ = LoadCoverImageAsync(drama.SeriesId, url);
    }

    private async Task LoadCoverImageAsync(string seriesId, string url)
    {
        var bmp = await CoverImageCache.LoadAsync(url);
        if (bmp is null) return;

        if (Drama?.SeriesId == seriesId && Drama?.CoverUrl == url)
        {
            CoverImage.Source = bmp;
            CoverImage.IsVisible = true;
            CenterWatermark.IsVisible = false;
        }
    }
}
