#pragma warning disable CA1416 // HTTP and WebSocket forwarding are server-only.
using System.Net.WebSockets;
using System.Text;

namespace Fx.ControlKit.Preview;

/// <summary>
/// Forwards browser traffic to a loopback web preview. The host maps the app route
/// and passes the request path, including the app prefix, so an ASP.NET path base matches.
/// This type does not reference ASP.NET Core endpoint types.
/// </summary>
public static class ProgramPreviewProxy
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public static async Task ForwardHttpAsync(
        IProgramPreviewHost host,
        string sessionId,
        string? token,
        string method,
        string pathAndQuery,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> requestHeaders,
        Stream? requestBody,
        string? browserPrefix,
        Stream responseBody,
        Action<int, IReadOnlyList<KeyValuePair<string, IEnumerable<string>>>> writeHead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        var target = host.ResolveWeb(sessionId, token);
        if (target.Upstream is null)
        {
            var message = target.StatusCode == 401
                ? "Preview session token was rejected."
                : "Preview session was not found.";
            writeHead(target.StatusCode, [new("Content-Type", ["text/plain"])]);
            await responseBody.WriteAsync(Encoding.UTF8.GetBytes(message), cancellationToken).ConfigureAwait(false);
            return;
        }

        host.Touch(sessionId, token);
        var relative = string.IsNullOrEmpty(pathAndQuery) ? "/" : pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery;
        var upstream = new Uri(target.Upstream, relative);
        using var request = new HttpRequestMessage(new HttpMethod(string.IsNullOrWhiteSpace(method) ? "GET" : method), upstream);
        if (requestBody is not null && method is not ("GET" or "HEAD"))
            request.Content = new StreamContent(requestBody);
        foreach (var header in requestHeaders)
        {
            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                var kept = WithoutPreviewCookie(header.Value);
                if (kept is not null)
                    request.Headers.TryAddWithoutValidation(header.Key, kept);
                continue;
            }
            if (SkipRequestHeader(header.Key)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var headers = new List<KeyValuePair<string, IEnumerable<string>>>();
        CopyHeaders(response.Headers, headers, target.Upstream, browserPrefix);
        if (response.Content is not null)
            CopyHeaders(response.Content.Headers, headers, target.Upstream, browserPrefix);
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(browserPrefix))
            headers.Add(new("Set-Cookie", [$"{ProgramPreviewHttp.CookieName}={Uri.EscapeDataString(token)}; Path={browserPrefix}; HttpOnly; SameSite=Lax"]));

        var media = response.Content?.Headers.ContentType?.MediaType ?? "";
        var length = response.Content?.Headers.ContentLength;
        var html = media.Contains("html", StringComparison.OrdinalIgnoreCase);
        var css = media.Contains("css", StringComparison.OrdinalIgnoreCase);
        var rewrite = response.Content is not null
            && browserPrefix is not null
            && (html || css)
            && length is null or <= 4_000_000;
        if (!rewrite)
        {
            writeHead((int)response.StatusCode, headers);
            if (response.Content is not null)
                await response.Content.CopyToAsync(responseBody, cancellationToken).ConfigureAwait(false);
            return;
        }

        var text = await response.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (html) text = RewriteHtml(text, browserPrefix!);
        if (css || html) text = RewriteCss(text, browserPrefix!);
        writeHead((int)response.StatusCode, headers);
        await responseBody.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    public static async Task ForwardWebSocketAsync(
        IProgramPreviewHost host,
        string sessionId,
        string? token,
        string pathAndQuery,
        WebSocket browser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(browser);
        var target = host.ResolveWeb(sessionId, token);
        if (target.Upstream is null)
        {
            if (browser.State == WebSocketState.Open)
                await browser.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Preview was rejected.", CancellationToken.None).ConfigureAwait(false);
            return;
        }

        host.Touch(sessionId, token);
        var relative = string.IsNullOrEmpty(pathAndQuery) ? "/" : pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery;
        var scheme = target.Upstream.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var upstream = new UriBuilder(target.Upstream) { Scheme = scheme }.Uri;
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(upstream, relative), cancellationToken).ConfigureAwait(false);
        var outbound = CopyWebSocketAsync(browser, client, cancellationToken);
        var inbound = CopyWebSocketAsync(client, browser, cancellationToken);
        await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
        try { client.Abort(); } catch { }
        try { browser.Abort(); } catch { }
    }

    private static void CopyHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> source,
        List<KeyValuePair<string, IEnumerable<string>>> destination,
        Uri upstream,
        string? browserPrefix)
    {
        foreach (var header in source)
        {
            if (SkipResponseHeader(header.Key)) continue;
            if (header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) && browserPrefix is not null)
            {
                var rewritten = header.Value.Select(value => RewriteLocation(value, upstream, browserPrefix)).ToArray();
                destination.Add(new(header.Key, rewritten));
                continue;
            }
            destination.Add(new(header.Key, header.Value.ToArray()));
        }
    }

    private static string RewriteLocation(string value, Uri upstream, string browserPrefix)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && absolute.Host.Equals(upstream.Host, StringComparison.OrdinalIgnoreCase)
            && absolute.Port == upstream.Port)
            return browserPrefix + absolute.PathAndQuery;
        if (value.StartsWith("//", StringComparison.Ordinal)) return value;
        if (value.StartsWith('/') && !value.StartsWith(browserPrefix + "/", StringComparison.Ordinal) && value != browserPrefix)
            return browserPrefix + value;
        return value;
    }

    private static string RewriteHtml(string text, string prefix)
    {
        text = RewriteRoot(text, "href=\"", prefix);
        text = RewriteRoot(text, "href='", prefix);
        text = RewriteRoot(text, "src=\"", prefix);
        text = RewriteRoot(text, "src='", prefix);
        text = RewriteRoot(text, "action=\"", prefix);
        text = RewriteRoot(text, "action='", prefix);
        return text;
    }

    private static string RewriteCss(string text, string prefix)
    {
        text = RewriteRoot(text, "url(\"", prefix);
        text = RewriteRoot(text, "url('", prefix);
        return RewriteRoot(text, "url(", prefix);
    }

    private static string RewriteRoot(string text, string opener, string prefix)
    {
        var owned = opener + prefix + "/";
        var protocol = opener + "//";
        var rooted = opener + "/";
        text = text.Replace(owned, opener + "\u0001", StringComparison.Ordinal);
        text = text.Replace(protocol, opener + "\u0002", StringComparison.Ordinal);
        text = text.Replace(rooted, owned, StringComparison.Ordinal);
        text = text.Replace(opener + "\u0002", protocol, StringComparison.Ordinal);
        return text.Replace(opener + "\u0001", owned, StringComparison.Ordinal);
    }

    private static string? WithoutPreviewCookie(IEnumerable<string> values)
    {
        var kept = new List<string>();
        var name = ProgramPreviewHttp.CookieName + "=";
        foreach (var value in values)
        {
            foreach (var part in value.Split(';'))
            {
                var piece = part.Trim();
                if (piece.Length == 0 || piece.StartsWith(name, StringComparison.Ordinal)) continue;
                kept.Add(piece);
            }
        }
        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    private static bool SkipRequestHeader(string name)
        => name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static bool SkipResponseHeader(string name)
        => name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Frame-Options", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Security-Policy", StringComparison.OrdinalIgnoreCase);

    private static async Task CopyWebSocketAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested && source.State == WebSocketState.Open)
            {
                var result = await source.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (destination.State == WebSocketState.Open)
                        await destination.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed.", CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }
}
