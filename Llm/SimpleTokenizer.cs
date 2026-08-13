using System.Text.RegularExpressions;

namespace Fx.ControlKit.Llm;

public class TokenItem
{
    public int Index { get; set; }
    public string Token { get; set; } = "";
    public int TokenId { get; set; }
}

public class SimpleTokenizer
{
    private readonly Dictionary<string, int> _strToInt;
    private readonly Dictionary<int, string> _intToStr;
    public const string UnkToken = "<|unk|>";
    public const string EndOfTextToken = "<|endoftext|>";

    public SimpleTokenizer(Dictionary<string, int> vocab)
    {
        _strToInt = new Dictionary<string, int>(vocab, StringComparer.OrdinalIgnoreCase);
        _intToStr = vocab.ToDictionary(kv => kv.Value, kv => kv.Key);
    }

    public List<int> Encode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new List<int>();

        var splitPattern = @"([,.:;?_!""()']|--|\s)";
        var rawTokens = Regex.Split(text, splitPattern);

        var preprocessed = rawTokens
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        var ids = new List<int>();
        foreach (var token in preprocessed)
        {
            if (_strToInt.TryGetValue(token, out var id))
            {
                ids.Add(id);
            }
            else if (_strToInt.TryGetValue(UnkToken, out var unkId))
            {
                ids.Add(unkId);
            }
            else
            {
                ids.Add(0);
            }
        }

        return ids;
    }

    public string Decode(List<int> ids)
    {
        if (ids == null || ids.Count == 0)
            return string.Empty;

        var tokens = ids.Select(id =>
        {
            if (_intToStr.TryGetValue(id, out var token))
                return token;
            return UnkToken;
        }).ToList();

        var text = string.Join(" ", tokens);
        text = Regex.Replace(text, @"\s+([,.:;?!""()'])", "$1");

        return text;
    }
}
