using System;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class RMSNorm
{
    private readonly float[] _scale;
    private readonly float _eps;

    public RMSNorm(int embDim, float eps = 1e-5f)
    {
        _scale = Enumerable.Repeat(1.0f, embDim).ToArray();
        _eps = eps;
    }

    public float[] Forward(float[] x)
    {
        int n = x.Length;
        double meanSq = x.Select(val => (double)val * val).Average();
        float rms = (float)Math.Sqrt(meanSq + _eps);

        float[] output = new float[n];
        for (int i = 0; i < n; i++)
        {
            output[i] = _scale[i] * (x[i] / rms);
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

public static class SiLU
{
    public static float Forward(float x)
    {
        float sigmoid = 1.0f / (1.0f + (float)Math.Exp(-x));
        return x * sigmoid;
    }
}

public class SwiGLU
{
    private readonly TensorOps.Linear _linearW;
    private readonly TensorOps.Linear _linearV;

    public SwiGLU(int inDim, int outDim)
    {
        _linearW = new TensorOps.Linear(inDim, outDim, useBias: false);
        _linearV = new TensorOps.Linear(inDim, outDim, useBias: false);
    }

    public float[] Forward(float[] x)
    {
        float[] wOut = _linearW.Forward(x);
        float[] vOut = _linearV.Forward(x);

        int n = wOut.Length;
        float[] output = new float[n];
        for (int i = 0; i < n; i++)
        {
            output[i] = SiLU.Forward(wOut[i]) * vOut[i];
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
