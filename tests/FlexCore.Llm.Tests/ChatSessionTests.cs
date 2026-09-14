using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Providers;
using Xunit;

namespace FlexCore.Llm.Tests;

public class ChatSessionTests
{
    private static string Reply(string text) => $$"""{"choices":[{"message":{"content":"{{text}}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2},"model":"llama-3.3-70b-versatile"}""";

    private static (LlmClient Client, TestHost Host) Build()
    {
        var host = new TestHost().Env("GROQ_API_KEY", "k");
        var client = new LlmClient(new ILlmProvider[] { new OpenAiCompatibleChatProvider(ProviderKeys.Groq, host.Http, host.Credentials) }, null, RetryPolicy.None, host.Credentials);
        return (client, host);
    }

    [Fact]
    public async Task Ask_appends_user_and_assistant_turns_and_sends_the_whole_history()
    {
        var (client, host) = Build();
        host.Enqueue(CannedResponse.Ok(Reply("first")), CannedResponse.Ok(Reply("second")));
        var session = client.StartSession("groq:llama-3.3-70b-versatile", "You are terse.", new ChatSessionOptions { Temperature = 0.1, UserId = "u1" });

        var first = await session.AskAsync("one");
        var second = await session.AskAsync("two");

        Assert.Equal("first", first.Text);
        Assert.Equal("second", second.Text);
        Assert.Equal(4, session.Messages.Count);
        Assert.Equal(2, session.Turns);
        Assert.Equal("second", session.LastReply);
        Assert.Equal(new LlmUsage(8, 4), session.TotalUsage);

        var sent = host.Handler.Last.Json.GetProperty("messages");
        Assert.Equal(4, sent.GetArrayLength());
        Assert.Equal("system", sent[0].GetProperty("role").GetString());
        Assert.Equal("You are terse.", sent[0].GetProperty("content").GetString());
        Assert.Equal("one", sent[1].GetProperty("content").GetString());
        Assert.Equal("first", sent[2].GetProperty("content").GetString());
        Assert.Equal("two", sent[3].GetProperty("content").GetString());
        Assert.Equal(0.1, host.Handler.Last.Json.GetProperty("temperature").GetDouble(), 6);

        session.Reset();
        Assert.Empty(session.Messages);
        Assert.Equal("You are terse.", session.System);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task Streaming_appends_the_assembled_reply_on_the_final_delta()
    {
        var (client, host) = Build();
        var sse = Streams.Sse(
            """{"choices":[{"delta":{"content":"Hel"}}]}""",
            """{"choices":[{"delta":{"content":"lo"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
            "[DONE]");
        host.Enqueue(CannedResponse.Sse(sse));
        var session = client.StartSession("groq:llama-3.3-70b-versatile");

        var deltas = await Streams.Collect(session.AskStreamingAsync("hi"));

        Assert.Equal("Hello", Streams.Text(deltas));
        Assert.Equal("Hello", session.LastReply);
        Assert.Equal(ChatRole.Assistant, session.Messages[^1].Role);
        Assert.Equal(new LlmUsage(1, 1), session.TotalUsage);
    }

    [Fact]
    public async Task RunTools_answers_tool_calls_until_the_model_replies_in_text()
    {
        var (client, host) = Build();
        host.Enqueue(
            CannedResponse.Ok("""{"choices":[{"message":{"content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{\"path\":\"a.cs\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""),
            CannedResponse.Ok("""{"choices":[{"message":{"content":null,"tool_calls":[{"id":"c2","type":"function","function":{"name":"boom","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""),
            CannedResponse.Ok(Reply("fixed")));
        var session = client.StartSession("groq:llama-3.3-70b-versatile", options: new ChatSessionOptions
        {
            Tools = new[] { new ToolDefinition("read", "read a file", null), new ToolDefinition("boom", "fails", null) },
        });

        var result = await session.RunToolsAsync("fix the file", (call, _) => call.Name == "read"
            ? Task.FromResult("class A {}")
            : throw new InvalidOperationException("tool failed"));

        Assert.Equal("fixed", result.Text);
        Assert.Equal(3, host.Handler.Requests.Count);
        var final = host.Handler.Last.Json.GetProperty("messages");
        // user, assistant(tool call), tool result, assistant(tool call), tool result(error) — then the text reply is appended after the call.
        Assert.Equal(5, final.GetArrayLength());
        Assert.Equal("tool", final[2].GetProperty("role").GetString());
        Assert.Equal("class A {}", final[2].GetProperty("content").GetString());
        Assert.Equal("tool failed", final[4].GetProperty("content").GetString());
        Assert.Equal(6, session.Messages.Count);
    }

    [Fact]
    public async Task MaxTurns_drops_the_oldest_turn_and_fork_is_independent()
    {
        var (client, host) = Build();
        host.Enqueue(CannedResponse.Ok(Reply("a")), CannedResponse.Ok(Reply("b")), CannedResponse.Ok(Reply("c")), CannedResponse.Ok(Reply("d")));
        var session = client.StartSession("groq:llama-3.3-70b-versatile", options: new ChatSessionOptions { MaxTurns = 2 });

        await session.AskAsync("1");
        await session.AskAsync("2");
        var fork = session.Fork();
        await session.AskAsync("3");

        Assert.Equal(2, session.Turns);
        Assert.Equal("2", session.Messages[0].Text);
        Assert.Equal(4, fork.Messages.Count);

        await fork.AskAsync("x");
        Assert.Equal("d", fork.LastReply);
        Assert.Equal("c", session.LastReply);
    }
}
