using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

/// <summary>Writes the completed report snapshot to selectable, vector PDF pages. It never
/// inspects the viewer DOM, executes SQL, opens a browser, or changes report pagination.</summary>
internal static class ReportPdfExport
{
    private const double Px = .75;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private sealed record Ink(string Family = "Arial", double Size = 10, bool Bold = false,
        bool Italic = false, bool Underline = false, string Color = "#000000", bool Native = false)
    {
        // Older Crystal PDF exports realize Arial smaller than the authored em.
        // Reference regular 8/10pt -> 7.15/8.95pt; bold 10/14pt -> 8.75/12.25pt.
        // This PDF-only compatibility policy never changes screen or page geometry.
        private bool LegacyArial => Native && Family.Equals("Arial", StringComparison.OrdinalIgnoreCase);
        public double Em => LegacyArial ? Math.Round(Size * (Bold ? .875 : .895) * 20, MidpointRounding.AwayFromZero) / 20 : Size;
        public double Leading(double lineHeight) => LegacyArial ? Size * 1.14 : Size * lineHeight;
    }
    private sealed record Run(string Text, Ink Ink);
    private sealed record TextLine(IReadOnlyList<Run> Runs, double Width, double Height, bool ParagraphEnd);

    internal static byte[] Export(ReportTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.PageSnapshots.Count == 0 && tab.Pages.Count == 0)
            throw new InvalidOperationException("There are no completed report pages to export.");
        var document = new NativePdfDocument();
        if (tab.PageSnapshots.Count > 0)
        {
            foreach (var snapshot in tab.PageSnapshots) Positioned(document, snapshot);
        }
        else
        {
            foreach (var html in tab.Pages) Tabular(document, tab, html);
        }
        return document.ToArray();
    }

    private static void Positioned(NativePdfDocument document, ReportPageSnapshot snapshot)
    {
        var size = snapshot.Size;
        var width = (size.MarginLeftTwips + size.ContentWidthTwips + size.MarginRightTwips) / 20d;
        var height = (size.MarginTopTwips + size.ContentHeightTwips + size.MarginBottomTwips) / 20d;
        var page = document.AddPage(width, height);
        var left = size.MarginLeftTwips / 20d;
        var top = size.MarginTopTwips / 20d;
        page.Save();
        page.Clip(left, top, size.ContentWidthTwips / 20d, size.ContentHeightTwips / 20d);
        foreach (var band in snapshot.Bands)
        {
            var x = left + band.LeftTwips / 20d;
            var y = top + band.TopTwips / 20d;
            var w = (band.WidthTwips > 0 ? band.WidthTwips : size.ContentWidthTwips) / 20d;
            var h = band.HeightTwips / 20d;
            if (h <= 0 || w <= 0) continue;
            page.Save();
            page.Clip(x, y, w, h);
            Fill(page, x, y, w, h, band.Background);
            foreach (var item in band.Objects)
            {
                try { Object(page, item, x, y); }
                catch (Exception error) when (error is NotSupportedException or InvalidDataException)
                {
                    throw new NotSupportedException($"PDF page {snapshot.Number}, object '{item.Element.Name}': {error.Message}", error);
                }
            }
            page.Restore();
        }
        page.Restore();
    }

    private static void Object(NativePdfPage page, ReportPageObject item, double bandX, double bandY)
    {
        var element = item.Element;
        var x = bandX + element.LeftTwips / 20d;
        var y = bandY + element.TopTwips / 20d;
        var width = Math.Max(0, element.WidthTwips) / 20d;
        var height = Math.Max(0, element.HeightTwips) / 20d;
        if (element.Kind == "Line")
        {
            var style = element.Visual.TopLine is "NoLine" or "" ? "Single" : element.Visual.TopLine;
            Stroke(page, x, y, x + element.WidthTwips / 20d, y + element.HeightTwips / 20d,
                style, element.Visual.BorderColor, element.Visual.LineWidthTwips);
            return;
        }
        Fill(page, x, y, width, height, element.BackgroundColor);
        var visual = element.Visual;
        var outside = element.Kind == "Field" && !element.CanGrow &&
            (element.NativeNumberFormat is not null || element.NativeNumericFormat is not null);
        Borders(page, x, y, width, height, visual, outside);
        if (element.Kind == "Box" || width <= 0 || height <= 0) return;
        page.Save();
        page.Clip(x, y, width, height);
        if (item.Table is { } table)
            Fragment(page, table, x, y, new(element.FontFamily, (double)element.FontSize, element.Bold, element.Italic, element.Underline, Color(element.TextColor)));
        else if (item.Analysis is not null)
            throw new NotSupportedException("This analytical object has no printable table fragments. Export HTML for this chart until native chart PDF support is available.");
        else if (element.Kind == "Picture")
        {
            // The viewer sizes the image inside the authored border-box, so opaque
            // artwork must not cover its border or consume its indentation.
            var left = BorderWidth(visual.LeftLine, visual.LineWidthTwips) + element.IndentTwips / 20d;
            var top = BorderWidth(visual.TopLine, visual.LineWidthTwips);
            x += left; y += top;
            width = Math.Max(0, width - left - BorderWidth(visual.RightLine, visual.LineWidthTwips));
            height = Math.Max(0, height - top - BorderWidth(visual.BottomLine, visual.LineWidthTwips));
            page.Clip(x, y, width, height);
            if (width == 0 || height == 0) { page.Restore(); return; }
            if (ReportObjectVisual.IsEmbeddedImage(visual.ImageDataUrl))
                Picture(page, visual.ImageDataUrl, x, y, width, height, visual.ImageFit);
            else if (visual.VectorImage is { } vector) Vector(page, vector, x, y, width, height, visual.ImageFit);
            else Text(page, Runs(item.FormattedContent, InkFor(element)), x, y, width, height, Align(element), false);
        }
        else
        {
            var left = outside ? 0 : BorderWidth(visual.LeftLine, visual.LineWidthTwips);
            var right = outside ? 0 : BorderWidth(visual.RightLine, visual.LineWidthTwips);
            var top = outside ? 0 : BorderWidth(visual.TopLine, visual.LineWidthTwips);
            var bottom = outside ? 0 : BorderWidth(visual.BottomLine, visual.LineWidthTwips);
            var indent = element.IndentTwips / 20d;
            var wrap = ReportObjectRenderer.WrapText(element);
            Text(page, Runs(item.FormattedContent, InkFor(element)), x + left + indent, y + top,
                Math.Max(0, width - left - right - indent), Math.Max(0, height - top - bottom), Align(element), wrap,
                element.CanGrow && element.Kind == "Field" ? element.MaxNumberOfLines : 0,
                measuredLines: item.TextLines);
        }
        page.Restore();
    }

    private static Ink InkFor(ReportDesignerElement element) => new(element.FontFamily, (double)element.FontSize,
        element.Bold, element.Italic, element.Underline, Color(element.TextColor), element.UseCrystalScreenMetrics);

    private static string Align(ReportDesignerElement element) => element.HorizontalAlignment.ToLowerInvariant() switch
    {
        "right" => "right", "center" or "horizontalcenter" => "center", "justified" => "justify",
        "default" when element.NativeNumericFormat is not null || element.NativeNumberFormat is not null => "right", _ => "left"
    };

    private static void Borders(NativePdfPage page, double x, double y, double w, double h, ReportObjectVisual visual, bool outside)
    {
        var l = BorderWidth(visual.LeftLine, visual.LineWidthTwips);
        var t = BorderWidth(visual.TopLine, visual.LineWidthTwips);
        // Native non-growing numeric borders are outside their text box in the viewer.
        var ox = outside ? x - l : x;
        var oy = outside ? y - t : y;
        var ow = outside ? w + l + BorderWidth(visual.RightLine, visual.LineWidthTwips) : w;
        var oh = outside ? h + t + BorderWidth(visual.BottomLine, visual.LineWidthTwips) : h;
        Stroke(page, ox, oy + t / 2, ox + ow, oy + t / 2, visual.TopLine, visual.BorderColor, visual.LineWidthTwips);
        Stroke(page, ox, oy + oh - BorderWidth(visual.BottomLine, visual.LineWidthTwips) / 2, ox + ow, oy + oh - BorderWidth(visual.BottomLine, visual.LineWidthTwips) / 2, visual.BottomLine, visual.BorderColor, visual.LineWidthTwips);
        Stroke(page, ox + l / 2, oy, ox + l / 2, oy + oh, visual.LeftLine, visual.BorderColor, visual.LineWidthTwips);
        Stroke(page, ox + ow - BorderWidth(visual.RightLine, visual.LineWidthTwips) / 2, oy, ox + ow - BorderWidth(visual.RightLine, visual.LineWidthTwips) / 2, oy + oh, visual.RightLine, visual.BorderColor, visual.LineWidthTwips);
    }

    private static double BorderWidth(string style, int? width) => style switch
    {
        "Double" or "DoubleLine" => Math.Max(2.25, (width ?? 15) * .15),
        "Single" or "SingleLine" or "Dash" or "DashLine" or "Dot" or "DotLine" => Math.Max(.75, (width ?? 15) / 20d), _ => 0
    };

    private static void Stroke(NativePdfPage page, double x1, double y1, double x2, double y2, string style, string color, int? width)
    {
        if (style is "NoLine" or "" or "None") return;
        var points = width is null ? .75 : Math.Max(.05, width.Value / 20d);
        if (style is "Double" or "DoubleLine")
        {
            var total = Math.Max(2.25, points * 3);
            var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            var dx = length == 0 ? 0 : -(y2 - y1) / length * total / 3;
            var dy = length == 0 ? 0 : (x2 - x1) / length * total / 3;
            page.Line(x1 - dx, y1 - dy, x2 - dx, y2 - dy, Color(color), total / 3);
            page.Line(x1 + dx, y1 + dy, x2 + dx, y2 + dy, Color(color), total / 3);
        }
        else if (style is "Single" or "SingleLine" or "Dash" or "DashLine" or "Dot" or "DotLine")
            page.Line(x1, y1, x2, y2, Color(color), points,
                style.StartsWith("Dash", StringComparison.Ordinal) ? [3 * points, 2 * points] : style.StartsWith("Dot", StringComparison.Ordinal) ? [points, points] : null);
        else throw new NotSupportedException($"Unsupported line style '{style}'.");
    }

    private static void Fill(NativePdfPage page, double x, double y, double w, double h, string value)
    {
        if (w > 0 && h > 0 && value is not ("" or "transparent" or "none")) page.Rectangle(x, y, w, h, Color(value));
    }

    private static string Color(string? value)
    {
        value = value?.Trim().ToLowerInvariant();
        return value switch
        {
            "black" => "#000000", "white" => "#ffffff", "red" => "#ff0000", "blue" => "#0000ff",
            "gray" or "grey" => "#808080", "green" => "#008000", "transparent" or "none" => "#00000000", null or "" => "#000000",
            _ when Regex.IsMatch(value, "^#(?:[0-9a-f]{6}|[0-9a-f]{8})$") => value,
            _ when Regex.IsMatch(value, "^#[0-9a-f]{3,4}$") => "#" + string.Concat(value.Skip(1).Select(c => new string(c, 2))),
            _ when Enum.TryParse<System.Drawing.KnownColor>(value, true, out var named) && named is >= System.Drawing.KnownColor.AliceBlue and <= System.Drawing.KnownColor.YellowGreen => "#" + (System.Drawing.Color.FromKnownColor(named).ToArgb() & 0xffffff).ToString("X6", Invariant),
            _ => throw new NotSupportedException($"Unsupported PDF color '{value}'.")
        };
    }

    private static IReadOnlyList<Run> Runs(string html, Ink ink, bool collapse = false)
    {
        var parser = new HtmlParser();
        var dom = parser.ParseDocument("<html><body>" + html + "</body></html>");
        var result = new List<Run>();
        Walk(dom.Body!, ink);
        if (result.Count == 0) result.Add(new("", ink));
        return result;
        void Walk(INode node, Ink current)
        {
            if (node is IText text)
            {
                var value = text.Data.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                if (collapse) value = Regex.Replace(value, @"\s+", " ");
                if (value.Length > 0) result.Add(new(value, current));
                return;
            }
            if (node is not IElement e) return;
            if (e.HasAttribute("data-fx-native-field-border") || e.GetAttribute("aria-hidden") == "true") return;
            if (e.LocalName == "br") { result.Add(new("\n", current)); return; }
            if (e.LocalName is not ("body" or "span" or "div" or "p" or "b" or "strong" or "i" or "em" or "u" or "a"))
                throw new NotSupportedException($"Unsupported report PDF text element '{e.LocalName}'.");
            current = Apply(current, Styles(e));
            if (e.LocalName is "b" or "strong") current = current with { Bold = true };
            if (e.LocalName is "i" or "em") current = current with { Italic = true };
            if (e.LocalName == "u") current = current with { Underline = true };
            var block = e.LocalName is "div" or "p";
            if (block && result.Count > 0 && !result[^1].Text.EndsWith('\n')) result.Add(new("\n", current));
            foreach (var child in e.ChildNodes) Walk(child, current);
            if (block && result.Count > 0 && !result[^1].Text.EndsWith('\n')) result.Add(new("\n", current));
        }
    }

    private static Ink Apply(Ink ink, IReadOnlyDictionary<string, string> css)
    {
        if (css.TryGetValue("font-family", out var family)) ink = ink with { Family = family.Split(',')[0].Trim(' ', '\'', '"') };
        if (css.TryGetValue("font-size", out var size)) ink = ink with { Size = Length(size, 0, ink.Size) };
        // Native rich runs carry their authored size separately from the rounded
        // browser px size; use it for PDF realization and paragraph leading.
        if (ink.Native && css.TryGetValue("--fx-report-font-points", out var authored)) ink = ink with { Size = Length(authored) };
        if (css.TryGetValue("font-weight", out var weight)) ink = ink with { Bold = weight is "bold" or "bolder" || double.TryParse(weight, out var number) && number >= 600 };
        if (css.TryGetValue("font-style", out var italic)) ink = ink with { Italic = italic == "italic" || italic == "oblique" };
        if (css.TryGetValue("text-decoration", out var decoration)) ink = ink with { Underline = decoration.Contains("underline", StringComparison.Ordinal) };
        if (css.TryGetValue("color", out var color) && !color.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) ink = ink with { Color = Color(color) };
        return ink;
    }

    private static IReadOnlyList<TextLine> Lines(IReadOnlyList<Run> runs, double width, bool wrap, double lineHeight = 1.15)
    {
        var lines = new List<TextLine>();
        var current = new List<Run>();
        var used = 0d;
        var tallest = runs.Count == 0 ? 10 * lineHeight : runs[0].Ink.Leading(lineHeight);
        void Finish(bool paragraphEnd = false)
        {
            lines.Add(new(current.ToArray(), used, tallest, paragraphEnd));
            current.Clear(); used = 0; tallest = runs.Count == 0 ? 10 * lineHeight : runs[0].Ink.Leading(lineHeight);
        }
        void Add(string value, Ink ink)
        {
            if (value.Length == 0) return;
            var adjust = 0d;
            if (current.Count > 0 && current[^1].Ink == ink)
            {
                if (ink.Native) adjust = NativePdfFont.Resolve(ink.Family, ink.Bold, ink.Italic).Kerning(current[^1].Text[^1], value[0]) * ink.Em / 1000;
                current[^1] = current[^1] with { Text = current[^1].Text + value };
            }
            else current.Add(new(value, ink));
            used += Measure(value, ink) + adjust; tallest = Math.Max(tallest, ink.Leading(lineHeight));
        }
        foreach (var run in runs)
        {
            foreach (Match token in Regex.Matches(run.Text, @"\n|[^\S\n]+|[^\s]+"))
            {
                var value = token.Value;
                if (value == "\n") { Finish(true); continue; }
                // CSS pre-wrap uses eight-space tab stops. Keep tabs selectable as spaces.
                if (value.Contains('\t'))
                {
                    var expanded = new StringBuilder();
                    var cursor = used;
                    var space = Measure(" ", run.Ink);
                    foreach (var character in value)
                    {
                        var count = character == '\t' ? Math.Max(1, 8 - (int)Math.Floor(cursor / space) % 8) : 1;
                        expanded.Append(character == '\t' ? ' ' : character, count);
                        cursor += character == '\t' ? count * space : Measure(character.ToString(), run.Ink);
                    }
                    value = expanded.ToString();
                }
                var measured = Measure(value, run.Ink);
                if (wrap && width > 0 && used > 0 && used + measured > width + .01) Finish();
                if (wrap && width > 0 && measured > width + .01)
                {
                    foreach (var rune in value.EnumerateRunes())
                    {
                        var part = rune.ToString();
                        if (used > 0 && used + Measure(part, run.Ink) > width + .01) Finish();
                        Add(part, run.Ink);
                    }
                }
                else Add(value, run.Ink);
            }
        }
        if (current.Count > 0 || lines.Count == 0) Finish(true);
        return lines;
    }

    // Reuse completed line breaks, not a second wrap at the smaller PDF em.
    // Slice the original runs so rich text keeps its fonts and all characters.
    private static IReadOnlyList<TextLine>? MeasuredLines(IReadOnlyList<Run> runs, IReadOnlyList<ReportTextLine>? measured, double leading, int maxLines)
    {
        if (measured is null || measured.Count == 0) return null;
        var text = string.Concat(runs.Select(r => r.Text));
        if (text.Contains('\t') || measured[0].Start != 0
            || measured[^1].End != text.Length && (maxLines <= 0 || measured.Count != maxLines)) return null;
        var lines = new List<TextLine>();
        var previous = 0;
        foreach (var line in measured)
        {
            if (line.Start != previous || line.End < line.Start || line.End > text.Length) return null;
            var slice = text[line.Start..line.End].TrimEnd('\n');
            // Explicit blank lines / HTML blocks need the paragraph-aware path.
            if (slice.Contains('\n') || line.End - line.Start - slice.Length > 1) return null;
            var parts = new List<Run>();
            var at = 0;
            foreach (var run in runs)
            {
                var start = Math.Max(line.Start, at);
                var end = Math.Min(line.Start + slice.Length, at + run.Text.Length);
                if (end > start) parts.Add(new(run.Text[(start - at)..(end - at)], run.Ink));
                at += run.Text.Length;
            }
            lines.Add(new(parts, parts.Sum(r => Measure(r.Text, r.Ink)),
                parts.Select(r => r.Ink.Leading(leading)).DefaultIfEmpty(runs[0].Ink.Leading(leading)).Max(),
                line.End == text.Length || line.End > 0 && text[line.End - 1] == '\n'));
            previous = line.End;
        }
        return lines;
    }

    private static double Measure(string text, Ink ink) => NativePdfPage.MeasureText(text, ink.Em, ink.Family, ink.Bold, ink.Italic, ink.Native);

    private static double Text(NativePdfPage page, IReadOnlyList<Run> runs, double x, double y, double width, double height,
        string alignment, bool wrap, int maxLines = 0, double lineHeight = 1.15, IReadOnlyList<ReportTextLine>? measuredLines = null)
    {
        if (width <= 0 || height <= 0) return 0;
        var lines = wrap ? MeasuredLines(runs, measuredLines, lineHeight, maxLines) ?? Lines(runs, width, true, lineHeight)
            : Lines(runs, width, false, lineHeight);
        page.Save(); page.Clip(x, y, width, height);
        var at = y;
        foreach (var line in lines.Take(maxLines > 0 ? maxLines : int.MaxValue))
        {
            if (at >= y + height) break;
            var offset = alignment == "right" ? width - line.Width : alignment == "center" ? (width - line.Width) / 2 : 0;
            var cursor = x + offset;
            var ascent = line.Runs.Count == 0 ? line.Height / lineHeight * .9 : line.Runs.Max(r => NativePdfFont.Resolve(r.Ink.Family, r.Ink.Bold, r.Ink.Italic).Ascent * r.Ink.Em / 1000);
            var descent = line.Runs.Count == 0 ? line.Height / lineHeight * .2 : line.Runs.Max(r => -NativePdfFont.Resolve(r.Ink.Family, r.Ink.Bold, r.Ink.Italic).Descent * r.Ink.Em / 1000);
            var baseline = at + (line.Height - ascent - descent) / 2 + ascent;
            var spaces = line.Runs.Sum(run => run.Text.Count(c => c == ' '));
            var extra = alignment == "justify" && !line.ParagraphEnd && spaces > 0 ? Math.Max(0, width - line.Width) / spaces : 0;
            foreach (var run in line.Runs)
            {
                if (extra == 0)
                {
                    page.Text(run.Text, cursor, baseline, run.Ink.Em, run.Ink.Family, run.Ink.Bold, run.Ink.Italic, run.Ink.Color, run.Ink.Underline, run.Ink.Native);
                    cursor += Measure(run.Text, run.Ink);
                }
                else foreach (Match token in Regex.Matches(run.Text, @"[^ ]+| +"))
                {
                    page.Text(token.Value, cursor, baseline, run.Ink.Em, run.Ink.Family, run.Ink.Bold, run.Ink.Italic, run.Ink.Color, run.Ink.Underline, run.Ink.Native);
                    cursor += Measure(token.Value, run.Ink) + extra * token.Value.Count(c => c == ' ');
                }
            }
            at += line.Height;
        }
        page.Restore(); return at - y;
    }

    private static Dictionary<string, string> Styles(IElement element) =>
        (element.GetAttribute("style") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Split(':', 2)).Where(s => s.Length == 2)
        .GroupBy(s => s[0].Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);

    private static double Length(string value, double reference = 0, double font = 10)
    {
        value = value.Trim();
        if (value.StartsWith("calc(", StringComparison.Ordinal))
        {
            // Generated tabular CSS only multiplies its authored size by viewer zoom.
            // Export is always at 100%, so no screen zoom is inherited.
            var number = Regex.Match(value, @"[-+]?[0-9]*\.?[0-9]+(?:px|pt|em|in|cm|mm)");
            if (!number.Success) throw new NotSupportedException("Unsupported calculated report length.");
            value = number.Value;
        }
        var match = Regex.Match(value, @"^([-+]?[0-9]*\.?[0-9]+)(px|pt|em|in|cm|mm|%)?$");
        if (!match.Success) throw new NotSupportedException($"Unsupported report length '{value}'.");
        var n = double.Parse(match.Groups[1].Value, Invariant);
        return match.Groups[2].Value switch { "px" => n * Px, "em" => n * font, "in" => n * 72, "cm" => n * 72 / 2.54, "mm" => n * 72 / 25.4, "%" => n * reference / 100, _ => n };
    }

    private static void Fragment(NativePdfPage page, ReportTableFragment table, double x, double y, Ink ink)
    {
        var widths = table.Columns.Select(c => c.WidthTwips / 20d).ToArray();
        var h = table.HeightTwips / 20d;
        var cells = table.LayoutCells ?? table.Cells.Select((value, index) => new ReportTableCellFragment(index, 1, value, null, true, true, table.Row, index)).ToArray();
        foreach (var cell in cells)
        {
            var cx = x + widths.Take(cell.Column).Sum();
            var cw = widths.Skip(cell.Column).Take(cell.ColumnSpan).Sum();
            var bold = table.IsHeader || table.IsTotal || table.Columns.Skip(cell.Column).Take(cell.ColumnSpan).Any(c => c.IsRowHeader);
            Fill(page, cx, y, cw, h, table.IsHeader ? "#e8efed" : table.IsTotal ? "#f1f4f3" : "#ffffff");
            page.Line(cx, y, cx, y + h, "#cbd5cb", .75); page.Line(cx + cw, y, cx + cw, y + h, "#cbd5cb", .75);
            if (cell.First) page.Line(cx, y, cx + cw, y, "#cbd5cb", .75);
            if (cell.Last) page.Line(cx, y + h, cx + cw, y + h, "#cbd5cb", .75);
            if (cell.Nested is { } nested) Fragment(page, nested, cx, y, ink);
            else Text(page, [new(cell.Text, ink with { Bold = bold })], cx + 2.25, y + Math.Max(1.5, (h - ink.Size * 1.4) / 2), Math.Max(0, cw - 4.5), Math.Max(0, h - 3), "left", false, lineHeight: 1.4);
        }
    }

    private static void Picture(NativePdfPage page, string url, double x, double y, double w, double h, string fit)
    {
        if (!ReportObjectVisual.IsEmbeddedImage(url)) throw new NotSupportedException("Only embedded report images can be exported.");
        var bytes = Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]);
        var (iw, ih) = NativePdfPage.ImageSize(bytes);
        if (fit != "fill")
        {
            var scale = fit == "cover" ? Math.Max(w / iw, h / ih) : Math.Min(w / iw, h / ih);
            x += (w - iw * scale) / 2; y += (h - ih * scale) / 2; w = iw * scale; h = ih * scale;
        }
        page.Image(bytes, x, y, w, h);
    }

    private static void Vector(NativePdfPage page, ReportVectorImage vector, double x, double y, double w, double h, string fit)
    {
        vector.Validate();
        var sx = w / vector.Width; var sy = h / vector.Height;
        if (fit != "fill") { var scale = fit == "cover" ? Math.Max(sx, sy) : Math.Min(sx, sy); x += (w - vector.Width * scale) / 2; y += (h - vector.Height * scale) / 2; sx = sy = scale; }
        page.Save(); page.Transform(sx, 0, 0, sy, x - vector.Left * sx, y - vector.Top * sy);
        page.SetLineStyle(1, 1); // The report's generated SVG explicitly uses round caps and joins.
        foreach (var shape in vector.Shapes)
        {
            page.Save();
            if (shape.Clip is { } clip) page.Clip(clip[0], clip[1], clip[2], clip[3]);
            foreach (var polygon in shape.ClipPolygons ?? [])
            {
                var clipping = new StringBuilder().Append(N(polygon[0])).Append(' ').Append(N(polygon[1])).Append(" m\n");
                for (var index = 2; index < polygon.Length; index += 2)
                    clipping.Append(N(polygon[index])).Append(' ').Append(N(polygon[index + 1])).Append(" l\n");
                page.ClipPath(clipping.Append("h\n").ToString());
            }
            if (shape.Transform is { } matrix) page.Transform(matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]);
            var p = shape.Points; var fill = shape.Fill == "none" ? null : Color(shape.Fill); var stroke = shape.Stroke == "none" || shape.StrokeWidth == 0 ? null : Color(shape.Stroke);
            if (shape.Kind == "Rectangle") page.Rectangle(p[0], p[1], p[2], p[3], fill, stroke, shape.StrokeWidth);
            else if (shape.Kind == "Ellipse") page.Ellipse(p[0], p[1], p[2], p[3], fill, stroke, shape.StrokeWidth);
            else if (shape.Text is { } text)
            {
                if (stroke is not null) throw new NotSupportedException("Outlined vector text cannot yet be exported faithfully.");
                if (fill is null) { page.Restore(); continue; }
                var tx = p[0]; var ty = p[1];
                var tw = text.Advances?.Sum() ?? NativePdfPage.MeasureText(text.Value, text.FontSize, text.FontFamily, text.Weight >= 600, text.Italic);
                var anchor = text.Anchor == "middle" ? .5 : text.Anchor == "end" ? 1 : 0;
                tx -= tw * anchor;
                ty -= (text.VerticalAdvances?.Sum() ?? 0) * anchor;
                var metrics = NativePdfFont.Resolve(text.FontFamily, text.Weight >= 600, text.Italic);
                if (text.Baseline == "text-before-edge") ty += text.FontSize * metrics.Ascent / 1000;
                if (text.Baseline == "text-after-edge") ty += text.FontSize * metrics.Descent / 1000;
                if (text.Advances is { } advances)
                {
                    for (var index = 0; index < text.Value.Length; index++)
                    {
                        page.Text(text.Value[index].ToString(), tx, ty, text.FontSize, text.FontFamily, text.Weight >= 600, text.Italic, fill ?? "#000000", text.Underline);
                        if (text.StrikeOut) page.Line(tx, ty - text.FontSize * .3, tx + advances[index], ty - text.FontSize * .3, fill ?? "#000000", Math.Max(.3, text.FontSize / 20));
                        tx += advances[index]; ty += text.VerticalAdvances?[index] ?? 0;
                    }
                }
                else
                {
                    page.Text(text.Value, tx, ty, text.FontSize, text.FontFamily, text.Weight >= 600, text.Italic, fill ?? "#000000", text.Underline);
                    if (text.StrikeOut) page.Line(tx, ty - text.FontSize * .3, tx + tw, ty - text.FontSize * .3, fill ?? "#000000", Math.Max(.3, text.FontSize / 20));
                }
            }
            else
            {
                var path = new StringBuilder();
                if (shape.Commands is { } commands)
                    foreach (var command in commands) path.Append(string.Join(' ', command.Points.Select(N))).Append(' ').Append(command.Kind switch { "Move" => "m", "Line" => "l", "Cubic" => "c", "Close" => "h", _ => throw new NotSupportedException("Unsupported vector command.") }).Append('\n');
                else
                {
                    path.Append(N(p[0])).Append(' ').Append(N(p[1])).Append(" m\n");
                    var step = shape.Kind == "Bezier" ? 6 : 2;
                    for (var index = 2; index < p.Length; index += step) path.Append(string.Join(' ', p.Skip(index).Take(step).Select(N))).Append(shape.Kind == "Bezier" ? " c\n" : " l\n");
                    if (shape.Kind == "Polygon") path.Append("h\n");
                }
                page.Path(path.ToString(), fill, stroke, shape.StrokeWidth, !shape.Winding);
            }
            page.Restore();
        }
        page.Restore();
    }

    private static string N(double value) => value.ToString("0.#####", Invariant);

    private static void Tabular(NativePdfDocument document, ReportTab tab, string html)
    {
        var parsed = new HtmlParser().ParseDocument("<html><body>" + html + "</body></html>");
        var body = parsed.Body!;
        if (body.QuerySelector("article.fx-report-positioned-page") is not null)
            throw new NotSupportedException("Positioned report PDF requires its completed PageSnapshots. Reload this report before exporting.");
        var size = tab.PositionedPage;
        var width = size is null ? tab.Orientation == ReportOrientation.Landscape ? 792 : 612 : (size.ContentWidthTwips + size.MarginLeftTwips + size.MarginRightTwips) / 20d;
        var height = size is null ? tab.Orientation == ReportOrientation.Landscape ? 612 : 792 : (size.ContentHeightTwips + size.MarginTopTwips + size.MarginBottomTwips) / 20d;
        var page = document.AddPage(width, height);
        var marginX = 50 * Px; var y = 28 * Px; var contentWidth = width - marginX * 2;
        var header = body.QuerySelector(".fx-rpt-header");
        if (header is not null)
        {
            var title = header.QuerySelector(".fx-rpt-title"); var subtitle = header.QuerySelector(".fx-rpt-subtitle");
            if (title is not null) Text(page, Runs(title.InnerHtml, new(Size: 14 * Px, Bold: true, Color: "#111111"), true), marginX, y, contentWidth, 17 * Px, Styles(title).GetValueOrDefault("text-align", "center"), false);
            if (header.QuerySelector(".fx-rpt-date") is { } date) Text(page, Runs(date.InnerHtml, new(Size: 10 * Px, Color: "#888888"), true), marginX, y, contentWidth, 14 * Px, "right", false);
            y += 17 * Px;
            if (subtitle is not null) { y += Text(page, Runs(subtitle.InnerHtml, new(Size: 11 * Px, Color: "#555555"), true), marginX, y, contentWidth, 14 * Px, Styles(subtitle).GetValueOrDefault("text-align", "center"), false); }
            y += 5 * Px; page.Line(marginX, y, width - marginX, y, "#333333", 1.5); y += 11 * Px;
        }
        var table = body.QuerySelector("table.fx-rpt-table");
        if (table is null) throw new NotSupportedException("This report tab has no recognized printable report table.");
        var headers = table.QuerySelectorAll("thead th").ToArray();
        if (headers.Length == 0) throw new NotSupportedException("The tabular report has no printable column geometry.");
        var widths = headers.Select(h => Styles(h).TryGetValue("width", out var v) ? Length(v, contentWidth) : 0).ToArray();
        var total = widths.Sum();
        if (total <= 0) Array.Fill(widths, contentWidth / widths.Length);
        else if (widths.Any(w => w <= 0))
        {
            var remainder = Math.Max(contentWidth - total, contentWidth / widths.Length);
            var empty = widths.Count(w => w <= 0);
            for (var index = 0; index < widths.Length; index++) if (widths[index] <= 0) widths[index] = remainder / empty;
        }
        if (Math.Abs(widths.Sum() - contentWidth) > .1) { var scale = contentWidth / widths.Sum(); for (var index = 0; index < widths.Length; index++) widths[index] *= scale; }
        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var cells = row.Children.Where(e => e.LocalName is "td" or "th").ToArray();
            if (cells.Length == 0) continue;
            var isHeader = row.ParentElement?.LocalName == "thead";
            var groupHeader = row.ClassList.Contains("fx-rpt-group-header");
            var groupFooter = row.ClassList.Contains("fx-rpt-group-footer");
            var grandTotal = row.ClassList.Contains("fx-rpt-grand-total");
            var level = Enumerable.Range(0, 6).FirstOrDefault(i => row.ClassList.Contains("fx-rpt-group-l" + i));
            var font = (groupHeader ? level == 0 ? 12 : 11 : grandTotal ? 11 : 10) * Px;
            var padY = (isHeader ? 4 : grandTotal ? 5 : groupHeader || groupFooter ? 2 : 1) * Px;
            var extraTop = groupHeader && level == 0 ? 4 * Px : groupFooter && level == 0 ? 2 * Px : 0;
            var entries = new List<(IElement Cell, IReadOnlyList<Run> Runs, double X, double Width, double Pad, string Align, bool Wrap, double TextHeight)>();
            var column = 0;
            foreach (var cell in cells)
            {
                var span = int.TryParse(cell.GetAttribute("colspan"), out var number) ? Math.Clamp(number, 1, widths.Length) : 1;
                if (column + span > widths.Length) throw new NotSupportedException("Tabular PDF column spans exceed the completed report columns.");
                var cw = widths.Skip(column).Take(span).Sum(); var cx = marginX + widths.Take(column).Sum();
                var css = Styles(cell); var ink = Apply(new(Size: font, Bold: isHeader || groupHeader && level < 4 || groupFooter && level == 0 || grandTotal, Color: "#333333"), css);
                var pad = groupHeader || groupFooter && column == 0 ? (level == 0 ? groupFooter ? 0 : 6 : Math.Min(48, level * 12)) * Px : 6 * Px;
                if (css.TryGetValue("padding-left", out var padding)) pad = Length(padding);
                var runs = Runs(cell.InnerHtml, ink, true);
                var wrap = !isHeader && !cell.ClassList.Contains("fx-rpt-col-nowrap");
                var lines = Lines(runs, Math.Max(1, cw - pad - 6 * Px), wrap);
                var th = lines.Sum(l => l.Height);
                if (cell.QuerySelector(".fx-rpt-fixed-field") is { } fixedField)
                {
                    var fixedStyle = Styles(fixedField);
                    if (fixedStyle.TryGetValue("--fx-nogrow-lines", out var cap) && double.TryParse(cap, NumberStyles.Float, Invariant, out var limit))
                    {
                        var multiplier = fixedStyle.TryGetValue("--fx-nogrow-wrap", out var budget) && double.TryParse(budget, NumberStyles.Float, Invariant, out var factor) ? Math.Max(1, Math.Ceiling(factor)) : 1;
                        th = Math.Min(th, Math.Max(1, limit) * multiplier * ink.Size * 1.15);
                    }
                }
                entries.Add((cell, runs, cx, cw, pad, css.GetValueOrDefault("text-align", "left"), wrap, th)); column += span;
            }
            var rowHeight = entries.Max(e => e.TextHeight) + padY * 2 + extraTop + (isHeader ? 1.5 : groupHeader || groupFooter ? 0 : .75);
            if (y + rowHeight > height - 12) throw new NotSupportedException("A completed tabular report page exceeds its paper height. Review the report pagination before exporting PDF; no rows were omitted.");
            foreach (var entry in entries)
            {
                var css = Styles(entry.Cell);
                Fill(page, entry.X, y, entry.Width, rowHeight, css.GetValueOrDefault("background", isHeader ? "#f0f0f0" : grandTotal ? "#e0e0e0" : "transparent"));
                if (groupFooter && level == 0 || grandTotal) page.Line(entry.X, y, entry.X + entry.Width, y, "#333333", 1.5);
                if (css.ContainsKey("border-top")) page.Line(entry.X, y, entry.X + entry.Width, y, "#999999", .75);
                Text(page, entry.Runs, entry.X + entry.Pad, y + padY + extraTop, Math.Max(1, entry.Width - entry.Pad - 6 * Px), entry.TextHeight, entry.Align, entry.Wrap);
                if (isHeader || grandTotal || !groupHeader && !groupFooter) page.Line(entry.X, y + rowHeight, entry.X + entry.Width, y + rowHeight, isHeader ? "#999999" : grandTotal ? "#333333" : "#f0f0f0", isHeader || grandTotal ? 1.5 : .75);
            }
            y += rowHeight;
        }
        if (body.QuerySelector(".fx-rpt-footer") is { } footer)
        {
            y += 11 * Px;
            if (y + 18 * Px > height) throw new NotSupportedException("The report footer falls outside its paper height; PDF export did not omit it.");
            page.Line(marginX, y, width - marginX, y, "#cccccc", .75); y += 4 * Px;
            var cells = footer.Children.ToArray();
            for (var index = 0; index < cells.Length; index++) Text(page, Runs(cells[index].InnerHtml, new(Size: 9 * Px, Color: "#888888"), true), marginX, y, contentWidth, 14 * Px, index == 0 ? "left" : index == cells.Length - 1 ? "right" : "center", false);
        }
    }
}
