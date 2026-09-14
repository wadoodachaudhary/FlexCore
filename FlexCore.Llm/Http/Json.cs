using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Fx.ControlKit.Llm.Http;

/// <summary>Serialisation defaults and tolerant readers shared by the adapters.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(JsonNode node) => node.ToJsonString(Options);

    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        JsonElement element => JsonSerializer.SerializeToNode(element, Options),
        JsonDocument document => JsonSerializer.SerializeToNode(document.RootElement, Options),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), Options),
    };

    public static JsonNode? ToNode(JsonElement? element)
        => element is { } value ? JsonSerializer.SerializeToNode(value, Options) : null;

    /// <summary>Copies caller-supplied extras onto <paramref name="target"/>; a null value removes the key.</summary>
    public static void MergeExtras(JsonObject target, IReadOnlyDictionary<string, object?>? extras, Func<string, bool>? accept = null)
    {
        if (extras is null) return;
        foreach (var (key, value) in extras)
        {
            if (accept is not null && !accept(key)) continue;
            if (value is null)
            {
                target.Remove(key);
            }
            else
            {
                target[key] = ToNode(value);
            }
        }
    }

    public static int GetInt(JsonElement obj, string name, int fallback = 0)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt32(out var n) ? n : (int)Math.Round(el.GetDouble()),
            JsonValueKind.String => int.TryParse(el.GetString(), out var n) ? n : fallback,
            _ => fallback,
        };
    }

    public static int? GetNullableInt(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt32(out var n) ? n : (int)Math.Round(el.GetDouble()),
            JsonValueKind.String => int.TryParse(el.GetString(), out var n) ? n : null,
            _ => null,
        };
    }

    public static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public static bool GetBool(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    public static JsonElement? GetObject(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object
            ? el
            : null;

    public static JsonElement? GetArray(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
            ? el
            : null;

    public static DateTimeOffset? UnixSeconds(JsonElement obj, string name)
    {
        var seconds = GetNullableInt(obj, name);
        return seconds is { } s and > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
    }

    public static JsonDocument ParseDocument(string json, string providerKey)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new LlmResponseException($"{providerKey} returned a body that is not JSON.", providerKey, rawJson: json, inner: ex);
        }
    }
}
