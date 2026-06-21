using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class QwenConfig
{
    public int VocabSize { get; set; } = 151936;
    public int ContextLength { get; set; } = 40960;
    public int EmbDim { get; set; } = 1024;
    public int NHeads { get; set; } = 16;
    public int NLayers { get; set; } = 28;
    public int HiddenDim { get; set; } = 3072;
    public int HeadDim { get; set; } = 128;
    public bool QkNorm { get; set; } = true;
    public int NKvGroups { get; set; } = 8;
    public double RopeBase { get; set; } = 1000000.0;
    public string[] LayerTypes { get; set; } = Array.Empty<string>();

    public int LinearConvKernelDim { get; set; } = 4;
    public int LinearKeyHeadDim { get; set; } = 128;
    public int LinearValueHeadDim { get; set; } = 128;
    public int LinearNumKeyHeads { get; set; } = 16;
    public int LinearNumValueHeads { get; set; } = 16;
    public float LayerNormEps { get; set; } = 1e-6f;
}

public class Qwen3_5RMSNormGated
{
    private readonly float[] _weight;
    private readonly float _eps;

    public Qwen3_5RMSNormGated(int dim, float eps = 1e-6f)
    {
        _weight = Enumerable.Repeat(1.0f, dim).ToArray();
        _eps = eps;
    }

    public float[] Forward(float[] x, float[] gate)
    {
        int n = x.Length;
        double sumSq = 0;
        for (int i = 0; i < n; i++) sumSq += (double)x[i] * x[i];
        float rms = (float)Math.Sqrt(sumSq / n + _eps);

        float[] output = new float[n];
        for (int i = 0; i < n; i++)
        {
            float normX = x[i] / rms;
            output[i] = _weight[i] * normX * SiLU.Forward(gate[i]);
        }
        return output;
    }
}

public class Qwen3_5GatedDeltaNet
{
    private readonly TensorOps.Linear _inProjQkv;
    private readonly TensorOps.Linear _inProjZ;
    private readonly TensorOps.Linear _inProjB;
    private readonly TensorOps.Linear _inProjA;
    private readonly TensorOps.Linear _outProj;
    private readonly Qwen3_5RMSNormGated _norm;

    private readonly float[][] _convWeight;
    private readonly float[] _dtBias;
    private readonly float[] _aLog;

    private readonly int _embDim;
    private readonly int _keyDim;
    private readonly int _valueDim;
    private readonly int _convDim;
    private readonly int _convKernelSize;
    private readonly int _numKHeads;
    private readonly int _numVHeads;
    private readonly int _headKDim;
    private readonly int _headVDim;

    public Qwen3_5GatedDeltaNet(QwenConfig cfg, Random? rand = null)
    {
        var r = rand ?? new Random(42);

        _embDim = cfg.EmbDim;
        _numKHeads = cfg.LinearNumKeyHeads;
        _numVHeads = cfg.LinearNumValueHeads;
        _headKDim = cfg.LinearKeyHeadDim;
        _headVDim = cfg.LinearValueHeadDim;

        _keyDim = _numKHeads * _headKDim;
        _valueDim = _numVHeads * _headVDim;
        _convDim = _keyDim * 2 + _valueDim;
        _convKernelSize = cfg.LinearConvKernelDim;

        _inProjQkv = new TensorOps.Linear(_embDim, _convDim, useBias: false, rand: r);
        _inProjZ = new TensorOps.Linear(_embDim, _valueDim, useBias: false, rand: r);
        _inProjB = new TensorOps.Linear(_embDim, _numVHeads, useBias: false, rand: r);
        _inProjA = new TensorOps.Linear(_embDim, _numVHeads, useBias: false, rand: r);
        _outProj = new TensorOps.Linear(_valueDim, _embDim, useBias: false, rand: r);

        _norm = new Qwen3_5RMSNormGated(_headVDim, cfg.LayerNormEps);

        _convWeight = new float[_convDim][];
        for (int i = 0; i < _convDim; i++)
        {
            _convWeight[i] = new float[_convKernelSize];
            for (int k = 0; k < _convKernelSize; k++)
            {
                _convWeight[i][k] = (float)(r.NextDouble() * 2.0 - 1.0) * 0.02f;
            }
        }

        _dtBias = Enumerable.Repeat(0.1f, _numVHeads).ToArray();
        _aLog = Enumerable.Repeat(-0.5f, _numVHeads).ToArray();
    }

    private static float Sigmoid(float x) => 1.0f / (1.0f + (float)Math.Exp(-x));
    private static float Softplus(float x) => (float)Math.Log(1.0 + Math.Exp(x));

    private static float[] L2Norm(float[] x, float eps = 1e-6f)
    {
        float sumSq = 0f;
        for (int i = 0; i < x.Length; i++) sumSq += x[i] * x[i];
        float invNorm = 1.0f / (float)Math.Sqrt(sumSq + eps);
        float[] output = new float[x.Length];
        for (int i = 0; i < x.Length; i++) output[i] = x[i] * invNorm;
        return output;
    }

    public float[][][] Forward(float[][][] x)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;

        float[][][] mixedQkv = _inProjQkv.Forward3D(x);
        float[][][] z = _inProjZ.Forward3D(x);
        float[][][] b = _inProjB.Forward3D(x);
        float[][][] a = _inProjA.Forward3D(x);

        float[][][] convOut = new float[batchSize][][];
        for (int batch = 0; batch < batchSize; batch++)
        {
            convOut[batch] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                convOut[batch][t] = new float[_convDim];
                for (int c = 0; c < _convDim; c++)
                {
                    float sum = 0f;
                    for (int k = 0; k < _convKernelSize; k++)
                    {
                        int sourceT = t - (_convKernelSize - 1) + k;
                        if (sourceT >= 0)
                        {
                            sum += mixedQkv[batch][sourceT][c] * _convWeight[c][k];
                        }
                    }
                    convOut[batch][t][c] = SiLU.Forward(sum);
                }
            }
        }

        float[][][] queries = new float[batchSize][][];
        float[][][] keys = new float[batchSize][][];
        float[][][] values = new float[batchSize][][];

        for (int batch = 0; batch < batchSize; batch++)
        {
            queries[batch] = new float[seqLen][];
            keys[batch] = new float[seqLen][];
            values[batch] = new float[seqLen][];

            for (int t = 0; t < seqLen; t++)
            {
                queries[batch][t] = new float[_keyDim];
                keys[batch][t] = new float[_keyDim];
                values[batch][t] = new float[_valueDim];

                Array.Copy(convOut[batch][t], 0, queries[batch][t], 0, _keyDim);
                Array.Copy(convOut[batch][t], _keyDim, keys[batch][t], 0, _keyDim);
                Array.Copy(convOut[batch][t], _keyDim * 2, values[batch][t], 0, _valueDim);
            }
        }

        float[][][] coreAttnOut = new float[batchSize][][];
        int numGroups = _numVHeads / _numKHeads;
        float scale = 1.0f / (float)Math.Sqrt(_headKDim);

        for (int batch = 0; batch < batchSize; batch++)
        {
            coreAttnOut[batch] = new float[seqLen][];
            float[][][] state = new float[_numVHeads][][]; // state per head: [headKDim][headVDim]
            for (int h = 0; h < _numVHeads; h++)
            {
                state[h] = new float[_headKDim][];
                for (int dk = 0; dk < _headKDim; dk++)
                {
                    state[h][dk] = new float[_headVDim];
                }
            }

            for (int t = 0; t < seqLen; t++)
            {
                coreAttnOut[batch][t] = new float[_valueDim];

                for (int h = 0; h < _numVHeads; h++)
                {
                    int kh = h / numGroups; // Key-head mapping

                    float[] q_t = new float[_headKDim];
                    float[] k_t = new float[_headKDim];
                    float[] v_t = new float[_headVDim];

                    Array.Copy(queries[batch][t], kh * _headKDim, q_t, 0, _headKDim);
                    Array.Copy(keys[batch][t], kh * _headKDim, k_t, 0, _headKDim);
                    Array.Copy(values[batch][t], h * _headVDim, v_t, 0, _headVDim);

                    q_t = L2Norm(q_t);
                    k_t = L2Norm(k_t);

                    for (int i = 0; i < _headKDim; i++) q_t[i] *= scale;

                    float b_val = Sigmoid(b[batch][t][h]);
                    float g_val = (float)Math.Exp(-Math.Exp(_aLog[h]) * Softplus(a[batch][t][h] + _dtBias[h]));

                    for (int dk = 0; dk < _headKDim; dk++)
                    {
                        for (int dv = 0; dv < _headVDim; dv++)
                        {
                            state[h][dk][dv] *= g_val;
                        }
                    }

                    float[] kv_mem = new float[_headVDim];
                    for (int dv = 0; dv < _headVDim; dv++)
                    {
                        float sum = 0f;
                        for (int dk = 0; dk < _headKDim; dk++)
                        {
                            sum += state[h][dk][dv] * k_t[dk];
                        }
                        kv_mem[dv] = sum;
                    }

                    float[] delta = new float[_headVDim];
                    for (int dv = 0; dv < _headVDim; dv++)
                    {
                        delta[dv] = (v_t[dv] - kv_mem[dv]) * b_val;
                    }

                    for (int dk = 0; dk < _headKDim; dk++)
                    {
                        for (int dv = 0; dv < _headVDim; dv++)
                        {
                            state[h][dk][dv] += k_t[dk] * delta[dv];
                        }
                    }

                    float[] headOut = new float[_headVDim];
                    for (int dv = 0; dv < _headVDim; dv++)
                    {
                        float sum = 0f;
                        for (int dk = 0; dk < _headKDim; dk++)
                        {
                            sum += state[h][dk][dv] * q_t[dk];
                        }
                        headOut[dv] = sum;
                    }

                    float[] z_t = new float[_headVDim];
                    Array.Copy(z[batch][t], h * _headVDim, z_t, 0, _headVDim);

                    float[] normHeadOut = _norm.Forward(headOut, z_t);

                    Array.Copy(normHeadOut, 0, coreAttnOut[batch][t], h * _headVDim, _headVDim);
                }
            }
        }

        return _outProj.Forward3D(coreAttnOut);
    }
}

public class QwenFeedForward
{
    private readonly TensorOps.Linear _fc1;
    private readonly TensorOps.Linear _fc2;
    private readonly TensorOps.Linear _fc3;

    public QwenFeedForward(QwenConfig cfg, Random? rand = null)
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
            output[i] = SiLU.Forward(x_fc1[i]) * x_fc2[i];
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

public class QwenTransformerBlock
{
    private readonly GroupedQueryAttention? _att;
    private readonly Qwen3_5GatedDeltaNet? _linearAtt;
    private readonly QwenFeedForward _ff;
    private readonly RMSNorm _norm1;
    private readonly RMSNorm _norm2;

    public string LayerType { get; }

    public QwenTransformerBlock(QwenConfig cfg, string layerType, Random? rand = null)
    {
        LayerType = layerType;
        _ff = new QwenFeedForward(cfg, rand);
        _norm1 = new RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        _norm2 = new RMSNorm(cfg.EmbDim, cfg.LayerNormEps);

        if (layerType == "linear_attention")
        {
            _linearAtt = new Qwen3_5GatedDeltaNet(cfg, rand);
        }
        else // full_attention or default
        {
            _att = new GroupedQueryAttention(
                dIn: cfg.EmbDim,
                dOut: cfg.NHeads * cfg.HeadDim,
                contextLength: cfg.ContextLength,
                dropout: 0.0f,
                numHeadsQ: cfg.NHeads,
                numHeadsKV: cfg.NKvGroups,
                qkNorm: cfg.QkNorm,
                useBias: false
            );
        }
    }

    public float[][][] Forward(float[][][] x, float[][]? cos = null, float[][]? sin = null, int startPos = 0, KVCache? cache = null)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        int dim = x[0][0].Length;

        float[][][] norm1X = _norm1.Forward3D(x);
        float[][][] attnOut;

        if (_linearAtt != null)
        {
            attnOut = _linearAtt.Forward(norm1X);
        }
        else
        {
            attnOut = _att!.Forward(norm1X, cos, sin, startPos, cache);
        }

        float[][][] xAttention = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            xAttention[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                xAttention[b][t] = new float[dim];
                for (int d = 0; d < dim; d++)
                {
                    xAttention[b][t][d] = x[b][t][d] + attnOut[b][t][d];
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
                    output[b][t][d] = xAttention[b][t][d] + ffOut[b][t][d];
                }
            }
        }

        return output;
    }
}

public class Qwen3Model
{
    private readonly Embedding _tokEmb;
    private readonly List<QwenTransformerBlock> _trfBlocks = new();
    private readonly RMSNorm _finalNorm;
    private readonly TensorOps.Linear _outHead;
    private readonly float[][] _cos;
    private readonly float[][] _sin;

    public QwenConfig Config { get; }

    public Qwen3Model(QwenConfig cfg, Random? rand = null)
    {
        Config = cfg;
        var r = rand ?? new Random(42);

        _tokEmb = new Embedding(cfg.VocabSize, cfg.EmbDim, r);

        string[] layers = cfg.LayerTypes;
        if (layers == null || layers.Length == 0)
        {
            layers = Enumerable.Repeat("full_attention", cfg.NLayers).ToArray();
        }

        foreach (var layerType in layers)
        {
            _trfBlocks.Add(new QwenTransformerBlock(cfg, layerType, r));
        }

        _finalNorm = new RMSNorm(cfg.EmbDim, cfg.LayerNormEps);
        _outHead = new TensorOps.Linear(cfg.EmbDim, cfg.VocabSize, useBias: false, rand: r);

        var (cos, sin) = RoPEHelper.ComputeRopeParams(cfg.HeadDim, cfg.RopeBase, cfg.ContextLength);
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
            x = _trfBlocks[i].Forward(x, _cos, _sin, startPos, cache);
        }

        x = _finalNorm.Forward3D(x);

        return _outHead.Forward3D(x);
    }
}

public class Qwen3_5Model : Qwen3Model
{
    public Qwen3_5Model(QwenConfig cfg, Random? rand = null) : base(cfg, rand)
    {
    }
}
