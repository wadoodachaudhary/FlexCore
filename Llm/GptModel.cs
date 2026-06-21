using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class GPTConfig
{
    public int VocabSize { get; set; } = 50257;
    public int ContextLength { get; set; } = 1024;
    public int EmbDim { get; set; } = 768;
    public int NHeads { get; set; } = 12;
    public int NLayers { get; set; } = 12;
    public float DropRate { get; set; } = 0.1f;
    public bool QkvBias { get; set; } = false;
}

public class GELU
{
    public float Forward(float x)
    {
        float inner = (float)Math.Sqrt(2.0 / Math.PI) * (x + 0.044715f * x * x * x);
        return 0.5f * x * (1.0f + (float)Math.Tanh(inner));
    }

    public float[] Forward(float[] x)
    {
        float[] output = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            output[i] = Forward(x[i]);
        }
        return output;
    }
}

public class LayerNorm
{
    private readonly float[] _scale;
    private readonly float[] _shift;
    private readonly float _eps = 1e-5f;

    public float[] Scale => _scale;
    public float[] Shift => _shift;

    public LayerNorm(int embDim)
    {
        _scale = Enumerable.Repeat(1.0f, embDim).ToArray();
        _shift = Enumerable.Repeat(0.0f, embDim).ToArray();
    }

    public float[] Forward(float[] x)
    {
        int n = x.Length;
        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            sum += x[i];
        }
        float mean = sum / n;

        float sumSqDiff = 0f;
        for (int i = 0; i < n; i++)
        {
            float diff = x[i] - mean;
            sumSqDiff += diff * diff;
        }
        float variance = sumSqDiff / n;
        float stdDev = (float)Math.Sqrt(variance + _eps);

        float[] output = new float[n];
        for (int i = 0; i < n; i++)
        {
            output[i] = _scale[i] * ((x[i] - mean) / stdDev) + _shift[i];
        }

        return output;
    }

    public float[][] ForwardBatch(float[][] x)
    {
        int batchSize = x.Length;
        float[][] output = new float[batchSize][];
        for (int b = 0; b < batchSize; b++)
        {
            output[b] = Forward(x[b]);
        }
        return output;
    }

    public float[][][] Forward3D(float[][][] x)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        float[][][] output = new float[batchSize][][];

        for (int b = 0; b < batchSize; b++)
        {
            output[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                output[b][t] = Forward(x[b][t]);
            }
        }

        return output;
    }
}

public class FeedForward
{
    private readonly TensorOps.Linear _linear1;
    private readonly TensorOps.Linear _linear2;
    private readonly GELU _gelu = new();

    public FeedForward(GPTConfig cfg)
    {
        _linear1 = new TensorOps.Linear(cfg.EmbDim, 4 * cfg.EmbDim, useBias: true);
        _linear2 = new TensorOps.Linear(4 * cfg.EmbDim, cfg.EmbDim, useBias: true);
    }

    public float[] Forward(float[] x)
    {
        var h1 = _linear1.Forward(x);
        var activated = _gelu.Forward(h1);
        var h2 = _linear2.Forward(activated);
        return h2;
    }

    public float[][] ForwardBatch(float[][] x)
    {
        int batchSize = x.Length;
        float[][] output = new float[batchSize][];
        for (int b = 0; b < batchSize; b++)
        {
            output[b] = Forward(x[b]);
        }
        return output;
    }

    public float[][][] Forward3D(float[][][] x)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        float[][][] output = new float[batchSize][][];

        for (int b = 0; b < batchSize; b++)
        {
            output[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                output[b][t] = Forward(x[b][t]);
            }
        }

        return output;
    }
}

public class TransformerBlock
{
    private readonly MultiHeadAttention _attn;
    private readonly FeedForward _ff;
    private readonly LayerNorm _norm1;
    private readonly LayerNorm _norm2;
    private readonly float _dropRate;
    private readonly Random _random = new(42);

    public TransformerBlock(GPTConfig cfg)
    {
        _attn = new MultiHeadAttention(
            dIn: cfg.EmbDim,
            dOut: cfg.EmbDim,
            contextLength: cfg.ContextLength,
            dropout: cfg.DropRate,
            numHeads: cfg.NHeads,
            useBias: cfg.QkvBias
        );
        _ff = new FeedForward(cfg);
        _norm1 = new LayerNorm(cfg.EmbDim);
        _norm2 = new LayerNorm(cfg.EmbDim);
        _dropRate = cfg.DropRate;
    }

    public float[][][] Forward(float[][][] x)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        int dim = x[0][0].Length;

        float[][][] norm1X = _norm1.Forward3D(x);
        float[][][] attnOut = _attn.Forward(norm1X);
        
        float[][][] xAttention = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            xAttention[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                xAttention[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    float droppedVal = (_random.NextDouble() >= _dropRate) ? attnOut[b][t][d] / (1f - _dropRate) : 0f;
                    xAttention[b][t][d] = x[b][t][d] + droppedVal;
                }
            }
        }

        float[][][] norm2X = _norm2.Forward3D(xAttention);
        float[][][] ffOut = _ff.Forward3D(norm2X);

        float[][][] output = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            output[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                output[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    float droppedVal = (_random.NextDouble() >= _dropRate) ? ffOut[b][t][d] / (1f - _dropRate) : 0f;
                    output[b][t][d] = xAttention[b][t][d] + droppedVal;
                }
            }
        }

        return output;
    }
}

public class GPTModel
{
    private readonly Embedding _tokEmb;
    private readonly PositionalEmbedding _posEmb;
    private readonly List<TransformerBlock> _trfBlocks = new();
    private readonly LayerNorm _finalNorm;
    private readonly TensorOps.Linear _outHead;
    private readonly float _dropRate;
    private readonly Random _random = new(42);

    public GPTConfig Config { get; }

    public GPTModel(GPTConfig cfg)
    {
        Config = cfg;
        _tokEmb = new Embedding(cfg.VocabSize, cfg.EmbDim);
        _posEmb = new PositionalEmbedding(cfg.ContextLength, cfg.EmbDim);
        _dropRate = cfg.DropRate;

        for (int i = 0; i < cfg.NLayers; i++)
        {
            _trfBlocks.Add(new TransformerBlock(cfg));
        }

        _finalNorm = new LayerNorm(cfg.EmbDim);
        _outHead = new TensorOps.Linear(cfg.EmbDim, cfg.VocabSize, useBias: false);
    }

    public float[][][] ForwardRepresentations(int[][] inIdx)
    {
        int batchSize = inIdx.Length;
        int seqLen = inIdx[0].Length;

        float[][][] x = EmbeddingProcessor.ProcessBatch(inIdx, _tokEmb, _posEmb);

        if (_dropRate > 0f)
        {
            float scale = 1f / (1f - _dropRate);
            for (int b = 0; b < batchSize; b++)
            {
                for (int t = 0; t < seqLen; t++)
                {
                    for (int d = 0; d < Config.EmbDim; d++)
                    {
                        if (_random.NextDouble() < _dropRate)
                            x[b][t][d] = 0f;
                        else
                            x[b][t][d] *= scale;
                    }
                }
            }
        }

        foreach (var block in _trfBlocks)
        {
            x = block.Forward(x);
        }

        x = _finalNorm.Forward3D(x);

        return x;
    }

    public float[][][] Forward(int[][] inIdx)
    {
        float[][][] x = ForwardRepresentations(inIdx);

        float[][][] logits = _outHead.Forward3D(x);

        return logits;
    }

    public float[][] ForwardLastToken(int[][] inIdx)
    {
        int batchSize = inIdx.Length;
        int seqLen = inIdx[0].Length;

        float[][][] x = ForwardRepresentations(inIdx);

        float[][] lastLogits = new float[batchSize][];
        for (int b = 0; b < batchSize; b++)
        {
            lastLogits[b] = _outHead.Forward(x[b][seqLen - 1]);
        }

        return lastLogits;
    }
}
