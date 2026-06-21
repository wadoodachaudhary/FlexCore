using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class BPETokenizerSimple
{
    public Dictionary<int, string> vocab { get; private set; } = new();
    public Dictionary<string, int> inverse_vocab { get; private set; } = new();
    public Dictionary<Tuple<int, int>, int> bpe_merges { get; private set; } = new();

    public BPETokenizerSimple()
    {
    }

    public void Train(string text, int vocabSize, HashSet<string>? allowedSpecial = null)
    {
        var processedTextList = new List<char>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ' && i != 0)
            {
                processedTextList.Add('Ġ');
            }
            if (c != ' ')
            {
                processedTextList.Add(c);
            }
        }
        string processedText = new string(processedTextList.ToArray());

        var uniqueChars = new List<string>();
        for (int i = 0; i < 256; i++)
        {
            uniqueChars.Add(((char)i).ToString());
        }

        var sortedChars = processedText.Distinct().OrderBy(c => c).ToList();
        foreach (char c in sortedChars)
        {
            string cStr = c.ToString();
            if (!uniqueChars.Contains(cStr))
            {
                uniqueChars.Add(cStr);
            }
        }

        if (!uniqueChars.Contains("Ġ"))
        {
            uniqueChars.Add("Ġ");
        }

        vocab = new Dictionary<int, string>();
        inverse_vocab = new Dictionary<string, int>();
        for (int i = 0; i < uniqueChars.Count; i++)
        {
            vocab[i] = uniqueChars[i];
            inverse_vocab[uniqueChars[i]] = i;
        }

        if (allowedSpecial != null)
        {
            foreach (var token in allowedSpecial)
            {
                if (!inverse_vocab.ContainsKey(token))
                {
                    int newId = vocab.Count;
                    vocab[newId] = token;
                    inverse_vocab[token] = newId;
                }
            }
        }

        var tokenIds = processedText.Select(c => inverse_vocab[c.ToString()]).ToList();

        bpe_merges = new Dictionary<Tuple<int, int>, int>();
        int startId = vocab.Count;
        for (int newId = startId; newId < vocabSize; newId++)
        {
            if (tokenIds.Count < 2) break;

            var pairId = FindFreqPair(tokenIds);
            if (pairId == null) break;

            var updated = ReplacePair(tokenIds, pairId, newId);
            if (updated.SequenceEqual(tokenIds)) break;

            tokenIds = updated;
            bpe_merges[pairId] = newId;
        }

        foreach (var kvp in bpe_merges)
        {
            var pair = kvp.Key;
            int newId = kvp.Value;
            string mergedToken = vocab[pair.Item1] + vocab[pair.Item2];
            vocab[newId] = mergedToken;
            inverse_vocab[mergedToken] = newId;
        }
    }

    public List<int> Encode(string text)
    {
        var tokens = new List<string>();
        string replaced = text.Replace("\n", " \n ");
        string[] words = replaced.Split(new[] { ' ', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            if (i > 0 && !word.StartsWith("\n"))
            {
                tokens.Add("Ġ" + word);
            }
            else
            {
                tokens.Add(word);
            }
        }

        var tokenIds = new List<int>();
        foreach (var token in tokens)
        {
            if (inverse_vocab.TryGetValue(token, out int id))
            {
                tokenIds.Add(id);
            }
            else
            {
                tokenIds.AddRange(TokenizeWithBpe(token));
            }
        }

        return tokenIds;
    }

    private List<int> TokenizeWithBpe(string token)
    {
        var tokenIds = new List<int>();
        foreach (char c in token)
        {
            string cStr = c.ToString();
            if (inverse_vocab.TryGetValue(cStr, out int id))
            {
                tokenIds.Add(id);
            }
            else
            {
                throw new ArgumentException($"Character not found in vocab: {c}");
            }
        }

        bool canMerge = true;
        while (canMerge && tokenIds.Count > 1)
        {
            canMerge = false;
            var newTokens = new List<int>();
            int i = 0;
            while (i < tokenIds.Count - 1)
            {
                var pair = Tuple.Create(tokenIds[i], tokenIds[i + 1]);
                if (bpe_merges.TryGetValue(pair, out int mergedTokenId))
                {
                    newTokens.Add(mergedTokenId);
                    i += 2;
                    canMerge = true;
                }
                else
                {
                    newTokens.Add(tokenIds[i]);
                    i++;
                }
            }
            if (i < tokenIds.Count)
            {
                newTokens.Add(tokenIds[i]);
            }
            tokenIds = newTokens;
        }

        return tokenIds;
    }

    public string Decode(List<int> tokenIds)
    {
        var decodedString = new System.Text.StringBuilder();
        foreach (int tokenId in tokenIds)
        {
            if (!vocab.TryGetValue(tokenId, out string? token))
            {
                throw new ArgumentException($"Token ID {tokenId} not found in vocab.");
            }
            if (token.StartsWith("Ġ"))
            {
                decodedString.Append(" " + token.Substring(1));
            }
            else
            {
                decodedString.Append(token);
            }
        }
        return decodedString.ToString();
    }

    private static Tuple<int, int>? FindFreqPair(List<int> tokenIds)
    {
        if (tokenIds.Count < 2) return null;

        var pairCounts = new Dictionary<Tuple<int, int>, int>();
        for (int i = 0; i < tokenIds.Count - 1; i++)
        {
            var pair = Tuple.Create(tokenIds[i], tokenIds[i + 1]);
            if (pairCounts.TryGetValue(pair, out var count))
            {
                pairCounts[pair] = count + 1;
            }
            else
            {
                pairCounts[pair] = 1;
            }
        }

        if (pairCounts.Count == 0) return null;

        Tuple<int, int>? bestPair = null;
        int maxCount = -1;
        foreach (var kvp in pairCounts)
        {
            if (kvp.Value > maxCount)
            {
                maxCount = kvp.Value;
                bestPair = kvp.Key;
            }
        }
        return bestPair;
    }

    private static List<int> ReplacePair(List<int> tokenIds, Tuple<int, int> pairId, int newId)
    {
        var replaced = new List<int>();
        int i = 0;
        while (i < tokenIds.Count)
        {
            if (i < tokenIds.Count - 1 && tokenIds[i] == pairId.Item1 && tokenIds[i + 1] == pairId.Item2)
            {
                replaced.Add(newId);
                i += 2;
            }
            else
            {
                replaced.Add(tokenIds[i]);
                i++;
            }
        }
        return replaced;
    }
}
