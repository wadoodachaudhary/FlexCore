namespace Fx.ControlKit.Llm;

public class GPTDataset
{
    public List<int[]> InputIds { get; } = new();
    public List<int[]> TargetIds { get; } = new();

    public GPTDataset(string txt, SimpleTokenizer tokenizer, int maxLength, int stride)
    {
        var tokenIds = tokenizer.Encode(txt);
        
        if (tokenIds.Count <= maxLength)
        {
            throw new ArgumentException($"Number of tokenized inputs ({tokenIds.Count}) must at least be greater than maxLength ({maxLength}).");
        }

        for (int i = 0; i <= tokenIds.Count - maxLength - 1; i += stride)
        {
            var inputChunk = tokenIds.GetRange(i, maxLength).ToArray();
            var targetChunk = tokenIds.GetRange(i + 1, maxLength).ToArray();
            
            InputIds.Add(inputChunk);
            TargetIds.Add(targetChunk);
        }
    }

    public int Count => InputIds.Count;

    public (int[] input, int[] target) GetItem(int index)
    {
        return (InputIds[index], TargetIds[index]);
    }
}

public class GPTDataLoader
{
    private readonly GPTDataset _dataset;
    private readonly int _batchSize;
    private readonly bool _shuffle;
    private readonly bool _dropLast;
    private readonly Random _random = new(42);

    public GPTDataLoader(GPTDataset dataset, int batchSize = 4, bool shuffle = true, bool dropLast = true)
    {
        _dataset = dataset;
        _batchSize = batchSize;
        _shuffle = shuffle;
        _dropLast = dropLast;
    }

    public IEnumerable<(int[][] inputs, int[][] targets)> GetBatches()
    {
        int totalSamples = _dataset.Count;
        var indices = Enumerable.Range(0, totalSamples).ToList();

        if (_shuffle)
        {
            for (int i = indices.Count - 1; i > 0; i--)
            {
                int k = _random.Next(i + 1);
                var temp = indices[i];
                indices[i] = indices[k];
                indices[k] = temp;
            }
        }

        int limit = _dropLast ? (totalSamples / _batchSize) * _batchSize : totalSamples;

        for (int i = 0; i < limit; i += _batchSize)
        {
            int currentBatchSize = Math.Min(_batchSize, limit - i);
            int[][] batchInputs = new int[currentBatchSize][];
            int[][] batchTargets = new int[currentBatchSize][];

            for (int b = 0; b < currentBatchSize; b++)
            {
                var idx = indices[i + b];
                var (input, target) = _dataset.GetItem(idx);
                batchInputs[b] = input;
                batchTargets[b] = target;
            }

            yield return (batchInputs, batchTargets);
        }
    }
}
