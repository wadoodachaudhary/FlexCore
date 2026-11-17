using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Fx.ControlKit.Llm.Local;

/// <summary>
/// Autoregressive text generator with temperature scaling and fast top-k filtering.
/// </summary>
public static class GptGenerator
{
    private static readonly Random Rand = new(42);

    public static List<int> Generate(
        GPTModel model,
        List<int> promptTokens,
        int maxNewTokens = 128,
        double temperature = 0.7,
        int topK = 40,
        int? eosTokenId = LocalTokenizer.EndOfTextTokenId,
        CancellationToken cancellationToken = default)
    {
        var currentSequence = new List<int>(promptTokens);

        for (int step = 0; step < maxNewTokens; step++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            int nextToken = SampleNextToken(model, currentSequence, temperature, topK);
            if (eosTokenId.HasValue && nextToken == eosTokenId.Value) break;

            currentSequence.Add(nextToken);
        }

        return currentSequence;
    }

    public static async IAsyncEnumerable<int> GenerateStreamAsync(
        GPTModel model,
        List<int> promptTokens,
        int maxNewTokens = 128,
        double temperature = 0.7,
        int topK = 40,
        int? eosTokenId = LocalTokenizer.EndOfTextTokenId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var currentSequence = new List<int>(promptTokens);

        for (int step = 0; step < maxNewTokens; step++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            int nextToken = SampleNextToken(model, currentSequence, temperature, topK);
            if (eosTokenId.HasValue && nextToken == eosTokenId.Value) break;

            currentSequence.Add(nextToken);
            yield return nextToken;

            // Yield control briefly to avoid starving UI / thread pool
            await Task.Yield();
        }
    }

    private static int SampleNextToken(GPTModel model, List<int> sequence, double temperature, int topK)
    {
        float[] logits = model.ForwardLastToken(sequence.ToArray());

        if (temperature <= 0.01)
        {
            // Greedy
            int bestIdx = 0;
            float maxLogit = float.MinValue;
            for (int i = 0; i < logits.Length; i++)
            {
                if (logits[i] > maxLogit)
                {
                    maxLogit = logits[i];
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        // Fast Top-K selection using a min-heap of size topK
        if (topK > 0 && topK < logits.Length)
        {
            var pq = new PriorityQueue<int, float>();
            for (int i = 0; i < logits.Length; i++)
            {
                if (pq.Count < topK)
                {
                    pq.Enqueue(i, logits[i]);
                }
                else if (pq.TryPeek(out _, out float minPqLogit) && logits[i] > minPqLogit)
                {
                    pq.Dequeue();
                    pq.Enqueue(i, logits[i]);
                }
            }

            float threshold = float.MinValue;
            if (pq.TryPeek(out _, out float lowestLogit))
            {
                threshold = lowestLogit;
            }

            for (int i = 0; i < logits.Length; i++)
            {
                if (logits[i] < threshold) logits[i] = float.MinValue;
            }
        }

        float[] probs = TensorOps.Softmax(logits, temperature);

        // Weighted sampling from multinomial distribution
        double r = Rand.NextDouble();
        double cumulative = 0.0;
        for (int i = 0; i < probs.Length; i++)
        {
            cumulative += probs[i];
            if (r <= cumulative) return i;
        }

        return probs.Length - 1;
    }
}
