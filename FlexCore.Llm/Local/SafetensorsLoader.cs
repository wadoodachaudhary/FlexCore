using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Fx.ControlKit.Llm.Local;

/// <summary>
/// High-performance parser and loader for HuggingFace and DotnetLLM Safetensors format in pure C#.
/// Supports F32, F16, and BF16 with SIMD-friendly bulk reads.
/// </summary>
public static class SafetensorsLoader
{
    public sealed class TensorInfo
    {
        public string Dtype { get; set; } = "F32";
        public List<int> Shape { get; set; } = new();
        public List<long> DataOffsets { get; set; } = new();
    }

    public static bool TryLoad(GPTModel model, string filePath, out string? errorMessage)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                errorMessage = $"Safetensors file not found at: {filePath}";
                return false;
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var br = new BinaryReader(fs);

            // 1. Read header size (8 bytes, little-endian unsigned 64-bit int)
            ulong headerSize = br.ReadUInt64();
            if (headerSize == 0 || headerSize > 100_000_000)
            {
                errorMessage = "Invalid safetensors header length.";
                return false;
            }

            byte[] headerBytes = br.ReadBytes((int)headerSize);
            string headerJson = Encoding.UTF8.GetString(headerBytes);
            long dataStart = 8 + (long)headerSize;

            var tensors = JsonSerializer.Deserialize<Dictionary<string, TensorInfo>>(headerJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (tensors == null || tensors.Count == 0)
            {
                errorMessage = "No tensors declared in safetensors header.";
                return false;
            }

            int embDim = model.Config.EmbDim;

            // 2. Token & Position Embeddings
            LoadRowMajorMatrix(model.TokEmb, tensors, fs, dataStart, "tok_emb.weight", "wte.weight", "transformer.wte.weight");
            LoadRowMajorMatrix(model.PosEmb, tensors, fs, dataStart, "pos_emb.weight", "wpe.weight", "transformer.wpe.weight");

            // 3. Blocks
            for (int b = 0; b < model.Blocks.Count; b++)
            {
                var block = model.Blocks[b];
                string hPrefix = $"h.{b}.";
                string trfPrefix = $"transformer.h.{b}.";
                string dnPrefix = $"trf_blocks.{b}.";

                // Norm1
                LoadVectorDirect(block.Norm1Scale, tensors, fs, dataStart, dnPrefix + "norm1.scale", hPrefix + "ln_1.weight", trfPrefix + "ln_1.weight");
                LoadVectorDirect(block.Norm1Shift, tensors, fs, dataStart, dnPrefix + "norm1.shift", hPrefix + "ln_1.bias", trfPrefix + "ln_1.bias");

                // Attention weights: check for split (DotnetLLM) or fused c_attn (HuggingFace)
                if (TryGetTensor(tensors, out var qTensor, dnPrefix + "att.W_query.weight"))
                {
                    // DotnetLLM format: separate W_query, W_key, W_value
                    LoadLinearMatrix(block.Attention.WQuery, tensors, fs, dataStart, dnPrefix + "att.W_query.weight");
                    LoadLinearMatrix(block.Attention.WKey, tensors, fs, dataStart, dnPrefix + "att.W_key.weight");
                    LoadLinearMatrix(block.Attention.WValue, tensors, fs, dataStart, dnPrefix + "att.W_value.weight");

                    if (block.Attention.BQuery != null) LoadVectorDirect(block.Attention.BQuery, tensors, fs, dataStart, dnPrefix + "att.W_query.bias");
                    if (block.Attention.BKey != null) LoadVectorDirect(block.Attention.BKey, tensors, fs, dataStart, dnPrefix + "att.W_key.bias");
                    if (block.Attention.BValue != null) LoadVectorDirect(block.Attention.BValue, tensors, fs, dataStart, dnPrefix + "att.W_value.bias");
                }
                else if (TryGetTensor(tensors, out var cAttnW, hPrefix + "attn.c_attn.weight", trfPrefix + "attn.c_attn.weight"))
                {
                    // HuggingFace fused Conv1D format: [embDim, 3 * embDim]
                    var floats = ReadTensorFloats(fs, dataStart, cAttnW);
                    for (int j = 0; j < embDim; j++)
                    {
                        int rowOffset = j * 3 * embDim;
                        for (int c = 0; c < embDim; c++)
                        {
                            block.Attention.WQuery[c][j] = floats[rowOffset + c];
                            block.Attention.WKey[c][j] = floats[rowOffset + embDim + c];
                            block.Attention.WValue[c][j] = floats[rowOffset + 2 * embDim + c];
                        }
                    }

                    if (block.Attention.BQuery != null && TryGetTensor(tensors, out var cAttnB, hPrefix + "attn.c_attn.bias", trfPrefix + "attn.c_attn.bias"))
                    {
                        var bFloats = ReadTensorFloats(fs, dataStart, cAttnB);
                        Array.Copy(bFloats, 0, block.Attention.BQuery, 0, embDim);
                        if (block.Attention.BKey != null) Array.Copy(bFloats, embDim, block.Attention.BKey, 0, embDim);
                        if (block.Attention.BValue != null) Array.Copy(bFloats, 2 * embDim, block.Attention.BValue, 0, embDim);
                    }
                }

                // Output projection
                if (TryGetTensor(tensors, out _, dnPrefix + "att.out_proj.weight"))
                {
                    LoadLinearMatrix(block.Attention.OutProjWeight, tensors, fs, dataStart, dnPrefix + "att.out_proj.weight");
                    if (block.Attention.OutProjBias != null) LoadVectorDirect(block.Attention.OutProjBias, tensors, fs, dataStart, dnPrefix + "att.out_proj.bias");
                }
                else if (TryGetTensor(tensors, out var outProjTensor, hPrefix + "attn.c_proj.weight", trfPrefix + "attn.c_proj.weight"))
                {
                    // HuggingFace Conv1D: [embDim, embDim] -> transpose
                    var floats = ReadTensorFloats(fs, dataStart, outProjTensor);
                    for (int j = 0; j < embDim; j++)
                    {
                        int rowOffset = j * embDim;
                        for (int c = 0; c < embDim; c++)
                        {
                            block.Attention.OutProjWeight[c][j] = floats[rowOffset + c];
                        }
                    }
                    if (block.Attention.OutProjBias != null) LoadVectorDirect(block.Attention.OutProjBias, tensors, fs, dataStart, hPrefix + "attn.c_proj.bias", trfPrefix + "attn.c_proj.bias");
                }

                // Norm2
                LoadVectorDirect(block.Norm2Scale, tensors, fs, dataStart, dnPrefix + "norm2.scale", hPrefix + "ln_2.weight", trfPrefix + "ln_2.weight");
                LoadVectorDirect(block.Norm2Shift, tensors, fs, dataStart, dnPrefix + "norm2.shift", hPrefix + "ln_2.bias", trfPrefix + "ln_2.bias");

                // FFN (MLP)
                if (TryGetTensor(tensors, out _, dnPrefix + "ff.layers.0.weight"))
                {
                    LoadLinearMatrix(block.Ffn.W1, tensors, fs, dataStart, dnPrefix + "ff.layers.0.weight");
                    if (block.Ffn.B1 != null) LoadVectorDirect(block.Ffn.B1, tensors, fs, dataStart, dnPrefix + "ff.layers.0.bias");
                    LoadLinearMatrix(block.Ffn.W2, tensors, fs, dataStart, dnPrefix + "ff.layers.2.weight");
                    if (block.Ffn.B2 != null) LoadVectorDirect(block.Ffn.B2, tensors, fs, dataStart, dnPrefix + "ff.layers.2.bias");
                }
                else if (TryGetTensor(tensors, out var fcW, hPrefix + "mlp.c_fc.weight", trfPrefix + "mlp.c_fc.weight"))
                {
                    // HuggingFace: [embDim, 4 * embDim]
                    int hiddenDim = embDim * 4;
                    var floats = ReadTensorFloats(fs, dataStart, fcW);
                    for (int j = 0; j < embDim; j++)
                    {
                        int rowOffset = j * hiddenDim;
                        for (int c = 0; c < hiddenDim; c++)
                        {
                            block.Ffn.W1[c][j] = floats[rowOffset + c];
                        }
                    }
                    if (block.Ffn.B1 != null) LoadVectorDirect(block.Ffn.B1, tensors, fs, dataStart, hPrefix + "mlp.c_fc.bias", trfPrefix + "mlp.c_fc.bias");

                    if (TryGetTensor(tensors, out var projW, hPrefix + "mlp.c_proj.weight", trfPrefix + "mlp.c_proj.weight"))
                    {
                        // HuggingFace: [4 * embDim, embDim]
                        var projFloats = ReadTensorFloats(fs, dataStart, projW);
                        for (int j = 0; j < hiddenDim; j++)
                        {
                            int rowOffset = j * embDim;
                            for (int c = 0; c < embDim; c++)
                            {
                                block.Ffn.W2[c][j] = projFloats[rowOffset + c];
                            }
                        }
                    }
                    if (block.Ffn.B2 != null) LoadVectorDirect(block.Ffn.B2, tensors, fs, dataStart, hPrefix + "mlp.c_proj.bias", trfPrefix + "mlp.c_proj.bias");
                }
            }

            // 4. Final Norm
            LoadVectorDirect(model.FinalNormScale, tensors, fs, dataStart, "final_norm.scale", "ln_f.weight", "transformer.ln_f.weight");
            LoadVectorDirect(model.FinalNormShift, tensors, fs, dataStart, "final_norm.shift", "ln_f.bias", "transformer.ln_f.bias");

            // 5. Output Head
            if (TryGetTensor(tensors, out var outHeadInfo, "out_head.weight", "lm_head.weight"))
            {
                LoadRowMajorMatrix(model.OutHeadWeight, tensors, fs, dataStart, "out_head.weight", "lm_head.weight");
            }
            else
            {
                // Tied weights: copy token embeddings
                for (int i = 0; i < model.Config.VocabSize; i++)
                {
                    Array.Copy(model.TokEmb[i], model.OutHeadWeight[i], embDim);
                }
            }

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Error loading safetensors: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetTensor(Dictionary<string, TensorInfo> tensors, out TensorInfo tensor, params string[] names)
    {
        foreach (var name in names)
        {
            if (tensors.TryGetValue(name, out var t))
            {
                tensor = t;
                return true;
            }
        }
        tensor = new TensorInfo();
        return false;
    }

    private static float[] ReadTensorFloats(Stream fs, long dataStart, TensorInfo info)
    {
        long start = dataStart + info.DataOffsets[0];
        long byteLength = info.DataOffsets[1] - info.DataOffsets[0];
        int floatCount = (int)(byteLength / 4);
        if (info.Dtype == "F16" || info.Dtype == "BF16") floatCount = (int)(byteLength / 2);

        fs.Seek(start, SeekOrigin.Begin);
        byte[] rawBytes = new byte[byteLength];
        int totalRead = 0;
        while (totalRead < rawBytes.Length)
        {
            int read = fs.Read(rawBytes, totalRead, rawBytes.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        float[] result = new float[floatCount];
        if (info.Dtype == "F32")
        {
            Buffer.BlockCopy(rawBytes, 0, result, 0, rawBytes.Length);
        }
        else if (info.Dtype == "F16")
        {
            var halfSpan = MemoryMarshal.Cast<byte, Half>(rawBytes);
            for (int i = 0; i < halfSpan.Length; i++) result[i] = (float)halfSpan[i];
        }
        else if (info.Dtype == "BF16")
        {
            var u16Span = MemoryMarshal.Cast<byte, ushort>(rawBytes);
            for (int i = 0; i < u16Span.Length; i++)
            {
                uint u32 = ((uint)u16Span[i]) << 16;
                result[i] = BitConverter.UInt32BitsToSingle(u32);
            }
        }
        return result;
    }

    private static void LoadRowMajorMatrix(float[][] target, Dictionary<string, TensorInfo> tensors, Stream fs, long dataStart, params string[] names)
    {
        if (!TryGetTensor(tensors, out var info, names) || info.DataOffsets.Count < 2) return;

        var floats = ReadTensorFloats(fs, dataStart, info);
        int rows = Math.Min(target.Length, info.Shape.Count > 0 ? info.Shape[0] : target.Length);
        int cols = Math.Min(target[0].Length, info.Shape.Count > 1 ? info.Shape[1] : target[0].Length);

        for (int r = 0; r < rows; r++)
        {
            Array.Copy(floats, r * cols, target[r], 0, cols);
        }
    }

    private static void LoadLinearMatrix(float[][] target, Dictionary<string, TensorInfo> tensors, Stream fs, long dataStart, params string[] names)
    {
        if (!TryGetTensor(tensors, out var info, names) || info.DataOffsets.Count < 2) return;

        var floats = ReadTensorFloats(fs, dataStart, info);
        int rows = Math.Min(target.Length, info.Shape.Count > 0 ? info.Shape[0] : target.Length);
        int cols = Math.Min(target[0].Length, info.Shape.Count > 1 ? info.Shape[1] : target[0].Length);

        for (int r = 0; r < rows; r++)
        {
            Array.Copy(floats, r * cols, target[r], 0, cols);
        }
    }

    private static void LoadVectorDirect(float[] target, Dictionary<string, TensorInfo> tensors, Stream fs, long dataStart, params string[] names)
    {
        if (!TryGetTensor(tensors, out var info, names) || info.DataOffsets.Count < 2) return;

        var floats = ReadTensorFloats(fs, dataStart, info);
        int count = Math.Min(target.Length, floats.Length);
        Array.Copy(floats, 0, target, 0, count);
    }
}
