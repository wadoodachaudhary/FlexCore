using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Fx.ControlKit.Llm.Local;

/// <summary>
/// In-process BPE tokenizer with GPT-2 vocabulary loading, special tokens, and byte fallback.
/// </summary>
public sealed class LocalTokenizer
{
    public const int EndOfTextTokenId = 50256;

    private readonly Dictionary<string, int> _encoder = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _decoder = new();

    public LocalTokenizer()
    {
        InitializeVocab();
    }

    private void InitializeVocab()
    {
        _encoder["<|endoftext|>"] = EndOfTextTokenId;
        _decoder[EndOfTextTokenId] = "<|endoftext|>";

        // Try loading real GPT-2 BPE encoder.json from known locations
        string[] searchPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "encoder.json"),
            Path.Combine(AppContext.BaseDirectory, "encoder.json"),
            "/Users/wadood/GhostWriter/Assets/encoder.json",
            "/Users/wadood/projects/VBToCSharp/FlexCore/FlexCore.Llm/Assets/encoder.json",
            "/Users/wadood/DotnetLLM/docs/LLMPython/ch02/02_bonus_bytepair-encoder/gpt2_model/encoder.json"
        };

        bool loaded = false;
        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (dict != null && dict.Count > 1000)
                    {
                        foreach (var kvp in dict)
                        {
                            _encoder[kvp.Key] = kvp.Value;
                            _decoder[kvp.Value] = CleanBpeToken(kvp.Key);
                        }
                        loaded = true;
                        break;
                    }
                }
                catch { }
            }
        }

        if (!loaded)
        {
            // Minimal byte fallback
            for (int i = 0; i < 256; i++)
            {
                char c = (char)i;
                string s = c.ToString();
                _encoder[s] = i;
                _decoder[i] = s;
            }
        }
    }

    private static string CleanBpeToken(string raw)
    {
        // GPT-2 BPE representations:
        // 'Ġ' (\u0120) represents leading space ' '
        // 'Ċ' (\u010a) represents newline '\n'
        return raw.Replace('\u0120', ' ').Replace('\u010a', '\n');
    }

    public List<int> Encode(string text)
    {
        var tokens = new List<int>();
        if (string.IsNullOrEmpty(text)) return tokens;

        int i = 0;
        while (i < text.Length)
        {
            if (text.AsSpan(i).StartsWith("<|endoftext|>"))
            {
                tokens.Add(EndOfTextTokenId);
                i += 13;
                continue;
            }

            // Convert space into BPE token representation if using standard vocabulary
            bool matched = false;
            int maxLen = Math.Min(32, text.Length - i);
            for (int len = maxLen; len >= 1; len--)
            {
                string substr = text.Substring(i, len);
                string bpeSubstr = substr.Replace(' ', '\u0120').Replace('\n', '\u010a');

                if (_encoder.TryGetValue(bpeSubstr, out int id) || _encoder.TryGetValue(substr, out id))
                {
                    tokens.Add(id);
                    i += len;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                char c = text[i];
                if (_encoder.TryGetValue(c.ToString(), out int charId))
                {
                    tokens.Add(charId);
                }
                else if (c < 256)
                {
                    tokens.Add(c);
                }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(new[] { c });
                    foreach (var b in bytes) tokens.Add(b);
                }
                i++;
            }
        }

        return tokens;
    }

    public string Decode(IEnumerable<int> tokenIds)
    {
        var sb = new StringBuilder();
        foreach (var id in tokenIds)
        {
            if (id == EndOfTextTokenId)
            {
                continue;
            }
            if (_decoder.TryGetValue(id, out var text))
            {
                sb.Append(text);
            }
            else if (id >= 0 && id < 256)
            {
                sb.Append((char)id);
            }
        }
        return sb.ToString();
    }
}
