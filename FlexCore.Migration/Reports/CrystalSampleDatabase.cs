namespace Fx.ControlKit.Reports;

/// <summary>
/// Locates the Crystal sample pack shipped with FlexCore.
/// The pack is keyed by report binary and schema fingerprint. Callers open
/// <see cref="Path"/> themselves; this type does not query the database.
/// </summary>
public static class CrystalSampleDatabase
{
    /// <summary>File name of the shipped corpus. A per-translation session pack uses a different name.</summary>
    public const string FileName = "CrystalSamples.db";

    /// <summary>Path of the pack relative to the application base directory, using the project layout.</summary>
    public const string RelativePath = "Reports/CrystalSamples/CrystalSamples.db";

    /// <summary>Absolute path of the pack copied next to the running application.</summary>
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Reports", "CrystalSamples", FileName);
}
