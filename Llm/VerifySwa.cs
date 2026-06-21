using System;
using System.Collections.Generic;

namespace Fx.ControlKit.Llm;

public static class VerifySwa
{
    public static void RunVerification()
    {
        Console.WriteLine("[VerifySwa] Starting Sliding Window Attention Verification...");

        try
        {
            Console.WriteLine("[VerifySwa] Testing MultiHeadAttentionWithSWA...");
            int dIn = 128;
            int dOut = 128;
            int numHeads = 4;
            int windowSize = 4;
            var attSwa = new MultiHeadAttentionWithSWA(dIn, dOut, dropout: 0.0f, numHeads: numHeads, qkvBias: false, slidingWindowSize: windowSize);

            float[][][] x = new float[1][][];
            x[0] = new float[10][];
            var rand = new Random(42);
            for (int t = 0; t < 10; t++)
            {
                x[0][t] = new float[dIn];
                for (int d = 0; d < dIn; d++)
                {
                    x[0][t][d] = (float)(rand.NextDouble() * 2 - 1);
                }
            }

            var outAttn = attSwa.Forward(x, useCache: false);
            Console.WriteLine($"[VerifySwa] Attention forward output shape: [{outAttn.Length}, {outAttn[0].Length}, {outAttn[0][0].Length}]");
            if (outAttn.Length != 1 || outAttn[0].Length != 10 || outAttn[0][0].Length != dOut)
            {
                throw new Exception("Attention output shape mismatch!");
            }

            Console.WriteLine("[VerifySwa] Testing GPTModelWithSWA...");
            var cfg = new GPTConfigSWA
            {
                VocabSize = 1000,
                ContextLength = 64,
                EmbDim = 64,
                NHeads = 2,
                NLayers = 4,
                SlidingWindowSize = 8,
                SlidingWindowStride = 1 // 1:1 ratio
            };

            var model = new GPTModelWithSWA(cfg);
            int[][] inIdx = new int[][] { new int[] { 10, 20, 30, 40, 50 } };
            
            var logits1 = model.Forward(inIdx, useCache: false);
            Console.WriteLine($"[VerifySwa] Model forward logits shape: [{logits1.Length}, {logits1[0].Length}, {logits1[0][0].Length}]");
            if (logits1.Length != 1 || logits1[0].Length != 5 || logits1[0][0].Length != cfg.VocabSize)
            {
                throw new Exception("Model logits shape mismatch!");
            }

            Console.WriteLine("[VerifySwa] Testing SwaGenerator cached text generation...");
            var prompt = new List<int> { 1, 2, 3 };
            var generated = SwaGenerator.GenerateTextSimpleCached(model, prompt, maxNewTokens: 5);
            Console.WriteLine($"[VerifySwa] Generated tokens length: {generated.Count} (Prompt: {prompt.Count}, Expected total: 8)");
            if (generated.Count != 8)
            {
                throw new Exception("Generated tokens count mismatch!");
            }

            Console.WriteLine("[VerifySwa] Testing MemoryEstimatorSwa calculation...");
            var est = MemoryEstimatorSwa.EstimateTotals(
                contextLength: 32768,
                slidingWindowSize: 1024,
                embDim: 4096,
                nHeads: 32,
                nLayers: 32,
                nKvGroups: 4,
                batchSize: 1,
                dtype: "bf16",
                swaRatio: "5:1"
            );

            Console.WriteLine($"[VerifySwa] NSwaLayers: {est.NSwaLayers}, NFullLayers: {est.NFullLayers}");
            Console.WriteLine($"[VerifySwa] MHA KV total: {MemoryEstimatorSwa.ConvertBytes(est.TotalMhaAllFull)} (Expected ~17.18 GB)");
            Console.WriteLine($"[VerifySwa] GQA KV total: {MemoryEstimatorSwa.ConvertBytes(est.TotalGqaAllFull)} (Expected ~4.29 GB)");
            Console.WriteLine($"[VerifySwa] GQA + SWA (5:1): {MemoryEstimatorSwa.ConvertBytes(est.TotalMixedGqa)} (Expected ~0.78 GB)");

            if (est.NSwaLayers != 27 || est.NFullLayers != 5)
            {
                throw new Exception("Distributed layers count mismatch!");
            }

            Console.WriteLine("[VerifySwa] ALL SLIDING WINDOW ATTENTION TESTS PASSED SUCCESSFULLY!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VerifySwa] FAILED: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            throw;
        }
    }
}
