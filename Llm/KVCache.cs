using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class KVCache
{
    private readonly List<float[][]> _keys = new();   // List of shape [seqLen][batchSize][dim]
    private readonly List<float[][]> _values = new();  // List of shape [seqLen][batchSize][dim]

    public int SeqLen => _keys.Count;

    public void Clear()
    {
        _keys.Clear();
        _values.Clear();
    }

    public (float[][][] keys, float[][][] values) Update(float[][] k, float[][] v)
    {
        int batchSize = k.Length;
        int dim = k[0].Length;

        float[][] kCopy = new float[batchSize][];
        float[][] vCopy = new float[batchSize][];
        for (int b = 0; b < batchSize; b++)
        {
            kCopy[b] = new float[dim];
            vCopy[b] = new float[dim];
            Array.Copy(k[b], kCopy[b], dim);
            Array.Copy(v[b], vCopy[b], dim);
        }

        _keys.Add(kCopy);
        _values.Add(vCopy);

        int seqLen = _keys.Count;
        float[][][] keysHistory = new float[batchSize][][];
        float[][][] valuesHistory = new float[batchSize][][];

        for (int b = 0; b < batchSize; b++)
        {
            keysHistory[b] = new float[seqLen][];
            valuesHistory[b] = new float[seqLen][];
            for (int t = 0; t < seqLen; t++)
            {
                keysHistory[b][t] = _keys[t][b];
                valuesHistory[b][t] = _values[t][b];
            }
        }

        return (keysHistory, valuesHistory);
    }
}
