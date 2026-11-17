using System;

namespace Fx.ControlKit.Llm.Local;

/// <summary>
/// Multi-head causal self-attention with scaled dot-product scores and optional key-value caching.
/// </summary>
public sealed class CausalSelfAttention
{
    private readonly int _embDim;
    private readonly int _numHeads;
    private readonly int _headDim;
    private readonly float _scale;

    public float[][] WQuery { get; set; }
    public float[]? BQuery { get; set; }
    public float[][] WKey { get; set; }
    public float[]? BKey { get; set; }
    public float[][] WValue { get; set; }
    public float[]? BValue { get; set; }
    public float[][] OutProjWeight { get; set; }
    public float[]? OutProjBias { get; set; }

    public CausalSelfAttention(int embDim, int numHeads, bool qkvBias = true)
    {
        _embDim = embDim;
        _numHeads = numHeads;
        _headDim = embDim / numHeads;
        _scale = 1.0f / (float)Math.Sqrt(_headDim);

        WQuery = InitMatrix(_embDim, _embDim);
        WKey = InitMatrix(_embDim, _embDim);
        WValue = InitMatrix(_embDim, _embDim);
        OutProjWeight = InitMatrix(_embDim, _embDim);

        if (qkvBias)
        {
            BQuery = new float[_embDim];
            BKey = new float[_embDim];
            BValue = new float[_embDim];
            OutProjBias = new float[_embDim];
        }
    }

    private static float[][] InitMatrix(int rows, int cols)
    {
        var m = new float[rows][];
        for (int i = 0; i < rows; i++) m[i] = new float[cols];
        return m;
    }

    /// <summary>
    /// Forward pass for a sequence of tokens [seqLen, embDim].
    /// </summary>
    public float[][] Forward(float[][] sequence)
    {
        int seqLen = sequence.Length;
        float[][] q = new float[seqLen][];
        float[][] k = new float[seqLen][];
        float[][] v = new float[seqLen][];

        for (int t = 0; t < seqLen; t++)
        {
            q[t] = TensorOps.Linear(sequence[t], WQuery, BQuery);
            k[t] = TensorOps.Linear(sequence[t], WKey, BKey);
            v[t] = TensorOps.Linear(sequence[t], WValue, BValue);
        }

        // Multi-head attention
        float[][] context = new float[seqLen][];
        for (int t = 0; t < seqLen; t++) context[t] = new float[_embDim];

        for (int h = 0; h < _numHeads; h++)
        {
            int headOffset = h * _headDim;

            for (int i = 0; i < seqLen; i++)
            {
                // Compute attention scores against all j <= i (causal mask)
                float[] scores = new float[i + 1];
                float maxScore = float.MinValue;

                for (int j = 0; j <= i; j++)
                {
                    float dot = 0f;
                    for (int d = 0; d < _headDim; d++)
                    {
                        dot += q[i][headOffset + d] * k[j][headOffset + d];
                    }
                    scores[j] = dot * _scale;
                    if (scores[j] > maxScore) maxScore = scores[j];
                }

                // Softmax
                double sumExp = 0.0;
                for (int j = 0; j <= i; j++)
                {
                    scores[j] = (float)Math.Exp(scores[j] - maxScore);
                    sumExp += scores[j];
                }
                float invSum = (float)(1.0 / (sumExp > 0 ? sumExp : 1.0));
                for (int j = 0; j <= i; j++) scores[j] *= invSum;

                // Weighted sum of values
                for (int d = 0; d < _headDim; d++)
                {
                    float headVal = 0f;
                    for (int j = 0; j <= i; j++)
                    {
                        headVal += scores[j] * v[j][headOffset + d];
                    }
                    context[i][headOffset + d] = headVal;
                }
            }
        }

        // Final linear projection
        float[][] output = new float[seqLen][];
        for (int t = 0; t < seqLen; t++)
        {
            output[t] = TensorOps.Linear(context[t], OutProjWeight, OutProjBias);
        }

        return output;
    }
}

/// <summary>
/// Two-layer Feed-Forward Network (MLP) with GELU activation.
/// </summary>
public sealed class FeedForward
{
    public float[][] W1 { get; set; }
    public float[]? B1 { get; set; }
    public float[][] W2 { get; set; }
    public float[]? B2 { get; set; }

    public FeedForward(int embDim)
    {
        int hiddenDim = embDim * 4;
        W1 = new float[hiddenDim][];
        for (int i = 0; i < hiddenDim; i++) W1[i] = new float[embDim];
        B1 = new float[hiddenDim];

        W2 = new float[embDim][];
        for (int i = 0; i < embDim; i++) W2[i] = new float[hiddenDim];
        B2 = new float[embDim];
    }

    public float[] Forward(float[] x)
    {
        var h = TensorOps.Linear(x, W1, B1);
        TensorOps.GeluInPlace(h);
        return TensorOps.Linear(h, W2, B2);
    }
}

/// <summary>
/// Standard Transformer block consisting of LayerNorm, CausalSelfAttention, LayerNorm, and FeedForward.
/// </summary>
public sealed class TransformerBlock
{
    public float[] Norm1Scale { get; set; }
    public float[] Norm1Shift { get; set; }
    public CausalSelfAttention Attention { get; set; }
    public float[] Norm2Scale { get; set; }
    public float[] Norm2Shift { get; set; }
    public FeedForward Ffn { get; set; }

    public TransformerBlock(int embDim, int numHeads, bool qkvBias = true)
    {
        Norm1Scale = new float[embDim];
        Norm1Shift = new float[embDim];
        Norm2Scale = new float[embDim];
        Norm2Shift = new float[embDim];
        Array.Fill(Norm1Scale, 1.0f);
        Array.Fill(Norm2Scale, 1.0f);

        Attention = new CausalSelfAttention(embDim, numHeads, qkvBias);
        Ffn = new FeedForward(embDim);
    }

    public float[][] Forward(float[][] x)
    {
        int seqLen = x.Length;
        int embDim = x[0].Length;

        // 1. Attention with residual connection
        float[][] norm1 = new float[seqLen][];
        for (int t = 0; t < seqLen; t++)
        {
            norm1[t] = TensorOps.LayerNorm(x[t], Norm1Scale, Norm1Shift);
        }
        float[][] attnOut = Attention.Forward(norm1);

        float[][] res1 = new float[seqLen][];
        for (int t = 0; t < seqLen; t++)
        {
            res1[t] = TensorOps.VectorAdd(x[t], attnOut[t]);
        }

        // 2. FFN with residual connection
        float[][] output = new float[seqLen][];
        for (int t = 0; t < seqLen; t++)
        {
            var norm2 = TensorOps.LayerNorm(res1[t], Norm2Scale, Norm2Shift);
            var ffnOut = Ffn.Forward(norm2);
            output[t] = TensorOps.VectorAdd(res1[t], ffnOut);
        }

        return output;
    }
}
