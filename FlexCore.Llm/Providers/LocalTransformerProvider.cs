using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Fx.ControlKit.Llm.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fx.ControlKit.Llm.Providers;

/// <summary>
/// In-process transformer provider running local LLMs without Ollama, Python, or native daemons.
/// Executes Safetensors decoder-only models directly in pure C# .NET.
/// </summary>
public sealed class LocalTransformerProvider : ILlmProvider
{
    private static readonly string[] SearchDirectories = new[]
    {
        "/Users/wadood/DotnetLLM/weights",
        Path.Combine(AppContext.BaseDirectory, "weights"),
        Path.Combine(Directory.GetCurrentDirectory(), "weights"),
        Path.Combine(AppContext.BaseDirectory, "App_Data", "weights")
    };

    private readonly LocalTokenizer _tokenizer = new();
    private readonly ConcurrentDictionary<string, GPTModel> _loadedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    public string Key => ProviderKeys.Local;
    public IReadOnlyList<string> Aliases => new[] { "transformer", "inprocess", "native", "builtin", "flexkit", "flexkitllm" };
    public LlmCapabilities Capabilities => LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.ListModels;
    public bool IsConfigured => true;

    public LocalTransformerProvider(ILogger<LocalTransformerProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<LocalTransformerProvider>.Instance;
    }

    public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();
        var model = GetOrLoadModel(request.Model.Model);
        string prompt = BuildPromptText(request.Messages, request.System);
        var inputTokens = _tokenizer.Encode(prompt);

        int maxNewTokens = request.MaxOutputTokens ?? 256;
        double temperature = request.Temperature ?? 0.7;

        var fullSequence = GptGenerator.Generate(
            model,
            inputTokens,
            maxNewTokens: maxNewTokens,
            temperature: temperature,
            topK: 40,
            eosTokenId: LocalTokenizer.EndOfTextTokenId,
            cancellationToken: cancellationToken);

        int generatedCount = Math.Max(0, fullSequence.Count - inputTokens.Count);
        var generatedTokens = fullSequence.Skip(inputTokens.Count);
        string outputText = _tokenizer.Decode(generatedTokens).Trim();
        sw.Stop();

        var usage = new LlmUsage(inputTokens.Count, generatedCount);
        var result = new ChatResult(
            outputText,
            request.Model,
            usage,
            FinishReasons.Stop,
            null,
            sw.Elapsed,
            null);

        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = GetOrLoadModel(request.Model.Model);
        string prompt = BuildPromptText(request.Messages, request.System);
        var inputTokens = _tokenizer.Encode(prompt);

        int maxNewTokens = request.MaxOutputTokens ?? 256;
        double temperature = request.Temperature ?? 0.7;
        int generatedCount = 0;

        await foreach (var tokenId in GptGenerator.GenerateStreamAsync(
            model,
            inputTokens,
            maxNewTokens: maxNewTokens,
            temperature: temperature,
            topK: 40,
            eosTokenId: LocalTokenizer.EndOfTextTokenId,
            cancellationToken: cancellationToken))
        {
            generatedCount++;
            string piece = _tokenizer.Decode(new[] { tokenId });
            yield return ChatDelta.Text(piece);
        }

        yield return ChatDelta.Final(
            new LlmUsage(inputTokens.Count, generatedCount),
            FinishReasons.Stop,
            null,
            request.Model);
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = new List<ModelInfo>
        {
            new(Key, "local-gpt2-small") { DisplayName = "Local In-Process GPT-2 Small (124M)", ContextTokens = 1024, MaxOutputTokens = 512, Capabilities = Capabilities },
            new(Key, "local-gpt2-medium") { DisplayName = "Local In-Process GPT-2 Medium (355M)", ContextTokens = 1024, MaxOutputTokens = 512, Capabilities = Capabilities },
            new(Key, "smalltestmodel") { DisplayName = "Local In-Process Small Test Model", ContextTokens = 256, MaxOutputTokens = 256, Capabilities = Capabilities }
        };

        // Scan disk for discovered safetensors
        foreach (var dir in SearchDirectories)
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.safetensors"))
                    {
                        string id = Path.GetFileNameWithoutExtension(file);
                        if (!models.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
                        {
                            models.Add(new ModelInfo(Key, id)
                            {
                                DisplayName = $"Local Safetensors: {id}",
                                ContextTokens = 1024,
                                MaxOutputTokens = 512,
                                Capabilities = Capabilities
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not list safetensors in {Directory}", dir);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ModelInfo>>(models);
    }

    public Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("Local transformer provider does not generate images.");

    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("Local transformer provider embedding endpoint is not implemented.");

    private GPTModel GetOrLoadModel(string? modelName)
    {
        string name = string.IsNullOrWhiteSpace(modelName) ? "local-gpt2-small" : modelName;

        return _loadedModels.GetOrAdd(name, key =>
        {
            var config = key.Contains("medium", StringComparison.OrdinalIgnoreCase)
                ? GptConfig.Gpt2Medium
                : (key.Contains("smalltest", StringComparison.OrdinalIgnoreCase) ? GptConfig.SmallTest : GptConfig.Gpt2Small);

            var model = new GPTModel(config);

            string? err = null;
            string? foundPath = FindSafetensorsPath(key);
            if (foundPath != null && SafetensorsLoader.TryLoad(model, foundPath, out err))
            {
                _logger.LogInformation("Loaded local model weights for {Name} from {Path}", key, foundPath);
                return model;
            }

            if (err != null)
            {
                _logger.LogWarning("Failed loading weights for {Name} ({Error}); initializing synthetic weights for testing.", key, err);
            }
            else
            {
                _logger.LogInformation("No weights file found on disk for {Name}; initializing synthetic weights for testing.", key);
            }

            model.InitializeSyntheticWeights();
            return model;
        });
    }

    private static string? FindSafetensorsPath(string modelName)
    {
        string normReq = NormalizeName(modelName);

        foreach (var dir in SearchDirectories)
        {
            if (Directory.Exists(dir))
            {
                string exact = Path.Combine(dir, $"{modelName}.safetensors");
                if (File.Exists(exact)) return exact;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.safetensors"))
                    {
                        string baseName = Path.GetFileNameWithoutExtension(file);
                        string normBase = NormalizeName(baseName);

                        if (normBase == normReq ||
                            normBase.Contains(normReq, StringComparison.OrdinalIgnoreCase) ||
                            normReq.Contains(normBase, StringComparison.OrdinalIgnoreCase))
                        {
                            return file;
                        }
                    }
                }
                catch { }
            }
        }
        return null;
    }

    private static string NormalizeName(string name)
        => name.ToLowerInvariant()
               .Replace("local-", "")
               .Replace("local_", "")
               .Replace("-", "")
               .Replace("_", "");

    private static string BuildPromptText(IReadOnlyList<ChatMessage> messages, string? system)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(system))
        {
            sb.AppendLine($"[System: {system}]");
        }

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System)
            {
                sb.AppendLine($"[System: {msg.Text}]");
            }
            else if (msg.Role == ChatRole.User)
            {
                sb.AppendLine($"User: {msg.Text}");
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                sb.AppendLine($"Assistant: {msg.Text}");
            }
        }
        sb.Append("Assistant: ");
        return sb.ToString();
    }
}
