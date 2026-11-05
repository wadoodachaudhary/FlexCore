using System.Runtime.CompilerServices;
using Fx.ControlKit.Llm;

namespace FlexCore.ShellSample;

/// <summary>
/// Stand-in model for the sample host. It exercises the real code-tool service
/// without an API key. A configured Llm provider replaces it.
/// </summary>
public sealed class SampleLlmClient : ILlmClient
{
    private readonly SampleProvider _provider = new();
    public IReadOnlyList<ILlmProvider> Providers => [_provider];
    public ILlmProvider Resolve(string providerKey) => _provider;
    public ILlmProvider Resolve(ModelRef model) => _provider;
    public bool TryResolve(string providerKey, out ILlmProvider? provider)
    {
        provider = _provider;
        return true;
    }

    public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Reply(request));

    public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = Reply(request).Text;
        for (var i = 0; i < text.Length; i += 24)
            yield return ChatDelta.Text(text.Substring(i, Math.Min(24, text.Length - i)));
        yield return ChatDelta.Final(null, FinishReasons.Stop, resolved: request.Model);
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string providerKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo("openai", "sample")]);

    public Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ProviderProbe> ProbeAsync(string providerKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderProbe(providerKey, ProbeStatus.Ok, true, TimeSpan.Zero, Message: "Sample model."));

    private static ChatResult Reply(ChatRequest request)
    {
        var system = request.System ?? "";
        var text = system.Contains("Tool: Explainer", StringComparison.Ordinal)
            ? """
              ## What this method does

              `Score` checks every contiguous subarray and returns the largest sum.

              - The outer loops choose a start `i` and an end `j`.
              - The inner loop adds the values between them.
              - Three nested loops make this **O(n³)**. Kadane's algorithm does it in one pass.
              """
            : system.Contains("Tool: DiagramGenerator", StringComparison.Ordinal)
                ? """{"summary":"Order owns its lines.","mermaid":"classDiagram\n    class Order {\n        +int Id\n        +string Customer\n        +decimal Total()\n    }\n    class OrderLine {\n        +string Sku\n        +int Quantity\n    }\n    Order --> OrderLine : Lines"}"""
                : system.Contains("Tool: Chat", StringComparison.Ordinal)
                    ? "Tell me the language and the error, and I will sketch a fix."
                    : "```csharp\nConsole.WriteLine(\"sample\");\n```";
        return new ChatResult(text, request.Model, null, FinishReasons.Stop, null, TimeSpan.Zero, null);
    }

    private sealed class SampleProvider : ILlmProvider
    {
        public string Key => ProviderKeys.OpenAi;
        public LlmCapabilities Capabilities => LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.Vision;
        public bool IsConfigured => true;
        public IReadOnlyList<ModelInfo> ConfiguredModels => [new ModelInfo(Key, "sample")];
        public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult(ConfiguredModels);
        public Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
