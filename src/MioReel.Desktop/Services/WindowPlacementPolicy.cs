using Avalonia;
using MioReel.Core.Models;

namespace MioReel.Desktop.Services;

public static class WindowPlacementPolicy
{
    public static WindowPlacement Fit(WindowPlacement placement, PixelRect workArea, double scale, bool mini)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var availableWidth = workArea.Width / scale;
        var availableHeight = workArea.Height / scale;
        var width = double.IsFinite(placement.Width) && placement.Width > 0 ? placement.Width : mini ? 360 : 1260;
        var height = double.IsFinite(placement.Height) && placement.Height > 0 ? placement.Height : mini ? 640 : 780;
        width = Math.Min(availableWidth, Math.Max(mini ? 340 : 680, width));
        height = Math.Min(availableHeight, Math.Max(mini ? 280 : 520, height));
        return placement with
        {
            X = Math.Clamp(placement.X, workArea.X, Math.Max(workArea.X, workArea.Right - (int)Math.Ceiling(width * scale))),
            Y = Math.Clamp(placement.Y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - (int)Math.Ceiling(height * scale))),
            Width = width, Height = height
        };
    }
}
