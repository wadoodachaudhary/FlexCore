using System;
using System.Linq;

namespace Fx.ControlKit.Llm;

public static class RoPEHelper
{
    public static (float[][] cos, float[][] sin) ComputeRopeParams(int headDim, double thetaBase, int contextLength)
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
                cos[t][i] = (float)Math.Cos(angle);
                sin[t][i] = (float)Math.Sin(angle);

                cos[t][i + halfDim] = cos[t][i];
                sin[t][i + halfDim] = sin[t][i];
            }
        }

        return (cos, sin);
    }

    public static float[][] ApplyRoPE(float[][] x, float[][] cos, float[][] sin, int startPos = 0)
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
                float x1 = x[t][d];
                float x2 = x[t][d + halfDim];

                float c = cos[globalPos][d];
                float s = sin[globalPos][d];

                output[t][d] = (x1 * c) + (-x2 * s);
                output[t][d + halfDim] = (x2 * c) + (x1 * s);
            }
        }
        return output;
    }
}

public class GroupedQueryAttention
{
    private readonly TensorOps.Linear _wQuery;
    private readonly TensorOps.Linear _wKey;
    private readonly TensorOps.Linear _wValue;
    private readonly TensorOps.Linear _outProj;
    private readonly LayerNorm? _qNorm;
    private readonly LayerNorm? _kNorm;
    private readonly float _dropoutRate;
    private readonly Random _random = new(42);

    public int DIn { get; }
    public int DOut { get; }
    public int ContextLength { get; }
    public int NumHeadsQ { get; }
    public int NumHeadsKV { get; }
    public int HeadDim { get; }
    public int NumGroups { get; }

    public GroupedQueryAttention(int dIn, int dOut, int contextLength, float dropout, int numHeadsQ, int numHeadsKV, bool qkNorm = false, bool useBias = false)
    {
        if (numHeadsQ % numHeadsKV != 0)
        {
            throw new ArgumentException("numHeadsQ must be divisible by numHeadsKV");
        }

        DIn = dIn;
        DOut = dOut;
        ContextLength = contextLength;
        NumHeadsQ = numHeadsQ;
        NumHeadsKV = numHeadsKV;
        HeadDim = dOut / numHeadsQ;
        NumGroups = numHeadsQ / numHeadsKV;
        _dropoutRate = dropout;

        _wQuery = new TensorOps.Linear(dIn, numHeadsQ * HeadDim, useBias);
        _wKey = new TensorOps.Linear(dIn, numHeadsKV * HeadDim, useBias);
        _wValue = new TensorOps.Linear(dIn, numHeadsKV * HeadDim, useBias);
        _outProj = new TensorOps.Linear(dOut, dIn, useBias);

        if (qkNorm)
        {
            _qNorm = new LayerNorm(HeadDim);
            _kNorm = new LayerNorm(HeadDim);
        }
    }

    public float[][][] Forward(float[][][] x, float[][]? cos = null, float[][]? sin = null, int startPos = 0, KVCache? cache = null)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        float scale = 1.0f / (float)Math.Sqrt(HeadDim);

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
            float[][][] qHeads = SplitHeads(queries[b], seqLen, NumHeadsQ);
            float[][][] kHeads = SplitHeads(finalKeys[b], targetSeqLen, NumHeadsKV);
            float[][][] vHeads = SplitHeads(finalValues[b], targetSeqLen, NumHeadsKV);

            if (_qNorm != null && _kNorm != null)
            {
                for (int qh = 0; qh < NumHeadsQ; qh++)
                {
                    qHeads[qh] = _qNorm.ForwardBatch(qHeads[qh]);
                }
                for (int kvh = 0; kvh < NumHeadsKV; kvh++)
                {
                    kHeads[kvh] = _kNorm.ForwardBatch(kHeads[kvh]);
                }
            }

            if (cos != null && sin != null)
            {
                for (int qh = 0; qh < NumHeadsQ; qh++)
                {
                    qHeads[qh] = RoPEHelper.ApplyRoPE(qHeads[qh], cos, sin, startPos);
                }
                for (int kvh = 0; kvh < NumHeadsKV; kvh++)
                {
                    kHeads[kvh] = RoPEHelper.ApplyRoPE(kHeads[kvh], cos, sin, startPos);
                }
            }

            float[][][] contextHeads = new float[NumHeadsQ][][];

            for (int qh = 0; qh < NumHeadsQ; qh++)
            {
                int kvh = qh / NumGroups;

                float[][] q = qHeads[qh];
                float[][] k = kHeads[kvh];
                float[][] v = vHeads[kvh];

                float[][] attnScores = new float[seqLen][];
                for (int i = 0; i < seqLen; i++)
                {
                    attnScores[i] = new float[targetSeqLen];
                    for (int j = 0; j < targetSeqLen; j++)
                    {
                        float sum = 0f;
                        for (int d = 0; d < HeadDim; d++)
                        {
                            sum += q[i][d] * k[j][d];
                        }
                        attnScores[i][j] = sum * scale;

                        if (seqLen > 1 && j > i)
                        {
                            attnScores[i][j] = float.NegativeInfinity;
                        }
                    }
                }

                TensorOps.Softmax(attnScores);
                TensorOps.Dropout(attnScores, _dropoutRate, _random);

                contextHeads[qh] = new float[seqLen][];
                for (int i = 0; i < seqLen; i++)
                {
                    contextHeads[qh][i] = new float[HeadDim];
                    for (int d = 0; d < HeadDim; d++)
                    {
                        float sum = 0f;
                        for (int j = 0; j < targetSeqLen; j++)
                        {
                            sum += attnScores[i][j] * v[j][d];
                        }
                        contextHeads[qh][i][d] = sum;
                    }
                }
            }

            float[][] concatenatedContext = ConcatHeads(contextHeads, seqLen);
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
            for (int h = 0; h < NumHeadsQ; h++)
            {
                Array.Copy(heads[h][i], 0, concatenated[i], h * HeadDim, HeadDim);
            }
        }
        return concatenated;
    }
}
