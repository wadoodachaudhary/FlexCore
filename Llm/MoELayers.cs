using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class MoEFeedForward
{
    private readonly TensorOps.Linear _gate;
    private readonly TensorOps.Linear[] _fc1;
    private readonly TensorOps.Linear[] _fc2;
    private readonly TensorOps.Linear[] _fc3;
    private readonly int _numExpertsPerTok;
    private readonly int _numExperts;

    public MoEFeedForward(int embDim, int numExperts, int moeHiddenDim, int numExpertsPerTok)
    {
        _numExperts = numExperts;
        _numExpertsPerTok = numExpertsPerTok;

        _gate = new TensorOps.Linear(embDim, numExperts, useBias: false);

        _fc1 = new TensorOps.Linear[numExperts];
        _fc2 = new TensorOps.Linear[numExperts];
        _fc3 = new TensorOps.Linear[numExperts];

        for (int i = 0; i < numExperts; i++)
        {
            _fc1[i] = new TensorOps.Linear(embDim, moeHiddenDim, useBias: false);
            _fc2[i] = new TensorOps.Linear(embDim, moeHiddenDim, useBias: false);
            _fc3[i] = new TensorOps.Linear(moeHiddenDim, embDim, useBias: false);
        }
    }

    public float[] Forward(float[] x)
    {
        int embDim = x.Length;

        float[] scores = _gate.Forward(x);

        var topK = scores
            .Select((s, idx) => new { Score = s, Index = idx })
            .OrderByDescending(item => item.Score)
            .Take(_numExpertsPerTok)
            .ToList();

        float maxScore = topK.Max(item => item.Score);
        double sumExp = topK.Sum(item => Math.Exp(item.Score - maxScore));
        var probs = topK.Select(item => (float)(Math.Exp(item.Score - maxScore) / sumExp)).ToList();

        float[] output = new float[embDim];
        for (int k = 0; k < _numExpertsPerTok; k++)
        {
            int expertId = topK[k].Index;
            float weight = probs[k];

            float[] fc1Out = _fc1[expertId].Forward(x);
            float[] fc2Out = _fc2[expertId].Forward(x);

            float[] hidden = new float[fc1Out.Length];
            for (int i = 0; i < fc1Out.Length; i++)
            {
                hidden[i] = SiLU.Forward(fc1Out[i]) * fc2Out[i];
            }

            float[] expertOut = _fc3[expertId].Forward(hidden);

            for (int d = 0; d < embDim; d++)
            {
                output[d] += expertOut[d] * weight;
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
