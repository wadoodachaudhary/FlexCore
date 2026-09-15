namespace Fx.ControlKit.Llm;

/// <summary>
/// A model as listed by a provider or by the built-in <see cref="ModelCatalog"/>.
/// Token limits and capabilities are null when the source does not report them.
/// </summary>
public sealed record ModelInfo(string Provider, string Id)
{
    public string? DisplayName { get; init; }
    public int? ContextTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public LlmCapabilities? Capabilities { get; init; }
    public DateTimeOffset? Created { get; init; }
    public string? OwnedBy { get; init; }
    /// <summary>Provider-specific extras (Ollama family/parameter size, Gemini limits) kept as strings.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public ModelRef Ref => new(Provider, Id);
    public override string ToString() => Ref.ToString();
}

public sealed record ImageRequest
{
    public required ModelRef Model { get; init; }
    public required string Prompt { get; init; }
    public int Count { get; init; } = 1;
    /// <summary>"1024x1024", "1536x1024", "1024x1536" or "auto".</summary>
    public string Size { get; init; } = "1024x1024";
    /// <summary>"low", "medium", "high" or "auto" where the provider supports it.</summary>
    public string? Quality { get; init; }
    /// <summary>"png", "jpeg" or "webp".</summary>
    public string OutputFormat { get; init; } = "png";
    public TimeSpan? Timeout { get; init; }
    public IReadOnlyDictionary<string, object?>? Extras { get; init; }
}

public sealed record GeneratedImage(ReadOnlyMemory<byte> Bytes, string MimeType, string? RevisedPrompt = null)
{
    public string ToBase64() => Convert.ToBase64String(Bytes.Span);
}

public sealed record ImageResult(
    IReadOnlyList<GeneratedImage> Images,
    ModelRef Resolved,
    LlmUsage? Usage,
    TimeSpan Elapsed,
    string? RawJson)
{
    public GeneratedImage First => Images[0];
}

public sealed record EmbeddingRequest
{
    public required ModelRef Model { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    /// <summary>Requested vector length where the provider supports shortening (OpenAI text-embedding-3, Gemini).</summary>
    public int? Dimensions { get; init; }
    public TimeSpan? Timeout { get; init; }
    public IReadOnlyDictionary<string, object?>? Extras { get; init; }

    public static EmbeddingRequest For(ModelRef model, params string[] inputs) => new() { Model = model, Inputs = inputs };
}

public sealed record EmbeddingResult(
    IReadOnlyList<float[]> Vectors,
    ModelRef Resolved,
    LlmUsage? Usage,
    TimeSpan Elapsed,
    string? RawJson)
{
    public int Dimensions => Vectors.Count == 0 ? 0 : Vectors[0].Length;
}
