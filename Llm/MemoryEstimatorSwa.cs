using System;
using System.Collections.Generic;

namespace Fx.ControlKit.Llm;

public class MemoryEstimatorSwa
{
    private static readonly Dictionary<string, int> DtypeBytes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "fp32", 4 },
        { "bf16", 2 },
        { "fp16", 2 },
        { "fp8", 1 },
        { "int8", 1 }
    };

    public static string ConvertBytes(double n)
    {
        double gb = n / (1000.0 * 1000.0 * 1000.0);
        return $"{gb:N2} GB";
    }

    public static double CalcKvBytesPerLayer(int batch, int contextLength, int headDim, int nKvHeads, int bytesPerElem)
    {
        return (double)batch * contextLength * headDim * nKvHeads * 2.0 * bytesPerElem;
    }

    public static (int swa, int full) DistributeLayers(int nLayers, int a, int b)
    {
        int block = a + b;
        if (block == 0) return (0, 0);

        int blocks = nLayers / block;
        int rem = nLayers % block;
        int swa = blocks * a + Math.Min(a, rem);
        int full = blocks * b + Math.Max(0, rem - a);
        return (swa, full);
    }

    public class EstimationResult
    {
        public int BytesPerElem { get; set; }
        public int HeadDim { get; set; }
        public int NKvHeadsGqa { get; set; }
        public int EffW { get; set; }
        public int NSwaLayers { get; set; }
        public int NFullLayers { get; set; }
        public double TotalMhaAllFull { get; set; }
        public double TotalGqaAllFull { get; set; }
        public double TotalMixedMha { get; set; }
        public double TotalMixedGqa { get; set; }
    }

    public static EstimationResult EstimateTotals(
        int contextLength, 
        int slidingWindowSize, 
        int embDim, 
        int nHeads, 
        int nLayers, 
        int nKvGroups, 
        int batchSize, 
        string dtype, 
        string swaRatio)
    {
        if (nHeads % nKvGroups != 0)
        {
            throw new ArgumentException("nKvGroups must divide nHeads exactly.");
        }

        int bytesPerElem = DtypeBytes.GetValueOrDefault(dtype, 2);
        int headDim = (int)Math.Ceiling((double)embDim / nHeads);
        int nKvHeadsMha = nHeads;
        int nKvHeadsGqa = nHeads / nKvGroups;

        int aSwa = 1;
        int bFull = 0;
        if (!string.IsNullOrEmpty(swaRatio))
        {
            var parts = swaRatio.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b))
            {
                aSwa = a;
                bFull = b;
            }
        }

        var (nSwaLayers, nFullLayers) = DistributeLayers(nLayers, aSwa, bFull);
        int effW = Math.Min(contextLength, slidingWindowSize);

        double perMhaFull = CalcKvBytesPerLayer(batchSize, contextLength, headDim, nKvHeadsMha, bytesPerElem);
        double perGqaFull = CalcKvBytesPerLayer(batchSize, contextLength, headDim, nKvHeadsGqa, bytesPerElem);
        double perMhaSwa = CalcKvBytesPerLayer(batchSize, effW, headDim, nKvHeadsMha, bytesPerElem);
        double perGqaSwa = CalcKvBytesPerLayer(batchSize, effW, headDim, nKvHeadsGqa, bytesPerElem);

        double totalMhaAllFull = perMhaFull * nLayers;
        double totalGqaAllFull = perGqaFull * nLayers;
        double totalMixedMha = nSwaLayers * perMhaSwa + nFullLayers * perMhaFull;
        double totalMixedGqa = nSwaLayers * perGqaSwa + nFullLayers * perGqaFull;

        return new EstimationResult
        {
            BytesPerElem = bytesPerElem,
            HeadDim = headDim,
            NKvHeadsGqa = nKvHeadsGqa,
            EffW = effW,
            NSwaLayers = nSwaLayers,
            NFullLayers = nFullLayers,
            TotalMhaAllFull = totalMhaAllFull,
            TotalGqaAllFull = totalGqaAllFull,
            TotalMixedMha = totalMixedMha,
            TotalMixedGqa = totalMixedGqa
        };
    }
}
