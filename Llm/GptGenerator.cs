using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class GptGenerator
{
    private static readonly Random Rand = new(42);

    public static List<int> GenerateText(
        GPTModel model, 
        List<int> startTokens, 
        int maxNewTokens, 
        int contextSize, 
        double temperature = 1.0, 
        int? topK = null, 
        int? eosId = null)
    {
        try
        {
            var currentSequence = new List<int>(startTokens);

            for (int step = 0; step < maxNewTokens; step++)
            {
                int count = Math.Min(currentSequence.Count, contextSize);
                int startIndex = currentSequence.Count - count;
                var condTokens = currentSequence.GetRange(startIndex, count).ToArray();

                int[][] batchInput = new int[1][];
                batchInput[0] = condTokens;

                float[][] lastLogits;
                try
                {
                    lastLogits = model.ForwardLastToken(batchInput); // Shape: [1, vocabSize]
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Model ForwardLastToken failed at generation step {step} with context size {condTokens.Length}.", ex);
                }

                float[] lastTokenLogits = lastLogits[0]; // Shape: [vocabSize]

                float[] logitsCopy = new float[lastTokenLogits.Length];
                Array.Copy(lastTokenLogits, logitsCopy, lastTokenLogits.Length);

                int nextTokenId;

                if (temperature > 0.0)
                {
                    if (topK.HasValue && topK.Value > 0)
                    {
                        try
                        {
                            ApplyTopK(logitsCopy, topK.Value);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException($"ApplyTopK failed (k={topK.Value}, logits length={logitsCopy.Length}) at step {step}.", ex);
                        }
                    }

                    for (int i = 0; i < logitsCopy.Length; i++)
                    {
                        if (logitsCopy[i] != float.NegativeInfinity)
                        {
                            logitsCopy[i] = (float)(logitsCopy[i] / temperature);
                        }
                    }

                    float[] probs;
                    try
                    {
                        probs = CalculateSoftmax(logitsCopy);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"CalculateSoftmax failed (logits length={logitsCopy.Length}) at step {step}.", ex);
                    }

                    try
                    {
                        nextTokenId = SampleFromDistribution(probs);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"SampleFromDistribution failed at step {step}.", ex);
                    }
                }
                else
                {
                    try
                    {
                        nextTokenId = ArgMax(logitsCopy);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"ArgMax failed (values length={logitsCopy.Length}) at step {step}.", ex);
                    }
                }

                if (eosId.HasValue && nextTokenId == eosId.Value)
                {
                    break;
                }

                currentSequence.Add(nextTokenId);
            }

            return currentSequence;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"GenerateText failed globally with prompt length {startTokens.Count}.", ex);
        }
    }

    public static List<int> GenerateText(
        TorchSharpGPTModel model, 
        List<int> startTokens, 
        int maxNewTokens, 
        int contextSize, 
        double temperature = 1.0, 
        int? topK = null, 
        int? eosId = null)
    {
        try
        {
            var currentSequence = new List<int>(startTokens);

            for (int step = 0; step < maxNewTokens; step++)
            {
                int count = Math.Min(currentSequence.Count, contextSize);
                int startIndex = currentSequence.Count - count;
                var condTokens = currentSequence.GetRange(startIndex, count).ToArray();

                float[] lastTokenLogits;
                try
                {
                    lastTokenLogits = model.ForwardLastToken(condTokens); // Shape: [vocabSize]
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"TorchSharp Model ForwardLastToken failed at generation step {step} with context size {condTokens.Length}.", ex);
                }

                float[] logitsCopy = new float[lastTokenLogits.Length];
                Array.Copy(lastTokenLogits, logitsCopy, lastTokenLogits.Length);

                int nextTokenId;

                if (temperature > 0.0)
                {
                    if (topK.HasValue && topK.Value > 0)
                    {
                        try
                        {
                            ApplyTopK(logitsCopy, topK.Value);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException($"ApplyTopK failed (k={topK.Value}, logits length={logitsCopy.Length}) at step {step}.", ex);
                        }
                    }

                    for (int i = 0; i < logitsCopy.Length; i++)
                    {
                        if (logitsCopy[i] != float.NegativeInfinity)
                        {
                            logitsCopy[i] = (float)(logitsCopy[i] / temperature);
                        }
                    }

                    float[] probs;
                    try
                    {
                        probs = CalculateSoftmax(logitsCopy);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"CalculateSoftmax failed (logits length={logitsCopy.Length}) at step {step}.", ex);
                    }

                    try
                    {
                        nextTokenId = SampleFromDistribution(probs);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"SampleFromDistribution failed at step {step}.", ex);
                    }
                }
                else
                {
                    try
                    {
                        nextTokenId = ArgMax(logitsCopy);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"ArgMax failed (values length={logitsCopy.Length}) at step {step}.", ex);
                    }
                }

                if (eosId.HasValue && nextTokenId == eosId.Value)
                {
                    break;
                }

                currentSequence.Add(nextTokenId);
            }

            return currentSequence;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"GenerateText (TorchSharp) failed globally with prompt length {startTokens.Count}.", ex);
        }
    }

    private static void ApplyTopK(float[] logits, int k)
    {
        if (k >= logits.Length) return;

        float[] temp = new float[logits.Length];
        Array.Copy(logits, temp, logits.Length);

        Array.Sort(temp);

        float minAllowedVal = temp[temp.Length - k];

        for (int i = 0; i < logits.Length; i++)
        {
            if (logits[i] < minAllowedVal)
            {
                logits[i] = float.NegativeInfinity;
            }
        }
    }

    private static float[] CalculateSoftmax(float[] logits)
    {
        int n = logits.Length;
        float[] probs = new float[n];

        float max = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            if (logits[i] > max) max = logits[i];
        }

        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            if (logits[i] == float.NegativeInfinity) continue;
            sum += (float)Math.Exp(logits[i] - max);
        }

        for (int i = 0; i < n; i++)
        {
            if (logits[i] == float.NegativeInfinity)
            {
                probs[i] = 0f;
            }
            else
            {
                probs[i] = (float)Math.Exp(logits[i] - max) / sum;
            }
        }

        return probs;
    }

    private static int SampleFromDistribution(float[] probs)
    {
        double r = Rand.NextDouble();
        double cumulativeSum = 0.0;
        for (int i = 0; i < probs.Length; i++)
        {
            cumulativeSum += probs[i];
            if (r <= cumulativeSum)
            {
                return i;
            }
        }
        return probs.Length - 1; // Fallback
    }

    private static int ArgMax(float[] values)
    {
        int bestIdx = 0;
        float maxVal = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > maxVal)
            {
                maxVal = values[i];
                bestIdx = i;
            }
        }
        return bestIdx;
    }
}
