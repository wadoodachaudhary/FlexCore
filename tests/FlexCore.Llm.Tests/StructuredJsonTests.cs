using System.Text.Json;
using Fx.ControlKit.Llm;
using Xunit;

namespace FlexCore.Llm.Tests;

public class StructuredJsonTests
{
    [Fact]
    public void Extracts_from_code_fences_and_surrounding_prose()
    {
        Assert.Equal("""{"a":1}""", StructuredJson.ExtractObject("Here you go:\n```json\n{\"a\":1}\n```\nDone."));
        Assert.Equal("""{"a":{"b":2}}""", StructuredJson.ExtractObject("Sure! {\"a\":{\"b\":2}} trailing"));
        Assert.Equal("""{"s":"}"}""", StructuredJson.ExtractObject("{\"s\":\"}\"} extra"));
    }

    [Theory]
    [InlineData("""{"summary":"a story","themes":["love","loss""", """{"summary":"a story","themes":["love","loss"]}""")]
    [InlineData("""{"summary":"a stor""", """{"summary":"a stor"}""")]
    [InlineData("""{"summary":"ok","synop""", """{"summary":"ok"}""")]
    [InlineData("""{"summary":"ok","count":12""", """{"summary":"ok","count":12}""")]
    [InlineData("""{"summary":"ok","count":""", """{"summary":"ok"}""")]
    [InlineData("""{"list":[{"name":"x","done":true},{"name":"y",""", """{"list":[{"name":"x","done":true},{"name":"y"}]}""")]
    [InlineData("""{"flag":tru""", """{}""")]
    public void Repairs_truncated_objects(string truncated, string expected)
    {
        var repaired = StructuredJson.Repair(truncated);
        Assert.Equal(expected, repaired);
        using var doc = JsonDocument.Parse(repaired);
    }

    [Fact]
    public void TryParse_returns_a_document_after_repair_and_TryDeserialize_maps_it()
    {
        Assert.True(StructuredJson.TryParse("```json\n{\"summary\":\"cut off", out var doc));
        using (doc!)
        {
            Assert.Equal("cut off", doc!.RootElement.GetProperty("summary").GetString());
        }

        Assert.True(StructuredJson.TryDeserialize<SummaryDoc>("{\"summary\":\"s\",\"themes\":[\"a\",\"b\"", out var value));
        Assert.Equal("s", value!.Summary);
        Assert.Equal(new[] { "a", "b" }, value.Themes);

        Assert.False(StructuredJson.TryParse("no json here", out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Loose_extraction_reads_properties_out_of_prose()
    {
        const string raw = "summary: \"The tale\", themes: [\"war\", \"peace\", \"war\"], conflicts: alpha; beta, gamma }";

        Assert.Equal("The tale", StructuredJson.ExtractLooseString(raw, "synopsis", "summary"));
        Assert.Equal(new[] { "war", "peace" }, StructuredJson.ExtractLooseArray(raw, "themes"));
        Assert.Equal(new[] { "alpha", "beta" }, StructuredJson.ExtractLooseArray(raw, "conflicts", limit: 2));
        Assert.Null(StructuredJson.ExtractLooseString(raw, "missing"));
        Assert.Equal("plain answer", StructuredJson.LooseText("```json\n\"plain answer\"\n```"));
    }

    [Fact]
    public void ChatResult_extension_parses_the_text()
    {
        var result = new ChatResult("```json\n{\"ok\":true}\n```", ModelRef.Parse("groq:x"), null, FinishReasons.Stop, null, TimeSpan.Zero, null);
        Assert.True(result.TryParseJson<Flag>(out var flag));
        Assert.True(flag!.Ok);
    }

    private sealed record SummaryDoc(string Summary, string[] Themes);

    private sealed record Flag(bool Ok);
}
