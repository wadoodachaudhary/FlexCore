using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fx.ControlKit.Llm.Configuration;

/// <summary>
/// Per-user <see cref="LlmModelConfig"/> storage. A user's own entries win;
/// seeded defaults fill in for models they have not touched; models nobody
/// knows fall back to <see cref="LlmModelConfig.Unknown"/>. A null or empty
/// user id addresses the shared "anonymous" bucket.
/// </summary>
public interface IModelConfigStore
{
    /// <summary>The user's entry, else the seed entry, else null. <see cref="LlmClient"/> applies settings only when this is non-null.</summary>
    LlmModelConfig? Find(string? userId, string? modelId);

    /// <summary>Always a config: <see cref="Find"/> or <see cref="LlmModelConfig.Unknown"/>.</summary>
    LlmModelConfig Get(string? userId, string? modelId);

    /// <summary>Every config visible to the user (own entries plus unshadowed seeds), sorted by id.</summary>
    IReadOnlyList<LlmModelConfig> GetAll(string? userId);

    bool IsDisplayed(string? userId, string? modelId);

    LlmModelConfig Save(string? userId, LlmModelConfig config);

    /// <summary>Removes the user's entry; the seed (if any) shows through again.</summary>
    bool Delete(string? userId, string modelId);

    /// <summary>Replaces the user's entries with the seed table.</summary>
    void ResetToDefaults(string? userId);
}

/// <summary>Shared bookkeeping for the stores: seeds, per-user maps, sanitising and locking. Subclasses supply persistence.</summary>
public abstract class ModelConfigStoreBase : IModelConfigStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, LlmModelConfig>> _byUser = new(StringComparer.OrdinalIgnoreCase);

    protected ModelConfigStoreBase(IReadOnlyDictionary<string, LlmModelConfig>? seeds = null)
    {
        Seeds = seeds ?? ModelConfigSeeds.Default;
    }

    public IReadOnlyDictionary<string, LlmModelConfig> Seeds { get; }

    public LlmModelConfig? Find(string? userId, string? modelId)
    {
        var key = (modelId ?? string.Empty).Trim();
        if (key.Length == 0) return null;
        var map = LoadForUser(userId);
        if (map.TryGetValue(key, out var own)) return own;
        return Seeds.TryGetValue(key, out var seeded) ? seeded : null;
    }

    public LlmModelConfig Get(string? userId, string? modelId)
        => Find(userId, modelId) ?? LlmModelConfig.Unknown((modelId ?? string.Empty).Trim());

    public IReadOnlyList<LlmModelConfig> GetAll(string? userId)
    {
        var snapshot = new Dictionary<string, LlmModelConfig>(LoadForUser(userId), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Seeds)
        {
            if (!snapshot.ContainsKey(pair.Key)) snapshot[pair.Key] = pair.Value;
        }

        return snapshot.Values.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool IsDisplayed(string? userId, string? modelId) => Get(userId, modelId).Display;

    public LlmModelConfig Save(string? userId, LlmModelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Id)) throw new ArgumentException("Model id cannot be empty.", nameof(config));

        var sanitized = config.Sanitized();
        lock (_lock)
        {
            var map = LoadForUserNoLock(userId);
            map[sanitized.Id] = sanitized;
            Persist(userId, map);
        }

        return sanitized;
    }

    public bool Delete(string? userId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        lock (_lock)
        {
            var map = LoadForUserNoLock(userId);
            if (!map.Remove(modelId.Trim())) return false;
            Persist(userId, map);
            return true;
        }
    }

    public void ResetToDefaults(string? userId)
    {
        lock (_lock)
        {
            var map = Seeds.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            _byUser[UserKey(userId)] = map;
            Persist(userId, map);
        }
    }

    /// <summary>Reads the persisted entries for a user; null when nothing has been stored yet (the seeds are then written out).</summary>
    protected abstract Dictionary<string, LlmModelConfig>? Load(string? userId);

    protected abstract void Persist(string? userId, Dictionary<string, LlmModelConfig> map);

    protected static string UserKey(string? userId)
        => string.IsNullOrWhiteSpace(userId) ? "anonymous" : userId.Trim().ToLowerInvariant();

    private Dictionary<string, LlmModelConfig> LoadForUser(string? userId)
    {
        lock (_lock) return LoadForUserNoLock(userId);
    }

    private Dictionary<string, LlmModelConfig> LoadForUserNoLock(string? userId)
    {
        var key = UserKey(userId);
        if (_byUser.TryGetValue(key, out var existing)) return existing;

        var map = Load(userId);
        if (map is null)
        {
            map = Seeds.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            try
            {
                Persist(userId, map);
            }
            catch (Exception)
            {
                // In-memory state stands in until the next successful write.
            }
        }

        _byUser[key] = map;
        return map;
    }
}

/// <summary>Configs held in memory only; useful for tests and single-process tools.</summary>
public sealed class InMemoryModelConfigStore : ModelConfigStoreBase
{
    private readonly Dictionary<string, Dictionary<string, LlmModelConfig>> _persisted = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryModelConfigStore(IReadOnlyDictionary<string, LlmModelConfig>? seeds = null) : base(seeds)
    {
    }

    protected override Dictionary<string, LlmModelConfig>? Load(string? userId)
        => _persisted.TryGetValue(UserKey(userId), out var map) ? new Dictionary<string, LlmModelConfig>(map, StringComparer.OrdinalIgnoreCase) : null;

    protected override void Persist(string? userId, Dictionary<string, LlmModelConfig> map)
        => _persisted[UserKey(userId)] = new Dictionary<string, LlmModelConfig>(map, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One JSON file per user at <c>{root}/{sanitised-user-id}/llm-models.json</c>
/// — the layout GhostWriter uses, so its files are read as they are. The
/// file is an array of <see cref="LlmModelConfig"/> in camel case.
/// </summary>
public sealed class JsonFileModelConfigStore : ModelConfigStoreBase
{
    public const string FileName = "llm-models.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger _logger;

    public JsonFileModelConfigStore(string rootDirectory, IReadOnlyDictionary<string, LlmModelConfig>? seeds = null, ILogger<JsonFileModelConfigStore>? logger = null)
        : base(seeds)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("A root directory is required.", nameof(rootDirectory));
        RootDirectory = Path.GetFullPath(rootDirectory);
        _logger = logger ?? NullLogger<JsonFileModelConfigStore>.Instance;
    }

    public string RootDirectory { get; }

    public string PathFor(string? userId) => Path.Combine(RootDirectory, SanitizeUserId(userId), FileName);

    protected override Dictionary<string, LlmModelConfig>? Load(string? userId)
    {
        var path = PathFor(userId);
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var map = new Dictionary<string, LlmModelConfig>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json)) return map;

            var loaded = JsonSerializer.Deserialize<List<LlmModelConfig>>(json, JsonOptions);
            foreach (var entry in loaded ?? new List<LlmModelConfig>())
            {
                if (string.IsNullOrWhiteSpace(entry.Id)) continue;
                map[entry.Id.Trim()] = entry.Sanitized();
            }

            return map;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read model configs from {Path}; using the seeded defaults.", path);
            return Seeds.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    protected override void Persist(string? userId, Dictionary<string, LlmModelConfig> map)
    {
        var path = PathFor(userId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var ordered = map.Values.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(ordered, JsonOptions));
    }

    private static string SanitizeUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return "anonymous";
        var cleaned = Regex.Replace(userId, @"[^a-zA-Z0-9\-_]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "anonymous" : cleaned.ToLowerInvariant();
    }
}

/// <summary>The store used when none is configured: knows no model, so the client applies no per-model settings.</summary>
public sealed class NullModelConfigStore : IModelConfigStore
{
    public static readonly NullModelConfigStore Instance = new();

    public LlmModelConfig? Find(string? userId, string? modelId) => null;
    public LlmModelConfig Get(string? userId, string? modelId) => LlmModelConfig.Unknown((modelId ?? string.Empty).Trim());
    public IReadOnlyList<LlmModelConfig> GetAll(string? userId) => Array.Empty<LlmModelConfig>();
    public bool IsDisplayed(string? userId, string? modelId) => true;
    public LlmModelConfig Save(string? userId, LlmModelConfig config) => throw new NotSupportedException("No model config store is configured. Set Llm:ModelConfigDirectory or call LlmBuilder.UseJsonModelConfigStore.");
    public bool Delete(string? userId, string modelId) => false;
    public void ResetToDefaults(string? userId) { }
}

/// <summary>
/// Seed table: per-model values calibrated from the model card and the
/// chunker's constants. ChunkChars is roughly ContextTokens × 4 × 0.75; cloud
/// models get generous timeouts, local ones short.
/// </summary>
public static class ModelConfigSeeds
{
    public static readonly IReadOnlyDictionary<string, LlmModelConfig> Default = Build();

    private static Dictionary<string, LlmModelConfig> Build()
    {
        var d = new Dictionary<string, LlmModelConfig>(StringComparer.OrdinalIgnoreCase);

        void Add(string id, string display, int ctx, int chunk, int timeout, int maxOut = 0, double temp = 0.7)
            => d[id] = new LlmModelConfig(id, display, ctx, chunk, timeout, maxOut, temp, true, true);

        // Local Ollama
        Add("qwen3:32b",            "Qwen 3 32B",            32_000, 24_000, 240);
        Add("qwen3:8b",             "Qwen 3 8B",              8_000,  6_000,  60);
        Add("qwen2.5-coder:32b",    "Qwen 2.5 Coder 32B",    32_000, 24_000, 240);
        Add("deepseek-r1:32b",      "DeepSeek R1 32B",       32_000, 24_000, 300);
        Add("deepseek-r1:14b",      "DeepSeek R1 14B",       32_000, 24_000, 240);
        Add("mistral:7b",           "Mistral 7B",             8_000,  6_000,  60);
        Add("mistral:latest",       "Mistral",                8_000,  6_000,  60);
        Add("mistral-nemo:latest",  "Mistral Nemo",          32_000, 24_000, 120);
        Add("wizardlm2:7b",         "WizardLM 2 7B",          8_000,  6_000,  60);
        Add("wizardlm2:8x22b",      "WizardLM 2 8x22B",      64_000, 48_000, 600);
        Add("phi3:medium",          "Phi 3 Medium",          16_000, 12_000, 120);
        Add("phi3:mini",            "Phi 3 Mini",             8_000,  6_000,  60);
        Add("phi4:latest",          "Phi 4",                 32_000, 24_000, 120);
        Add("llama3.1:8b",          "Llama 3.1 8B",          64_000, 48_000, 120);
        Add("llama3.1:70b",         "Llama 3.1 70B",         64_000, 48_000, 600);
        Add("gemma:latest",         "Gemma",                 32_000, 24_000, 120);
        Add("gemma3:latest",        "Gemma 3",               32_000, 24_000, 120);
        Add("gemma4:e2b",           "Gemma 4 Efficient 2B",  32_000, 24_000,  60);
        Add("gemma4:e4b",           "Gemma 4 Efficient 4B",  32_000, 24_000,  60);
        Add("gemma4:26b",           "Gemma 4 26B",           64_000, 48_000, 180);
        Add("gemma4:31b",           "Gemma 4 31B",           64_000, 48_000, 240);
        Add("gemma4:31b-cloud",     "Gemma 4 31B (Cloud)",  128_000, 96_000, 240);
        Add("gemma4:latest",        "Gemma 4",               32_000, 24_000,  60);
        Add("wizard-vicuna-uncensored:13b", "Wizard Vicuna Uncensored 13B", 8_000, 6_000, 90);

        // Cloud-proxied through the local daemon
        Add("deepseek-v4-pro:cloud", "DeepSeek V4 Pro (Cloud)", 128_000, 96_000, 240);

        // Anthropic
        Add("claude-sonnet-4-6",   "Claude Sonnet 4.6",      200_000, 150_000, 180);
        Add("claude-sonnet-4-5",   "Claude Sonnet 4.5",      200_000, 150_000, 180);
        Add("claude-opus-4-7",     "Claude Opus 4.7",        200_000, 150_000, 240);
        Add("claude-opus-4-1",     "Claude Opus 4.1",        200_000, 150_000, 240);
        Add("claude-haiku-4-5",    "Claude Haiku 4.5",       200_000, 150_000, 120);
        Add("claude-haiku-4-1",    "Claude Haiku 4.1",       200_000, 150_000, 120);

        // OpenAI
        Add("gpt-5.4",              "GPT-5.4",                256_000, 200_000, 240);
        Add("gpt-5",                "GPT-5",                  256_000, 200_000, 240);
        Add("gpt-4.1",              "GPT-4.1",                256_000, 200_000, 180);
        Add("gpt-4.1-mini",         "GPT-4.1 mini",           256_000, 200_000, 120);
        Add("o4-mini",              "o4-mini",                128_000, 96_000,  240);

        // Google Gemini
        Add("gemini-2.5-pro",       "Gemini 2.5 Pro",         512_000, 400_000, 240);
        Add("gemini-2.5-flash",     "Gemini 2.5 Flash",       512_000, 400_000, 120);

        // xAI
        Add("grok-4",               "Grok 4",                 256_000, 200_000, 240);

        // Groq-hosted
        Add("llama-3.3-70b-versatile",  "Llama 3.3 70B (Groq)", 128_000, 96_000, 60);
        Add("openai/gpt-oss-120b",      "GPT-OSS 120B (Groq)",  128_000, 96_000, 60);
        Add("openai/gpt-oss-20b",       "GPT-OSS 20B (Groq)",   128_000, 96_000, 60);

        // Hugging Face router
        Add("Qwen/Qwen3-235B",                 "Qwen 3 235B (HF)",   128_000, 96_000, 240);
        Add("Qwen/Qwen3-32B:nscale",           "Qwen 3 32B (HF)",    128_000, 96_000, 240);
        Add("alpindale/wizardlm-2-8x22b",      "WizardLM 2 8x22B (HF)", 64_000, 48_000, 240);
        Add("moonshotai/kimi-k2.6",            "Kimi K2.6 (HF)",      64_000, 48_000, 180);

        return d;
    }
}
