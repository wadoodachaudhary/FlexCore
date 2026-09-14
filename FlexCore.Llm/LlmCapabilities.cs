namespace Fx.ControlKit.Llm;

/// <summary>
/// What a provider adapter can do. A provider throws
/// <see cref="NotSupportedException"/> from any <see cref="ILlmProvider"/>
/// member whose capability flag is absent.
/// </summary>
[Flags]
public enum LlmCapabilities
{
    None = 0,
    Chat = 1 << 0,
    Streaming = 1 << 1,
    JsonMode = 1 << 2,
    JsonSchema = 1 << 3,
    Vision = 1 << 4,
    ImageGeneration = 1 << 5,
    Embeddings = 1 << 6,
    Tools = 1 << 7,
    Reasoning = 1 << 8,
    PromptCaching = 1 << 9,
    ListModels = 1 << 10,
}
