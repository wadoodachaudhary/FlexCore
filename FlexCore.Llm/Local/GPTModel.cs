using System;
using System.Collections.Generic;

namespace Fx.ControlKit.Llm.Local;

/// <summary>
/// In-process decoder-only GPT transformer model.
/// </summary>
public sealed class GPTModel
{
    public GptConfig Config { get; }
    public float[][] TokEmb { get; set; }
    public float[][] PosEmb { get; set; }
    public List<TransformerBlock> Blocks { get; }
    public float[] FinalNormScale { get; set; }
    public float[] FinalNormShift { get; set; }
    public float[][] OutHeadWeight { get; set; }
    public float[]? OutHeadBias { get; set; }

    public GPTModel(GptConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));

        TokEmb = new float[Config.VocabSize][];
        for (int i = 0; i < Config.VocabSize; i++) TokEmb[i] = new float[Config.EmbDim];

        PosEmb = new float[Config.ContextLength][];
        for (int i = 0; i < Config.ContextLength; i++) PosEmb[i] = new float[Config.EmbDim];

        Blocks = new List<TransformerBlock>(Config.NLayers);
        for (int i = 0; i < Config.NLayers; i++)
        {
            Blocks.Add(new TransformerBlock(Config.EmbDim, Config.NHeads, Config.QkvBias));
        }

        FinalNormScale = new float[Config.EmbDim];
        FinalNormShift = new float[Config.EmbDim];
        Array.Fill(FinalNormScale, 1.0f);

        OutHeadWeight = new float[Config.VocabSize][];
        for (int i = 0; i < Config.VocabSize; i++) OutHeadWeight[i] = new float[Config.EmbDim];
        OutHeadBias = new float[Config.VocabSize];
    }

    /// <summary>
    /// Computes logits for the final token in the prompt sequence [1, vocabSize].
    /// </summary>
    public float[] ForwardLastToken(int[] tokenIds)
    {
        ArgumentNullException.ThrowIfNull(tokenIds);
        if (tokenIds.Length == 0) return new float[Config.VocabSize];

        int seqLen = Math.Min(tokenIds.Length, Config.ContextLength);
        int offset = tokenIds.Length - seqLen;

        // 1. Embeddings
        float[][] x = new float[seqLen][];
        for (int i = 0; i < seqLen; i++)
        {
            int tid = tokenIds[offset + i];
            if (tid < 0 || tid >= Config.VocabSize) tid = 0;

            float[] tVec = TokEmb[tid];
            float[] pVec = PosEmb[i];
            x[i] = TensorOps.VectorAdd(tVec, pVec);
        }

        // 2. Transformer blocks
        foreach (var block in Blocks)
        {
            x = block.Forward(x);
        }

        // 3. Final norm on the last token only
        float[] lastTokenHidden = TensorOps.LayerNorm(x[seqLen - 1], FinalNormScale, FinalNormShift);

        // 4. Output projection head
        return TensorOps.Linear(lastTokenHidden, OutHeadWeight, OutHeadBias);
    }

    /// <summary>
    /// Seeds the model weights with reproducible synthetic distributions so the model
    /// can run and test end-to-end even when a multi-gigabyte safetensors file is absent.
    /// </summary>
    public void InitializeSyntheticWeights(int seed = 42)
    {
        var rand = new Random(seed);
        float scale = 0.02f;

        for (int i = 0; i < Config.VocabSize; i++)
        {
            for (int j = 0; j < Config.EmbDim; j++)
            {
                TokEmb[i][j] = (float)(rand.NextDouble() * 2.0 - 1.0) * scale;
                OutHeadWeight[i][j] = TokEmb[i][j]; // Weight tying
            }
        }

        for (int i = 0; i < Config.ContextLength; i++)
        {
            for (int j = 0; j < Config.EmbDim; j++)
            {
                PosEmb[i][j] = (float)(rand.NextDouble() * 2.0 - 1.0) * scale;
            }
        }

        foreach (var block in Blocks)
        {
            InitWeightMatrix(block.Attention.WQuery, rand, scale);
            InitWeightMatrix(block.Attention.WKey, rand, scale);
            InitWeightMatrix(block.Attention.WValue, rand, scale);
            InitWeightMatrix(block.Attention.OutProjWeight, rand, scale);
            InitWeightMatrix(block.Ffn.W1, rand, scale);
            InitWeightMatrix(block.Ffn.W2, rand, scale);
        }
    }

    private static void InitWeightMatrix(float[][] mat, Random rand, float scale)
    {
        for (int i = 0; i < mat.Length; i++)
        {
            for (int j = 0; j < mat[i].Length; j++)
            {
                mat[i][j] = (float)(rand.NextDouble() * 2.0 - 1.0) * scale;
            }
        }
    }
}
