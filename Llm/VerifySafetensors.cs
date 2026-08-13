using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Fx.ControlKit.Llm;

public static class VerifySafetensors
{
    public static void RunVerification()
    {
        Console.WriteLine("[VerifySafetensors] Starting Safetensors Parser Verification...");

        string tempPath = Path.Combine(Path.GetTempPath(), "test_mock_model.safetensors");

        try
        {
            // 1. Build a valid, mock .safetensors file in memory
            // We'll define two tensors:
            // - "transformer.wte.weight": F32, shape [2, 3], offsets [0, 24] (data: 1.0f to 6.0f)
            // - "transformer.ln_f.bias": BF16, shape [4], offsets [24, 32] (data: 1.0f to 4.0f)
            //   In float32:
            //   1.0f = 0x3F800000 -> BF16 = 0x3F80 (bytes: 0x80, 0x3F)
            //   2.0f = 0x40000000 -> BF16 = 0x4000 (bytes: 0x00, 0x40)
            //   3.0f = 0x40400000 -> BF16 = 0x4040 (bytes: 0x40, 0x40)
            //   4.0f = 0x40800000 -> BF16 = 0x4080 (bytes: 0x80, 0x40)

            string headerJson = "{\"transformer.wte.weight\":{\"dtype\":\"F32\",\"shape\":[2,4],\"data_offsets\":[0,32]},\"transformer.ln_f.bias\":{\"dtype\":\"BF16\",\"shape\":[4],\"data_offsets\":[32,40]}}";
            byte[] headerBytes = Encoding.UTF8.GetBytes(headerJson);
            ulong headerSize = (ulong)headerBytes.Length;

            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                // Write header size (8 bytes)
                fs.Write(BitConverter.GetBytes(headerSize), 0, 8);
                // Write header string
                fs.Write(headerBytes, 0, headerBytes.Length);

                // Write Tensor 1 data (8 floats = 32 bytes)
                float[] t1Data = new float[] { 1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f };
                byte[] t1Bytes = new byte[32];
                for (int i = 0; i < t1Data.Length; i++)
                {
                    Array.Copy(BitConverter.GetBytes(t1Data[i]), 0, t1Bytes, i * 4, 4);
                }
                fs.Write(t1Bytes, 0, t1Bytes.Length);

                // Write Tensor 2 data (4 bfloats = 8 bytes)
                byte[] t2Bytes = new byte[]
                {
                    0x80, 0x3F, // 1.0f
                    0x00, 0x40, // 2.0f
                    0x40, 0x40, // 3.0f
                    0x80, 0x40  // 4.0f
                };
                fs.Write(t2Bytes, 0, t2Bytes.Length);
            }

            Console.WriteLine("[VerifySafetensors] Mock Safetensors file generated successfully.");

            // 2. Instantiate small model
            var cfg = new GPTConfig
            {
                VocabSize = 2,
                ContextLength = 10,
                EmbDim = 4,
                NHeads = 1,
                NLayers = 1
            };
            var model = new GPTModel(cfg);

            // 3. Load from mock safetensors
            SafetensorsLoader.LoadModel(model, tempPath);

            // 4. Validate loaded weights
            var tokEmb = (Embedding)typeof(GPTModel)
                .GetField("_tokEmb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(model)!;

            var finalNorm = (LayerNorm)typeof(GPTModel)
                .GetField("_finalNorm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(model)!;

            // Check embedding values
            Console.WriteLine($"[VerifySafetensors] Embedding row 0: {tokEmb.Weights[0][0]}, {tokEmb.Weights[0][1]}, {tokEmb.Weights[0][2]}, {tokEmb.Weights[0][3]} (Expected: 1, 2, 3, 4)");
            Console.WriteLine($"[VerifySafetensors] Embedding row 1: {tokEmb.Weights[1][0]}, {tokEmb.Weights[1][1]}, {tokEmb.Weights[1][2]}, {tokEmb.Weights[1][3]} (Expected: 5, 6, 7, 8)");

            if (Math.Abs(tokEmb.Weights[0][0] - 1.0f) > 1e-5 ||
                Math.Abs(tokEmb.Weights[0][1] - 2.0f) > 1e-5 ||
                Math.Abs(tokEmb.Weights[0][2] - 3.0f) > 1e-5 ||
                Math.Abs(tokEmb.Weights[0][3] - 4.0f) > 1e-5 ||
                Math.Abs(tokEmb.Weights[1][0] - 5.0f) > 1e-5 ||
                Math.Abs(tokEmb.Weights[1][1] - 6.0f) > 1e-5 ||
                Math.Abs(tokEmb.Weights[1][2] - 7.0f) > 1e-5 ||
                Math.Abs(tokEmb.Weights[1][3] - 8.0f) > 1e-5)
            {
                throw new Exception("F32 Embedding weights loading verification failed!");
            }

            // Check LayerNorm bias values (parsed from BF16)
            Console.WriteLine($"[VerifySafetensors] LayerNorm Bias (BF16): {finalNorm.Shift[0]}, {finalNorm.Shift[1]}, {finalNorm.Shift[2]}, {finalNorm.Shift[3]} (Expected: 1, 2, 3, 4)");
            if (Math.Abs(finalNorm.Shift[0] - 1.0f) > 1e-5 ||
                Math.Abs(finalNorm.Shift[1] - 2.0f) > 1e-5 ||
                Math.Abs(finalNorm.Shift[2] - 3.0f) > 1e-5 ||
                Math.Abs(finalNorm.Shift[3] - 4.0f) > 1e-5)
            {
                throw new Exception("BF16 LayerNorm Shift loading verification failed!");
            }

            Console.WriteLine("[VerifySafetensors] ALL SAFETENSORS PARSER VERIFICATION TESTS PASSED SUCCESSFULLY!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VerifySafetensors] FAILED: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
