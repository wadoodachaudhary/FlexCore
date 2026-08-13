using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class MuonOptimizer
{
    private readonly float _lr;
    private readonly float _momentum;
    private readonly float _weightDecay;
    private readonly Dictionary<float[][], float[][]> _states = new(); // momentum state for each parameter matrix

    public MuonOptimizer(float lr, float momentum = 0.95f, float weightDecay = 0.1f)
    {
        _lr = lr;
        _momentum = momentum;
        _weightDecay = weightDecay;
    }

    public void Step(float[][] w, float[][] grad)
    {
        int rows = w.Length;
        int cols = w[0].Length;

        // 1. Get or create momentum state
        if (!_states.TryGetValue(w, out var m))
        {
            m = new float[rows][];
            for (int i = 0; i < rows; i++)
            {
                m[i] = new float[cols];
            }
            _states[w] = m;
        }

        // 2. Momentum update: m = momentum * m + grad
        float[][] update = new float[rows][];
        for (int i = 0; i < rows; i++)
        {
            update[i] = new float[cols];
            for (int j = 0; j < cols; j++)
            {
                m[i][j] = _momentum * m[i][j] + grad[i][j];
                // Nesterov momentum update
                update[i][j] = grad[i][j] + _momentum * m[i][j];
            }
        }

        // 3. Orthogonalize the update matrix using Newton-Schulz 5
        float[][] orthogonalUpdate = NewtonSchulz5(update);

        // 4. Update weight: w = w - lr * (orthogonalUpdate + wd * w)
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                w[i][j] = w[i][j] - _lr * (orthogonalUpdate[i][j] + _weightDecay * w[i][j]);
            }
        }
    }

    public static float[][] NewtonSchulz5(float[][] G, int steps = 5, float eps = 1e-7f)
    {
        int rows = G.Length;
        int cols = G[0].Length;

        bool transposed = false;
        float[][] X;

        if (rows > cols)
        {
            transposed = true;
            X = Transpose(G);
        }
        else
        {
            X = Clone(G);
        }

        int M = X.Length;
        int N = X[0].Length;

        // Frobenius norm of X
        double sumSq = 0;
        for (int i = 0; i < M; i++)
        {
            for (int j = 0; j < N; j++)
            {
                sumSq += (double)X[i][j] * X[i][j];
            }
        }
        float norm = (float)Math.Sqrt(sumSq);

        // Normalize
        float scale = 1.0f / (norm + eps);
        for (int i = 0; i < M; i++)
        {
            for (int j = 0; j < N; j++)
            {
                X[i][j] *= scale;
            }
        }

        // Constants optimised for convergence of polar decomposition
        const float a = 3.4445f;
        const float b = -4.7750f;
        const float c = 2.0315f;

        for (int step = 0; step < steps; step++)
        {
            // A = X @ X^T -> Size [M][M]
            float[][] A = new float[M][];
            for (int i = 0; i < M; i++)
            {
                A[i] = new float[M];
                for (int j = 0; j < M; j++)
                {
                    float sum = 0f;
                    for (int k = 0; k < N; k++)
                    {
                        sum += X[i][k] * X[j][k];
                    }
                    A[i][j] = sum;
                }
            }

            // A_sq = A @ A -> Size [M][M]
            float[][] A_sq = new float[M][];
            for (int i = 0; i < M; i++)
            {
                A_sq[i] = new float[M];
                for (int j = 0; j < M; j++)
                {
                    float sum = 0f;
                    for (int k = 0; k < M; k++)
                    {
                        sum += A[i][k] * A[k][j];
                    }
                    A_sq[i][j] = sum;
                }
            }

            // B = b * A + c * A_sq -> Size [M][M]
            float[][] B = new float[M][];
            for (int i = 0; i < M; i++)
            {
                B[i] = new float[M];
                for (int j = 0; j < M; j++)
                {
                    B[i][j] = b * A[i][j] + c * A_sq[i][j];
                }
            }

            // X_new = a * X + B @ X -> Size [M][N]
            float[][] X_new = new float[M][];
            for (int i = 0; i < M; i++)
            {
                X_new[i] = new float[N];
                for (int j = 0; j < N; j++)
                {
                    float sum = 0f;
                    for (int k = 0; k < M; k++)
                    {
                        sum += B[i][k] * X[k][j];
                    }
                    X_new[i][j] = a * X[i][j] + sum;
                }
            }

            X = X_new;
        }

        if (transposed)
        {
            return Transpose(X);
        }
        return X;
    }

    private static float[][] Transpose(float[][] m)
    {
        int r = m.Length;
        int c = m[0].Length;
        float[][] t = new float[c][];
        for (int i = 0; i < c; i++)
        {
            t[i] = new float[r];
            for (int j = 0; j < r; j++)
            {
                t[i][j] = m[j][i];
            }
        }
        return t;
    }

    private static float[][] Clone(float[][] m)
    {
        int r = m.Length;
        int c = m[0].Length;
        float[][] clone = new float[r][];
        for (int i = 0; i < r; i++)
        {
            clone[i] = new float[c];
            Array.Copy(m[i], clone[i], c);
        }
        return clone;
    }
}
