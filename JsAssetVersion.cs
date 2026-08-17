namespace Fx.ControlKit;

/// <summary>Per-build cache-buster for dynamically imported JS modules. The
/// module URLs are not fingerprinted by the asset pipeline, so without a
/// version query a browser can keep executing a STALE module forever while
/// the fingerprinted CSS updates around it (fresh look, old behavior).</summary>
internal static class FxJsAsset
{
    internal static readonly string Version = ComputeVersion();

    private static string ComputeVersion()
    {
        try
        {
            var asm = typeof(FxJsAsset).Assembly;
            var location = asm.Location;
            return string.IsNullOrEmpty(location)
                ? asm.GetName().Version?.ToString() ?? "1"
                : System.IO.File.GetLastWriteTimeUtc(location).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return "1";
        }
    }

    internal static string Versioned(string path) => $"{path}?v={Version}";
}
