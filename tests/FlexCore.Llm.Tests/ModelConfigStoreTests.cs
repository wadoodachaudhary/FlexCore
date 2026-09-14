using System.Text.Json;
using Fx.ControlKit.Llm.Configuration;
using Xunit;

namespace FlexCore.Llm.Tests;

public class ModelConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flexcore-llm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Seeds_are_written_on_first_use_and_user_edits_win_over_seeds()
    {
        var store = new JsonFileModelConfigStore(_root);

        var seeded = store.Get("Alice Smith", "qwen3:32b");
        Assert.Equal("Qwen 3 32B", seeded.DisplayName);
        Assert.Equal(240, seeded.TimeoutSeconds);
        Assert.True(File.Exists(Path.Combine(_root, "alice-smith", JsonFileModelConfigStore.FileName)));

        store.Save("Alice Smith", seeded with { TimeoutSeconds = 30, DefaultTemperature = 5, ChunkChars = -1 });
        var reloaded = new JsonFileModelConfigStore(_root).Find("alice smith", "qwen3:32b");
        Assert.NotNull(reloaded);
        Assert.Equal(30, reloaded!.TimeoutSeconds);
        Assert.Equal(2, reloaded.DefaultTemperature);
        Assert.Equal(LlmModelConfig.DefaultChunkChars, reloaded.ChunkChars);

        Assert.Null(store.Find("alice smith", "nobody-knows-this"));
        Assert.Equal(24_000, store.Get("alice smith", "nobody-knows-this").ContextTokens);
        Assert.True(store.Delete("alice smith", "qwen3:32b"));
        Assert.Equal(240, store.Get("alice smith", "qwen3:32b").TimeoutSeconds);
        Assert.False(store.Delete("alice smith", "qwen3:32b"));
    }

    [Fact]
    public void Reads_ghostwriter_shaped_files_and_display_toggle()
    {
        var directory = Path.Combine(_root, "anonymous");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, JsonFileModelConfigStore.FileName), """
            [
              {"id":"claude-sonnet-4-6","displayName":"Sonnet","contextTokens":200000,"chunkChars":150000,"timeoutSeconds":180,"maxOutputTokens":0,"defaultTemperature":0.7,"chunkOnlyWhenTooLarge":true,"display":false},
              {"id":"cloud-ollama:deepseek-v4-pro:cloud","chunkChars":96000,"timeoutSeconds":240}
            ]
            """);
        var store = new JsonFileModelConfigStore(_root);

        Assert.False(store.IsDisplayed(null, "claude-sonnet-4-6"));
        Assert.True(store.IsDisplayed("", "gpt-5.4"));
        Assert.Equal(240, store.Find(null, "cloud-ollama:deepseek-v4-pro:cloud")!.TimeoutSeconds);
        var all = store.GetAll(null);
        Assert.Contains(all, c => c.Id == "claude-sonnet-4-6" && c.DisplayName == "Sonnet");
        Assert.Contains(all, c => c.Id == "gpt-5.4");
        Assert.Equal(all.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Select(c => c.Id), all.Select(c => c.Id));

        store.ResetToDefaults(null);
        Assert.True(store.IsDisplayed(null, "claude-sonnet-4-6"));
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, JsonFileModelConfigStore.FileName)));
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.True(json.RootElement[0].TryGetProperty("displayName", out _), "camel-case property names");
    }

    [Fact]
    public void In_memory_store_and_null_store()
    {
        var memory = new InMemoryModelConfigStore(new Dictionary<string, LlmModelConfig>());
        Assert.Null(memory.Find("u", "x"));
        memory.Save("u", new LlmModelConfig("x", MaxOutputTokens: 10));
        Assert.Equal(10, memory.Find("u", "x")!.MaxOutputTokens);
        Assert.Null(memory.Find("other", "x"));
        Assert.Throws<ArgumentException>(() => memory.Save("u", new LlmModelConfig(" ")));

        Assert.Null(NullModelConfigStore.Instance.Find("u", "qwen3:32b"));
        Assert.Empty(NullModelConfigStore.Instance.GetAll("u"));
        Assert.Throws<NotSupportedException>(() => NullModelConfigStore.Instance.Save("u", new LlmModelConfig("x")));
    }
}
