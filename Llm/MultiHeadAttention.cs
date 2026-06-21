using System;

namespace Fx.ControlKit.Llm;

public class MultiHeadAttention
{
    private readonly TensorOps.Linear _wQuery;
    private readonly TensorOps.Linear _wKey;
    private readonly TensorOps.Linear _wValue;
    private readonly TensorOps.Linear _outProj;
    private readonly float _dropoutRate;
    private readonly Random _random = new(42);

    public int DIn { get; }
    public int DOut { get; }
    public int ContextLength { get; }
    public int NumHeads { get; }
    public int HeadDim { get; }

    public MultiHeadAttention(int dIn, int dOut, int contextLength, float dropout, int numHeads, bool useBias = false)
    {
        if (dOut % numHeads != 0)
        {
            throw new ArgumentException("dOut must be divisible by numHeads");
        }

        DIn = dIn;
        DOut = dOut;
        ContextLength = contextLength;
        NumHeads = numHeads;
        HeadDim = dOut / numHeads;
        _dropoutRate = dropout;

        _wQuery = new TensorOps.Linear(dIn, dOut, useBias);
        _wKey = new TensorOps.Linear(dIn, dOut, useBias);
        _wValue = new TensorOps.Linear(dIn, dOut, useBias);
        _outProj = new TensorOps.Linear(dOut, dOut, useBias);
    }

    public float[][][] Forward(float[][][] x)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        float scale = 1.0f / (float)Math.Sqrt(HeadDim);

        float[][][] output = new float[batchSize][][];

        for (int b = 0; b < batchSize; b++)
        {
            float[][] queries = _wQuery.ForwardBatch(x[b]);
            float[][] keys = _wKey.ForwardBatch(x[b]);
            float[][] values = _wValue.ForwardBatch(x[b]);

            float[][][] queriesHead = SplitHeads(queries, seqLen);
            float[][][] keysHead = SplitHeads(keys, seqLen);
            float[][][] valuesHead = SplitHeads(values, seqLen);

            float[][][] contextHead = new float[NumHeads][][];

            for (int h = 0; h < NumHeads; h++)
            {
                float[][] q = queriesHead[h];
                float[][] k = keysHead[h];
                float[][] v = valuesHead[h];

                float[][] attnScores = new float[seqLen][];
                for (int i = 0; i < seqLen; i++)
                {
                    attnScores[i] = new float[seqLen];
                    for (int j = 0; j < seqLen; j++)
                    {
                        float sum = 0f;
                        for (int d = 0; d < HeadDim; d++)
                        {
                            sum += q[i][d] * k[j][d];
                        }
                        attnScores[i][j] = sum * scale;

                        if (j > i)
                        {
                            attnScores[i][j] = float.NegativeInfinity;
                        }
                    }
                }

                TensorOps.Softmax(attnScores);

                TensorOps.Dropout(attnScores, _dropoutRate, _random);

                contextHead[h] = new float[seqLen][];
                for (int i = 0; i < seqLen; i++)
                {
                    contextHead[h][i] = new float[HeadDim];
                    for (int d = 0; d < HeadDim; d++)
                    {
                        float sum = 0f;
                        for (int j = 0; j < seqLen; j++)
                        {
                            sum += attnScores[i][j] * v[j][d];
                        }
                        contextHead[h][i][d] = sum;
                    }
                }
            }

            float[][] concatenatedContext = ConcatHeads(contextHead, seqLen);

            output[b] = _outProj.ForwardBatch(concatenatedContext);
        }

        return output;
    }

    private float[][][] SplitHeads(float[][] projection, int seqLen)
    {
        float[][][] heads = new float[NumHeads][][];
        for (int h = 0; h < NumHeads; h++)
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
