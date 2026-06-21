using System;

namespace Fx.ControlKit.Llm;

public class CausalAttention
{
    private readonly TensorOps.Linear _wQuery;
    private readonly TensorOps.Linear _wKey;
    private readonly TensorOps.Linear _wValue;
    private readonly float _dropoutRate;
    private readonly Random _random = new(42);

    public int DIn { get; }
    public int DOut { get; }
    public int ContextLength { get; }

    public CausalAttention(int dIn, int dOut, int contextLength, float dropout, bool useBias = false)
    {
        DIn = dIn;
        DOut = dOut;
        ContextLength = contextLength;
        _dropoutRate = dropout;

        _wQuery = new TensorOps.Linear(dIn, dOut, useBias);
        _wKey = new TensorOps.Linear(dIn, dOut, useBias);
        _wValue = new TensorOps.Linear(dIn, dOut, useBias);
    }

    public float[][][] Forward(float[][][] x)
    {
        int batchSize = x.Length;
        int seqLen = x[0].Length;
        float scale = 1.0f / (float)Math.Sqrt(DOut);

        float[][][] output = new float[batchSize][][];

        for (int b = 0; b < batchSize; b++)
        {
            float[][] queries = _wQuery.ForwardBatch(x[b]); // [seqLen, dOut]
            float[][] keys = _wKey.ForwardBatch(x[b]);       // [seqLen, dOut]
            float[][] values = _wValue.ForwardBatch(x[b]);   // [seqLen, dOut]

            float[][] attnScores = new float[seqLen][];
            for (int i = 0; i < seqLen; i++)
            {
                attnScores[i] = new float[seqLen];
                for (int j = 0; j < seqLen; j++)
                {
                    float sum = 0f;
                    for (int d = 0; d < DOut; d++)
                    {
                        sum += queries[i][d] * keys[j][d];
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

            output[b] = new float[seqLen][];
            for (int i = 0; i < seqLen; i++)
            {
                output[b][i] = new float[DOut];
                for (int d = 0; d < DOut; d++)
                {
                    float sum = 0f;
                    for (int j = 0; j < seqLen; j++)
                    {
                        sum += attnScores[i][j] * values[j][d];
                    }
                    output[b][i][d] = sum;
                }
            }
        }

        return output;
    }
}
