using System.Text.Json;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Generic file-backed implementation of <see cref="IGridSettingsStore"/> —
/// stores every grid's settings in a single JSON file as a flat dictionary
/// keyed by the grid's <see cref="GridControl{T}.PersistenceKey"/>.
///
/// <para>Default for any FlexCore consumer that doesn't have a database
/// to persist into. Apps with their own grid-layout table (HomeFront /
/// HomeFrontPOC use <c>dbo.AppGridLayout</c>) should register a project-
/// specific implementation instead.</para>
///
/// <para>Pass the file path via the constructor — the file is created on
/// first save and read on every load. Concurrent writes are guarded with
/// a per-instance lock; cross-process safety isn't a concern for the
/// per-circuit lifetime this store typically registers under.</para>
/// </summary>
public sealed class JsonFileGridSettingsStore : IGridSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public JsonFileGridSettingsStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath must be non-empty.", nameof(filePath));
        _filePath = filePath;
    }

    public Task<GridSettings?> LoadAsync(string key)
    {
        if (string.IsNullOrEmpty(key)) return Task.FromResult<GridSettings?>(null);
        var all = ReadAll();
        return Task.FromResult(all.TryGetValue(key, out var s) ? s : null);
    }

    public Task SaveAsync(string key, GridSettings settings)
    {
        if (string.IsNullOrEmpty(key)) return Task.CompletedTask;
        lock (_lock)
        {
            var all = ReadAll();
            all[key] = settings;
            WriteAll(all);
        }
        return Task.CompletedTask;
    }

    private Dictionary<string, GridSettings> ReadAll()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, GridSettings>(StringComparer.Ordinal);
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, GridSettings>(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, GridSettings>>(json, JsonOpts)
                   ?? new Dictionary<string, GridSettings>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Corrupt file? Don't crash — start fresh, the next save will
            // rewrite a valid one.
            return new Dictionary<string, GridSettings>(StringComparer.Ordinal);
        }
    }

    private void WriteAll(Dictionary<string, GridSettings> all)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(all, JsonOpts));
    }
}
