using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class GemmaConfig
{
    public int VocabSize { get; set; } = 262144;
    public int ContextLength { get; set; } = 32768;
    public int EmbDim { get; set; } = 640;
    public int NHeads { get; set; } = 4;
    public int NLayers { get; set; } = 18;
    public int HiddenDim { get; set; } = 2048;
    public int HeadDim { get; set; } = 256;
    public bool QkNorm { get; set; } = true;
    public int NKvGroups { get; set; } = 1;
    public double RopeLocalBase { get; set; } = 10000.0;
    public double RopeBase { get; set; } = 1000000.0;
    public int SlidingWindow { get; set; } = 512;
    public string[] LayerTypes { get; set; } = Array.Empty<string>();
    public float? QueryPreAttnScalar { get; set; } = 256.0f;
    public float LayerNormEps { get; set; } = 1e-6f;

    public int VocabSizePerLayerInput { get; set; } = 262144;
    public int HiddenSizePerLayerInput { get; set; } = 256;
    public int NumKvSharedLayers { get; set; } = 20;
    public bool UseDoubleWideMlp { get; set; } = true;
    public float FinalLogitSoftcap { get; set; } = 30.0f;
    public bool TieWordEmbeddings { get; set; } = true;
    public string RopeGlobalType { get; set; } = "proportional";
    public float RopeGlobalPartialRotaryFactor { get; set; } = 0.25f;
    public int NKvHeads { get; set; } = 1;
    public int GlobalHeadDim { get; set; } = 512;
    public double RopeGlobalBase { get; set; } = 1000000.0;
}

public class Gemma3RMSNorm
{
    private readonly float[] _scale;
    private readonly float[]? _shift;
    private readonly float _eps;

    public Gemma3RMSNorm(int dim, float eps = 1e-6f, bool bias = false)
    {
        _scale = new float[dim]; // zero-centered weights
        if (bias)
        {
            _shift = new float[dim];
        }
        _eps = eps;
    }

    public float[] Forward(float[] x)
    {
        int n = x.Length;
        double sumSq = 0;
        for (int i = 0; i < n; i++) sumSq += (double)x[i] * x[i];
        float rms = (float)Math.Sqrt(sumSq / n + _eps);

        float[] output = new float[n];
        for (int i = 0; i < n; i++)
        {
            float normX = x[i] / rms;
            output[i] = normX * (1.0f + _scale[i]);
            if (_shift != null)
            {
                output[i] += _shift[i];
            }
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

public class Gemma4RMSNorm
{
    private readonly float[]? _weight;
    private readonly float _eps;

    public Gemma4RMSNorm(int dim, float eps = 1e-6f, bool withScale = true)
    {
        if (withScale)
        {
            _weight = Enumerable.Repeat(1.0f, dim).ToArray();
        }
        _eps = eps;
    }

    public float[] Forward(float[] x)
    {
        int n = x.Length;
        double sumSq = 0;
        for (int i = 0; i < n; i++) sumSq += (double)x[i] * x[i];
        float rms = (float)Math.Sqrt(sumSq / n + _eps);

        float[] output = new float[n];
        for (int i = 0; i < n; i++)
        {
            float normX = x[i] / rms;
            output[i] = (_weight != null) ? normX * _weight[i] : normX;
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

public class GemmaFeedForward
{
    private readonly TensorOps.Linear _fc1;
    private readonly TensorOps.Linear _fc2;
    private readonly TensorOps.Linear _fc3;
    private readonly GELU _gelu = new();

    public GemmaFeedForward(GemmaConfig cfg, int multiplier = 1, Random? rand = null)
    {
        int intermediateSize = cfg.HiddenDim * multiplier;
        _fc1 = new TensorOps.Linear(cfg.EmbDim, intermediateSize, useBias: false, rand: rand);
        _fc2 = new TensorOps.Linear(cfg.EmbDim, intermediateSize, useBias: false, rand: rand);
        _fc3 = new TensorOps.Linear(intermediateSize, cfg.EmbDim, useBias: false, rand: rand);
    }

    public float[] Forward(float[] x)
    {
        float[] x_fc1 = _fc1.Forward(x);
        float[] x_fc2 = _fc2.Forward(x);
        float[] output = new float[x_fc1.Length];
        for (int i = 0; i < x_fc1.Length; i++)
        {
            output[i] = _gelu.Forward(x_fc1[i]) * x_fc2[i]; // Tanh-approximate GELU
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

public class GemmaAttention
{
    private readonly TensorOps.Linear _wQuery;
    private readonly TensorOps.Linear _wKey;
    private readonly TensorOps.Linear _wValue;
    private readonly TensorOps.Linear _outProj;
    private readonly Gemma3RMSNorm? _qNorm;
    private readonly Gemma3RMSNorm? _kNorm;

    private readonly float _scaling;
    private readonly int _embDim;
    private readonly int _headDim;
    private readonly int _numHeads;
    private readonly int _numKvGroups;
    private readonly int _groupSize;

    public GemmaAttention(GemmaConfig cfg, Random? rand = null)
    {
        _embDim = cfg.EmbDim;
        _headDim = cfg.HeadDim;
        _numHeads = cfg.NHeads;
        _numKvGroups = cfg.NKvGroups;
        _groupSize = _numHeads / _numKvGroups;

        _wQuery = new TensorOps.Linear(_embDim, _numHeads * _headDim, useBias: false, rand: rand);
        _wKey = new TensorOps.Linear(_embDim, _numKvGroups * _headDim, useBias: false, rand: rand);
        _wValue = new TensorOps.Linear(_embDim, _numKvGroups * _headDim, useBias: false, rand: rand);
        _outProj = new TensorOps.Linear(_numHeads * _headDim, _embDim, useBias: false, rand: rand);

        if (cfg.QkNorm)
        {
            _qNorm = new Gemma3RMSNorm(_headDim, cfg.LayerNormEps);
            _kNorm = new Gemma3RMSNorm(_headDim, cfg.LayerNormEps);
        }

        if (cfg.QueryPreAttnScalar.HasValue)
        {
            _scaling = 1.0f / (float)Math.Sqrt(cfg.QueryPreAttnScalar.Value);
        }
        else
        {
            _scaling = 1.0f / (float)Math.Sqrt(_headDim);
        }
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
            float[][][] kHeads = SplitHeads(finalKeys[b], targetSeqLen, _numKvGroups);
            float[][][] vHeads = SplitHeads(finalValues[b], targetSeqLen, _numKvGroups);

            if (_qNorm != null && _kNorm != null)
            {
                for (int h = 0; h < _numHeads; h++) qHeads[h] = _qNorm.ForwardBatch(qHeads[h]);
                for (int h = 0; h < _numKvGroups; h++) kHeads[h] = _kNorm.ForwardBatch(kHeads[h]);
            }

            for (int h = 0; h < _numHeads; h++) qHeads[h] = RoPEHelper.ApplyRoPE(qHeads[h], cos, sin, startPos);
            for (int h = 0; h < _numKvGroups; h++) kHeads[h] = RoPEHelper.ApplyRoPE(kHeads[h], cos, sin, startPos);

            float[][][] contextHeads = new float[_numHeads][][];

            for (int h = 0; h < _numHeads; h++)
            {
                int kvh = h / _groupSize;

                float[][] q = qHeads[h];
                float[][] k = kHeads[kvh];
                float[][] v = vHeads[kvh];

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

public class Gemma3TransformerBlock
{
    private readonly GemmaAttention _attn;
    private readonly GemmaFeedForward _ff;
    private readonly Gemma3RMSNorm _inputLayernorm;
    private readonly Gemma3RMSNorm _postAttentionLayernorm;
    private readonly Gemma3RMSNorm _preFeedforwardLayernorm;
    private readonly Gemma3RMSNorm _postFeedforwardLayernorm;

    public string AttnType { get; }

    public Gemma3TransformerBlock(GemmaConfig cfg, string attnType, Random? rand = null)
    {
        AttnType = attnType;
        _attn = new GemmaAttention(cfg, rand);
        _ff = new GemmaFeedForward(cfg, rand: rand);
        _inputLayernorm = new Gemma3RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        _postAttentionLayernorm = new Gemma3RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        _preFeedforwardLayernorm = new Gemma3RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        _postFeedforwardLayernorm = new Gemma3RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
    }

    public float[][][] Forward(float[][][] x, float[][] cosGlobal, float[][] sinGlobal, float[][] cosLocal, float[][] sinLocal, int slidingWindow, int startPos = 0, KVCache? cache = null)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        int dim = x[0][0].Length;

        float[][] cos = (AttnType == "sliding_attention") ? cosLocal : cosGlobal;
        float[][] sin = (AttnType == "sliding_attention") ? sinLocal : sinGlobal;
        int window = (AttnType == "sliding_attention") ? slidingWindow : -1;

        float[][][] shortcut = x;
        float[][][] normX = _inputLayernorm.Forward3D(x);
        float[][][] attnOut = _attn.Forward(normX, cos, sin, window, startPos, cache);
        float[][][] postAttnNorm = _postAttentionLayernorm.Forward3D(attnOut);

        float[][][] xAttnResidual = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            xAttnResidual[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                xAttnResidual[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    xAttnResidual[b][t][d] = shortcut[b][t][d] + postAttnNorm[b][t][d];
                }
            }
        }

        shortcut = xAttnResidual;
        float[][][] normXff = _preFeedforwardLayernorm.Forward3D(xAttnResidual);
        float[][][] ffOut = _ff.Forward3D(normXff);
        float[][][] postFfNorm = _postFeedforwardLayernorm.Forward3D(ffOut);

        float[][][] output = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            output[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                output[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    output[b][t][d] = shortcut[b][t][d] + postFfNorm[b][t][d];
                }
            }
        }

        return output;
    }
}

public class Gemma3Model
{
    private readonly Embedding _tokEmb;
    private readonly List<Gemma3TransformerBlock> _blocks = new();
    private readonly Gemma3RMSNorm _finalNorm;
    private readonly TensorOps.Linear _outHead;

    private readonly float[][] _cosLocal;
    private readonly float[][] _sinLocal;
    private readonly float[][] _cosGlobal;
    private readonly float[][] _sinGlobal;

    public GemmaConfig Config { get; }

    public Gemma3Model(GemmaConfig cfg, Random? rand = null)
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
            _blocks.Add(new Gemma3TransformerBlock(cfg, attnType, r));
        }

        _finalNorm = new Gemma3RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        _outHead = new TensorOps.Linear(cfg.EmbDim, cfg.VocabSize, useBias: false, rand: r);

        var (cosL, sinL) = RoPEHelper.ComputeRopeParams(cfg.HeadDim, cfg.RopeLocalBase, cfg.ContextLength);
        _cosLocal = cosL;
        _sinLocal = sinL;

        var (cosG, sinG) = RoPEHelper.ComputeRopeParams(cfg.HeadDim, cfg.RopeBase, cfg.ContextLength);
        _cosGlobal = cosG;
        _sinGlobal = sinG;
    }

    public float[][][] Forward(int[][] inIdx, int startPos = 0, List<KVCache>? caches = null)
    {
        int batchSize = inIdx.Length;
        int seqLen = inIdx[0].Length;

        float scaleVal = (float)Math.Sqrt(Config.EmbDim);
        float[][][] x = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            x[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                x[b][t] = new float[Config.EmbDim];
                var vec = _tokEmb.Lookup(inIdx[b][t]);
                for (int d = 0; d < Config.EmbDim; d++)
                {
                    x[b][t][d] = vec[d] * scaleVal;
                }
            }
        }

        for (int i = 0; i < _blocks.Count; i++)
        {
            var cache = (caches != null && i < caches.Count) ? caches[i] : null;
            x = _blocks[i].Forward(x, _cosGlobal, _sinGlobal, _cosLocal, _sinLocal, Config.SlidingWindow, startPos, cache);
        }

        x = _finalNorm.Forward3D(x);

        return _outHead.Forward3D(x);
    }
}

public class Gemma4Attention
{
    private readonly TensorOps.Linear _wQuery;
    private readonly TensorOps.Linear _wKey;
    private readonly TensorOps.Linear _wValue;
    private readonly TensorOps.Linear _outProj;
    private readonly Gemma4RMSNorm _qNorm;
    private readonly Gemma4RMSNorm _kNorm;
    private readonly Gemma4RMSNorm _vNorm;

    private readonly float _scaling;
    private readonly int _embDim;
    private readonly int _headDim;
    private readonly int _numHeads;
    private readonly int _numKvHeads;
    private readonly int _groupSize;

    public Gemma4Attention(GemmaConfig cfg, bool isSliding, Random? rand = null)
    {
        _embDim = cfg.EmbDim;
        _headDim = isSliding ? cfg.HeadDim : cfg.GlobalHeadDim;
        _numHeads = cfg.NHeads;
        _numKvHeads = cfg.NKvHeads;
        _groupSize = _numHeads / _numKvHeads;

        _wQuery = new TensorOps.Linear(_embDim, _numHeads * _headDim, useBias: false, rand: rand);
        _wKey = new TensorOps.Linear(_embDim, _numKvHeads * _headDim, useBias: false, rand: rand);
        _wValue = new TensorOps.Linear(_embDim, _numKvHeads * _headDim, useBias: false, rand: rand);
        _outProj = new TensorOps.Linear(_numHeads * _headDim, _embDim, useBias: false, rand: rand);

        _qNorm = new Gemma4RMSNorm(_headDim, cfg.LayerNormEps);
        _kNorm = new Gemma4RMSNorm(_headDim, cfg.LayerNormEps);
        _vNorm = new Gemma4RMSNorm(_headDim, cfg.LayerNormEps, withScale: false);

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

            for (int h = 0; h < _numHeads; h++) qHeads[h] = _qNorm.ForwardBatch(qHeads[h]);
            for (int h = 0; h < _numKvHeads; h++) kHeads[h] = _kNorm.ForwardBatch(kHeads[h]);
            for (int h = 0; h < _numKvHeads; h++) vHeads[h] = _vNorm.ForwardBatch(vHeads[h]);

            for (int h = 0; h < _numHeads; h++) qHeads[h] = RoPEHelper.ApplyRoPE(qHeads[h], cos, sin, startPos);
            for (int h = 0; h < _numKvHeads; h++) kHeads[h] = RoPEHelper.ApplyRoPE(kHeads[h], cos, sin, startPos);

            float[][][] contextHeads = new float[_numHeads][][];

            for (int h = 0; h < _numHeads; h++)
            {
                int kvh = h / _groupSize;

                float[][] q = qHeads[h];
                float[][] k = kHeads[kvh];
                float[][] v = vHeads[kvh];

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

public class Gemma4DenseBlock
{
    private readonly Gemma4Attention _attn;
    private readonly GemmaFeedForward _mlp;
    private readonly Gemma4RMSNorm _inputLayernorm;
    private readonly Gemma4RMSNorm _postAttentionLayernorm;
    private readonly Gemma4RMSNorm _preFeedforwardLayernorm;
    private readonly Gemma4RMSNorm _postFeedforwardLayernorm;

    private readonly TensorOps.Linear? _perLayerInputGate;
    private readonly TensorOps.Linear? _perLayerProjection;
    private readonly Gemma4RMSNorm? _postPerLayerInputNorm;

    private readonly float _layerScalar = 1.0f;
    private readonly int _hiddenSizePerLayerInput;
    public string LayerType { get; }

    public Gemma4DenseBlock(GemmaConfig cfg, int layerIdx, Random? rand = null)
    {
        var r = rand ?? new Random(42);
        LayerType = cfg.LayerTypes[layerIdx];
        bool isSliding = LayerType == "sliding_attention";

        _attn = new Gemma4Attention(cfg, isSliding, r);
        _mlp = new GemmaFeedForward(cfg, multiplier: (cfg.UseDoubleWideMlp && (layerIdx >= cfg.NLayers - cfg.NumKvSharedLayers)) ? 2 : 1, rand: r);

        _inputLayernorm = new Gemma4RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        _postAttentionLayernorm = new Gemma4RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        _preFeedforwardLayernorm = new Gemma4RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        _postFeedforwardLayernorm = new Gemma4RMSNorm(cfg.EmbDim, cfg.LayerNormEps);

        _hiddenSizePerLayerInput = cfg.HiddenSizePerLayerInput;
        if (_hiddenSizePerLayerInput > 0)
        {
            _perLayerInputGate = new TensorOps.Linear(cfg.EmbDim, _hiddenSizePerLayerInput, useBias: false, rand: r);
            _perLayerProjection = new TensorOps.Linear(_hiddenSizePerLayerInput, cfg.EmbDim, useBias: false, rand: r);
            _postPerLayerInputNorm = new Gemma4RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        }
    }

    public float[][][] Forward(float[][][] x, float[][][]? perLayerInputForBlock, float[][] cos, float[][] sin, int slidingWindow, int startPos = 0, KVCache? cache = null)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        int dim = x[0][0].Length;

        float[][][] shortcut = x;
        float[][][] normX = _inputLayernorm.Forward3D(x);
        float[][][] attnOut = _attn.Forward(normX, cos, sin, slidingWindow, startPos, cache);
        float[][][] postAttnNorm = _postAttentionLayernorm.Forward3D(attnOut);

        float[][][] xAttnResidual = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            xAttnResidual[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                xAttnResidual[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    xAttnResidual[b][t][d] = shortcut[b][t][d] + postAttnNorm[b][t][d];
                }
            }
        }

        shortcut = xAttnResidual;
        float[][][] normXff = _preFeedforwardLayernorm.Forward3D(xAttnResidual);
        float[][][] ffOut = _mlp.Forward3D(normXff);
        float[][][] postFfNorm = _postFeedforwardLayernorm.Forward3D(ffOut);

        float[][][] output = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            output[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                output[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    output[b][t][d] = shortcut[b][t][d] + postFfNorm[b][t][d];
                }
            }
        }

        if (_hiddenSizePerLayerInput > 0 && perLayerInputForBlock != null)
        {
            float[][][] gatedPerLayer = _perLayerInputGate!.Forward3D(output);
            float[][][] projectedPerLayer = new float[batchSize][][];
            GELU gelu = new();

            for (int b = 0; b < batchSize; b++)
            {
                projectedPerLayer[b] = new float[seqLen][];
                for (int t = 0; t < seqLen; t++)
                {
                    float[] gateVal = new float[_hiddenSizePerLayerInput];
                    for (int j = 0; j < _hiddenSizePerLayerInput; j++)
                    {
                        gateVal[j] = gelu.Forward(gatedPerLayer[b][t][j]) * perLayerInputForBlock[b][t][j];
                    }
                    float[] projVec = _perLayerProjection!.Forward(gateVal);
                    projectedPerLayer[b][t] = _postPerLayerInputNorm!.Forward(projVec);
                }
            }

            for (int b = 0; b < batchSize; b++)
            {
                for (int t = 0; t < seqLen; t++)
                {
                    for (int d = 0; d < dim; d++)
                    {
                        output[b][t][d] += projectedPerLayer[b][t][d];
                    }
                }
            }
        }

        if (Math.Abs(_layerScalar - 1.0f) > 1e-5f)
        {
            for (int b = 0; b < batchSize; b++)
            {
                for (int t = 0; t < seqLen; t++)
                {
                    for (int d = 0; d < dim; d++)
                    {
                        output[b][t][d] *= _layerScalar;
                    }
                }
            }
        }

        return output;
    }
}

public class Gemma4Model
{
    private readonly Embedding _tokEmb;
    private readonly List<Gemma4DenseBlock> _blocks = new();
    private readonly Gemma4RMSNorm _finalNorm;
    private readonly TensorOps.Linear _outHead;

    private readonly Embedding? _embedTokensPerLayer;
    private readonly TensorOps.Linear? _perLayerModelProjection;
    private readonly Gemma4RMSNorm? _perLayerProjectionNorm;

    private readonly float[][] _cosLocal;
    private readonly float[][] _sinLocal;
    private readonly float[][] _cosGlobal;
    private readonly float[][] _sinGlobal;

    public GemmaConfig Config { get; }

    public Gemma4Model(GemmaConfig cfg, Random? rand = null)
    {
        Config = cfg;
        var r = rand ?? new Random(42);

        _tokEmb = new Embedding(cfg.VocabSize, cfg.EmbDim, r);

        for (int i = 0; i < cfg.NLayers; i++)
        {
            _blocks.Add(new Gemma4DenseBlock(cfg, i, r));
        }

        _finalNorm = new Gemma4RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        _outHead = new TensorOps.Linear(cfg.EmbDim, cfg.VocabSize, useBias: false, rand: r);

        if (cfg.TieWordEmbeddings)
        {
        }

        int layerInputDim = cfg.HiddenSizePerLayerInput;
        if (layerInputDim > 0)
        {
            _embedTokensPerLayer = new Embedding(cfg.VocabSizePerLayerInput, cfg.NLayers * layerInputDim, r);
            _perLayerModelProjection = new TensorOps.Linear(cfg.EmbDim, cfg.NLayers * layerInputDim, useBias: false, rand: r);
            _perLayerProjectionNorm = new Gemma4RMSNorm(layerInputDim, cfg.LayerNormEps);
        }

        var (cosL, sinL) = RoPEHelper.ComputeRopeParams(cfg.HeadDim, cfg.RopeLocalBase, cfg.ContextLength);
        _cosLocal = cosL;
        _sinLocal = sinL;

        var (cosG, sinG) = RoPEHelper.ComputeRopeParams(cfg.GlobalHeadDim, cfg.RopeGlobalBase, cfg.ContextLength);
        _cosGlobal = cosG;
        _sinGlobal = sinG;
    }

    public float[][][] Forward(int[][] inIdx, int startPos = 0, List<KVCache>? caches = null)
    {
        int batchSize = inIdx.Length;
        int seqLen = inIdx[0].Length;

        float scaleVal = (float)Math.Sqrt(Config.EmbDim);
        float[][][] x = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            x[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                x[b][t] = new float[Config.EmbDim];
                var vec = _tokEmb.Lookup(inIdx[b][t]);
                for (int d = 0; d < Config.EmbDim; d++)
                {
                    x[b][t][d] = vec[d] * scaleVal;
                }
            }
        }

        float[][][][]? perLayerInputs = null; // shape: [batchSize][seqLen][nLayers][layerInputDim]
        int layerInputDim = Config.HiddenSizePerLayerInput;
        if (layerInputDim > 0)
        {
            perLayerInputs = new float[batchSize][][][];
            for (int b = 0; b < batchSize; b++)
            {
                perLayerInputs[b] = new float[seqLen][][];
                for (int t = 0; t < seqLen; t++)
                {
                    perLayerInputs[b][t] = new float[Config.NLayers][];
                    float[] lLookup = _embedTokensPerLayer!.Lookup(inIdx[b][t]);
                    float[] lProj = _perLayerModelProjection!.Forward(x[b][t]);

                    float scaleProj = 1.0f / (float)Math.Sqrt(Config.EmbDim);
                    float scaleLookup = (float)Math.Sqrt(layerInputDim);

                    for (int i = 0; i < Config.NLayers; i++)
                    {
                        float[] sliceProj = new float[layerInputDim];
                        float[] sliceLookup = new float[layerInputDim];
                        for (int j = 0; j < layerInputDim; j++)
                        {
                            sliceProj[j] = lProj[i * layerInputDim + j] * scaleProj;
                            sliceLookup[j] = lLookup[i * layerInputDim + j] * scaleLookup;
                        }

                        float[] normProj = _perLayerProjectionNorm!.Forward(sliceProj);
                        perLayerInputs[b][t][i] = new float[layerInputDim];
                        for (int j = 0; j < layerInputDim; j++)
                        {
                            perLayerInputs[b][t][i][j] = (normProj[j] + sliceLookup[j]) * 0.70710678f; // scale by 1/sqrt(2)
                        }
                    }
                }
            }
        }

        for (int i = 0; i < _blocks.Count; i++)
        {
            var cache = (caches != null && i < caches.Count) ? caches[i] : null;
            bool isSliding = _blocks[i].LayerType == "sliding_attention";
            float[][] cos = isSliding ? _cosLocal : _cosGlobal;
            float[][] sin = isSliding ? _sinLocal : _sinGlobal;
            int window = isSliding ? Config.SlidingWindow : -1;

            float[][][]? perLayerInputForBlock = null;
            if (perLayerInputs != null)
            {
                perLayerInputForBlock = new float[batchSize][][];
                for (int b = 0; b < batchSize; b++)
                {
                    perLayerInputForBlock[b] = new float[seqLen][];
                    for (int t = 0; t < seqLen; t++)
                    {
                        perLayerInputForBlock[b][t] = perLayerInputs[b][t][i];
                    }
                }
            }

            x = _blocks[i].Forward(x, perLayerInputForBlock, cos, sin, window, startPos, cache);
        }

        x = _finalNorm.Forward3D(x);

        float[][][] logits = _outHead.Forward3D(x);

        float softcap = Config.FinalLogitSoftcap;
        if (softcap > 0)
        {
            for (int b = 0; b < batchSize; b++)
            {
                for (int t = 0; t < seqLen; t++)
                {
                    for (int v = 0; v < Config.VocabSize; v++)
                    {
                        logits[b][t][v] = (float)Math.Tanh(logits[b][t][v] / softcap) * softcap;
                    }
                }
            }
        }

        return logits;
    }
}
