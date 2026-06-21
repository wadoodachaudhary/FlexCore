using System.Text.Json;

namespace Fx.ControlKit.Grid;

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
