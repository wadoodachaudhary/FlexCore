namespace Fx.ControlKit.Llm.Local;

/// <summary>
/// Architecture configuration for an in-process decoder-only Transformer model.
/// </summary>
public sealed class GptConfig
{
    public int VocabSize { get; set; } = 50257;
    public int ContextLength { get; set; } = 1024;
    public int EmbDim { get; set; } = 768;
    public int NHeads { get; set; } = 12;
    public int NLayers { get; set; } = 12;
    public float DropRate { get; set; } = 0.0f;
    public bool QkvBias { get; set; } = true;

    public static GptConfig Gpt2Small => new()
    {
        VocabSize = 50257,
        ContextLength = 1024,
        EmbDim = 768,
        NHeads = 12,
        NLayers = 12,
        QkvBias = true
    };

    public static GptConfig Gpt2Medium => new()
    {
        VocabSize = 50257,
        ContextLength = 1024,
        EmbDim = 1024,
        NHeads = 16,
        NLayers = 24,
        QkvBias = true
    };

    public static GptConfig SmallTest => new()
    {
        VocabSize = 50257,
        ContextLength = 256,
        EmbDim = 256,
        NHeads = 4,
        NLayers = 4,
        QkvBias = true
    };
}
