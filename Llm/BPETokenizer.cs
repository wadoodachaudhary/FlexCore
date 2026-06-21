using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Llm;

public class BPETokenizer
{
    public Dictionary<int, string> vocab { get; private set; } = new();
    public Dictionary<string, int> inverse_vocab { get; private set; } = new();
    public Dictionary<Tuple<int, int>, int> bpe_merges { get; private set; } = new();
    public Dictionary<Tuple<string, string>, int> bpe_ranks { get; private set; } = new();

    public BPETokenizer()
    {
    }

    public void Train(string text, int vocabSize, HashSet<string>? allowedSpecial = null)
    {
        var tokens = PretokenizeText(text);

        var uniqueChars = new List<string>();
        for (int i = 0; i < 256; i++)
        {
            uniqueChars.Add(((char)i).ToString());
        }

        var uniqueCharsSet = new HashSet<char>();
        foreach (var tok in tokens)
        {
            foreach (char c in tok)
            {
                uniqueCharsSet.Add(c);
            }
        }

        var sortedChars = uniqueCharsSet.OrderBy(c => c).ToList();
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

        var tokenIdSequences = new List<List<int>>();
        foreach (var tok in tokens)
        {
            var seq = new List<int>();
            foreach (char c in tok)
            {
                seq.Add(inverse_vocab[c.ToString()]);
            }
            tokenIdSequences.Add(seq);
        }

        bpe_merges = new Dictionary<Tuple<int, int>, int>();
        int startId = vocab.Count;
        for (int newId = startId; newId < vocabSize; newId++)
        {
            var pairId = FindFreqPair(tokenIdSequences);
            if (pairId == null) break;

            tokenIdSequences = ReplacePair(tokenIdSequences, pairId, newId);
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

    public void LoadVocabAndMergesFromOpenAI(string vocabPath, string bpeMergesPath)
    {
        string vocabJson = File.ReadAllText(vocabPath);
        var loadedVocab = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabJson) 
            ?? throw new InvalidOperationException("Failed to load OpenAI vocabulary json.");

        vocab = loadedVocab.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
        inverse_vocab = loadedVocab.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        if (!inverse_vocab.TryGetValue("Ċ", out int cId) || cId != 198)
        {
            throw new KeyNotFoundException("Vocabulary missing GPT-2 newline glyph 'Ċ' at id 198.");
        }

        if (!inverse_vocab.TryGetValue("<|endoftext|>", out int eotId) || eotId != 50256)
        {
            throw new KeyNotFoundException("Vocabulary missing <|endoftext|> at id 50256.");
        }

        if (!inverse_vocab.ContainsKey("\n"))
        {
            inverse_vocab["\n"] = 198;
        }

        if (!inverse_vocab.ContainsKey("\r"))
        {
            if (vocab.ContainsKey(201))
            {
                inverse_vocab["\r"] = 201;
            }
            else
            {
                throw new KeyNotFoundException("Vocabulary missing carriage return token at id 201.");
            }
        }

        bpe_ranks = new Dictionary<Tuple<string, string>, int>();
        var lines = File.ReadAllLines(bpeMergesPath);
        int startIndex = 0;
        if (lines.Length > 0 && lines[0].StartsWith("#"))
        {
            startIndex = 1;
        }

        int rank = 0;
        for (int i = startIndex; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            string token1 = parts[0];
            string token2 = parts[1];
            if (inverse_vocab.ContainsKey(token1) && inverse_vocab.ContainsKey(token2))
            {
                bpe_ranks[Tuple.Create(token1, token2)] = rank++;
            }
        }
    }

    public List<int> Encode(string text, HashSet<string>? allowedSpecial = null)
    {
        var specialsInVocab = inverse_vocab.Keys
            .Where(tok => tok.StartsWith("<|") && tok.EndsWith("|>"))
            .ToList();

        if (allowedSpecial == null)
        {
            var disallowed = specialsInVocab.Where(tok => text.Contains(tok)).ToList();
            if (disallowed.Count > 0)
            {
                throw new ArgumentException($"Disallowed special tokens encountered in text: {string.Join(", ", disallowed)}");
            }
        }
        else
        {
            var disallowed = specialsInVocab.Where(tok => text.Contains(tok) && !allowedSpecial.Contains(tok)).ToList();
            if (disallowed.Count > 0)
            {
                throw new ArgumentException($"Disallowed special tokens encountered in text: {string.Join(", ", disallowed)}");
            }
        }

        var tokenIds = new List<int>();

        if (allowedSpecial != null && allowedSpecial.Count > 0)
        {
            var escTokens = allowedSpecial.OrderByDescending(tok => tok.Length).Select(Regex.Escape);
            string specialPattern = "(" + string.Join("|", escTokens) + ")";

            int lastIndex = 0;
            var matches = Regex.Matches(text, specialPattern);
            foreach (Match match in matches)
            {
                string prefix = text.Substring(lastIndex, match.Index - lastIndex);
                tokenIds.AddRange(Encode(prefix, allowedSpecial: null));

                string specialToken = match.Value;
                if (inverse_vocab.TryGetValue(specialToken, out int sId))
                {
                    tokenIds.Add(sId);
                }
                else
                {
                    throw new ArgumentException($"Special token {specialToken} not found in vocabulary.");
                }
                lastIndex = match.Index + match.Length;
            }

            text = text.Substring(lastIndex);

            var disallowed = specialsInVocab.Where(tok => text.Contains(tok) && !allowedSpecial.Contains(tok)).ToList();
            if (disallowed.Count > 0)
            {
                throw new ArgumentException($"Disallowed special tokens encountered in text: {string.Join(", ", disallowed)}");
            }
        }

        var tokens = PretokenizeText(text);
        foreach (var tok in tokens)
        {
            if (inverse_vocab.TryGetValue(tok, out int id))
            {
                tokenIds.Add(id);
            }
            else
            {
                tokenIds.AddRange(TokenizeWithBpe(tok));
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

        if (bpe_ranks.Count == 0)
        {
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

        var symbols = tokenIds.Select(idNum => vocab[idNum]).ToList();

        while (true)
        {
            var pairs = new HashSet<Tuple<string, string>>();
            for (int k = 0; k < symbols.Count - 1; k++)
            {
                pairs.Add(Tuple.Create(symbols[k], symbols[k + 1]));
            }

            if (pairs.Count == 0) break;

            int minRank = int.MaxValue;
            Tuple<string, string>? bigram = null;
            foreach (var p in pairs)
            {
                if (bpe_ranks.TryGetValue(p, out int rank))
                {
                    if (rank < minRank)
                    {
                        minRank = rank;
                        bigram = p;
                    }
                }
            }

            if (bigram == null) break;

            string first = bigram.Item1;
            string second = bigram.Item2;
            var newSymbols = new List<string>();
            int idx = 0;
            while (idx < symbols.Count)
            {
                if (idx < symbols.Count - 1 && symbols[idx] == first && symbols[idx + 1] == second)
                {
                    newSymbols.Add(first + second);
                    idx += 2;
                }
                else
                {
                    newSymbols.Add(symbols[idx]);
                    idx++;
                }
            }
            symbols = newSymbols;

            if (symbols.Count == 1) break;
        }

        return symbols.Select(sym => inverse_vocab[sym]).ToList();
    }

    public string Decode(List<int> tokenIds)
    {
        var outList = new List<string>();
        foreach (int tid in tokenIds)
        {
            if (!vocab.TryGetValue(tid, out string? tok))
            {
                throw new ArgumentException($"Token ID {tid} not found in vocab.");
            }

            if (tid == 198 || tok == "\n")
            {
                outList.Add("\n");
            }
            else if (tid == 201 || tok == "\r")
            {
                outList.Add("\r");
            }
            else if (tok.StartsWith("Ġ"))
            {
                outList.Add(" " + tok.Substring(1));
            }
            else
            {
                outList.Add(tok);
            }
        }
        return string.Join("", outList);
    }

    public void SaveVocabAndMerges(string vocabPath, string bpeMergesPath)
    {
        var vocabOptions = new JsonSerializerOptions 
        { 
            WriteIndented = true, 
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        };
        var stringKeyVocab = vocab.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
        string vocabJson = JsonSerializer.Serialize(stringKeyVocab, vocabOptions);
        File.WriteAllText(vocabPath, vocabJson);

        var mergesList = bpe_merges.Select(kvp => new MergeEntry
        {
            Pair = new List<int> { kvp.Key.Item1, kvp.Key.Item2 },
            NewId = kvp.Value
        }).ToList();
        string mergesJson = JsonSerializer.Serialize(mergesList, vocabOptions);
        File.WriteAllText(bpeMergesPath, mergesJson);
    }

    public void LoadVocabAndMerges(string vocabPath, string bpeMergesPath)
    {
        string vocabJson = File.ReadAllText(vocabPath);
        var stringKeyVocab = JsonSerializer.Deserialize<Dictionary<string, string>>(vocabJson) 
            ?? throw new InvalidOperationException("Failed to deserialize vocab.");
        vocab = stringKeyVocab.ToDictionary(kvp => int.Parse(kvp.Key), kvp => kvp.Value);
        inverse_vocab = vocab.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

        string mergesJson = File.ReadAllText(bpeMergesPath);
        var mergesList = JsonSerializer.Deserialize<List<MergeEntry>>(mergesJson) 
            ?? throw new InvalidOperationException("Failed to deserialize BPE merges.");
        bpe_merges = new Dictionary<Tuple<int, int>, int>();
        foreach (var merge in mergesList)
        {
            var pair = Tuple.Create(merge.Pair[0], merge.Pair[1]);
            bpe_merges[pair] = merge.NewId;
        }
    }

    public class MergeEntry
    {
        public List<int> Pair { get; set; } = new();
        public int NewId { get; set; }
    }

    public static List<string> PretokenizeText(string text)
    {
        var tokens = new List<string>();
        var parts = Regex.Split(text, @"(\r\n|\r|\n)");
        foreach (var part in parts)
        {
            if (part == "") continue;
            if (part == "\r\n")
            {
                tokens.Add("\r");
                tokens.Add("\n");
                continue;
            }
            if (part == "\r")
            {
                tokens.Add("\r");
                continue;
            }
            if (part == "\n")
            {
                tokens.Add("\n");
                continue;
            }

            int pendingSpaces = 0;
            var matches = Regex.Matches(part, @"( +)|(\S+)");
            foreach (Match m in matches)
            {
                if (m.Groups[1].Success)
                {
                    pendingSpaces += m.Groups[1].Value.Length;
                }
                else
                {
                    string word = m.Groups[2].Value;
                    if (pendingSpaces > 0)
                    {
                        for (int s = 0; s < pendingSpaces - 1; s++)
                        {
                            tokens.Add("Ġ");
                        }
                        tokens.Add("Ġ" + word);
                        pendingSpaces = 0;
                    }
                    else
                    {
                        tokens.Add(word);
                    }
                }
            }
            for (int s = 0; s < pendingSpaces; s++)
            {
                tokens.Add("Ġ");
            }
        }
        return tokens;
    }

    private static Tuple<int, int>? FindFreqPair(List<List<int>> tokenIdSequences)
    {
        var pairCounts = new Dictionary<Tuple<int, int>, int>();
        foreach (var seq in tokenIdSequences)
        {
            for (int i = 0; i < seq.Count - 1; i++)
            {
                var pair = Tuple.Create(seq[i], seq[i + 1]);
                if (pairCounts.TryGetValue(pair, out var count))
                {
                    pairCounts[pair] = count + 1;
                }
                else
                {
                    pairCounts[pair] = 1;
                }
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

    private static List<List<int>> ReplacePair(List<List<int>> tokenIdSequences, Tuple<int, int> pairId, int newId)
    {
        var replacedSequences = new List<List<int>>();
        foreach (var seq in tokenIdSequences)
        {
            var replaced = new List<int>();
            int i = 0;
            while (i < seq.Count)
            {
                if (i < seq.Count - 1 && seq[i] == pairId.Item1 && seq[i + 1] == pairId.Item2)
                {
                    replaced.Add(newId);
                    i += 2;
                }
                else
                {
                    replaced.Add(seq[i]);
                    i++;
                }
            }
            replacedSequences.Add(replaced);
        }
        return replacedSequences;
    }
}
