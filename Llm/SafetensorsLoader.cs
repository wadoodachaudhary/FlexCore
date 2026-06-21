using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Fx.ControlKit.Llm;

public class SafetensorsLoader
{
    public class TensorInfo
    {
        public string Dtype { get; set; } = "";
        public List<int> Shape { get; set; } = new();
        public List<long> DataOffsets { get; set; } = new();
    }

    private static object GetPrivateField(object obj, string name)
    {
        var field = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
        {
            field = obj.GetType().BaseType?.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        }
        return field?.GetValue(obj) ?? throw new InvalidOperationException($"Field '{name}' not found on type {obj.GetType().Name}");
    }

    public static void LoadModel(GPTModel model, string filePath)
    {
        var (tensors, dataStart, fs) = OpenSafetensors(filePath);
        try
        {
            var tokEmb = (Embedding)GetPrivateField(model, "_tokEmb");
            var posEmb = (PositionalEmbedding)GetPrivateField(model, "_posEmb");

            LoadEmbedding(tokEmb, tensors, fs, dataStart, "transformer.wte.weight");
            LoadPositionalEmbedding(posEmb, tensors, fs, dataStart, "transformer.wpe.weight");

            var finalNorm = (LayerNorm)GetPrivateField(model, "_finalNorm");
            LoadLayerNorm(finalNorm, tensors, fs, dataStart, "transformer.ln_f");

            var outHead = (TensorOps.Linear)GetPrivateField(model, "_outHead");
            if (TryGetTensor(tensors, "lm_head.weight", out _))
            {
                LoadLinear(outHead, tensors, fs, dataStart, "lm_head");
            }
            else
            {
                for (int i = 0; i < tokEmb.Weights.Length; i++)
                {
                    Array.Copy(tokEmb.Weights[i], outHead.Weights[i], tokEmb.EmbeddingDim);
                }
            }

            var blocks = (List<TransformerBlock>)GetPrivateField(model, "_trfBlocks");
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                var norm1 = (LayerNorm)GetPrivateField(block, "_norm1");
                var norm2 = (LayerNorm)GetPrivateField(block, "_norm2");
                var ff = (FeedForward)GetPrivateField(block, "_ff");
                var attn = (MultiHeadAttention)GetPrivateField(block, "_attn");

                LoadLayerNorm(norm1, tensors, fs, dataStart, $"transformer.h.{i}.ln_1");
                LoadLayerNorm(norm2, tensors, fs, dataStart, $"transformer.h.{i}.ln_2");

                var ffLinear1 = (TensorOps.Linear)GetPrivateField(ff, "_linear1");
                var ffLinear2 = (TensorOps.Linear)GetPrivateField(ff, "_linear2");
                LoadLinear(ffLinear1, tensors, fs, dataStart, $"transformer.h.{i}.mlp.c_fc");
                LoadLinear(ffLinear2, tensors, fs, dataStart, $"transformer.h.{i}.mlp.c_proj");

                var wQuery = (TensorOps.Linear)GetPrivateField(attn, "_wQuery");
                var wKey = (TensorOps.Linear)GetPrivateField(attn, "_wKey");
                var wValue = (TensorOps.Linear)GetPrivateField(attn, "_wValue");
                var outProj = (TensorOps.Linear)GetPrivateField(attn, "_outProj");

                LoadAttentionQKV(wQuery, wKey, wValue, tensors, fs, dataStart, $"transformer.h.{i}.attn.c_attn");
                LoadLinear(outProj, tensors, fs, dataStart, $"transformer.h.{i}.attn.c_proj");
            }

            Console.WriteLine($"[SafetensorsLoader] Successfully loaded model weights from {Path.GetFileName(filePath)}");
        }
        finally
        {
            fs.Dispose();
        }
    }

    public static void LoadModelSWA(GPTModelWithSWA model, string filePath)
    {
        var (tensors, dataStart, fs) = OpenSafetensors(filePath);
        try
        {
            var tokEmb = (Embedding)GetPrivateField(model, "_tokEmb");
            var posEmb = (PositionalEmbedding)GetPrivateField(model, "_posEmb");

            LoadEmbedding(tokEmb, tensors, fs, dataStart, "transformer.wte.weight");
            LoadPositionalEmbedding(posEmb, tensors, fs, dataStart, "transformer.wpe.weight");

            var finalNorm = (LayerNorm)GetPrivateField(model, "_finalNorm");
            LoadLayerNorm(finalNorm, tensors, fs, dataStart, "transformer.ln_f");

            var outHead = (TensorOps.Linear)GetPrivateField(model, "_outHead");
            if (TryGetTensor(tensors, "lm_head.weight", out _))
            {
                LoadLinear(outHead, tensors, fs, dataStart, "lm_head");
            }
            else
            {
                for (int i = 0; i < tokEmb.Weights.Length; i++)
                {
                    Array.Copy(tokEmb.Weights[i], outHead.Weights[i], tokEmb.EmbeddingDim);
                }
            }

            var blocks = (List<TransformerBlockWithSWA>)GetPrivateField(model, "_trfBlocks");
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                var norm1 = (LayerNorm)GetPrivateField(block, "_norm1");
                var norm2 = (LayerNorm)GetPrivateField(block, "_norm2");
                var ff = (FeedForward)GetPrivateField(block, "_ff");
                var attn = (MultiHeadAttentionWithSWA)GetPrivateField(block, "_attn");

                LoadLayerNorm(norm1, tensors, fs, dataStart, $"transformer.h.{i}.ln_1");
                LoadLayerNorm(norm2, tensors, fs, dataStart, $"transformer.h.{i}.ln_2");

                var ffLinear1 = (TensorOps.Linear)GetPrivateField(ff, "_linear1");
                var ffLinear2 = (TensorOps.Linear)GetPrivateField(ff, "_linear2");
                LoadLinear(ffLinear1, tensors, fs, dataStart, $"transformer.h.{i}.mlp.c_fc");
                LoadLinear(ffLinear2, tensors, fs, dataStart, $"transformer.h.{i}.mlp.c_proj");

                var wQuery = (TensorOps.Linear)GetPrivateField(attn, "_wQuery");
                var wKey = (TensorOps.Linear)GetPrivateField(attn, "_wKey");
                var wValue = (TensorOps.Linear)GetPrivateField(attn, "_wValue");
                var outProj = (TensorOps.Linear)GetPrivateField(attn, "_outProj");

                LoadAttentionQKV(wQuery, wKey, wValue, tensors, fs, dataStart, $"transformer.h.{i}.attn.c_attn");
                LoadLinear(outProj, tensors, fs, dataStart, $"transformer.h.{i}.attn.c_proj");
            }

            Console.WriteLine($"[SafetensorsLoader] Successfully loaded SWA model weights from {Path.GetFileName(filePath)}");
        }
        finally
        {
            fs.Dispose();
        }
    }

    public static (Dictionary<string, TensorInfo> tensors, long dataStart, FileStream fs) OpenSafetensors(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Safetensors file not found at: {filePath}");
        }

        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] lenBytes = new byte[8];
        int read = fs.Read(lenBytes, 0, 8);
        if (read != 8)
        {
            fs.Dispose();
            throw new IOException("Failed to read header size");
        }

        ulong headerSize = BitConverter.ToUInt64(lenBytes, 0);
        byte[] headerBytes = new byte[headerSize];
        read = fs.Read(headerBytes, 0, (int)headerSize);
        if (read != (int)headerSize)
        {
            fs.Dispose();
            throw new IOException("Failed to read header JSON bytes");
        }

        string headerJson = Encoding.UTF8.GetString(headerBytes);
        var tensors = new Dictionary<string, TensorInfo>();

        using var doc = JsonDocument.Parse(headerJson);
        var root = doc.RootElement;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "__metadata__") continue;

            var dtype = property.Value.GetProperty("dtype").GetString() ?? "";
            var shape = new List<int>();
            foreach (var val in property.Value.GetProperty("shape").EnumerateArray())
            {
                shape.Add(val.GetInt32());
            }
            var dataOffsets = new List<long>();
            foreach (var val in property.Value.GetProperty("data_offsets").EnumerateArray())
            {
                dataOffsets.Add(val.GetInt64());
            }

            tensors[property.Name] = new TensorInfo
            {
                Dtype = dtype,
                Shape = shape,
                DataOffsets = dataOffsets
            };
        }

        long dataStart = 8 + (long)headerSize;
        return (tensors, dataStart, fs);
    }

    private static float[] ReadTensorData(FileStream fs, long dataStart, TensorInfo info)
    {
        long startOffset = dataStart + info.DataOffsets[0];
        long endOffset = dataStart + info.DataOffsets[1];
        long lengthBytes = endOffset - startOffset;

        fs.Position = startOffset;
        byte[] buffer = new byte[lengthBytes];
        int read = fs.Read(buffer, 0, (int)lengthBytes);
        if (read != lengthBytes)
        {
            throw new IOException("Failed to read all bytes for tensor");
        }

        int elementCount = 1;
        foreach (var dim in info.Shape)
        {
            elementCount *= dim;
        }

        float[] result = new float[elementCount];

        if (info.Dtype.Equals("F32", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < elementCount; i++)
            {
                result[i] = BitConverter.ToSingle(buffer, i * 4);
            }
        }
        else if (info.Dtype.Equals("BF16", StringComparison.OrdinalIgnoreCase))
        {
            byte[] f32Bytes = new byte[4];
            for (int i = 0; i < elementCount; i++)
            {
                f32Bytes[0] = 0;
                f32Bytes[1] = 0;
                f32Bytes[2] = buffer[i * 2];
                f32Bytes[3] = buffer[i * 2 + 1];
                result[i] = BitConverter.ToSingle(f32Bytes, 0);
            }
        }
        else if (info.Dtype.Equals("F16", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < elementCount; i++)
            {
                ushort val = BitConverter.ToUInt16(buffer, i * 2);
                result[i] = (float)BitConverter.Int16BitsToHalf((short)val);
            }
        }
        else
        {
            throw new NotSupportedException($"Unsupported tensor dtype: {info.Dtype}");
        }

        return result;
    }

    private static void Assign1D(float[] target, float[] source)
    {
        Array.Copy(source, target, Math.Min(target.Length, source.Length));
    }

    private static void Assign2D(float[][] target, float[] source)
    {
        int rows = target.Length;
        int cols = target[0].Length;
        for (int r = 0; r < rows; r++)
        {
            Array.Copy(source, r * cols, target[r], 0, cols);
        }
    }

    private static void Assign2DTransposed(float[][] target, float[] source)
    {
        int rows = target.Length;
        int cols = target[0].Length;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                target[r][c] = source[c * rows + r];
            }
        }
    }

    private static bool TryGetTensor(Dictionary<string, TensorInfo> tensors, string key, out TensorInfo info)
    {
        if (tensors.TryGetValue(key, out info!)) return true;

        if (key.StartsWith("transformer."))
        {
            if (tensors.TryGetValue(key.Substring(12), out info!)) return true;
        }
        else
        {
            if (tensors.TryGetValue("transformer." + key, out info!)) return true;
        }

        string mappedKey = MapHFKeyToPyTorchChapter7(key);
        if (mappedKey != key && tensors.TryGetValue(mappedKey, out info!)) return true;

        return false;
    }

    private static string MapHFKeyToPyTorchChapter7(string key)
    {
        if (key.Contains("wte.weight")) return "tok_emb.weight";
        if (key.Contains("wpe.weight")) return "pos_emb.weight";
        if (key.Contains("ln_f.weight")) return "final_norm.scale";
        if (key.Contains("ln_f.bias")) return "final_norm.shift";
        if (key.Contains("lm_head.weight")) return "out_head.weight";

        var match = System.Text.RegularExpressions.Regex.Match(key, @"h\.(\d+)\.(.*)");
        if (match.Success)
        {
            int idx = int.Parse(match.Groups[1].Value);
            string rest = match.Groups[2].Value;

            if (rest == "ln_1.weight") return $"trf_blocks.{idx}.norm1.scale";
            if (rest == "ln_1.bias") return $"trf_blocks.{idx}.norm1.shift";
            if (rest == "ln_2.weight") return $"trf_blocks.{idx}.norm2.scale";
            if (rest == "ln_2.bias") return $"trf_blocks.{idx}.norm2.shift";
            
            if (rest == "mlp.c_fc.weight") return $"trf_blocks.{idx}.ff.layers.0.weight";
            if (rest == "mlp.c_fc.bias") return $"trf_blocks.{idx}.ff.layers.0.bias";
            if (rest == "mlp.c_proj.weight") return $"trf_blocks.{idx}.ff.layers.2.weight";
            if (rest == "mlp.c_proj.bias") return $"trf_blocks.{idx}.ff.layers.2.bias";

            if (rest == "attn.c_proj.weight") return $"trf_blocks.{idx}.att.out_proj.weight";
            if (rest == "attn.c_proj.bias") return $"trf_blocks.{idx}.att.out_proj.bias";
        }

        return key;
    }

    private static void LoadEmbedding(Embedding embedding, Dictionary<string, TensorInfo> tensors, FileStream fs, long dataStart, string tensorKey)
    {
        if (TryGetTensor(tensors, tensorKey, out var info))
        {
            float[] data = ReadTensorData(fs, dataStart, info);
            Assign2D(embedding.Weights, data);
        }
    }

    private static void LoadPositionalEmbedding(PositionalEmbedding embedding, Dictionary<string, TensorInfo> tensors, FileStream fs, long dataStart, string tensorKey)
    {
        if (TryGetTensor(tensors, tensorKey, out var info))
        {
            float[] data = ReadTensorData(fs, dataStart, info);
            Assign2D(embedding.Weights, data);
        }
    }

    private static void LoadLayerNorm(LayerNorm norm, Dictionary<string, TensorInfo> tensors, FileStream fs, long dataStart, string namePrefix)
    {
        string weightKey = namePrefix + ".weight";
        string biasKey = namePrefix + ".bias";

        if (TryGetTensor(tensors, weightKey, out var wInfo))
        {
            float[] wData = ReadTensorData(fs, dataStart, wInfo);
            Assign1D(norm.Scale, wData);
        }

        if (TryGetTensor(tensors, biasKey, out var bInfo))
        {
            float[] bData = ReadTensorData(fs, dataStart, bInfo);
            Assign1D(norm.Shift, bData);
        }
    }

    private static void LoadLinear(TensorOps.Linear linear, Dictionary<string, TensorInfo> tensors, FileStream fs, long dataStart, string namePrefix)
    {
        string weightKey = namePrefix + ".weight";
        string biasKey = namePrefix + ".bias";

        if (TryGetTensor(tensors, weightKey, out var wInfo))
        {
            float[] wData = ReadTensorData(fs, dataStart, wInfo);
            int outDim = linear.Weights.Length;
            int inDim = linear.Weights[0].Length;

            bool isPyTorchChapter7 = tensors.Keys.Any(k => k.StartsWith("trf_blocks.") || k.Contains("tok_emb.weight") || k.Contains("final_norm."));

            if (isPyTorchChapter7)
            {
                Assign2D(linear.Weights, wData);
            }
            else if (wInfo.Shape[0] == inDim && wInfo.Shape[1] == outDim)
            {
                Assign2DTransposed(linear.Weights, wData);
            }
            else if (wInfo.Shape[0] == outDim && wInfo.Shape[1] == inDim)
            {
                Assign2D(linear.Weights, wData);
            }
            else
            {
                throw new InvalidOperationException($"Shape mismatch for linear weight {weightKey}. Expected [{outDim}, {inDim}] or [{inDim}, {outDim}], got [{wInfo.Shape[0]}, {wInfo.Shape[1]}]");
            }
        }

        if (linear.Bias != null && TryGetTensor(tensors, biasKey, out var bInfo))
        {
            float[] bData = ReadTensorData(fs, dataStart, bInfo);
            Assign1D(linear.Bias, bData);
        }
    }

    private static void LoadAttentionQKV(
        TensorOps.Linear wQuery, 
        TensorOps.Linear wKey, 
        TensorOps.Linear wValue, 
        Dictionary<string, TensorInfo> tensors, 
        FileStream fs, 
        long dataStart, 
        string namePrefix)
    {
        string weightKey = namePrefix + ".weight";
        string biasKey = namePrefix + ".bias";

        int embDim = wQuery.Weights[0].Length; // inDim

        if (TryGetTensor(tensors, weightKey, out var wInfo))
        {
            float[] wData = ReadTensorData(fs, dataStart, wInfo);
            if (wInfo.Shape[0] == embDim && wInfo.Shape[1] == 3 * embDim)
            {
                int rowSize = 3 * embDim;
                for (int r = 0; r < embDim; r++)
                {
                    for (int c = 0; c < embDim; c++)
                    {
                        wQuery.Weights[c][r] = wData[r * rowSize + c];
                        wKey.Weights[c][r] = wData[r * rowSize + embDim + c];
                        wValue.Weights[c][r] = wData[r * rowSize + 2 * embDim + c];
                    }
                }
            }
            else if (wInfo.Shape[0] == 3 * embDim && wInfo.Shape[1] == embDim)
            {
                for (int r = 0; r < embDim; r++)
                {
                    Array.Copy(wData, r * embDim, wQuery.Weights[r], 0, embDim);
                    Array.Copy(wData, (embDim + r) * embDim, wKey.Weights[r], 0, embDim);
                    Array.Copy(wData, (2 * embDim + r) * embDim, wValue.Weights[r], 0, embDim);
                }
            }
            else
            {
                throw new InvalidOperationException($"Shape mismatch for packed QKV weight {weightKey}. Expected [{embDim}, {3 * embDim}] or [{3 * embDim}, {embDim}], got [{wInfo.Shape[0]}, {wInfo.Shape[1]}]");
            }

            if (wQuery.Bias != null && wKey.Bias != null && wValue.Bias != null && TryGetTensor(tensors, biasKey, out var bInfo))
            {
                float[] bData = ReadTensorData(fs, dataStart, bInfo);
                if (bData.Length == 3 * embDim)
                {
                    Array.Copy(bData, 0, wQuery.Bias, 0, embDim);
                    Array.Copy(bData, embDim, wKey.Bias, 0, embDim);
                    Array.Copy(bData, 2 * embDim, wValue.Bias, 0, embDim);
                }
                else
                {
                    throw new InvalidOperationException($"Shape mismatch for packed QKV bias {biasKey}. Expected [{3 * embDim}], got [{bData.Length}]");
                }
            }
        }
        else
        {
            var parts = namePrefix.Split('.');
            int idx = -1;
            for (int p = 0; p < parts.Length; p++)
            {
                if (parts[p] == "h" && p + 1 < parts.Length && int.TryParse(parts[p + 1], out var lIdx))
                {
                    idx = lIdx;
                    break;
                }
            }
            if (idx >= 0)
            {
                string qWeightKey = $"trf_blocks.{idx}.att.W_query";
                string kWeightKey = $"trf_blocks.{idx}.att.W_key";
                string vWeightKey = $"trf_blocks.{idx}.att.W_value";
                
                LoadLinear(wQuery, tensors, fs, dataStart, qWeightKey);
                LoadLinear(wKey, tensors, fs, dataStart, kWeightKey);
                LoadLinear(wValue, tensors, fs, dataStart, vWeightKey);
            }
        }
    }

    public static void SaveModel(GPTModel model, string originalFilePath, string newFilePath)
    {
        var (originalTensors, originalDataStart, originalFs) = OpenSafetensors(originalFilePath);
        try
        {
            var keys = originalTensors.Keys.ToList();
            var newTensors = new Dictionary<string, TensorInfo>();
            
            var tokEmb = (Embedding)GetPrivateField(model, "_tokEmb");
            var posEmb = (PositionalEmbedding)GetPrivateField(model, "_posEmb");
            var finalNorm = (LayerNorm)GetPrivateField(model, "_finalNorm");
            var outHead = (TensorOps.Linear)GetPrivateField(model, "_outHead");
            var blocks = (List<TransformerBlock>)GetPrivateField(model, "_trfBlocks");
            
            long currentOffset = 0;
            foreach (var key in keys)
            {
                var info = originalTensors[key];
                int totalElements = info.Shape.Aggregate(1, (a, b) => a * b);
                long sizeBytes = (long)totalElements * (info.Dtype == "F32" ? 4 : 2);
                
                newTensors[key] = new TensorInfo
                {
                    Dtype = info.Dtype,
                    Shape = info.Shape,
                    DataOffsets = new List<long> { currentOffset, currentOffset + sizeBytes }
                };
                currentOffset += sizeBytes;
            }
            
            var headerObj = new Dictionary<string, object>();
            foreach (var pair in newTensors)
            {
                headerObj[pair.Key] = new
                {
                    dtype = pair.Value.Dtype,
                    shape = pair.Value.Shape,
                    data_offsets = pair.Value.DataOffsets
                };
            }
            
            string headerJson = JsonSerializer.Serialize(headerObj);
            byte[] headerBytes = Encoding.UTF8.GetBytes(headerJson);
            ulong headerSize = (ulong)headerBytes.Length;
            
            string? dir = Path.GetDirectoryName(newFilePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            using (var fsOut = new FileStream(newFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096 * 1024)) // 4MB buffer for fast write
            using (var bwOut = new BinaryWriter(fsOut))
            {
                bwOut.Write(headerSize);
                bwOut.Write(headerBytes);
                
                foreach (var key in keys)
                {
                    var info = originalTensors[key];
                    float[] tensorData;
                    
                    if (key.EndsWith("wte.weight"))
                    {
                        tensorData = Flatten2D(tokEmb.Weights);
                    }
                    else if (key.EndsWith("wpe.weight"))
                    {
                        tensorData = Flatten2D(posEmb.Weights);
                    }
                    else if (key.EndsWith("ln_f.weight"))
                    {
                        tensorData = finalNorm.Scale;
                    }
                    else if (key.EndsWith("ln_f.bias"))
                    {
                        tensorData = finalNorm.Shift;
                    }
                    else if (key == "lm_head.weight")
                    {
                        tensorData = Flatten2D(outHead.Weights);
                    }
                    else
                    {
                        var parts = key.Split('.');
                        int blockIdx = -1;
                        for (int p = 0; p < parts.Length; p++)
                        {
                            if (parts[p] == "h" && p + 1 < parts.Length && int.TryParse(parts[p + 1], out var idx))
                            {
                                blockIdx = idx;
                                break;
                            }
                        }
                        
                        if (blockIdx >= 0 && blockIdx < blocks.Count)
                        {
                            var block = blocks[blockIdx];
                            var norm1 = (LayerNorm)GetPrivateField(block, "_norm1");
                            var norm2 = (LayerNorm)GetPrivateField(block, "_norm2");
                            var ff = (FeedForward)GetPrivateField(block, "_ff");
                            var attn = (MultiHeadAttention)GetPrivateField(block, "_attn");
                            
                            var ffLinear1 = (TensorOps.Linear)GetPrivateField(ff, "_linear1");
                            var ffLinear2 = (TensorOps.Linear)GetPrivateField(ff, "_linear2");
                            
                            var wQuery = (TensorOps.Linear)GetPrivateField(attn, "_wQuery");
                            var wKey = (TensorOps.Linear)GetPrivateField(attn, "_wKey");
                            var wValue = (TensorOps.Linear)GetPrivateField(attn, "_wValue");
                            var outProj = (TensorOps.Linear)GetPrivateField(attn, "_outProj");
                            
                            if (key.EndsWith("ln_1.weight")) tensorData = norm1.Scale;
                            else if (key.EndsWith("ln_1.bias")) tensorData = norm1.Shift;
                            else if (key.EndsWith("ln_2.weight")) tensorData = norm2.Scale;
                            else if (key.EndsWith("ln_2.bias")) tensorData = norm2.Shift;
                            else if (key.EndsWith("mlp.c_fc.weight"))
                            {
                                tensorData = Flatten2DTransposed(ffLinear1.Weights);
                            }
                            else if (key.EndsWith("mlp.c_fc.bias")) tensorData = ffLinear1.Bias!;
                            else if (key.EndsWith("mlp.c_proj.weight"))
                            {
                                tensorData = Flatten2DTransposed(ffLinear2.Weights);
                            }
                            else if (key.EndsWith("mlp.c_proj.bias")) tensorData = ffLinear2.Bias!;
                            else if (key.EndsWith("attn.c_proj.weight"))
                            {
                                tensorData = Flatten2DTransposed(outProj.Weights);
                            }
                            else if (key.EndsWith("attn.c_proj.bias")) tensorData = outProj.Bias!;
                            else if (key.EndsWith("attn.c_attn.weight"))
                            {
                                int embDim = wQuery.Weights[0].Length;
                                tensorData = new float[embDim * 3 * embDim];
                                int rowSize = 3 * embDim;
                                
                                if (info.Shape[0] == embDim && info.Shape[1] == 3 * embDim)
                                {
                                    for (int r = 0; r < embDim; r++)
                                    {
                                        for (int c = 0; c < embDim; c++)
                                        {
                                            tensorData[r * rowSize + c] = wQuery.Weights[c][r];
                                            tensorData[r * rowSize + embDim + c] = wKey.Weights[c][r];
                                            tensorData[r * rowSize + 2 * embDim + c] = wValue.Weights[c][r];
                                        }
                                    }
                                }
                                else
                                {
                                    for (int r = 0; r < embDim; r++)
                                    {
                                        Array.Copy(wQuery.Weights[r], 0, tensorData, r * embDim, embDim);
                                        Array.Copy(wKey.Weights[r], 0, tensorData, (embDim + r) * embDim, embDim);
                                        Array.Copy(wValue.Weights[r], 0, tensorData, (2 * embDim + r) * embDim, embDim);
                                    }
                                }
                            }
                            else if (key.EndsWith("attn.c_attn.bias"))
                            {
                                int embDim = wQuery.Weights[0].Length;
                                tensorData = new float[3 * embDim];
                                Array.Copy(wQuery.Bias!, 0, tensorData, 0, embDim);
                                Array.Copy(wKey.Bias!, 0, tensorData, embDim, embDim);
                                Array.Copy(wValue.Bias!, 0, tensorData, 2 * embDim, embDim);
                            }
                            else
                            {
                                tensorData = ReadTensorData(originalFs, originalDataStart, info);
                            }
                        }
                        else
                        {
                            tensorData = ReadTensorData(originalFs, originalDataStart, info);
                        }
                    }
                    
                    int len = tensorData.Length;
                    byte[] bytes;
                    if (info.Dtype == "F32")
                    {
                        bytes = new byte[len * 4];
                        Buffer.BlockCopy(tensorData, 0, bytes, 0, bytes.Length);
                    }
                    else if (info.Dtype == "BF16")
                    {
                        bytes = new byte[len * 2];
                        for (int k = 0; k < len; k++)
                        {
                            byte[] fBytes = BitConverter.GetBytes(tensorData[k]);
                            bytes[k * 2] = fBytes[2];
                            bytes[k * 2 + 1] = fBytes[3];
                        }
                    }
                    else if (info.Dtype == "F16")
                    {
                        bytes = new byte[len * 2];
                        for (int k = 0; k < len; k++)
                        {
                            Half h = (Half)tensorData[k];
                            ushort val = BitConverter.HalfToUInt16Bits(h);
                            byte[] hBytes = BitConverter.GetBytes(val);
                            bytes[k * 2] = hBytes[0];
                            bytes[k * 2 + 1] = hBytes[1];
                        }
                    }
                    else
                    {
                        throw new NotSupportedException($"Unsupported dtype for saving: {info.Dtype}");
                    }
                    
                    bwOut.Write(bytes);
                }
            }
            
            Console.WriteLine($"[SafetensorsLoader] Successfully saved model weights to {Path.GetFileName(newFilePath)}");
        }
        finally
        {
            originalFs.Dispose();
        }
    }
    
    private static float[] Flatten2D(float[][] array)
    {
        int rows = array.Length;
        int cols = array[0].Length;
        float[] result = new float[rows * cols];
        for (int r = 0; r < rows; r++)
        {
            Array.Copy(array[r], 0, result, r * cols, cols);
        }
        return result;
    }
    
    private static float[] Flatten2DTransposed(float[][] array)
    {
        int rows = array.Length;
        int cols = array[0].Length;
        float[] result = new float[rows * cols];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                result[c * rows + r] = array[r][c];
            }
        }
        return result;
    }

    public static GPTConfig DetectGPTConfig(string filePath)
    {
        var (tensors, dataStart, fs) = OpenSafetensors(filePath);
        fs.Dispose();
        
        int embDim = 768;
        int nLayers = 12;
        int vocabSize = 50257;
        
        if (TryGetTensor(tensors, "transformer.wte.weight", out var wteInfo))
        {
            if (wteInfo.Shape.Count >= 2)
            {
                vocabSize = wteInfo.Shape[0];
                embDim = wteInfo.Shape[1];
            }
        }
        else if (TryGetTensor(tensors, "wte.weight", out var wteInfo2))
        {
            if (wteInfo2.Shape.Count >= 2)
            {
                vocabSize = wteInfo2.Shape[0];
                embDim = wteInfo2.Shape[1];
            }
        }
        
        int maxBlockIdx = -1;
        foreach (var key in tensors.Keys)
        {
            var parts = key.Split('.');
            for (int p = 0; p < parts.Length; p++)
            {
                if ((parts[p] == "h" || parts[p] == "trf_blocks") && p + 1 < parts.Length && int.TryParse(parts[p + 1], out var idx))
                {
                    if (idx > maxBlockIdx) maxBlockIdx = idx;
                }
            }
        }
        if (maxBlockIdx >= 0)
        {
            nLayers = maxBlockIdx + 1;
        }
        
        int nHeads = embDim / 64;
        
        return new GPTConfig
        {
            VocabSize = vocabSize,
            ContextLength = 1024,
            EmbDim = embDim,
            NHeads = nHeads,
            NLayers = nLayers,
            DropRate = 0.0f,
            QkvBias = true
        };
    }
}
