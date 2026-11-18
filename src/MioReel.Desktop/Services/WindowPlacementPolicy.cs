using Avalonia;
using MioReel.Core.Models;

namespace MioReel.Desktop.Services;

public static class WindowPlacementPolicy
{
    public const double MiniMinWidth = 288;
    public const double MiniMinHeight = 160;
    public const double NormalMinWidth = 340;
    public const double NormalMinHeight = 280;

    public const int EdgeLeft = 1;
    public const int EdgeRight = 2;
    public const int EdgeTop = 3;
    public const int EdgeTopLeft = 4;
    public const int EdgeTopRight = 5;
    public const int EdgeBottom = 6;
    public const int EdgeBottomLeft = 7;
    public const int EdgeBottomRight = 8;

    public static WindowPlacement Fit(WindowPlacement placement, PixelRect workArea, double scale, bool mini)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var availableWidth = workArea.Width / scale;
        var availableHeight = workArea.Height / scale;
        var width = double.IsFinite(placement.Width) && placement.Width > 0 ? placement.Width : mini ? 360 : 1260;
        var height = double.IsFinite(placement.Height) && placement.Height > 0 ? placement.Height : mini ? 640 : 780;
        width = Math.Min(availableWidth, Math.Max(mini ? MiniMinWidth : 680, width));
        height = Math.Min(availableHeight, Math.Max(mini ? MiniMinHeight : 520, height));
        return ClampPosition(placement with { Width = width, Height = height }, workArea, scale);
    }

    public static double GetVideoAspectRatio(int streamWidth, int streamHeight)
    {
        var w = Math.Max(1, streamWidth);
        var h = Math.Max(1, streamHeight);
        return Math.Clamp((double)w / h, 0.25, 4.0);
    }

    public static (double MinWidth, double MinHeight, double MaxWidth, double MaxHeight) GetMiniSizeBounds(
        PixelRect workArea,
        double scale,
        double aspectRatio)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        aspectRatio = Math.Clamp(double.IsFinite(aspectRatio) && aspectRatio > 0 ? aspectRatio : 16.0 / 9.0, 0.25, 4.0);

        var availW = Math.Max(120, workArea.Width / scale);
        var availH = Math.Max(120, workArea.Height / scale);

        var maxW = availW;
        var maxH = maxW / aspectRatio;
        if (maxH > availH)
        {
            maxH = availH;
            maxW = maxH * aspectRatio;
        }

        var minW = Math.Max(MiniMinWidth, MiniMinHeight * aspectRatio);
        var minH = minW / aspectRatio;
        if (minW > maxW)
        {
            minW = maxW;
            minH = minW / aspectRatio;
        }
        if (minH > maxH)
        {
            minH = maxH;
            minW = minH * aspectRatio;
        }

        return (Math.Round(minW), Math.Round(minH), Math.Floor(maxW), Math.Floor(maxH));
    }

    public static WindowPlacement FitMiniForAspect(
        WindowPlacement? saved,
        PixelPoint fallbackPosition,
        Point? anchorCenter,
        PixelRect workArea,
        double scale,
        int streamWidth,
        int streamHeight)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var ratio = GetVideoAspectRatio(streamWidth, streamHeight);
        var (minW, _, maxW, maxH) = GetMiniSizeBounds(workArea, scale, ratio);

        var targetArea = saved is { Width: > 0, Height: > 0 } && double.IsFinite(saved.Width) && double.IsFinite(saved.Height)
            ? saved.Width * saved.Height
            : ratio >= 1.0 ? 544.0 * 306.0 : 360.0 * 640.0;

        var rawW = Math.Sqrt(targetArea * ratio);
        var width = Math.Round(Math.Clamp(rawW, minW, Math.Max(minW, maxW)));
        var height = Math.Round(width / ratio);
        if (height > maxH && maxH > 0)
        {
            height = Math.Floor(maxH);
            width = Math.Round(height * ratio);
        }

        int x;
        int y;
        if (anchorCenter is { } center)
        {
            x = (int)Math.Round(center.X - (width * scale) / 2.0);
            y = (int)Math.Round(center.Y - (height * scale) / 2.0);
        }
        else if (saved is not null)
        {
            x = saved.X;
            y = saved.Y;
        }
        else
        {
            x = fallbackPosition.X;
            y = fallbackPosition.Y;
        }

        return ClampPosition(new WindowPlacement(x, y, width, height), workArea, scale);
    }

    public static WindowPlacement ScaleMiniPlacement(
        WindowPlacement current,
        double zoomFactor,
        PixelRect workArea,
        double scale,
        int streamWidth,
        int streamHeight)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        if (!double.IsFinite(zoomFactor) || zoomFactor <= 0) zoomFactor = 1;

        var ratio = GetVideoAspectRatio(streamWidth, streamHeight);
        var (minW, _, maxW, maxH) = GetMiniSizeBounds(workArea, scale, ratio);

        var center = new Point(
            current.X + (current.Width * scale) / 2.0,
            current.Y + (current.Height * scale) / 2.0);

        var curW = double.IsFinite(current.Width) && current.Width > 0 ? current.Width : 360;
        var width = Math.Round(Math.Clamp(curW * zoomFactor, minW, Math.Max(minW, maxW)));
        var height = Math.Round(width / ratio);
        if (height > maxH && maxH > 0)
        {
            height = Math.Floor(maxH);
            width = Math.Round(height * ratio);
        }

        var x = (int)Math.Round(center.X - (width * scale) / 2.0);
        var y = (int)Math.Round(center.Y - (height * scale) / 2.0);
        return ClampPosition(new WindowPlacement(x, y, width, height), workArea, scale);
    }

    public static (int ClientWidth, int ClientHeight) SnapMiniDimensions(
        int proposedClientWidth,
        int proposedClientHeight,
        int edge,
        int minClientWidth,
        int minClientHeight,
        int maxClientWidth,
        int maxClientHeight,
        double aspectRatio)
    {
        aspectRatio = Math.Clamp(double.IsFinite(aspectRatio) && aspectRatio > 0 ? aspectRatio : 16.0 / 9.0, 0.25, 4.0);
        maxClientWidth = Math.Max(minClientWidth, maxClientWidth);
        maxClientHeight = Math.Max(minClientHeight, maxClientHeight);

        int cw;
        int ch;
        if (edge is EdgeLeft or EdgeRight)
        {
            cw = Math.Clamp(proposedClientWidth, minClientWidth, maxClientWidth);
            ch = (int)Math.Round(cw / aspectRatio);
            if (ch < minClientHeight)
            {
                ch = minClientHeight;
                cw = (int)Math.Round(ch * aspectRatio);
            }
            else if (ch > maxClientHeight)
            {
                ch = maxClientHeight;
                cw = (int)Math.Round(ch * aspectRatio);
            }
        }
        else if (edge is EdgeTop or EdgeBottom)
        {
            ch = Math.Clamp(proposedClientHeight, minClientHeight, maxClientHeight);
            cw = (int)Math.Round(ch * aspectRatio);
            if (cw < minClientWidth)
            {
                cw = minClientWidth;
                ch = (int)Math.Round(cw / aspectRatio);
            }
            else if (cw > maxClientWidth)
            {
                cw = maxClientWidth;
                ch = (int)Math.Round(cw / aspectRatio);
            }
        }
        else
        {
            // Stateless orthogonal projection onto the aspect-ratio line prevents frame-to-frame oscillation on corner drags.
            var projectedH = (aspectRatio * proposedClientWidth + proposedClientHeight) / (aspectRatio * aspectRatio + 1.0);
            ch = Math.Clamp((int)Math.Round(projectedH), minClientHeight, maxClientHeight);
            cw = (int)Math.Round(ch * aspectRatio);
            if (cw < minClientWidth)
            {
                cw = minClientWidth;
                ch = (int)Math.Round(cw / aspectRatio);
            }
            else if (cw > maxClientWidth)
            {
                cw = maxClientWidth;
                ch = (int)Math.Round(cw / aspectRatio);
            }
        }

        return (Math.Max(1, cw), Math.Max(1, ch));
    }

    private static WindowPlacement ClampPosition(WindowPlacement placement, PixelRect workArea, double scale) =>
        placement with
        {
            X = Math.Clamp(placement.X, workArea.X, Math.Max(workArea.X, workArea.Right - (int)Math.Ceiling(placement.Width * scale))),
            Y = Math.Clamp(placement.Y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - (int)Math.Ceiling(placement.Height * scale)))
        };
}
