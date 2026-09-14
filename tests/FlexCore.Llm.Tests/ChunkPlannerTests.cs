using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Chunking;
using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Llm.Pricing;
using Xunit;

namespace FlexCore.Llm.Tests;

public class ChunkPlannerTests
{
    private static readonly ModelRef Phi = ModelRef.Parse("ollama:phi3:medium"); // 16,000 tokens / 3,000 reserve in the catalog

    private static string Paragraph(int words, string word = "lorem") => string.Join(' ', Enumerable.Repeat(word, words)) + ".";

    [Fact]
    public void Text_that_fits_is_a_single_chunk_and_the_margin_is_1024_tokens()
    {
        var planner = ChunkPlanner.Default;
        // available = 16,000 - fixed - 3,000 - 1,024
        var request = new ChunkRequest { Model = Phi, Text = new string('a', 4 * 11_000), FixedPromptChars = 4 * 900 };

        var plan = planner.Plan(request);

        Assert.False(plan.IsSplit);
        Assert.Equal(11_000, plan.TextTokens);
        Assert.Equal(16_000 - 900 - 3_000 - 1_024, plan.AvailableTokens);
        Assert.True(planner.Fits(request));
        Assert.False(planner.Fits(request with { FixedPromptChars = 4 * 1_000 }));
    }

    [Fact]
    public void Oversized_text_splits_on_paragraphs_then_sentences_with_previews_and_separators()
    {
        var planner = ChunkPlanner.Default;
        var paragraphs = Enumerable.Range(0, 6).Select(i => Paragraph(300, $"w{i}")).ToArray();
        var text = string.Join("\n\n", paragraphs);   // ~6 x 300 words

        // available = 2,000 - 500 - 1,024 = 476 tokens: two ~225-token paragraphs per chunk.
        var plan = planner.Plan(new ChunkRequest { Model = Phi, Text = text, ContextTokens = 2_000, ReservedOutputTokens = 500 });

        Assert.True(plan.IsSplit);
        Assert.Equal(3, plan.Count);
        Assert.All(plan.Chunks, c => Assert.True(c.EstimatedTokens <= plan.AvailableTokens));
        Assert.Equal(plan.Chunks.Count, plan.Chunks.Select(c => c.Index).Distinct().Count());
        Assert.Null(plan.Chunks[0].PreviousPreview);
        Assert.NotNull(plan.Chunks[0].NextPreview);
        Assert.True(plan.Chunks[0].NextPreview!.Length <= ChunkPlanner.PreviewChars + 3);
        Assert.Null(plan.Chunks[^1].NextPreview);
        Assert.Equal("\n\n", plan.Chunks[0].SeparatorToNext);

        var stitched = planner.Stitch(plan.Chunks, plan.Chunks.Select(c => c.Text.ToUpperInvariant()).ToList());
        Assert.Equal(text.ToUpperInvariant(), stitched);
    }

    [Fact]
    public void A_single_huge_paragraph_is_cut_on_sentences_then_words()
    {
        var planner = ChunkPlanner.Default;
        var sentence = Paragraph(40) + " ";
        var text = string.Concat(Enumerable.Repeat(sentence, 200)).Trim();

        var plan = planner.Plan(new ChunkRequest { Model = Phi, Text = text, ContextTokens = 3_000, ReservedOutputTokens = 300 });

        Assert.True(plan.Chunks.Count > 1);
        Assert.All(plan.Chunks, c => Assert.EndsWith(".", c.Text));
        Assert.Equal(" ", plan.Chunks[0].SeparatorToNext);
        Assert.Equal(text, planner.Stitch(plan.Chunks, plan.Chunks.Select(c => c.Text).ToList()));
    }

    [Fact]
    public void Too_little_room_throws_and_hard_target_splits_even_when_it_fits()
    {
        var planner = ChunkPlanner.Default;
        var small = new ChunkRequest { Model = Phi, Text = Paragraph(50), FixedPromptChars = 4 * 12_000 };
        var ex = Assert.Throws<ChunkPlanException>(() => planner.Plan(small));
        Assert.Contains(ChunkPlanner.MinimumChunkTokens.ToString(), ex.Message);

        var text = string.Join("\n\n", Enumerable.Range(0, 4).Select(_ => Paragraph(200)));
        var forced = planner.Plan(new ChunkRequest { Model = Phi, Text = text, TargetChunkChars = 1_800 });
        Assert.True(forced.IsSplit);
        Assert.All(forced.Chunks, c => Assert.True(c.EstimatedTokens <= 450 + 10));
    }

    [Fact]
    public void Model_config_supplies_window_chunk_size_and_mode()
    {
        var store = new InMemoryModelConfigStore(new Dictionary<string, LlmModelConfig>());
        store.Save("alice", new LlmModelConfig("phi3:medium", ContextTokens: 8_000, ChunkChars: 2_000, ChunkOnlyWhenTooLarge: false));
        var planner = new ChunkPlanner(LlmContextBudget.Default, store);
        var text = string.Join("\n\n", Enumerable.Range(0, 5).Select(_ => Paragraph(150)));

        var alice = planner.Plan(new ChunkRequest { Model = Phi, Text = text, UserId = "alice" });
        var nobody = planner.Plan(new ChunkRequest { Model = Phi, Text = text });

        Assert.Equal(8_000, alice.Budget.EstimatedContextTokens);
        Assert.True(alice.IsSplit);
        Assert.All(alice.Chunks, c => Assert.True(c.Text.Length <= 2_000 + 200));
        Assert.False(nobody.IsSplit);
        Assert.Equal(16_000, nobody.Budget.EstimatedContextTokens);
    }
}
