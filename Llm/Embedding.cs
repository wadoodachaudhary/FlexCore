using System;

namespace Fx.ControlKit.Llm;

public class Embedding
{
    public float[][] Weights { get; }
    public int VocabSize { get; }
    public int EmbeddingDim { get; }

    public Embedding(int vocabSize, int embeddingDim, Random? rand = null)
    {
        VocabSize = vocabSize;
        EmbeddingDim = embeddingDim;
        Weights = new float[vocabSize][];

        var r = rand ?? new Random(42);
        for (int i = 0; i < vocabSize; i++)
        {
            Weights[i] = new float[embeddingDim];
            for (int d = 0; d < embeddingDim; d++)
            {
                Weights[i][d] = (float)(r.NextDouble() * 2.0 - 1.0) * 0.1f;
            }
        }
    }

    public float[] Lookup(int tokenId)
    {
        if (tokenId < 0 || tokenId >= VocabSize)
        {
            return new float[EmbeddingDim];
        }
        return Weights[tokenId];
    }
}

public class PositionalEmbedding
{
    public float[][] Weights { get; }
    public int ContextLength { get; }
    public int EmbeddingDim { get; }

    public PositionalEmbedding(int contextLength, int embeddingDim, Random? rand = null)
    {
        ContextLength = contextLength;
        EmbeddingDim = embeddingDim;
        Weights = new float[contextLength][];

        var r = rand ?? new Random(42);
        for (int i = 0; i < contextLength; i++)
        {
            Weights[i] = new float[embeddingDim];
            for (int d = 0; d < embeddingDim; d++)
            {
                Weights[i][d] = (float)(r.NextDouble() * 2.0 - 1.0) * 0.1f;
            }
        }
    }

    public float[] Lookup(int pos)
    {
        if (pos < 0 || pos >= ContextLength)
        {
            return new float[EmbeddingDim];
        }
        return Weights[pos];
    }
}

public class EmbeddingProcessor
{
    public static float[][][] ProcessBatch(int[][] batchInputIds, Embedding tokenEmbedding, PositionalEmbedding posEmbedding)
    {
        int batchSize = batchInputIds.Length;
        int seqLen = batchInputIds[0].Length;
        int dim = tokenEmbedding.EmbeddingDim;

        float[][][] output = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            output[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                output[b][t] = new float[dim];
                var tokenVec = tokenEmbedding.Lookup(batchInputIds[b][t]);
                var posVec = posEmbedding.Lookup(t);
                
                for (int d = 0; d < dim; d++)
                {
                    output[b][t][d] = tokenVec[d] + posVec[d];
                }
            }
        }

        return output;
    }
}
