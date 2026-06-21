using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class TinyAyaConfig
{
    public int VocabSize { get; set; } = 262144;
    public int ContextLength { get; set; } = 500000;
    public int EmbDim { get; set; } = 2048;
    public int NHeads { get; set; } = 16;
    public int NLayers { get; set; } = 36;
    public int HiddenDim { get; set; } = 11008;
    public int HeadDim { get; set; } = 128;
    public int NKvHeads { get; set; } = 4;
    public bool AttentionBias { get; set; } = false;
    public int SlidingWindow { get; set; } = 4096;
    public string[] LayerTypes { get; set; } = Array.Empty<string>();
    public double RopeBase { get; set; } = 50000.0;
    public float LayerNormEps { get; set; } = 1e-5f;
    public float LogitScale { get; set; } = 1.0f;
    public bool TieWordEmbeddings { get; set; } = true;
}

public class CohereLayerNorm
{
    private readonly float[] _weight;
    private readonly float _eps;

    public CohereLayerNorm(int dim, float eps = 1e-5f)
    {
        _weight = Enumerable.Repeat(1.0f, dim).ToArray();
        _eps = eps;
    }

    public float[] Forward(float[] x)
    {
        int n = x.Length;
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += x[i];
        float mean = sum / n;

        float sumSqDiff = 0f;
        for (int i = 0; i < n; i++) sumSqDiff += (x[i] - mean) * (x[i] - mean);
        float variance = sumSqDiff / n;
        float rms = (float)Math.Sqrt(variance + _eps);

        float[] output = new float[n];
        for (int i = 0; i < n; i++)
        {
            output[i] = _weight[i] * ((x[i] - mean) / rms);
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

public static class TinyAyaRoPEHelper
{
    public static (float[][] cos, float[][] sin) ComputeRopeParamsAya(int headDim, double thetaBase, int contextLength)
    {
        int halfDim = headDim / 2;
        float[] invFreq = new float[halfDim];
        for (int i = 0; i < halfDim; i++)
        {
            invFreq[i] = (float)(1.0 / Math.Pow(thetaBase, (double)(2 * i) / headDim));
        }

        float[][] cos = new float[contextLength][];
        float[][] sin = new float[contextLength][];

        for (int t = 0; t < contextLength; t++)
        {
            cos[t] = new float[headDim];
            sin[t] = new float[headDim];

            for (int i = 0; i < halfDim; i++)
            {
                float angle = t * invFreq[i];
                cos[t][2 * i] = (float)Math.Cos(angle);
                sin[t][2 * i] = (float)Math.Sin(angle);

                cos[t][2 * i + 1] = cos[t][2 * i];
                sin[t][2 * i + 1] = sin[t][2 * i];
            }
        }

        return (cos, sin);
    }

    public static float[][] ApplyRoPEAya(float[][] x, float[][] cos, float[][] sin, int startPos = 0)
    {
        int seqLen = x.Length;
        int headDim = x[0].Length;
        int halfDim = headDim / 2;

        float[][] output = new float[seqLen][];
        for (int t = 0; t < seqLen; t++)
        {
            output[t] = new float[headDim];
            int globalPos = startPos + t;
            if (globalPos >= cos.Length)
            {
                globalPos = globalPos % cos.Length; // circular fallback
            }

            for (int d = 0; d < halfDim; d++)
            {
                float even = x[t][2 * d];
                float odd = x[t][2 * d + 1];

                float c = cos[globalPos][2 * d];
                float s = sin[globalPos][2 * d];

                output[t][2 * d] = (even * c) + (-odd * s);
                output[t][2 * d + 1] = (odd * c) + (even * s);
            }
        }
        return output;
    }
}

public class TinyAyaAttention
{
    private readonly TensorOps.Linear _wQuery;
    private readonly TensorOps.Linear _wKey;
    private readonly TensorOps.Linear _wValue;
    private readonly TensorOps.Linear _outProj;

    private readonly float _scaling;
    private readonly int _embDim;
    private readonly int _headDim;
    private readonly int _numHeads;
    private readonly int _numKvHeads;
    private readonly int _groupSize;
    private readonly string _attnType;

    public TinyAyaAttention(TinyAyaConfig cfg, string attnType, Random? rand = null)
    {
        _embDim = cfg.EmbDim;
        _headDim = cfg.HeadDim;
        _numHeads = cfg.NHeads;
        _numKvHeads = cfg.NKvHeads;
        _groupSize = _numHeads / _numKvHeads;
        _attnType = attnType;

        _wQuery = new TensorOps.Linear(_embDim, _numHeads * _headDim, cfg.AttentionBias, rand);
        _wKey = new TensorOps.Linear(_embDim, _numKvHeads * _headDim, cfg.AttentionBias, rand);
        _wValue = new TensorOps.Linear(_embDim, _numKvHeads * _headDim, cfg.AttentionBias, rand);
        _outProj = new TensorOps.Linear(_numHeads * _headDim, _embDim, cfg.AttentionBias, rand);

        _scaling = 1.0f / (float)Math.Sqrt(_headDim);
    }

    public float[][][] Forward(float[][][] x, float[][] cos, float[][] sin, int slidingWindow = -1, int startPos = 0, KVCache? cache = null)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;

        float[][][] queries = new float[batchSize][][];
        float[][][] keys = new float[batchSize][][];
        float[][][] values = new float[batchSize][][];

        for (int b = 0; b < batchSize; b++)
        {
            queries[b] = _wQuery.ForwardBatch(x[b]);
            keys[b] = _wKey.ForwardBatch(x[b]);
            values[b] = _wValue.ForwardBatch(x[b]);
        }

        float[][][] finalKeys;
        float[][][] finalValues;
        if (cache != null)
        {
            float[][] stepKeys = new float[batchSize][];
            float[][] stepValues = new float[batchSize][];
            for (int b = 0; b < batchSize; b++)
            {
                stepKeys[b] = keys[b][0];
                stepValues[b] = values[b][0];
            }
            var (cachedKeys, cachedValues) = cache.Update(stepKeys, stepValues);
            finalKeys = cachedKeys;
            finalValues = cachedValues;
        }
        else
        {
            finalKeys = keys;
            finalValues = values;
        }

        int targetSeqLen = finalKeys[0].Length;
        float[][][] output = new float[batchSize][][];

        for (int b = 0; b < batchSize; b++)
        {
            float[][][] qHeads = SplitHeads(queries[b], seqLen, _numHeads);
            float[][][] kHeads = SplitHeads(finalKeys[b], targetSeqLen, _numKvHeads);
            float[][][] vHeads = SplitHeads(finalValues[b], targetSeqLen, _numKvHeads);

            if (_attnType == "sliding_attention")
            {
                for (int h = 0; h < _numHeads; h++) qHeads[h] = TinyAyaRoPEHelper.ApplyRoPEAya(qHeads[h], cos, sin, startPos);
                for (int h = 0; h < _numKvHeads; h++) kHeads[h] = TinyAyaRoPEHelper.ApplyRoPEAya(kHeads[h], cos, sin, startPos);
            }

            if (_groupSize > 1)
            {
                float[][][] kHeadsRep = new float[_numHeads][][];
                float[][][] vHeadsRep = new float[_numHeads][][];
                for (int h = 0; h < _numHeads; h++)
                {
                    kHeadsRep[h] = kHeads[h / _groupSize];
                    vHeadsRep[h] = vHeads[h / _groupSize];
                }
                kHeads = kHeadsRep;
                vHeads = vHeadsRep;
            }

            float[][][] contextHeads = new float[_numHeads][][];

            for (int h = 0; h < _numHeads; h++)
            {
                float[][] q = qHeads[h];
                float[][] k = kHeads[h];
                float[][] v = vHeads[h];

                float[][] attnScores = new float[seqLen][];
                for (int i = 0; i < seqLen; i++)
                {
                    attnScores[i] = new float[targetSeqLen];
                    for (int j = 0; j < targetSeqLen; j++)
                    {
                        float sum = 0f;
                        for (int d = 0; d < _headDim; d++)
                        {
                            sum += q[i][d] * k[j][d];
                        }
                        attnScores[i][j] = sum * _scaling;

                        if (seqLen > 1 && j > i)
                        {
                            attnScores[i][j] = float.NegativeInfinity;
                        }
                        if (slidingWindow > 0 && (i - j >= slidingWindow))
                        {
                            attnScores[i][j] = float.NegativeInfinity;
                        }
                    }
                }

                TensorOps.Softmax(attnScores);

                contextHeads[h] = new float[seqLen][];
                for (int i = 0; i < seqLen; i++)
                {
                    contextHeads[h][i] = new float[_headDim];
                    for (int d = 0; d < _headDim; d++)
                    {
                        float sum = 0f;
                        for (int j = 0; j < targetSeqLen; j++)
                        {
                            sum += attnScores[i][j] * v[j][d];
                        }
                        contextHeads[h][i][d] = sum;
                    }
                }
            }

            float[][] concatenated = ConcatHeads(contextHeads, seqLen);
            output[b] = _outProj.ForwardBatch(concatenated);
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
                heads[h][i] = new float[_headDim];
                Array.Copy(projection[i], h * _headDim, heads[h][i], 0, _headDim);
            }
        }
        return heads;
    }

    private float[][] ConcatHeads(float[][][] heads, int seqLen)
    {
        float[][] concatenated = new float[seqLen][];
        for (int i = 0; i < seqLen; i++)
        {
            concatenated[i] = new float[_numHeads * _headDim];
            for (int h = 0; h < _numHeads; h++)
            {
                Array.Copy(heads[h][i], 0, concatenated[i], h * _headDim, _headDim);
            }
        }
        return concatenated;
    }
}

public class TinyAyaFeedForward
{
    private readonly TensorOps.Linear _fc1;
    private readonly TensorOps.Linear _fc2;
    private readonly TensorOps.Linear _fc3;

    public TinyAyaFeedForward(TinyAyaConfig cfg, Random? rand = null)
    {
        _fc1 = new TensorOps.Linear(cfg.EmbDim, cfg.HiddenDim, useBias: false, rand: rand);
        _fc2 = new TensorOps.Linear(cfg.EmbDim, cfg.HiddenDim, useBias: false, rand: rand);
        _fc3 = new TensorOps.Linear(cfg.HiddenDim, cfg.EmbDim, useBias: false, rand: rand);
    }

    public float[] Forward(float[] x)
    {
        float[] x_fc1 = _fc1.Forward(x);
        float[] x_fc2 = _fc2.Forward(x);
        float[] output = new float[x_fc1.Length];
        for (int i = 0; i < x_fc1.Length; i++)
        {
            output[i] = SiLU.Forward(x_fc1[i]) * x_fc2[i]; // SwiGLU
        }
        return _fc3.Forward(output);
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

public class TinyAyaTransformerBlock
{
    private readonly TinyAyaAttention _att;
    private readonly TinyAyaFeedForward _ff;
    private readonly CohereLayerNorm _inputLayernorm;

    public string AttnType { get; }

    public TinyAyaTransformerBlock(TinyAyaConfig cfg, string attnType, Random? rand = null)
    {
        AttnType = attnType;
        _att = new TinyAyaAttention(cfg, attnType, rand);
        _ff = new TinyAyaFeedForward(cfg, rand);
        _inputLayernorm = new CohereLayerNorm(cfg.EmbDim, cfg.LayerNormEps);
    }

    public float[][][] Forward(float[][][] x, float[][] cos, float[][] sin, int slidingWindow, int startPos = 0, KVCache? cache = null)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        int dim = x[0][0].Length;

        int window = (AttnType == "sliding_attention") ? slidingWindow : -1;

        float[][][] shortcut = x;
        float[][][] normX = _inputLayernorm.Forward3D(x);
        float[][][] attnOut = _att.Forward(normX, cos, sin, window, startPos, cache);
        float[][][] ffOut = _ff.Forward3D(normX);

        float[][][] output = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            output[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                output[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    output[b][t][d] = shortcut[b][t][d] + attnOut[b][t][d] + ffOut[b][t][d];
                }
            }
        }

        return output;
    }
}

public class TinyAyaModel
{
    private readonly Embedding _tokEmb;
    private readonly List<TinyAyaTransformerBlock> _trfBlocks = new();
    private readonly CohereLayerNorm _finalNorm;
    private readonly TensorOps.Linear _outHead;

    private readonly float[][] _cos;
    private readonly float[][] _sin;

    public TinyAyaConfig Config { get; }

    public TinyAyaModel(TinyAyaConfig cfg, Random? rand = null)
    {
        Config = cfg;
        var r = rand ?? new Random(42);

        _tokEmb = new Embedding(cfg.VocabSize, cfg.EmbDim, r);

        string[] layers = cfg.LayerTypes;
        if (layers == null || layers.Length == 0)
        {
            layers = Enumerable.Repeat("sliding_attention", cfg.NLayers).ToArray();
        }

        foreach (var attnType in layers)
        {
            _trfBlocks.Add(new TinyAyaTransformerBlock(cfg, attnType, r));
        }

        _finalNorm = new CohereLayerNorm(cfg.EmbDim, cfg.LayerNormEps);
        _outHead = new TensorOps.Linear(cfg.EmbDim, cfg.VocabSize, useBias: false, rand: r);

        if (cfg.TieWordEmbeddings)
        {
        }

        var (cos, sin) = TinyAyaRoPEHelper.ComputeRopeParamsAya(cfg.HeadDim, cfg.RopeBase, cfg.ContextLength);
        _cos = cos;
        _sin = sin;
    }

    public float[][][] Forward(int[][] inIdx, int startPos = 0, List<KVCache>? caches = null)
    {
        int batchSize = inIdx.Length;
        int seqLen = inIdx[0].Length;

        float[][][] x = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            x[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                x[b][t] = new float[Config.EmbDim];
                var vec = _tokEmb.Lookup(inIdx[b][t]);
                Array.Copy(vec, x[b][t], Config.EmbDim);
            }
        }

        for (int i = 0; i < _trfBlocks.Count; i++)
        {
            var cache = (caches != null && i < caches.Count) ? caches[i] : null;
            x = _trfBlocks[i].Forward(x, _cos, _sin, Config.SlidingWindow, startPos, cache);
        }

        x = _finalNorm.Forward3D(x);

        float[][][] logits = _outHead.Forward3D(x);

        if (Math.Abs(Config.LogitScale - 1.0f) > 1e-5f)
        {
            float scale = Config.LogitScale;
            for (int b = 0; b < batchSize; b++)
            {
                for (int t = 0; t < seqLen; t++)
                {
                    for (int v = 0; v < Config.VocabSize; v++)
                    {
                        logits[b][t][v] *= scale;
                    }
                }
            }
        }

        return logits;
    }
}
