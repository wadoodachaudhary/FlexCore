using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Fx.ControlKit.Llm.Local;

/// <summary>
/// SIMD-vectorized, parallel floating point math operations for transformer layers.
/// </summary>
public static class TensorOps
{
    private const float SqrtTwoOverPi = 0.7978845608f;
    private const float GeluCoeff = 0.044715f;

    public static float Gelu(float x)
    {
        float inner = SqrtTwoOverPi * (x + GeluCoeff * x * x * x);
        return 0.5f * x * (1.0f + (float)Math.Tanh(inner));
    }

    public static void GeluInPlace(float[] x)
    {
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = Gelu(x[i]);
        }
    }

    public static float[] LayerNorm(float[] x, float[] scale, float[] shift, float eps = 1e-5f)
    {
        int n = x.Length;
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += x[i];
        float mean = sum / n;

        float varianceSum = 0f;
        for (int i = 0; i < n; i++)
        {
            float diff = x[i] - mean;
            varianceSum += diff * diff;
        }
        float invStd = 1.0f / (float)Math.Sqrt(varianceSum / n + eps);

        float[] output = new float[n];
        for (int i = 0; i < n; i++)
        {
            output[i] = (x[i] - mean) * invStd * scale[i] + shift[i];
        }
        return output;
    }

    public static float[] Softmax(float[] logits, double temperature = 1.0)
    {
        float[] output = new float[logits.Length];
        if (logits.Length == 0) return output;

        float max = float.MinValue;
        float temp = (float)Math.Max(0.01, temperature);

        for (int i = 0; i < logits.Length; i++)
        {
            float val = logits[i] / temp;
            if (val > max) max = val;
        }

        double sumExp = 0.0;
        for (int i = 0; i < logits.Length; i++)
        {
            float exp = (float)Math.Exp((logits[i] / temp) - max);
            output[i] = exp;
            sumExp += exp;
        }

        float invSum = (float)(1.0 / (sumExp > 0 ? sumExp : 1.0));
        for (int i = 0; i < logits.Length; i++)
        {
            output[i] *= invSum;
        }

        return output;
    }

    public static float DotProduct(float[] a, float[] b, int length)
    {
        int i = 0;
        int vectorSize = Vector<float>.Count;
        Vector<float> acc = Vector<float>.Zero;

        int limit = length - (length % vectorSize);
        for (; i < limit; i += vectorSize)
        {
            var va = new Vector<float>(a, i);
            var vb = new Vector<float>(b, i);
            acc += va * vb;
        }

        float sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    public static float[] Linear(float[] input, float[][] weights, float[]? bias = null)
    {
        int outDim = weights.Length;
        int inDim = input.Length;
        float[] output = new float[outDim];

        if (outDim >= 1024)
        {
            Parallel.For(0, outDim, i =>
            {
                float sum = bias != null && i < bias.Length ? bias[i] : 0f;
                output[i] = sum + DotProduct(input, weights[i], inDim);
            });
        }
        else
        {
            for (int i = 0; i < outDim; i++)
            {
                float sum = bias != null && i < bias.Length ? bias[i] : 0f;
                output[i] = sum + DotProduct(input, weights[i], inDim);
            }
        }

        return output;
    }

    public static float[] VectorAdd(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        float[] c = new float[n];
        int i = 0;
        int vectorSize = Vector<float>.Count;

        int limit = n - (n % vectorSize);
        for (; i < limit; i += vectorSize)
        {
            var va = new Vector<float>(a, i);
            var vb = new Vector<float>(b, i);
            (va + vb).CopyTo(c, i);
        }

        for (; i < n; i++)
        {
            c[i] = a[i] + b[i];
        }
        return c;
    }
}
