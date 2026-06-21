using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class OlmoConfig
{
    public int VocabSize { get; set; } = 100278;
    public int ContextLength { get; set; } = 65536;
    public int EmbDim { get; set; } = 4096;
    public int NHeads { get; set; } = 32;
    public int NLayers { get; set; } = 32;
    public int HiddenDim { get; set; } = 11008;
    public int HeadDim { get; set; } = 128;
    public int NKvHeads { get; set; } = 32;
    public bool AttentionBias { get; set; } = false;
    public int SlidingWindow { get; set; } = 4096;
    public string[] LayerTypes { get; set; } = Array.Empty<string>();
    public double RopeBase { get; set; } = 500000.0;
    public float RopeAttentionFactor { get; set; } = 1.2079442f;
    public string RopeType { get; set; } = "yarn";
    public float RopeFactor { get; set; } = 8.0f;
    public int RopeOrigMax { get; set; } = 8192;
    public float BetaFast { get; set; } = 32.0f;
    public float BetaSlow { get; set; } = 1.0f;
    public float RmsNormEps { get; set; } = 1e-6f;
}

public class OlmoAttention
{
    private readonly TensorOps.Linear _wQuery;
    private readonly TensorOps.Linear _wKey;
    private readonly TensorOps.Linear _wValue;
    private readonly TensorOps.Linear _outProj;
    private readonly RMSNorm _qNorm;
    private readonly RMSNorm _kNorm;

    private readonly float _scaling;
    private readonly int _embDim;
    private readonly int _headDim;
    private readonly int _numHeads;
    private readonly int _numKvHeads;
    private readonly int _groupSize;

    public OlmoAttention(OlmoConfig cfg, Random? rand = null)
    {
        _embDim = cfg.EmbDim;
        _headDim = cfg.HeadDim;
        _numHeads = cfg.NHeads;
        _numKvHeads = cfg.NKvHeads;
        _groupSize = _numHeads / _numKvHeads;

        _wQuery = new TensorOps.Linear(_embDim, _numHeads * _headDim, cfg.AttentionBias, rand);
        _wKey = new TensorOps.Linear(_embDim, _numKvHeads * _headDim, cfg.AttentionBias, rand);
        _wValue = new TensorOps.Linear(_embDim, _numKvHeads * _headDim, cfg.AttentionBias, rand);
        _outProj = new TensorOps.Linear(_numHeads * _headDim, _embDim, cfg.AttentionBias, rand);

        _qNorm = new RMSNorm(_numHeads * _headDim, cfg.RmsNormEps);
        _kNorm = new RMSNorm(_numKvHeads * _headDim, cfg.RmsNormEps);

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
            float[][] qBatch = _wQuery.ForwardBatch(x[b]);
            float[][] kBatch = _wKey.ForwardBatch(x[b]);
            float[][] vBatch = _wValue.ForwardBatch(x[b]);

            queries[b] = _qNorm.ForwardBatch(qBatch);
            keys[b] = _kNorm.ForwardBatch(kBatch);
            values[b] = vBatch; // values are not normalized
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

            for (int h = 0; h < _numHeads; h++) qHeads[h] = RoPEHelper.ApplyRoPE(qHeads[h], cos, sin, startPos);
            for (int h = 0; h < _numKvHeads; h++) kHeads[h] = RoPEHelper.ApplyRoPE(kHeads[h], cos, sin, startPos);

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

            for (int h = 0; h < _numHeads; h++)
            {
                for (int i = 0; i < seqLen; i++)
                {
                    for (int d = 0; d < _headDim; d++)
                    {
                        qHeads[h][i][d] *= _scaling;
                    }
                }
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
                        attnScores[i][j] = sum;

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

public class OlmoFeedForward
{
    private readonly TensorOps.Linear _fc1;
    private readonly TensorOps.Linear _fc2;
    private readonly TensorOps.Linear _fc3;

    public OlmoFeedForward(OlmoConfig cfg, Random? rand = null)
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
            output[i] = SiLU.Forward(x_fc1[i]) * x_fc2[i]; // SwiGLU activation
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

public class OlmoTransformerBlock
{
    private readonly OlmoAttention _att;
    private readonly OlmoFeedForward _ff;
    private readonly RMSNorm _postAttentionLayernorm;
    private readonly RMSNorm _postFeedforwardLayernorm;

    public string AttnType { get; }

    public OlmoTransformerBlock(OlmoConfig cfg, string attnType, Random? rand = null)
    {
        AttnType = attnType;
        _att = new OlmoAttention(cfg, rand);
        _ff = new OlmoFeedForward(cfg, rand);
        _postAttentionLayernorm = new RMSNorm(cfg.EmbDim, cfg.RmsNormEps);
        _postFeedforwardLayernorm = new RMSNorm(cfg.EmbDim, cfg.RmsNormEps);
    }

    public float[][][] Forward(float[][][] x, float[][] cos, float[][] sin, int slidingWindow, int startPos = 0, KVCache? cache = null)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        int dim = x[0][0].Length;

        int window = (AttnType == "sliding_attention") ? slidingWindow : -1;

        float[][][] shortcut = x;
        float[][][] attnOut = _att.Forward(x, cos, sin, window, startPos, cache);
        float[][][] normAttn = _postAttentionLayernorm.Forward3D(attnOut);

        float[][][] xAttnResidual = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            xAttnResidual[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                xAttnResidual[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    xAttnResidual[b][t][d] = shortcut[b][t][d] + normAttn[b][t][d];
                }
            }
        }

        shortcut = xAttnResidual;
        float[][][] ffOut = _ff.Forward3D(xAttnResidual);
        float[][][] normFf = _postFeedforwardLayernorm.Forward3D(ffOut);

        float[][][] output = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            output[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                output[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    output[b][t][d] = shortcut[b][t][d] + normFf[b][t][d];
                }
            }
        }

        return output;
    }
}

public class Olmo3Model
{
    private readonly Embedding _tokEmb;
    private readonly List<OlmoTransformerBlock> _blocks = new();
    private readonly RMSNorm _finalNorm;
    private readonly TensorOps.Linear _outHead;

    private readonly float[][] _cos;
    private readonly float[][] _sin;

    public OlmoConfig Config { get; }

    public Olmo3Model(OlmoConfig cfg, Random? rand = null)
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
            _blocks.Add(new OlmoTransformerBlock(cfg, attnType, r));
        }

        _finalNorm = new RMSNorm(cfg.EmbDim, cfg.RmsNormEps);
        _outHead = new TensorOps.Linear(cfg.EmbDim, cfg.VocabSize, useBias: false, rand: r);

        var (cos, sin) = ComputeRopeParamsOlmo(
            cfg.HeadDim,
            cfg.RopeBase,
            cfg.ContextLength,
            cfg.RopeAttentionFactor,
            cfg.RopeType,
            cfg.RopeFactor,
            cfg.RopeOrigMax,
            cfg.BetaFast,
            cfg.BetaSlow
        );
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

        for (int i = 0; i < _blocks.Count; i++)
        {
            var cache = (caches != null && i < caches.Count) ? caches[i] : null;
            x = _blocks[i].Forward(x, _cos, _sin, Config.SlidingWindow, startPos, cache);
        }

        x = _finalNorm.Forward3D(x);

        return _outHead.Forward3D(x);
    }

    public static (float[][] cos, float[][] sin) ComputeRopeParamsOlmo(int headDim, double thetaBase, int contextLength, float attentionFactor = 1.0f, string ropeType = "default", float ropeFactor = 1.0f, int ropeOrigMax = 8192, float betaFast = 32.0f, float betaSlow = 1.0f)
    {
        int halfDim = headDim / 2;
        float[] invFreq = new float[halfDim];

        if (ropeType == "yarn")
        {
            double base_val = thetaBase;
            double max_pos = ropeOrigMax;

            Func<double, int, double, double, double> findCorrectionDim = (numRotations, dim, b, maxP) =>
            {
                return (dim * Math.Log(maxP / (numRotations * 2 * Math.PI))) / (2 * Math.Log(b));
            };

            double lowVal = findCorrectionDim(betaFast, headDim, base_val, max_pos);
            double highVal = findCorrectionDim(betaSlow, headDim, base_val, max_pos);
            int low = Math.Max((int)Math.Floor(lowVal), 0);
            int high = Math.Min((int)Math.Ceiling(highVal), headDim - 1);

            float[] posFreqs = new float[halfDim];
            float[] invFreqExtrapolation = new float[halfDim];
            float[] invFreqInterpolation = new float[halfDim];

            for (int i = 0; i < halfDim; i++)
            {
                posFreqs[i] = (float)Math.Pow(thetaBase, (double)(2 * i) / headDim);
                invFreqExtrapolation[i] = 1.0f / posFreqs[i];
                invFreqInterpolation[i] = 1.0f / (ropeFactor * posFreqs[i]);
            }

            float[] ramp = new float[halfDim];
            float range = high - low;
            if (Math.Abs(range) < 1e-5f) range += 0.001f;

            for (int i = 0; i < halfDim; i++)
            {
                float val = (i - low) / range;
                ramp[i] = Math.Max(0.0f, Math.Min(1.0f, val));
            }

            for (int i = 0; i < halfDim; i++)
            {
                float extFactor = 1.0f - ramp[i];
                invFreq[i] = invFreqInterpolation[i] * (1.0f - extFactor) + invFreqExtrapolation[i] * extFactor;
            }
        }
        else
        {
            for (int i = 0; i < halfDim; i++)
            {
                invFreq[i] = (float)(1.0 / Math.Pow(thetaBase, (double)(2 * i) / headDim));
            }
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
                cos[t][i] = (float)Math.Cos(angle) * attentionFactor;
                sin[t][i] = (float)Math.Sin(angle) * attentionFactor;

                cos[t][i + halfDim] = cos[t][i];
                sin[t][i + halfDim] = sin[t][i];
            }
        }

        return (cos, sin);
    }
}
