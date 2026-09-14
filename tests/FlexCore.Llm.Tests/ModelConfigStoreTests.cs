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
    public void An_unreadable_file_serves_seeds_refuses_writes_and_is_replaced_only_by_reset()
    {
        var directory = Path.Combine(_root, "anonymous");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, JsonFileModelConfigStore.FileName);
        File.WriteAllText(path, "[{\"id\":\"qwen3:32b\",\"timeoutSeconds\":30}");   // truncated: not JSON
        var store = new JsonFileModelConfigStore(_root);

        // Reads degrade to the seeds instead of failing the LLM call that asked.
        Assert.Equal(240, store.Get(null, "qwen3:32b").TimeoutSeconds);
        Assert.Contains(store.GetAll(null), c => c.Id == "gpt-5.4");

        // A write must not replace what could not be read.
        Assert.ThrowsAny<Exception>(() => store.Save(null, new LlmModelConfig("qwen3:32b", TimeoutSeconds: 5)));
        Assert.ThrowsAny<Exception>(() => store.Delete(null, "qwen3:32b"));
        Assert.Equal("[{\"id\":\"qwen3:32b\",\"timeoutSeconds\":30}", File.ReadAllText(path));

        // Once the file is readable again the same store picks it up (nothing stale was cached).
        File.WriteAllText(path, "[{\"id\":\"qwen3:32b\",\"timeoutSeconds\":30}]");
        Assert.Equal(30, store.Get(null, "qwen3:32b").TimeoutSeconds);
        store.Save(null, new LlmModelConfig("qwen3:32b", TimeoutSeconds: 5));
        Assert.Equal(5, new JsonFileModelConfigStore(_root).Get(null, "qwen3:32b").TimeoutSeconds);

        // Reset is the explicit way past unreadable data.
        File.WriteAllText(path, "garbage");
        var broken = new JsonFileModelConfigStore(_root);
        Assert.ThrowsAny<Exception>(() => broken.Save(null, new LlmModelConfig("x")));
        broken.ResetToDefaults(null);
        Assert.Equal(240, broken.Get(null, "qwen3:32b").TimeoutSeconds);
        broken.Save(null, new LlmModelConfig("x", TimeoutSeconds: 9));
        Assert.Equal(9, new JsonFileModelConfigStore(_root).Find(null, "x")!.TimeoutSeconds);
    }

    [Fact]
    public async Task Concurrent_finds_and_saves_do_not_corrupt_the_map()
    {
        var store = new InMemoryModelConfigStore(new Dictionary<string, LlmModelConfig>());
        using var cts = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var i = 0; !cts.IsCancellationRequested; i++)
            {
                store.Save("u", new LlmModelConfig($"model-{i % 50}", TimeoutSeconds: i + 1));
                if (i % 7 == 0) store.Delete("u", $"model-{(i + 3) % 50}");
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                store.Find("u", $"model-{i % 50}");
                if (i % 100 == 0) store.GetAll("u");
            }
        })).ToArray();

        await Task.WhenAll(readers);
        cts.Cancel();
        await writer;
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
