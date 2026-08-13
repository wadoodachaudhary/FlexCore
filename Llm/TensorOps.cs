using System;

namespace Fx.ControlKit.Llm;

public static class TensorOps
{
    public class Linear
    {
        public float[][] Weights { get; }
        public float[]? Bias { get; }

        public Linear(int inDim, int outDim, bool useBias = false, Random? rand = null)
        {
            Weights = new float[outDim][];
            var r = rand ?? new Random(42);
            
            float limit = (float)Math.Sqrt(6.0 / (inDim + outDim));
            for (int i = 0; i < outDim; i++)
            {
                Weights[i] = new float[inDim];
                for (int j = 0; j < inDim; j++)
                {
                    Weights[i][j] = (float)(r.NextDouble() * 2.0 - 1.0) * limit;
                }
            }

            if (useBias)
            {
                Bias = new float[outDim];
            }
        }

        public float[] Forward(float[] x)
        {
            int outDim = Weights.Length;
            int inDim = Weights[0].Length;
            float[] output = new float[outDim];

            if ((long)outDim * inDim >= 1000000)
            {
                System.Threading.Tasks.Parallel.For(0, outDim, i =>
                {
                    float sum = Bias?[i] ?? 0f;
                    float[] row = Weights[i];
                    for (int j = 0; j < inDim; j++)
                    {
                        sum += x[j] * row[j];
                    }
                    output[i] = sum;
                });
            }
            else
            {
                for (int i = 0; i < outDim; i++)
                {
                    float sum = Bias?[i] ?? 0f;
                    float[] row = Weights[i];
                    for (int j = 0; j < inDim; j++)
                    {
                        sum += x[j] * row[j];
                    }
                    output[i] = sum;
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

    public static float[][] MatMul(float[][] A, float[][] B)
    {
        int M = A.Length;
        int K = A[0].Length;
        int N = B[0].Length;

        float[][] C = new float[M][];
        for (int i = 0; i < M; i++)
        {
            C[i] = new float[N];
            for (int j = 0; j < N; j++)
            {
                float sum = 0f;
                for (int k = 0; k < K; k++)
                {
                    sum += A[i][k] * B[k][j];
                }
                C[i][j] = sum;
            }
        }
        return C;
    }

    public static float[][] Transpose(float[][] A)
    {
        int M = A.Length;
        int N = A[0].Length;
        float[][] AT = new float[N][];
        for (int j = 0; j < N; j++)
        {
            AT[j] = new float[M];
            for (int i = 0; i < M; i++)
            {
                AT[j][i] = A[i][j];
            }
        }
        return AT;
    }

    public static void Softmax(float[][] matrix)
    {
        for (int i = 0; i < matrix.Length; i++)
        {
            float max = float.NegativeInfinity;
            for (int j = 0; j < matrix[i].Length; j++)
            {
                if (matrix[i][j] > max) max = matrix[i][j];
            }

            float sum = 0f;
            for (int j = 0; j < matrix[i].Length; j++)
            {
                if (matrix[i][j] == float.NegativeInfinity) continue;
                sum += (float)Math.Exp(matrix[i][j] - max);
            }

            for (int j = 0; j < matrix[i].Length; j++)
            {
                if (matrix[i][j] == float.NegativeInfinity)
                {
                    matrix[i][j] = 0f;
                }
                else
                {
                    matrix[i][j] = (float)Math.Exp(matrix[i][j] - max) / sum;
                }
            }
        }
    }

    public static void Dropout(float[][] matrix, float dropoutRate, Random rand)
    {
        if (dropoutRate <= 0f) return;
        float scale = 1f / (1f - dropoutRate);

        for (int i = 0; i < matrix.Length; i++)
        {
            for (int j = 0; j < matrix[i].Length; j++)
            {
                if (rand.NextDouble() < dropoutRate)
                {
                    matrix[i][j] = 0f;
                }
                else
                {
                    matrix[i][j] *= scale;
                }
            }
        }
    }
}
