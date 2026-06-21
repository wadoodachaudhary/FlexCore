using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class SpamDatasetItem
{
    public int[] TokenIds { get; set; } = Array.Empty<int>();
    public int Label { get; set; } // 0 for ham, 1 for spam
}

public class SpamDataset
{
    public List<SpamDatasetItem> Items { get; } = new();
    public int MaxLength { get; }

    public SpamDataset(List<string> texts, List<int> labels, SimpleTokenizer tokenizer, int? maxLength = null, int padTokenId = 50256)
    {
        var encodedTexts = texts.Select(text => tokenizer.Encode(text).ToArray()).ToList();

        if (maxLength == null)
        {
            MaxLength = encodedTexts.Count > 0 ? encodedTexts.Max(e => e.Length) : 0;
        }
        else
        {
            MaxLength = maxLength.Value;
            encodedTexts = encodedTexts.Select(e => e.Take(MaxLength).ToArray()).ToList();
        }

        for (int i = 0; i < encodedTexts.Count; i++)
        {
            var encoded = encodedTexts[i];
            var padded = new int[MaxLength];
            Array.Copy(encoded, padded, encoded.Length);
            
            for (int p = encoded.Length; p < MaxLength; p++)
            {
                padded[p] = padTokenId;
            }

            Items.Add(new SpamDatasetItem
            {
                TokenIds = padded,
                Label = labels[i]
            });
        }
    }
}

public class GPTClassifier
{
    private readonly GPTModel _gptModel;
    private readonly TensorOps.Linear _classHead;

    public GPTClassifier(GPTModel gptModel, int numClasses)
    {
        _gptModel = gptModel;
        _classHead = new TensorOps.Linear(gptModel.Config.EmbDim, numClasses, useBias: true);
    }

    public float[][] Forward(int[][] inIdx)
    {
        int batchSize = inIdx.Length;
        int seqLen = inIdx[0].Length;

        float[][][] x = _gptModel.ForwardRepresentations(inIdx); // Shape: [batchSize, seqLen, embDim]

        float[][] lastTokenStates = new float[batchSize][];
        for (int b = 0; b < batchSize; b++)
        {
            lastTokenStates[b] = x[b][seqLen - 1]; // Shape: [embDim]
        }

        float[][] classLogits = _classHead.ForwardBatch(lastTokenStates); // Shape: [batchSize, numClasses]
        
        return classLogits;
    }
}
