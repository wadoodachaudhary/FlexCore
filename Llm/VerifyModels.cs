using System;
using System.Collections.Generic;

namespace Fx.ControlKit.Llm;

public static class VerifyModels
{
    public static void RunVerification()
    {
        Console.WriteLine("[Verification] Starting LLM Model Verification...");

        try
        {
            var rand = new Random(123);

            Console.WriteLine("[Verification] Instantiating Qwen3 Model...");
            var qwenCfg = new QwenConfig
            {
                VocabSize = 1000,
                ContextLength = 256,
                EmbDim = 128,
                NHeads = 4,
                NLayers = 2,
                HiddenDim = 256,
                HeadDim = 32,
                NKvGroups = 2,
                LayerTypes = new[] { "full_attention", "linear_attention" }
            };
            var qwen = new Qwen3Model(qwenCfg, rand);
            int[][] inIdx = new int[][] { new[] { 1, 2, 3 } };
            var qwenOut = qwen.Forward(inIdx);
            Console.WriteLine($"[Verification] Qwen Forward pass successful. Output shape: [{qwenOut.Length}, {qwenOut[0].Length}, {qwenOut[0][0].Length}]");

            Console.WriteLine("[Verification] Instantiating Gemma3 Model...");
            var gemmaCfg = new GemmaConfig
            {
                VocabSize = 1000,
                ContextLength = 256,
                EmbDim = 128,
                NHeads = 4,
                NLayers = 2,
                HiddenDim = 256,
                HeadDim = 32,
                NKvGroups = 2,
                LayerTypes = new[] { "sliding_attention", "full_attention" },
                QueryPreAttnScalar = 32
            };
            var gemma3 = new Gemma3Model(gemmaCfg, rand);
            var gemma3Out = gemma3.Forward(inIdx);
            Console.WriteLine($"[Verification] Gemma3 Forward pass successful. Output shape: [{gemma3Out.Length}, {gemma3Out[0].Length}, {gemma3Out[0][0].Length}]");

            Console.WriteLine("[Verification] Instantiating Gemma4 Model...");
            var gemma4 = new Gemma4Model(gemmaCfg, rand);
            var gemma4Out = gemma4.Forward(inIdx);
            Console.WriteLine($"[Verification] Gemma4 Forward pass successful. Output shape: [{gemma4Out.Length}, {gemma4Out[0].Length}, {gemma4Out[0][0].Length}]");

            Console.WriteLine("[Verification] Instantiating OLMo3 Model...");
            var olmoCfg = new OlmoConfig
            {
                VocabSize = 1000,
                ContextLength = 256,
                EmbDim = 128,
                NHeads = 4,
                NLayers = 2,
                HiddenDim = 256,
                HeadDim = 32,
                NKvHeads = 2,
                LayerTypes = new[] { "sliding_attention", "full_attention" }
            };
            var olmo = new Olmo3Model(olmoCfg, rand);
            var olmoOut = olmo.Forward(inIdx);
            Console.WriteLine($"[Verification] OLMo3 Forward pass successful. Output shape: [{olmoOut.Length}, {olmoOut[0].Length}, {olmoOut[0][0].Length}]");

            Console.WriteLine("[Verification] Instantiating TinyAya Model...");
            var ayaCfg = new TinyAyaConfig
            {
                VocabSize = 1000,
                ContextLength = 256,
                EmbDim = 128,
                NHeads = 4,
                NLayers = 2,
                HiddenDim = 256,
                HeadDim = 32,
                NKvHeads = 2,
                LayerTypes = new[] { "sliding_attention", "full_attention" }
            };
            var aya = new TinyAyaModel(ayaCfg, rand);
            var ayaOut = aya.Forward(inIdx);
            Console.WriteLine($"[Verification] TinyAya Forward pass successful. Output shape: [{ayaOut.Length}, {ayaOut[0].Length}, {ayaOut[0][0].Length}]");

            Console.WriteLine("[Verification] Testing Muon Optimizer Step...");
            var muon = new MuonOptimizer(0.01f);
            float[][] w = new float[][] { new float[] { 1, 2 }, new float[] { 3, 4 } };
            float[][] grad = new float[][] { new float[] { 0.1f, -0.2f }, new float[] { 0.3f, 0.4f } };
            muon.Step(w, grad);
            Console.WriteLine($"[Verification] Muon step completed. Updated weight[0][0]: {w[0][0]}");

            VerifySwa.RunVerification();

            VerifySafetensors.RunVerification();

            Console.WriteLine("[Verification] ALL MODEL VERIFICATIONS COMPLETED SUCCESSFULLY!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Verification] FAILED: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}
