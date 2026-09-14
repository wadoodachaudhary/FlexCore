using System.Runtime.CompilerServices;
using System.Text;

namespace Fx.ControlKit.Llm.Http;

public readonly record struct SseEvent(string? Event, string Data)
{
    public bool IsDone => string.Equals(Data, "[DONE]", StringComparison.Ordinal);
}

/// <summary>
/// Minimal text/event-stream reader: joins multi-line <c>data:</c> fields,
/// dispatches on the blank line, ignores comments and <c>id:</c>/<c>retry:</c>.
/// </summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        string? eventName = null;
        var data = new StringBuilder();
        var hasData = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return new SseEvent(eventName, data.ToString());
                }

                eventName = null;
                data.Clear();
                hasData = false;
                continue;
            }

            if (line[0] == ':') continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (hasData) data.Append('\n');
                    data.Append(value);
                    hasData = true;
                    break;
            }
        }

        if (hasData)
        {
            yield return new SseEvent(eventName, data.ToString());
        }
    }
}

/// <summary>Newline-delimited JSON (Ollama streams): one non-empty line per document.</summary>
public static class NdjsonReader
{
    public static async IAsyncEnumerable<string> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (line.Trim().Length == 0) continue;
            yield return line;
        }
    }
}
