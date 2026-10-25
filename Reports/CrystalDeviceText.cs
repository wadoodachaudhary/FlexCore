using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

/// <summary>
/// Crystal lays a printed or exported report out on the printer device: GDI measures each glyph as a whole
/// 600-dpi pixel (the font's hinted hdmx width at that ppem, 83 for 10pt Arial) and wraps a line where the
/// next word's extent would pass the object's right edge. A browser measures the same text with fractional,
/// unhinted advances, so lines that Crystal keeps ("...(Base House)", 2101 device pixels in a 2100-pixel box)
/// wrap in HTML. This breaker reproduces Crystal's lines for native text whose font metrics are available and
/// hands the layout the same line bounds and character offsets the browser would have returned.
/// </summary>
internal static class CrystalDeviceText
{
    /// <summary>Crystal's printer layout resolution.</summary>
    internal const int DeviceDpi = 600;

    /// <summary>Set false to measure every native text object in the browser again (diagnostics only).</summary>
    internal static bool Enabled { get; set; } = true;

    internal static bool IsEligible(ReportDesignerElement element) => Enabled
        && element.UseCrystalScreenMetrics && element.Kind is "Text" or "Field" or "FieldHeading"
        && ReportObjectRenderer.WrapText(element) && !ReportObjectRenderer.ClipNativeLongWords(element)
        && !element.HorizontalAlignment.Equals("justified", StringComparison.OrdinalIgnoreCase)
        && ReportFontCatalog.NativeLineHeightPixels(element.FontFamily, element.FontSize, true) is not null;

    /// <summary>Measures the item's HTML with Crystal's device metrics, or null when a font or glyph is unavailable.</summary>
    internal static ReportTextMetrics? Measure(ReportDesignerElement element, string html)
    {
        if (!IsEligible(element) || string.IsNullOrEmpty(html)) return null;
        List<(string Text, CrystalDeviceFont Font, int Ppem)> runs;
        try { runs = Runs(element, html); }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or System.Xml.XmlException or FileNotFoundException) { return null; }
        if (runs is null) return null;
        // Chrome keeps line boxes in 1/64 px; use the same quantized leading so a native box and a browser-measured
        // sibling of the same font agree to the twip (12.16 px for 8pt Arial is 12.15625 px in the browser).
        var leading = Math.Floor(ReportFontCatalog.NativeLineHeightPixels(element.FontFamily, element.FontSize, true)!.Value * 64) / 64 * 15;
        var available = Math.Max(0, element.WidthTwips - element.IndentTwips
            - ReportObjectRenderer.BorderPixels(element.Visual.LeftLine, element.Visual.LineWidthTwips) * 15
            - ReportObjectRenderer.BorderPixels(element.Visual.RightLine, element.Visual.LineWidthTwips) * 15);
        // GDI paints an extent of E pixels on columns 0..E-1; Crystal keeps a word whose last column is the
        // box's right edge, so the limit is the device width plus one (2101 fits a 5040-twip, 2100-pixel box).
        var limit = (long)Math.Floor(available * (double)DeviceDpi / 1440) + 1;
        var lines = Break(runs, limit);
        if (lines is null) return null;
        var cap = ReportObjectRenderer.TextLineLimit(element);
        var visible = cap > 0 ? Math.Min(lines.Count, cap) : lines.Count;
        var borders = (ReportObjectRenderer.BorderPixels(element.Visual.TopLine, element.Visual.LineWidthTwips)
            + ReportObjectRenderer.BorderPixels(element.Visual.BottomLine, element.Visual.LineWidthTwips)) * 15;
        // Each line's bottom is the rounded cumulative leading and the box height is the last visible bottom plus
        // borders, so the final line never ends past the box (the paint loop drops a line that crosses the band edge).
        int Bottom(int count) => (int)Math.Round(count * leading, MidpointRounding.AwayFromZero);
        var bounds = lines.Select((line, index) => new ReportTextLine(Bottom(index), Bottom(index + 1), line.Start, line.End)).ToArray();
        return new ReportTextMetrics(Bottom(visible) + (int)Math.Round(borders, MidpointRounding.AwayFromZero), bounds) { Native = true };
    }

    /// <summary>The measured HTML, re-emitted as one block per native line so neither the viewer nor the PDF re-wraps it.</summary>
    internal static string ExplicitLines(string html, IReadOnlyList<ReportTextLine> lines)
    {
        if (lines.Count == 0 || lines.Any(l => l.Start < 0)) return html;
        List<Piece>? pieces;
        try { pieces = Pieces(html); }
        catch (System.Xml.XmlException) { return html; }
        if (pieces is null) return html;
        var at = pieces.Count == 0 ? 0 : pieces[^1].Start + pieces[^1].Text.Length;
        if (lines[^1].End != at) return html;
        var output = new StringBuilder();
        foreach (var line in lines)
        {
            output.Append("<span data-fx-native-line=\"true\" style=\"display:block;white-space:pre\">");
            foreach (var piece in pieces)
            {
                var start = Math.Max(line.Start, piece.Start);
                var end = Math.Min(line.End, piece.Start + piece.Text.Length);
                if (end <= start) continue;
                var slice = piece.Text[(start - piece.Start)..(end - piece.Start)];
                if (piece.Style is null) output.Append(WebUtility.HtmlEncode(slice));
                else output.Append("<span style=\"").Append(WebUtility.HtmlEncode(piece.Style)).Append("\">").Append(WebUtility.HtmlEncode(slice)).Append("</span>");
            }
            output.Append("</span>");
        }
        return output.ToString();
    }

    /// <summary>
    /// The widest native line in CSS pixels at the browser's realized em (unhinted advances), so a painted box can be
    /// widened by the sub-pixel overshoot of a line GDI fits but the browser would clip.
    /// </summary>
    internal static double? CssLineWidth(ReportDesignerElement element, string html, IReadOnlyList<ReportTextLine> lines)
    {
        List<(string Text, CrystalDeviceFont Font, int Ppem)> runs;
        try { runs = Runs(element, html)!; }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or System.Xml.XmlException or FileNotFoundException) { return null; }
        if (runs is null) return null;
        var text = string.Concat(runs.Select(r => r.Text));
        var widest = 0d;
        foreach (var line in lines)
        {
            if (line.Start < 0 || line.End > text.Length) return null;
            var width = 0d;
            var at = 0;
            foreach (var run in runs)
            {
                var start = Math.Max(line.Start, at);
                var end = Math.Min(line.End, at + run.Text.Length);
                for (var index = start; index < end; index++)
                {
                    var c = run.Text[index - at];
                    if (c is '\n' or '\r') continue;
                    width += run.Font.LinearAdvance(c) * run.Ppem * 96d / DeviceDpi;
                }
                at += run.Text.Length;
            }
            // Spaces that end a wrapped line hang past the edge in CSS and GDI alike.
            var trailing = 0d;
            var last = line.End - 1;
            while (last >= line.Start && text[last] is '\n' or '\r') last--;
            for (var index = last; index >= line.Start && text[index] == ' '; index--)
                trailing += Font(runs, index)?.LinearAdvance(' ') * Ppem(runs, index) * 96d / DeviceDpi ?? 0;
            widest = Math.Max(widest, width - trailing);
        }
        return widest;
        static CrystalDeviceFont? Font(List<(string Text, CrystalDeviceFont Font, int Ppem)> runs, int offset) => Locate(runs, offset)?.Font;
        static int Ppem(List<(string Text, CrystalDeviceFont Font, int Ppem)> runs, int offset) => Locate(runs, offset)?.Ppem ?? 0;
        static (string Text, CrystalDeviceFont Font, int Ppem)? Locate(List<(string Text, CrystalDeviceFont Font, int Ppem)> runs, int offset)
        {
            var at = 0;
            foreach (var run in runs) { if (offset < at + run.Text.Length) return run; at += run.Text.Length; }
            return null;
        }
    }

    /// <summary>
    /// A fixed one-line field clips at its right edge. When Crystal's device extent of the whole value fits the box,
    /// Crystal prints it in full; the browser's fractional width may still pass the edge, so the painted box widens.
    /// </summary>
    internal static void WidenSingleLine(ReportDesignerElement element, string html)
    {
        if (!Enabled || !element.UseCrystalScreenMetrics || element.Kind is not ("Text" or "Field" or "FieldHeading")
            || ReportObjectRenderer.WrapText(element) || string.IsNullOrEmpty(html)) return;
        List<(string Text, CrystalDeviceFont Font, int Ppem)>? runs;
        try { runs = Runs(element, html); }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or System.Xml.XmlException or FileNotFoundException) { return; }
        if (runs is null) return;
        var text = string.Concat(runs.Select(r => r.Text));
        var line = text.IndexOfAny(['\n', '\r']) is var cut and >= 0 ? text[..cut] : text;
        var device = 0L;
        var at = 0;
        foreach (var run in runs)
        {
            for (var index = 0; index < run.Text.Length && at + index < line.Length; index++)
            {
                var advance = run.Font.DeviceAdvance(run.Text[index], run.Ppem);
                if (advance < 0) return;
                device += advance;
            }
            at += run.Text.Length;
        }
        var available = Math.Max(0, element.WidthTwips - element.IndentTwips
            - ReportObjectRenderer.BorderPixels(element.Visual.LeftLine, element.Visual.LineWidthTwips) * 15
            - ReportObjectRenderer.BorderPixels(element.Visual.RightLine, element.Visual.LineWidthTwips) * 15);
        if (device > (long)Math.Floor(available * (double)DeviceDpi / 1440) + 1) return;
        WidenForOvershoot(element, html, [new ReportTextLine(0, 0, 0, line.Length)]);
    }

    /// <summary>Widens a painted, left-aligned, unbordered box by the sub-pixel overshoot of its widest native line.</summary>
    internal static void WidenForOvershoot(ReportDesignerElement element, string html, IReadOnlyList<ReportTextLine> lines)
    {
        // Only plain left-aligned text: a wider box would move right/centre-aligned text (numeric fields align right by
        // default), paint a background or border wider, and the PDF clips to the same box.
        var alignment = element.HorizontalAlignment.ToLowerInvariant();
        if (alignment is not ("left" or "default" or "") || element.NativeNumericFormat is not null || element.NativeNumberFormat is not null
            || ReportObjectRenderer.Color(element.BackgroundColor) != "transparent"
            || new[] { element.Visual.TopLine, element.Visual.BottomLine, element.Visual.LeftLine, element.Visual.RightLine }
                .Any(line => ReportObjectRenderer.BorderPixels(line, element.Visual.LineWidthTwips) > 0)) return;
        var text = CssLineWidth(element, html, lines);
        if (text is null) return;
        var inner = (element.WidthTwips - element.IndentTwips) / 15d;
        var overshoot = text.Value - inner;
        // Hinting moves a glyph by under half a device pixel, so a kept line overshoots by a fraction of a CSS pixel;
        // anything larger is not that effect and keeps the authored box (and clips, as the box always did).
        if (overshoot <= 0 || overshoot > MaxOvershootPixels) return;
        element.WidthTwips += (int)Math.Ceiling(overshoot * 15) + 15;
    }

    private const double MaxOvershootPixels = 3;

    /// <summary>One text node or one plain span of the item's HTML: its UTF-16 offset in the text, its decoded text and, for a span, its style attribute.</summary>
    private readonly record struct Piece(int Start, string Text, string? Style, bool IsSpan);

    // The paint loop measures, widens and re-emits the same HTML string in turn; one thread paints one report at a time,
    // so the last parse is kept per thread and handed to the next caller that brings the same string.
    [ThreadStatic] private static string? _lastPiecesHtml;
    [ThreadStatic] private static List<Piece>? _lastPieces;

    /// <summary>The item's HTML as text nodes and plain spans, or null when it carries other markup (the browser measures it).
    /// Throws <see cref="System.Xml.XmlException"/> for HTML the XML reader rejects, as parsing always did.</summary>
    private static List<Piece>? Pieces(string html)
    {
        if (ReferenceEquals(html, _lastPiecesHtml)) return _lastPieces;
        var pieces = ParsePieces(html);
        _lastPiecesHtml = html; _lastPieces = pieces;
        return pieces;
    }

    private static List<Piece>? ParsePieces(string html)
    {
        // Plain text (no markup, no entities, no carriage returns, no characters the XML reader would reject or
        // normalize) is one text node whose value is the HTML itself; the reader would return exactly that.
        if (IsPlainText(html)) return html.Length == 0 ? [] : [new Piece(0, html, null, false)];
        var tree = XElement.Parse("<text>" + html + "</text>", LoadOptions.PreserveWhitespace);
        var pieces = new List<Piece>();
        var at = 0;
        foreach (var node in tree.Nodes())
        {
            switch (node)
            {
                case XText text: pieces.Add(new(at, text.Value, null, false)); at += text.Value.Length; break;
                case XElement span when span.Name.LocalName == "span" && !span.HasElements:
                    pieces.Add(new(at, span.Value, (string?)span.Attribute("style"), true)); at += span.Value.Length; break;
                default: return null;
            }
        }
        return pieces;
    }

    private static bool IsPlainText(string html)
    {
        foreach (var c in html)
            if (c is '<' or '>' or '&' or '\r' || c < ' ' && c is not ('\t' or '\n') || c >= '\uD800') return false;
        return true;
    }

    [ThreadStatic] private static (string Family, bool Bold, bool Italic, CrystalDeviceFont? Font)? _lastFont;
    private static CrystalDeviceFont? ResolveFont(string family, bool bold, bool italic)
    {
        if (_lastFont is { } last && last.Bold == bold && last.Italic == italic && string.Equals(last.Family, family, StringComparison.Ordinal)) return last.Font;
        var font = CrystalDeviceFont.Resolve(family, bold, italic);
        _lastFont = (family, bold, italic, font);
        return font;
    }

    private static List<(string Text, CrystalDeviceFont Font, int Ppem)>? Runs(ReportDesignerElement element, string html)
    {
        var pieces = Pieces(html);
        if (pieces is null) return null; // decorated or unexpected markup: the browser measures it
        var runs = new List<(string, CrystalDeviceFont, int)>(pieces.Count);
        var visual = element.Visual.Runs;
        var spanIndex = 0;
        foreach (var piece in pieces)
        {
            string text = piece.Text; string family; decimal points; bool bold, italic;
            if (!piece.IsSpan) { family = element.FontFamily; points = element.FontSize; bold = element.Bold; italic = element.Italic; }
            else
            {
                var style = piece.Style ?? "";
                // Runs are emitted by ReportObjectRenderer.FontStyle; the authored point size rides along as a CSS variable.
                var run = spanIndex < visual.Count ? visual[spanIndex] : null;
                spanIndex++;
                family = StyleValue(style, "font-family") is { } f ? f.Split(',')[0].Trim(' ', '\'', '"') : run?.FontFamily ?? element.FontFamily;
                points = StyleValue(style, "--fx-report-font-points") is { } p && decimal.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : run?.FontSize ?? element.FontSize;
                bold = StyleValue(style, "font-weight") is { } w ? w is "bold" or "bolder" || double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) && weight >= 600 : run?.Bold ?? element.Bold;
                italic = StyleValue(style, "font-style") is { } s ? s is "italic" or "oblique" : run?.Italic ?? element.Italic;
            }
            if (text.Length == 0) continue;
            // A run in another size or family changes the line box; Crystal and the browser both lay such lines
            // out on the tallest font, which this breaker does not model.
            if (!family.Equals(element.FontFamily, StringComparison.OrdinalIgnoreCase) || points != element.FontSize) return null;
            var font = ResolveFont(family, bold, italic);
            if (font is null) return null;
            var ppem = (int)Math.Round((double)Math.Clamp(points, 1, 300) * DeviceDpi / 72, MidpointRounding.AwayFromZero);
            runs.Add((text, font, ppem));
        }
        return runs;
    }

    private static string? StyleValue(string style, string name)
    {
        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon > 0 && declaration.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return declaration[(colon + 1)..].Trim();
        }
        return null;
    }

    private readonly record struct Line(int Start, int End);

    /// <summary>GDI DT_WORDBREAK: lines break at spaces and line feeds only; a word wider than the box stays whole and clips.</summary>
    private static List<Line>? Break(List<(string Text, CrystalDeviceFont Font, int Ppem)> runs, long limit)
    {
        var text = string.Concat(runs.Select(r => r.Text)).Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length != runs.Sum(r => r.Text.Length)) return null; // CR handling changed offsets; let the browser measure
        var advances = new long[text.Length];
        var at = 0;
        foreach (var run in runs)
        {
            for (var index = 0; index < run.Text.Length; index++)
            {
                var c = text[at + index];
                if (c == '\n') { advances[at + index] = 0; continue; }
                // Tabs advance to CSS tab stops and a word wider than the box breaks inside the word: both stay with the browser.
                if (c == '\t' || char.IsSurrogate(c) || char.IsControl(c)) return null;
                var advance = run.Font.DeviceAdvance(c, run.Ppem);
                if (advance < 0) return null;
                advances[at + index] = advance;
            }
            at += run.Text.Length;
        }
        var lines = new List<Line>();
        var lineStart = 0;
        var used = 0L;          // extent of the words placed on the current line, including the spaces between them
        var index2 = 0;
        while (index2 < text.Length)
        {
            if (text[index2] == '\n')
            {
                lines.Add(new(lineStart, index2 + 1)); lineStart = index2 + 1; used = 0; index2++;
                continue;
            }
            // whitespace before the next word belongs to the line it follows (it hangs at a wrap)
            var spaceStart = index2;
            while (index2 < text.Length && text[index2] is ' ' or '\t') index2++;
            var spaces = 0L;
            for (var s = spaceStart; s < index2; s++) spaces += advances[s];
            if (index2 >= text.Length || text[index2] == '\n') { continue; }
            var wordStart = index2;
            while (index2 < text.Length && text[index2] is not (' ' or '\t' or '\n')) index2++;
            var word = 0L;
            for (var w = wordStart; w < index2; w++) word += advances[w];
            if (word > limit) return null;
            if (used > 0 && used + spaces + word > limit)
            {
                lines.Add(new(lineStart, wordStart)); lineStart = wordStart; used = word;
            }
            else used += spaces + word;
        }
        if (lineStart < text.Length || lines.Count == 0) lines.Add(new(lineStart, text.Length));
        return lines;
    }
}

/// <summary>
/// Integer device advances of an installed TrueType font at a given ppem: the hinted hdmx widths when the font
/// carries that size (Arial has 67, 75, 83, 92 and 100 for the common printer sizes), else the linear advance
/// rounded as GDI does without a device table. When the font is not installed, the bundled Liberation metrics
/// (Arial-compatible advances, no device table) are rounded the same way.
/// </summary>
internal sealed class CrystalDeviceFont
{
    private static readonly ConcurrentDictionary<string, Lazy<CrystalDeviceFont?>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[]? _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ushort> _glyphs = new();
    private readonly ushort[] _advances = [];
    private readonly Dictionary<int, byte[]> _device = new();
    private readonly NativePdfFont? _fallback;
    public int UnitsPerEm { get; } = 2048;
    public string Source { get; }

    public static CrystalDeviceFont? Resolve(string family, bool bold, bool italic)
    {
        var key = (family ?? "").Trim().Trim('\'', '"').ToLowerInvariant() + "|" + (bold ? "b" : "") + (italic ? "i" : "");
        return Cache.GetOrAdd(key, static k => new Lazy<CrystalDeviceFont?>(() => Load(k))).Value;
    }

    private static CrystalDeviceFont? Load(string key)
    {
        var parts = key.Split('|');
        var family = parts[0]; var bold = parts[1].Contains('b'); var italic = parts[1].Contains('i');
        foreach (var path in Candidates(family, bold, italic))
        {
            // A damaged or unexpected font file must not take the report down (or cache the failure): skip it.
            try { if (File.Exists(path)) return new CrystalDeviceFont(path); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        try { return new CrystalDeviceFont(NativePdfFont.Resolve(family, bold, italic)); }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static IEnumerable<string> Candidates(string family, bool bold, bool italic)
    {
        var (windows, mac) = family switch
        {
            "arial" or "helvetica" or "liberation sans" or "sans-serif" => (bold ? italic ? "arialbi" : "arialbd" : italic ? "ariali" : "arial", "Arial" + (bold ? " Bold" : "") + (italic ? " Italic" : "")),
            "times new roman" or "times" => (bold ? italic ? "timesbi" : "timesbd" : italic ? "timesi" : "times", "Times New Roman" + (bold ? " Bold" : "") + (italic ? " Italic" : "")),
            "courier new" or "courier" => (bold ? italic ? "courbi" : "courbd" : italic ? "couri" : "cour", "Courier New" + (bold ? " Bold" : "") + (italic ? " Italic" : "")),
            _ => (null, null)
        };
        if (windows is null) yield break;
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        yield return Path.Combine(systemRoot, "Fonts", windows + ".ttf");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts", windows + ".ttf");
        yield return "/System/Library/Fonts/Supplemental/" + mac + ".ttf";
        yield return "/Library/Fonts/" + mac + ".ttf";
        yield return "/Library/Fonts/Microsoft/" + mac + ".ttf";
        yield return "/usr/share/fonts/truetype/msttcorefonts/" + mac + ".ttf";
    }

    private CrystalDeviceFont(NativePdfFont fallback) { _fallback = fallback; Source = fallback.PostScriptName; }

    private CrystalDeviceFont(string path)
    {
        Source = path;
        var data = File.ReadAllBytes(path);
        if (data.Length is < 12 or > 64 * 1024 * 1024) throw new InvalidDataException("Unexpected font file size: " + path);
        _data = data;
        var tag = U32(0);
        var base_ = 0;
        if (tag == 0x74746366) // 'ttcf': the first face of a collection
        {
            if (data.Length < 16) throw new InvalidDataException("Truncated font collection: " + path);
            base_ = checked((int)U32(12));
            tag = U32(base_);
        }
        if (tag is not (0x00010000 or 0x74727565)) throw new InvalidDataException("Not a TrueType font: " + path);
        var tableCount = U16(base_ + 4);
        if (tableCount is 0 or > 128) throw new InvalidDataException("Invalid font table count: " + path);
        for (var index = 0; index < tableCount; index++)
        {
            var at = base_ + 12 + index * 16;
            if (at + 16 > data.Length) throw new InvalidDataException("Truncated font directory: " + path);
            var name = Encoding.ASCII.GetString(data, at, 4);
            var offset = checked((int)U32(at + 8));
            var length = checked((int)U32(at + 12));
            if (offset < 0 || length < 0 || offset + length > data.Length) throw new InvalidDataException("Invalid font table bounds: " + path);
            _tables[name] = (offset, length);
        }
        var head = Table("head", 54);
        UnitsPerEm = U16(head + 18);
        if (UnitsPerEm is < 16 or > 16384) throw new InvalidDataException("Invalid font units per em: " + path);
        var glyphCount = U16(Table("maxp", 6) + 4);
        var hhea = Table("hhea", 36);
        var metricCount = U16(hhea + 34);
        if (glyphCount == 0 || metricCount == 0 || metricCount > glyphCount) throw new InvalidDataException("Invalid font metric count: " + path);
        var hmtx = Table("hmtx", metricCount * 4);
        _advances = new ushort[glyphCount];
        for (var index = 0; index < glyphCount; index++) _advances[index] = U16(hmtx + Math.Min(index, metricCount - 1) * 4);
        LoadCmap();
        LoadDeviceMetrics(glyphCount);
    }

    /// <summary>The glyph's advance in whole device pixels at the ppem, as GDI reports it; -1 for an unsupported character.</summary>
    public long DeviceAdvance(char character, int ppem)
    {
        if (_fallback is not null)
        {
            try { return (long)Math.Round(_fallback.Width(character) / 1000d * ppem, MidpointRounding.AwayFromZero); }
            catch (NotSupportedException) { return -1; }
        }
        if (!_glyphs.TryGetValue(character, out var glyph))
            return char.IsControl(character) ? 0 : -1; // GDI font-links such a character to another face; the browser measures it
        if (_device.TryGetValue(ppem, out var widths) && glyph < widths.Length) return widths[glyph];
        return (long)Math.Round((double)_advances[glyph] * ppem / UnitsPerEm, MidpointRounding.AwayFromZero);
    }

    /// <summary>The glyph's unhinted advance as a fraction of the em (CSS measures text with these).</summary>
    public double LinearAdvance(char character)
    {
        if (_fallback is not null)
        {
            try { return _fallback.Width(character) / 1000d; }
            catch (NotSupportedException) { return 0; }
        }
        var glyph = _glyphs.GetValueOrDefault(character, (ushort)0);
        return (double)_advances[glyph] / UnitsPerEm;
    }

    private void LoadDeviceMetrics(int glyphCount)
    {
        if (!_tables.TryGetValue("hdmx", out var table) || table.Length < 8) return;
        var records = U16(table.Offset + 2);
        var recordSize = checked((int)U32(table.Offset + 4));
        if (recordSize < glyphCount + 2) return;
        for (var index = 0; index < records; index++)
        {
            var at = table.Offset + 8 + index * recordSize;
            if (at + 2 + glyphCount > table.Offset + table.Length) return;
            var ppem = _data![at];
            var widths = new byte[glyphCount];
            Array.Copy(_data, at + 2, widths, 0, glyphCount);
            _device[ppem] = widths;
        }
    }

    private void LoadCmap()
    {
        var cmap = Table("cmap", 4);
        var count = U16(cmap + 2);
        var best = -1; var bestScore = -1;
        for (var index = 0; index < count; index++)
        {
            var at = cmap + 4 + index * 8;
            if (at + 8 > _data!.Length) break;
            var platform = U16(at); var encoding = U16(at + 2); var offset = checked((int)U32(at + 4));
            var score = (platform, encoding) switch { (3, 10) => 4, (0, 4) => 3, (3, 1) => 2, (0, _) => 1, _ => 0 };
            if (score > bestScore && cmap + offset < _data.Length) { best = cmap + offset; bestScore = score; }
        }
        if (best < 0) throw new InvalidDataException("The font has no Unicode character map.");
        var format = U16(best);
        if (format == 4)
        {
            var segments = U16(best + 6) / 2;
            var ends = best + 14; var starts = ends + segments * 2 + 2; var deltas = starts + segments * 2; var ranges = deltas + segments * 2;
            for (var segment = 0; segment < segments; segment++)
            {
                int end = U16(ends + segment * 2), start = U16(starts + segment * 2), delta = U16(deltas + segment * 2), range = U16(ranges + segment * 2);
                if (start > end) continue;
                for (var code = start; code <= end && code != 0xFFFF; code++)
                {
                    int glyph;
                    if (range == 0) glyph = (code + delta) & 0xFFFF;
                    else
                    {
                        var address = ranges + segment * 2 + range + (code - start) * 2;
                        if (address + 2 > _data!.Length) continue;
                        glyph = U16(address);
                        if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                    }
                    if (glyph != 0 && glyph < _advances.Length) _glyphs[code] = (ushort)glyph;
                }
            }
        }
        else if (format == 12)
        {
            var groups = checked((int)U32(best + 12));
            for (var group = 0; group < groups; group++)
            {
                var at = best + 16 + group * 12;
                if (at + 12 > _data!.Length) break;
                var start = U32(at); var end = U32(at + 4); var glyph = U32(at + 8);
                for (var code = start; code <= end && code < 0x10000; code++)
                {
                    var id = glyph + (code - start);
                    if (id != 0 && id < (uint)_advances.Length) _glyphs[(int)code] = (ushort)id;
                }
            }
        }
        else throw new InvalidDataException("Unsupported character map format " + format + ".");
    }

    private int Table(string name, int minimum)
    {
        if (!_tables.TryGetValue(name, out var table) || table.Length < minimum) throw new InvalidDataException("Missing font table " + name + ".");
        return table.Offset;
    }

    private ushort U16(int at) => at < 0 || at + 2 > _data!.Length ? throw new InvalidDataException("Truncated font table.") : BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at, 2));
    private uint U32(int at) => at < 0 || at + 4 > _data!.Length ? throw new InvalidDataException("Truncated font table.") : BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at, 4));
}
