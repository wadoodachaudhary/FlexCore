using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Llm;

public class OpenAiGpt2Encoder
{
    private readonly Dictionary<string, int> encoder = new();
    private readonly Dictionary<int, string> decoder = new();
    private readonly Dictionary<byte, char> byte_encoder = new();
    private readonly Dictionary<char, byte> byte_decoder = new();
    private readonly Dictionary<Tuple<string, string>, int> bpe_ranks = new();
    private readonly Dictionary<string, string> cache = new();
    private readonly object _cacheLock = new();

    public OpenAiGpt2Encoder(string vocabJsonPath, string bpeMergesPath)
    {
        byte_encoder = BytesToUnicode();
        byte_decoder = byte_encoder.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

        string json = File.ReadAllText(vocabJsonPath);
        var loadedVocab = JsonSerializer.Deserialize<Dictionary<string, int>>(json) 
            ?? throw new InvalidOperationException("Failed to load OpenAI vocabulary json.");
        
        foreach (var kvp in loadedVocab)
        {
            encoder[kvp.Key] = kvp.Value;
            decoder[kvp.Value] = kvp.Key;
        }

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
            bpe_ranks[Tuple.Create(parts[0], parts[1])] = rank++;
        }
    }

    public static Dictionary<byte, char> BytesToUnicode()
    {
        var bs = new List<int>();
        for (int i = '!'; i <= '~'; i++) bs.Add(i);
        for (int i = '¡'; i <= '¬'; i++) bs.Add(i);
        for (int i = '®'; i <= 'ÿ'; i++) bs.Add(i);

        var cs = new List<int>(bs);
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (!bs.Contains(b))
            {
                bs.Add(b);
                cs.Add(256 + n);
                n++;
            }
        }

        var dict = new Dictionary<byte, char>();
        for (int i = 0; i < bs.Count; i++)
        {
            dict[(byte)bs[i]] = (char)cs[i];
        }
        return dict;
    }

    private static HashSet<Tuple<string, string>> GetPairs(List<string> word)
    {
        var pairs = new HashSet<Tuple<string, string>>();
        if (word.Count < 2) return pairs;
        string prevChar = word[0];
        for (int i = 1; i < word.Count; i++)
        {
            pairs.Add(Tuple.Create(prevChar, word[i]));
            prevChar = word[i];
        }
        return pairs;
    }

    public string Bpe(string token)
    {
        lock (_cacheLock)
        {
            if (cache.TryGetValue(token, out string? cached))
            {
                return cached;
            }
        }

        var word = token.Select(c => c.ToString()).ToList();
        var pairs = GetPairs(word);
        if (pairs.Count == 0)
        {
            return token;
        }

        while (true)
        {
            Tuple<string, string>? bigram = null;
            int minRank = int.MaxValue;
            foreach (var pair in pairs)
            {
                if (bpe_ranks.TryGetValue(pair, out int rank))
                {
                    if (rank < minRank)
                    {
                        minRank = rank;
                        bigram = pair;
                    }
                }
            }

            if (bigram == null) break;

            string first = bigram.Item1;
            string second = bigram.Item2;
            var newWord = new List<string>();
            int i = 0;
            while (i < word.Count)
            {
                int j = word.IndexOf(first, i);
                if (j == -1)
                {
                    newWord.AddRange(word.Skip(i));
                    break;
                }
                newWord.AddRange(word.Skip(i).Take(j - i));
                i = j;

                if (word[i] == first && i < word.Count - 1 && word[i + 1] == second)
                {
                    newWord.Add(first + second);
                    i += 2;
                }
                else
                {
                    newWord.Add(word[i]);
                    i++;
                }
            }

            word = newWord;
            if (word.Count == 1)
            {
                break;
            }
            else
            {
                pairs = GetPairs(word);
            }
        }

        string result = string.Join(" ", word);
        lock (_cacheLock)
        {
            cache[token] = result;
        }
        return result;
    }

    public List<int> Encode(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<int>();

        var bpeTokens = new List<int>();
        string[] parts = text.Split(new[] { "<|endoftext|>" }, StringSplitOptions.None);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                bpeTokens.Add(50256); // Special token ID for <|endoftext|>
            }

            string part = parts[i];
            if (string.IsNullOrEmpty(part)) continue;

            var matches = Regex.Matches(part, @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+");
            foreach (Match match in matches)
            {
                var utf8Bytes = System.Text.Encoding.UTF8.GetBytes(match.Value);
                var unicodeMappedStr = new System.Text.StringBuilder();
                foreach (byte b in utf8Bytes)
                {
                    unicodeMappedStr.Append(byte_encoder[b]);
                }

                string token = unicodeMappedStr.ToString();
                string bpeRes = Bpe(token);
                foreach (var bpeToken in bpeRes.Split(' '))
                {
                    if (encoder.TryGetValue(bpeToken, out int id))
                    {
                        bpeTokens.Add(id);
                    }
                }
            }
        }
        return bpeTokens;
    }

    public string Decode(List<int> tokens)
    {
        var stringBuilder = new System.Text.StringBuilder();
        var resultBuilder = new System.Text.StringBuilder();

        foreach (int token in tokens)
        {
            if (token == 50256)
            {
                if (stringBuilder.Length > 0)
                {
                    string decodedMapped = stringBuilder.ToString();
                    var bytes = new List<byte>();
                    foreach (char c in decodedMapped)
                    {
                        if (byte_decoder.TryGetValue(c, out byte b))
                        {
                            bytes.Add(b);
                        }
                    }
                    resultBuilder.Append(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
                    stringBuilder.Clear();
                }
                resultBuilder.Append("<|endoftext|>");
            }
            else if (decoder.TryGetValue(token, out string? value))
            {
                stringBuilder.Append(value);
            }
        }

        if (stringBuilder.Length > 0)
        {
            string decodedMapped = stringBuilder.ToString();
            var bytes = new List<byte>();
            foreach (char c in decodedMapped)
            {
                if (byte_decoder.TryGetValue(c, out byte b))
                {
                    bytes.Add(b);
                }
            }
            resultBuilder.Append(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
        }

        return resultBuilder.ToString();
    }
}
