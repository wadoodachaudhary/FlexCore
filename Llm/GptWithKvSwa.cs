using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class GPTConfigSWA
{
    public int VocabSize { get; set; } = 50257;
    public int ContextLength { get; set; } = 1024;
    public int EmbDim { get; set; } = 768;
    public int NHeads { get; set; } = 12;
    public int NLayers { get; set; } = 12;
    public float DropRate { get; set; } = 0.0f;
    public bool QkvBias { get; set; } = false;
    public int? SlidingWindowSize { get; set; } = 1024;
    public int SlidingWindowStride { get; set; } = 2; // K:1 schedule: K SWA layers followed by 1 regular layer
}

public class MultiHeadAttentionWithSWA
{
    private readonly TensorOps.Linear _wQuery;
    private readonly TensorOps.Linear _wKey;
    private readonly TensorOps.Linear _wValue;
    private readonly TensorOps.Linear _outProj;
    private readonly float _dropoutRate;
    private readonly Random _random = new(123);

    public int DIn { get; }
    public int DOut { get; }
    public int NumHeads { get; }
    public int HeadDim { get; }
    public int? SlidingWindowSize { get; set; }

    private List<float[][]>? _cacheK; // Cache of shape [seqLen][batchSize][dOut]
    private List<float[][]>? _cacheV;
    private int _ptrCurrentPos = 0;

    public MultiHeadAttentionWithSWA(int dIn, int dOut, float dropout, int numHeads, bool qkvBias, int? slidingWindowSize = null)
    {
        if (dOut % numHeads != 0)
        {
            throw new ArgumentException("dOut must be divisible by numHeads");
        }

        DIn = dIn;
        DOut = dOut;
        NumHeads = numHeads;
        HeadDim = dOut / numHeads;
        _dropoutRate = dropout;
        SlidingWindowSize = slidingWindowSize;

        _wQuery = new TensorOps.Linear(dIn, dOut, qkvBias);
        _wKey = new TensorOps.Linear(dIn, dOut, qkvBias);
        _wValue = new TensorOps.Linear(dIn, dOut, qkvBias);
        _outProj = new TensorOps.Linear(dOut, dOut, true);
    }

    public void ResetCache()
    {
        _cacheK = null;
        _cacheV = null;
        _ptrCurrentPos = 0;
    }

    public float[][][] Forward(float[][][] x, bool useCache = false)
    {
        int batchSize = x.Length;
        int numTokens = x[0].Length;

        float[][][] queries = new float[batchSize][][];
        float[][][] keysNew = new float[batchSize][][];
        float[][][] valuesNew = new float[batchSize][][];

        for (int b = 0; b < batchSize; b++)
        {
            queries[b] = _wQuery.ForwardBatch(x[b]);
            keysNew[b] = _wKey.ForwardBatch(x[b]);
            valuesNew[b] = _wValue.ForwardBatch(x[b]);
        }

        float[][][] keys;
        float[][][] values;

        int kStartPosAbs = 0;
        int qStartPosAbs = _ptrCurrentPos;

        if (useCache)
        {
            if (_cacheK == null)
            {
                _cacheK = new List<float[][]>();
                _cacheV = new List<float[][]>();
            }

            int oldLen = _cacheK!.Count;

            for (int t = 0; t < numTokens; t++)
            {
                float[][] stepK = new float[batchSize][];
                float[][] stepV = new float[batchSize][];
                for (int b = 0; b < batchSize; b++)
                {
                    stepK[b] = keysNew[b][t];
                    stepV[b] = valuesNew[b][t];
                }
                _cacheK!.Add(stepK);
                _cacheV!.Add(stepV);
            }

            int combinedLen = _cacheK!.Count;
            int attnKeep = combinedLen;
            if (SlidingWindowSize != null)
            {
                attnKeep = Math.Min(combinedLen, SlidingWindowSize.Value + numTokens - 1);
            }

            keys = new float[batchSize][][];
            values = new float[batchSize][][];
            for (int b = 0; b < batchSize; b++)
            {
                keys[b] = new float[attnKeep][];
                values[b] = new float[attnKeep][];
                for (int t = 0; t < attnKeep; t++)
                {
                    int srcIdx = combinedLen - attnKeep + t;
                    keys[b][t] = _cacheK![srcIdx][b];
                    values[b][t] = _cacheV![srcIdx][b];
                }
            }

            if (SlidingWindowSize != null && combinedLen > SlidingWindowSize.Value)
            {
                int cacheKeep = SlidingWindowSize.Value;
                var newCacheK = _cacheK!.Skip(combinedLen - cacheKeep).ToList();
                var newCacheV = _cacheV!.Skip(combinedLen - cacheKeep).ToList();
                _cacheK = newCacheK;
                _cacheV = newCacheV;
            }

            int dropped = combinedLen - attnKeep;
            kStartPosAbs = (qStartPosAbs - oldLen) + dropped;
            _ptrCurrentPos += numTokens;
        }
        else
        {
            keys = keysNew;
            values = valuesNew;
            _ptrCurrentPos = 0;
        }

        int targetSeqLen = keys[0].Length;
        float scale = 1.0f / (float)Math.Sqrt(HeadDim);
        float[][][] output = new float[batchSize][][];

        for (int b = 0; b < batchSize; b++)
        {
            float[][][] qHeads = SplitHeads(queries[b], numTokens, NumHeads);
            float[][][] kHeads = SplitHeads(keys[b], targetSeqLen, NumHeads);
            float[][][] vHeads = SplitHeads(values[b], targetSeqLen, NumHeads);

            float[][][] contextHeads = new float[NumHeads][][];

            for (int h = 0; h < NumHeads; h++)
            {
                float[][] q = qHeads[h];
                float[][] k = kHeads[h];
                float[][] v = vHeads[h];

                float[][] attnScores = new float[numTokens][];
                for (int i = 0; i < numTokens; i++)
                {
                    attnScores[i] = new float[targetSeqLen];
                    int qPos = qStartPosAbs + i;

                    for (int j = 0; j < targetSeqLen; j++)
                    {
                        int kPos = kStartPosAbs + j;
                        int diff = qPos - kPos;

                        bool mask = false;
                        if (diff < 0)
                        {
                            mask = true; // causal mask (future token)
                        }
                        else if (SlidingWindowSize != null && diff >= SlidingWindowSize.Value)
                        {
                            mask = true; // sliding window mask (too far in past)
                        }

                        if (mask)
                        {
                            attnScores[i][j] = float.NegativeInfinity;
                        }
                        else
                        {
                            float sum = 0f;
                            for (int d = 0; d < HeadDim; d++)
                            {
                                sum += q[i][d] * k[j][d];
                            }
                            attnScores[i][j] = sum * scale;
                        }
                    }
                }

                TensorOps.Softmax(attnScores);
                TensorOps.Dropout(attnScores, _dropoutRate, _random);

                contextHeads[h] = new float[numTokens][];
                for (int i = 0; i < numTokens; i++)
                {
                    contextHeads[h][i] = new float[HeadDim];
                    for (int d = 0; d < HeadDim; d++)
                    {
                        float sum = 0f;
                        for (int j = 0; j < targetSeqLen; j++)
                        {
                            if (!float.IsNegativeInfinity(attnScores[i][j]))
                            {
                                sum += attnScores[i][j] * v[j][d];
                            }
                        }
                        contextHeads[h][i][d] = sum;
                    }
                }
            }

            float[][] concatenatedContext = ConcatHeads(contextHeads, numTokens);
            output[b] = _outProj.ForwardBatch(concatenatedContext);
        }

        return output;
    }

    private float[][][] SplitHeads(float[][] projection, int seqLen, int numHeads)
    {
        float[][][] heads = new float[numHeads][][];
        for (int h = 0; h < numHeads; h++)
        {
            heads[h] = new float[seqLen][];
            for (int i = 0; i < seqLen; i++)
            {
                heads[h][i] = new float[HeadDim];
                Array.Copy(projection[i], h * HeadDim, heads[h][i], 0, HeadDim);
            }
        }
        return heads;
    }

    private float[][] ConcatHeads(float[][][] heads, int seqLen)
    {
        float[][] concatenated = new float[seqLen][];
        for (int i = 0; i < seqLen; i++)
        {
            concatenated[i] = new float[DOut];
            for (int h = 0; h < NumHeads; h++)
            {
                Array.Copy(heads[h][i], 0, concatenated[i], h * HeadDim, HeadDim);
            }
        }
        return concatenated;
    }
}

public class TransformerBlockWithSWA
{
    private readonly MultiHeadAttentionWithSWA _attn;
    private readonly FeedForward _ff;
    private readonly LayerNorm _norm1;
    private readonly LayerNorm _norm2;
    private readonly float _dropRate;
    private readonly Random _random = new(123);

    public MultiHeadAttentionWithSWA Attention => _attn;

    public TransformerBlockWithSWA(GPTConfigSWA cfg)
    {
        _attn = new MultiHeadAttentionWithSWA(
            dIn: cfg.EmbDim,
            dOut: cfg.EmbDim,
            dropout: cfg.DropRate,
            numHeads: cfg.NHeads,
            qkvBias: cfg.QkvBias,
            slidingWindowSize: cfg.SlidingWindowSize
        );
        _ff = new FeedForward(new GPTConfig
        {
            EmbDim = cfg.EmbDim,
            DropRate = cfg.DropRate,
            VocabSize = cfg.VocabSize,
            ContextLength = cfg.ContextLength,
            NHeads = cfg.NHeads,
            NLayers = cfg.NLayers,
            QkvBias = cfg.QkvBias
        });
        _norm1 = new LayerNorm(cfg.EmbDim);
        _norm2 = new LayerNorm(cfg.EmbDim);
        _dropRate = cfg.DropRate;
    }

    public float[][][] Forward(float[][][] x, bool useCache = false)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        int dim = x[0][0].Length;

        float[][][] norm1X = _norm1.Forward3D(x);
        float[][][] attnOut = _attn.Forward(norm1X, useCache);

        float[][][] xAttention = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            xAttention[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                xAttention[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    float val = attnOut[b][t][d];
                    if (_dropRate > 0f && _random.NextDouble() < _dropRate) val = 0f;
                    else if (_dropRate > 0f) val /= (1f - _dropRate);
                    xAttention[b][t][d] = x[b][t][d] + val;
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
                    float val = ffOut[b][t][d];
                    if (_dropRate > 0f && _random.NextDouble() < _dropRate) val = 0f;
                    else if (_dropRate > 0f) val /= (1f - _dropRate);
                    output[b][t][d] = xAttention[b][t][d] + val;
                }
            }
        }

        return output;
    }
}

public class GPTModelWithSWA
{
    private readonly Embedding _tokEmb;
    private readonly PositionalEmbedding _posEmb;
    private readonly List<TransformerBlockWithSWA> _trfBlocks = new();
    private readonly LayerNorm _finalNorm;
    private readonly TensorOps.Linear _outHead;
    private readonly float _dropRate;
    private readonly Random _random = new(123);

    public int CurrentPos { get; set; } = 0;
    public GPTConfigSWA Config { get; }
    public List<TransformerBlockWithSWA> Blocks => _trfBlocks;

    public GPTModelWithSWA(GPTConfigSWA cfg)
    {
        Config = cfg;
        _tokEmb = new Embedding(cfg.VocabSize, cfg.EmbDim);
        _posEmb = new PositionalEmbedding(cfg.ContextLength, cfg.EmbDim);
        _dropRate = cfg.DropRate;

        int windowStride = cfg.SlidingWindowStride;
        for (int i = 0; i < cfg.NLayers; i++)
        {
            var blk = new TransformerBlockWithSWA(cfg);

            bool useSwa;
            if (windowStride <= 0)
            {
                useSwa = windowStride < 0;
            }
            else
            {
                int group = windowStride + 1;
                useSwa = (i % group) < windowStride;
            }

            blk.Attention.SlidingWindowSize = useSwa ? cfg.SlidingWindowSize : null;
            _trfBlocks.Add(blk);
        }

        _finalNorm = new LayerNorm(cfg.EmbDim);
        _outHead = new TensorOps.Linear(cfg.EmbDim, cfg.VocabSize, useBias: false);
    }

    public void ResetKVCache()
    {
        foreach (var blk in _trfBlocks)
        {
            blk.Attention.ResetCache();
        }
        CurrentPos = 0;
    }

    public float[][][] Forward(int[][] inIdx, bool useCache = false)
    {
        int batchSize = inIdx.Length;
        int seqLen = inIdx[0].Length;

        int startPos = useCache ? CurrentPos : 0;
        if (useCache)
        {
            CurrentPos += seqLen;
        }

        float[][][] x = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            x[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                x[b][t] = new float[Config.EmbDim];
                var tokVec = _tokEmb.Lookup(inIdx[b][t]);
                var posVec = _posEmb.Lookup(startPos + t);

                for (int d = 0; d < Config.EmbDim; d++)
                {
                    x[b][t][d] = tokVec[d] + posVec[d];
                    if (_dropRate > 0f && _random.NextDouble() < _dropRate) x[b][t][d] = 0f;
                    else if (_dropRate > 0f) x[b][t][d] /= (1f - _dropRate);
                }
            }
        }

        foreach (var blk in _trfBlocks)
        {
            x = blk.Forward(x, useCache);
        }

        x = _finalNorm.Forward3D(x);
        float[][][] logits = _outHead.Forward3D(x);

        return logits;
    }
}

public static class SwaGenerator
{
    public static List<int> GenerateTextSimpleCached(GPTModelWithSWA model, List<int> promptTokens, int maxNewTokens)
    {
        model.ResetKVCache();
        int batchSize = 1;

        int[][] promptBatch = new int[batchSize][];
        promptBatch[0] = promptTokens.ToArray();

        float[][][] logits = model.Forward(promptBatch, useCache: true);

        List<int> result = new(promptTokens);

        for (int step = 0; step < maxNewTokens; step++)
        {
            int nextToken = ArgMax(logits[0][logits[0].Length - 1]);
            result.Add(nextToken);

            int[][] stepBatch = new int[batchSize][];
            stepBatch[0] = new int[] { nextToken };
            logits = model.Forward(stepBatch, useCache: true);
        }

        return result;
    }

    private static int ArgMax(float[] values)
    {
        int bestIdx = 0;
        float maxVal = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > maxVal)
            {
                maxVal = values[i];
                bestIdx = i;
            }
        }
        return bestIdx;
    }
}
