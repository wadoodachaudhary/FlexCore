using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TorchSharp;
using static TorchSharp.torch;

namespace Fx.ControlKit.Llm
{
    public class TorchSharpBlock : IDisposable
    {
        public Tensor ln1_w { get; }
        public Tensor ln1_b { get; }

        public Tensor w_query_w { get; }
        public Tensor w_query_b { get; }
        public Tensor w_key_w { get; }
        public Tensor w_key_b { get; }
        public Tensor w_value_w { get; }
        public Tensor w_value_b { get; }

        public Tensor out_proj_w { get; }
        public Tensor out_proj_b { get; }

        public Tensor ln2_w { get; }
        public Tensor ln2_b { get; }

        public Tensor mlp_fc_w { get; }
        public Tensor mlp_fc_b { get; }
        public Tensor mlp_proj_w { get; }
        public Tensor mlp_proj_b { get; }

        private readonly int _numHeads;
        private readonly int _headDim;

        public TorchSharpBlock(TransformerBlock managedBlock, int numHeads, int headDim)
        {
            _numHeads = numHeads;
            _headDim = headDim;

            var norm1 = (LayerNorm)GetPrivateField(managedBlock, "_norm1");
            var norm2 = (LayerNorm)GetPrivateField(managedBlock, "_norm2");
            var ff = (FeedForward)GetPrivateField(managedBlock, "_ff");
            var attn = (MultiHeadAttention)GetPrivateField(managedBlock, "_attn");

            ln1_w = ToTensor1D(norm1.Scale);
            ln1_b = ToTensor1D(norm1.Shift);

            var wQuery = (TensorOps.Linear)GetPrivateField(attn, "_wQuery");
            var wKey = (TensorOps.Linear)GetPrivateField(attn, "_wKey");
            var wValue = (TensorOps.Linear)GetPrivateField(attn, "_wValue");
            var outProj = (TensorOps.Linear)GetPrivateField(attn, "_outProj");

            w_query_w = ToTensor2D(wQuery.Weights);
            w_query_b = wQuery.Bias != null ? ToTensor1D(wQuery.Bias) : torch.zeros(wQuery.Weights.Length);
            
            w_key_w = ToTensor2D(wKey.Weights);
            w_key_b = wKey.Bias != null ? ToTensor1D(wKey.Bias) : torch.zeros(wKey.Weights.Length);

            w_value_w = ToTensor2D(wValue.Weights);
            w_value_b = wValue.Bias != null ? ToTensor1D(wValue.Bias) : torch.zeros(wValue.Weights.Length);

            out_proj_w = ToTensor2D(outProj.Weights);
            out_proj_b = outProj.Bias != null ? ToTensor1D(outProj.Bias) : torch.zeros(outProj.Weights.Length);

            ln2_w = ToTensor1D(norm2.Scale);
            ln2_b = ToTensor1D(norm2.Shift);

            var ffLinear1 = (TensorOps.Linear)GetPrivateField(ff, "_linear1");
            var ffLinear2 = (TensorOps.Linear)GetPrivateField(ff, "_linear2");

            mlp_fc_w = ToTensor2D(ffLinear1.Weights);
            mlp_fc_b = ffLinear1.Bias != null ? ToTensor1D(ffLinear1.Bias) : torch.zeros(ffLinear1.Weights.Length);

            mlp_proj_w = ToTensor2D(ffLinear2.Weights);
            mlp_proj_b = ffLinear2.Bias != null ? ToTensor1D(ffLinear2.Bias) : torch.zeros(ffLinear2.Weights.Length);
        }

        public Tensor Forward(Tensor x, Tensor causal_mask)
        {
            using (var scope = torch.NewDisposeScope())
            {
                var norm1 = LayerNorm(x, ln1_w, ln1_b);
                var attnOut = AttentionForward(norm1, causal_mask);
                var x_attn = x.add(attnOut);

                var norm2 = LayerNorm(x_attn, ln2_w, ln2_b);
                var ffOut = MLPForward(norm2);
                var result = x_attn.add(ffOut);
                
                return result.MoveToOuterDisposeScope();
            }
        }

        private Tensor LayerNorm(Tensor x, Tensor w, Tensor b)
        {
            return torch.nn.functional.layer_norm(x, new long[] { w.shape[0] }, w, b);
        }

        private Tensor AttentionForward(Tensor x, Tensor causal_mask)
        {
            using (var scope = torch.NewDisposeScope())
            {
                int seqLen = (int)x.shape[1];
                float scale = 1.0f / (float)Math.Sqrt(_headDim);

                var q = torch.nn.functional.linear(x, w_query_w, w_query_b); // [1, seqLen, dOut]
                var k = torch.nn.functional.linear(x, w_key_w, w_key_b);     // [1, seqLen, dOut]
                var v = torch.nn.functional.linear(x, w_value_w, w_value_b); // [1, seqLen, dOut]

                var q_heads = q.view(1, seqLen, _numHeads, _headDim).transpose(1, 2);
                var k_heads = k.view(1, seqLen, _numHeads, _headDim).transpose(1, 2);
                var v_heads = v.view(1, seqLen, _numHeads, _headDim).transpose(1, 2);

                var k_heads_t = k_heads.transpose(2, 3);
                var scores = torch.matmul(q_heads, k_heads_t).mul(scale);

                var masked_scores = scores.add(causal_mask);

                var weights = torch.nn.functional.softmax(masked_scores, dim: -1);

                var context = torch.matmul(weights, v_heads); // [1, numHeads, seqLen, headDim]

                var concat_context = context.transpose(1, 2).contiguous().view(1, seqLen, x.shape[2]);

                var result = torch.nn.functional.linear(concat_context, out_proj_w, out_proj_b);
                
                return result.MoveToOuterDisposeScope();
            }
        }

        private Tensor MLPForward(Tensor x)
        {
            using (var scope = torch.NewDisposeScope())
            {
                var h1 = torch.nn.functional.linear(x, mlp_fc_w, mlp_fc_b);
                var activated = torch.nn.functional.gelu(h1);
                var result = torch.nn.functional.linear(activated, mlp_proj_w, mlp_proj_b);
                return result.MoveToOuterDisposeScope();
            }
        }

        private Tensor ToTensor1D(float[] array)
        {
            return torch.tensor(array);
        }

        private Tensor ToTensor2D(float[][] jagged)
        {
            int rows = jagged.Length;
            int cols = jagged[0].Length;
            float[] flat = new float[rows * cols];
            for (int i = 0; i < rows; i++)
            {
                Array.Copy(jagged[i], 0, flat, i * cols, cols);
            }
            return torch.tensor(flat, new long[] { rows, cols });
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

        public void Dispose()
        {
            ln1_w.Dispose();
            ln1_b.Dispose();
            w_query_w.Dispose();
            w_query_b.Dispose();
            w_key_w.Dispose();
            w_key_b.Dispose();
            w_value_w.Dispose();
            w_value_b.Dispose();
            out_proj_w.Dispose();
            out_proj_b.Dispose();
            ln2_w.Dispose();
            ln2_b.Dispose();
            mlp_fc_w.Dispose();
            mlp_fc_b.Dispose();
            mlp_proj_w.Dispose();
            mlp_proj_b.Dispose();
        }
    }

    public class TorchSharpGPTModel : IDisposable
    {
        static TorchSharpGPTModel()
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    Console.WriteLine($"[PRELOAD] Base directory resolved: {baseDir}");
                    var nativeDir = Path.Combine(baseDir, "runtimes", "osx-arm64", "native");
                    if (!Directory.Exists(nativeDir))
                    {
                        Console.WriteLine($"[PRELOAD] osx-arm64 native directory not found: {nativeDir}. Trying osx-x64...");
                        nativeDir = Path.Combine(baseDir, "runtimes", "osx-x64", "native");
                    }
                    
                    Console.WriteLine($"[PRELOAD] Checking native directory: {nativeDir} (Exists: {Directory.Exists(nativeDir)})");
                    if (Directory.Exists(nativeDir))
                    {
                        var libs = new[] { "libomp.dylib", "libc10.dylib", "libtorch_cpu.dylib", "libLibTorchSharp.dylib" };
                        foreach (var lib in libs)
                        {
                            var libPath = Path.Combine(nativeDir, lib);
                            Console.WriteLine($"[PRELOAD] Checking library {lib} at {libPath} (Exists: {File.Exists(libPath)})");
                            if (File.Exists(libPath))
                            {
                                try
                                {
                                    System.Runtime.InteropServices.NativeLibrary.Load(libPath);
                                    Console.WriteLine($"[PRELOAD] Successfully loaded {lib}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[PRELOAD ERROR] Failed to load {lib}: {ex.Message}");
                                }
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("[PRELOAD WARNING] No native directory found under runtimes/osx-arm64 or runtimes/osx-x64.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[PRELOAD EXCEPTION] Failed during native library preload check: " + ex.ToString());
                }
            }

            try
            {
                int threads = Math.Max(1, Environment.ProcessorCount / 2);
                torch.set_num_threads(threads);
                Console.WriteLine($"[PRELOAD] Limit OpenMP threads to: {threads}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PRELOAD WARNING] Failed to set OpenMP threads: {ex.Message}");
            }
        }

        public Tensor tok_emb { get; }
        public Tensor pos_emb { get; }
        public List<TorchSharpBlock> blocks { get; } = new();
        public Tensor final_ln_w { get; }
        public Tensor final_ln_b { get; }
        public Tensor out_head_w { get; }

        public int EmbDim { get; }
        public int VocabSize { get; }
        public int NLayers { get; }
        public int NHeads { get; }
        public int ContextLength { get; }

        public TorchSharpGPTModel(GPTModel managedModel)
        {
            EmbDim = managedModel.Config.EmbDim;
            VocabSize = managedModel.Config.VocabSize;
            NLayers = managedModel.Config.NLayers;
            NHeads = managedModel.Config.NHeads;
            ContextLength = managedModel.Config.ContextLength;

            var tokEmb = (Embedding)GetPrivateField(managedModel, "_tokEmb");
            var posEmb = (PositionalEmbedding)GetPrivateField(managedModel, "_posEmb");
            var managedBlocks = (List<TransformerBlock>)GetPrivateField(managedModel, "_trfBlocks");
            var finalNorm = (LayerNorm)GetPrivateField(managedModel, "_finalNorm");
            var outHead = (TensorOps.Linear)GetPrivateField(managedModel, "_outHead");

            tok_emb = ToTensor2D(tokEmb.Weights);
            pos_emb = ToTensor2D(posEmb.Weights);

            int headDim = EmbDim / NHeads;
            foreach (var b in managedBlocks)
            {
                blocks.Add(new TorchSharpBlock(b, NHeads, headDim));
            }

            final_ln_w = ToTensor1D(finalNorm.Scale);
            final_ln_b = ToTensor1D(finalNorm.Shift);

            out_head_w = ToTensor2D(outHead.Weights);
        }

        public float[] ForwardLastToken(int[] tokenIds)
        {
            using (var outerScope = torch.NewDisposeScope())
            {
                int seqLen = tokenIds.Length;

                using var tokenIdsTensor = torch.tensor(tokenIds, dtype: ScalarType.Int64, device: tok_emb.device);
                using var tok_emb_x = torch.index_select(tok_emb, 0, tokenIdsTensor); // [seqLen, embDim]

                using var posIndicesTensor = torch.arange(0, seqLen, dtype: ScalarType.Int64, device: tok_emb.device);
                using var pos_emb_x = torch.index_select(pos_emb, 0, posIndicesTensor); // [seqLen, embDim]

                using var emb_sum = tok_emb_x.add(pos_emb_x);
                var x = emb_sum.unsqueeze(0); // [1, seqLen, embDim]

                using var ones = torch.ones(new long[] { seqLen, seqLen }, dtype: ScalarType.Float32, device: tok_emb.device);
                using var mask = torch.triu(ones, diagonal: 1);
                using var causalMaskBase = torch.zeros(new long[] { seqLen, seqLen }, dtype: ScalarType.Float32, device: tok_emb.device);
                using var causalMask = causalMaskBase.masked_fill(mask.to(ScalarType.Bool), float.NegativeInfinity);

                for (int i = 0; i < blocks.Count; i++)
                {
                    var next_x = blocks[i].Forward(x, causalMask);
                    x.Dispose();
                    x = next_x;
                }

                using var finalNormed = torch.nn.functional.layer_norm(x, new long[] { final_ln_w.shape[0] }, final_ln_w, final_ln_b);

                using var lastTokenRep = finalNormed.select(1, seqLen - 1); // [1, embDim]
                using var logitsTensor = torch.nn.functional.linear(lastTokenRep, out_head_w); // [1, vocabSize]

                float[] logits = logitsTensor.data<float>().ToArray();
                x.Dispose();

                return logits;
            }
        }

        private Tensor ToTensor1D(float[] array)
        {
            return torch.tensor(array);
        }

        private Tensor ToTensor2D(float[][] jagged)
        {
            int rows = jagged.Length;
            int cols = jagged[0].Length;
            float[] flat = new float[rows * cols];
            for (int i = 0; i < rows; i++)
            {
                Array.Copy(jagged[i], 0, flat, i * cols, cols);
            }
            return torch.tensor(flat, new long[] { rows, cols });
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

        public void Dispose()
        {
            tok_emb.Dispose();
            pos_emb.Dispose();
            foreach (var b in blocks)
            {
                b.Dispose();
            }
            final_ln_w.Dispose();
            final_ln_b.Dispose();
            out_head_w.Dispose();
        }
    }
}
