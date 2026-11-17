namespace MioReel.Desktop.Services;

public static class CatalogPrefetchPolicy
{
    public static bool ShouldPrefetch(double extentHeight, double offsetY, double viewportHeight) =>
        viewportHeight > 0 && extentHeight - offsetY - viewportHeight <= Math.Max(1000, viewportHeight * 2);
}

/// <summary>Layout changes may recheck the threshold, but only user input may grant another load.</summary>
public sealed class CatalogPrefetchGate
{
    private bool _hasRequest = true;

    public void RequestLoad() => _hasRequest = true;
    public void Reset() => _hasRequest = true;
    public void ConsumeRequest() => _hasRequest = false;

    public bool TryStart(double extentHeight, double offsetY, double viewportHeight)
    {
        if (!_hasRequest || !CatalogPrefetchPolicy.ShouldPrefetch(extentHeight, offsetY, viewportHeight)) return false;
        _hasRequest = false;
        return true;
    }
}
