using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Grid;

/// <summary>Managed PDF object writer shared by Grid and native report pages. No browser or external resources.</summary>
internal sealed class NativePdfDocument
{
    private readonly List<NativePdfPage> _pages = [];
    private readonly Dictionary<NativePdfFont, FontUse> _fonts = [];
    private readonly Dictionary<string, ImageUse> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<(double Fill, double Stroke), string> _opacity = [];

    public NativePdfPage AddPage(double width, double height)
    {
        if (_pages.Count >= 10000) throw new InvalidDataException("PDF exceeds 10,000 pages.");
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || width > 14400 || height > 14400)
            throw new InvalidDataException("PDF page dimensions must be positive and no greater than 200 inches.");
        var page = new NativePdfPage(this, width, height); _pages.Add(page); return page;
    }

    internal string Font(NativePdfFont font, string text)
    {
        if (!_fonts.TryGetValue(font, out var use)) _fonts.Add(font, use = new("F" + (_fonts.Count + 1), font));
        foreach (var character in text)
        {
            if (char.IsSurrogate(character) || char.IsControl(character)) throw new NotSupportedException($"PDF text contains unsupported character U+{(int)character:X4}.");
            use.Characters[character] = font.Glyph(character);
        }
        return use.Name;
    }

    internal string Image(byte[] bytes)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!_images.TryGetValue(key, out var image))
        {
            image = new("Im" + (_images.Count + 1), NativePdfImage.Read(bytes)); _images.Add(key, image);
        }
        return image.Name;
    }

    internal string Opacity(double fill, double stroke)
    {
        var key = (fill, stroke);
        if (!_opacity.TryGetValue(key, out var name)) _opacity.Add(key, name = "GS" + (_opacity.Count + 1));
        return name;
    }

    public byte[] ToArray()
    {
        if (_pages.Count == 0) throw new InvalidOperationException("There are no report pages to export.");
        if (_pages.Any(p => p.SavedStates != 0)) throw new InvalidOperationException("A PDF drawing state was not restored.");
        var objects = new PdfObjects();
        var catalogId = objects.Reserve(); var pagesId = objects.Reserve();
        var fontResources = new StringBuilder();
        foreach (var use in _fonts.Values)
        {
            var font = use.Font;
            var fileId = objects.Stream(font.Data, "/Length1 " + font.Data.Length);
            var descriptorId = objects.Add($"<< /Type /FontDescriptor /FontName /{font.PostScriptName} /Flags {font.Flags} /FontBBox [{string.Join(' ', font.BoundingBox.Select(Number))}] /ItalicAngle {Number(font.ItalicAngle)} /Ascent {Number(font.Ascent)} /Descent {Number(font.Descent)} /CapHeight {Number(font.CapHeight)} /StemV 80 /FontFile2 {fileId} 0 R >>");
            var mapping = new byte[((int)use.Characters.Keys.DefaultIfEmpty('\0').Max() + 1) * 2];
            foreach (var (character, glyph) in use.Characters) BinaryPrimitives.WriteUInt16BigEndian(mapping.AsSpan(character * 2), glyph);
            var mapId = objects.Stream(mapping);
            var widths = string.Join(' ', use.Characters.Keys.Order().Select(c => $"{(int)c} [{Number(font.Width(c))}]"));
            var descendantId = objects.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{font.PostScriptName} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptorId} 0 R /DW 1000 /W [{widths}] /CIDToGIDMap {mapId} 0 R >>");
            var unicodeId = objects.Stream(Encoding.ASCII.GetBytes(UnicodeMap(use.Characters.Keys)));
            var type0Id = objects.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{font.PostScriptName} /Encoding /Identity-H /DescendantFonts [{descendantId} 0 R] /ToUnicode {unicodeId} 0 R >>");
            fontResources.Append('/').Append(use.Name).Append(' ').Append(type0Id).Append(" 0 R ");
        }
        var imageResources = new StringBuilder();
        foreach (var use in _images.Values)
        {
            var image = use.Image;
            var maskId = image.Alpha is null ? 0 : objects.Stream(image.Alpha, $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8");
            var descriptor = $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /{image.ColorSpace} /BitsPerComponent 8" + (maskId == 0 ? "" : $" /SMask {maskId} 0 R");
            var id = objects.Stream(image.Data, descriptor, image.Jpeg ? "/DCTDecode" : null);
            imageResources.Append('/').Append(use.Name).Append(' ').Append(id).Append(" 0 R ");
        }
        var graphicsResources = new StringBuilder();
        foreach (var (alpha, name) in _opacity)
        {
            var id = objects.Add($"<< /Type /ExtGState /ca {Number(alpha.Fill)} /CA {Number(alpha.Stroke)} >>");
            graphicsResources.Append('/').Append(name).Append(' ').Append(id).Append(" 0 R ");
        }
        var pageIds = new List<int>();
        foreach (var page in _pages)
        {
            var content = "q\n1 0 0 -1 0 " + Number(page.Height) + " cm\n" + page.Content + "Q\n";
            var contentId = objects.Stream(Encoding.ASCII.GetBytes(content));
            pageIds.Add(objects.Add($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {Number(page.Width)} {Number(page.Height)}] /Resources << /Font << {fontResources} >> /XObject << {imageResources} >> /ExtGState << {graphicsResources} >> >> /Contents {contentId} 0 R >>"));
        }
        objects.Set(catalogId, $"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        objects.Set(pagesId, $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => id + " 0 R"))}] /Count {pageIds.Count} >>");
        return objects.Write(catalogId);
    }

    // The legacy Grid drawing stream and standard fonts remain unchanged; only object serialization is shared.
    public static byte[] WriteLegacy(double pageWidth, double pageHeight, IReadOnlyList<string> contents)
    {
        var objects = new PdfObjects(); var catalog = objects.Reserve(); var pages = objects.Reserve();
        var normal = objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var bold = objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        var ids = new List<int>();
        foreach (var content in contents)
        {
            var page = objects.Reserve(); var stream = objects.Stream(Encoding.ASCII.GetBytes(content), compress: false);
            ids.Add(page); objects.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {Number(pageWidth)} {Number(pageHeight)}] /Resources << /Font << /F1 {normal} 0 R /F2 {bold} 0 R >> >> /Contents {stream} 0 R >>");
        }
        objects.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        objects.Set(pages, $"<< /Type /Pages /Kids [{string.Join(' ', ids.Select(id => id + " 0 R"))}] /Count {ids.Count} >>");
        return objects.Write(catalog);
    }

    internal static string Number(double number)
    {
        if (!double.IsFinite(number) || Math.Abs(number) > 1e8) throw new InvalidDataException("PDF coordinate is invalid.");
        return number.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string UnicodeMap(IEnumerable<char> characters)
    {
        var result = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /FxUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var batch in characters.Order().Chunk(100))
        {
            result.Append(batch.Length).AppendLine(" beginbfchar");
            foreach (var character in batch) result.Append('<').Append(((int)character).ToString("X4")).Append("> <").Append(((int)character).ToString("X4")).AppendLine(">");
            result.AppendLine("endbfchar");
        }
        return result.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n").ToString();
    }

    private sealed record FontUse(string Name, NativePdfFont Font)
    {
        public Dictionary<char, ushort> Characters { get; } = [];
    }
    private sealed record ImageUse(string Name, NativePdfImage Image);

    private sealed class PdfObjects
    {
        private readonly List<byte[]> _objects = [];
        public int Reserve() { _objects.Add([]); return _objects.Count; }
        public int Add(string text) { var id = Reserve(); Set(id, text); return id; }
        public void Set(int id, string text) => _objects[id - 1] = Encoding.ASCII.GetBytes(text);
        public int Stream(byte[] bytes, string extra = "", string? existingFilter = null, bool compress = true)
        {
            if (existingFilter is null && compress)
            {
                using var zipped = new MemoryStream();
                using (var compressor = new ZLibStream(zipped, CompressionLevel.Optimal, leaveOpen: true)) compressor.Write(bytes);
                bytes = zipped.ToArray(); existingFilter = "/FlateDecode";
            }
            using var output = new MemoryStream();
            output.Write(Encoding.ASCII.GetBytes($"<< /Length {bytes.Length}{(extra.Length == 0 ? "" : " " + extra)}{(existingFilter is null ? "" : " /Filter " + existingFilter)} >>\nstream\n"));
            output.Write(bytes); output.Write("\nendstream"u8);
            var id = Reserve(); _objects[id - 1] = output.ToArray(); return id;
        }
        public byte[] Write(int catalog)
        {
            using var stream = new MemoryStream();
            void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));
            Write("%PDF-1.4\n"); var offsets = new List<long> { 0 };
            for (var i = 0; i < _objects.Count; i++)
            {
                offsets.Add(stream.Position); Write($"{i + 1} 0 obj\n"); stream.Write(_objects[i]); Write("\nendobj\n");
            }
            var xref = stream.Position; Write($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets.Skip(1)) Write(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
            Write($"trailer\n<< /Size {_objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF");
            return stream.ToArray();
        }
    }
}

/// <summary>Vector canvas with top-left point coordinates; text coordinates name the baseline.</summary>
internal sealed class NativePdfPage(NativePdfDocument document, double width, double height)
{
    private readonly StringBuilder _content = new();
    internal string Content => _content.ToString();
    internal int SavedStates { get; private set; }
    public double Width { get; } = width;
    public double Height { get; } = height;
    private static string N(double n) => NativePdfDocument.Number(n);
    public void Save() { _content.AppendLine("q"); SavedStates++; }
    public void Restore()
    {
        if (SavedStates == 0) throw new InvalidOperationException("PDF drawing state underflow.");
        _content.AppendLine("Q"); SavedStates--;
    }
    public void Clip(double x, double y, double w, double h)
    { Size(w, h); _content.AppendLine($"{N(x)} {N(y)} {N(w)} {N(h)} re W n"); }
    public void Transform(double a, double b, double c, double d, double e, double f) => _content.AppendLine($"{N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} cm");
    public void SetLineStyle(int cap, int join)
    {
        if (cap is < 0 or > 2 || join is < 0 or > 2) throw new InvalidDataException("Invalid PDF line cap or join style.");
        _content.Append(cap).Append(" J ").Append(join).AppendLine(" j");
    }
    public void ClipPath(string path, bool evenOdd = false)
    { ValidatePath(path); _content.Append(path).AppendLine(evenOdd ? " W* n" : " W n"); }
    public static double MeasureText(string text, double size, string family = "Arial", bool bold = false, bool italic = false, bool kerning = false)
    {
        FontSize(size); var font = NativePdfFont.Resolve(family, bold, italic);
        var width = 0d;
        for (var i = 0; i < text.Length; i++)
            width += font.Width(text[i]) + (kerning && i > 0 ? font.Kerning(text[i - 1], text[i]) : 0);
        return width * size / 1000;
    }
    public void Text(string text, double x, double baseline, double size = 10, string family = "Arial", bool bold = false, bool italic = false, string color = "#000000", bool underline = false, bool kerning = false)
    {
        if (text.Length == 0) return;
        if (text.Length > 1_000_000) throw new InvalidDataException("A PDF text run is too large.");
        FontSize(size); var font = NativePdfFont.Resolve(family, bold, italic); var resource = document.Font(font, text);
        Save(); Paint(color, null);
        _content.Append("BT /").Append(resource).Append(' ').Append(N(size)).Append(" Tf 1 0 0 -1 ").Append(N(x)).Append(' ').Append(N(baseline)).Append(kerning ? " Tm [<" : " Tm <");
        for (var i = 0; i < text.Length; i++)
        {
            var adjust = kerning && i > 0 ? font.Kerning(text[i - 1], text[i]) : 0;
            if (adjust != 0) _content.Append("> ").Append(N(-adjust)).Append(" <");
            _content.Append(((int)text[i]).ToString("X4", CultureInfo.InvariantCulture));
        }
        _content.AppendLine(kerning ? ">] TJ ET" : "> Tj ET"); Restore();
        if (underline) Line(x, baseline + size * .1, x + MeasureText(text, size, family, bold, italic, kerning), baseline + size * .1, color, Math.Max(.4, size / 16));
    }
    public void Line(double x1, double y1, double x2, double y2, string color = "#000000", double width = 1, double[]? dash = null)
    {
        StrokeWidth(width); Save(); Paint(null, color);
        _content.AppendLine(N(width) + " w");
        if (dash is not null)
        {
            if (dash.Length > 16 || dash.Any(d => !double.IsFinite(d) || d < 0) || dash.Length > 0 && dash.All(d => d == 0)) throw new InvalidDataException("Invalid PDF line dash pattern.");
            _content.AppendLine("[" + string.Join(' ', dash.Select(N)) + "] 0 d");
        }
        _content.AppendLine($"{N(x1)} {N(y1)} m {N(x2)} {N(y2)} l S"); Restore();
    }
    public void Rectangle(double x, double y, double w, double h, string? fill = null, string? stroke = null, double lineWidth = 1)
    {
        Size(w, h); Path($"{N(x)} {N(y)} m {N(x + w)} {N(y)} l {N(x + w)} {N(y + h)} l {N(x)} {N(y + h)} l h", fill, stroke, lineWidth);
    }
    public void Ellipse(double x, double y, double w, double h, string? fill = null, string? stroke = null, double lineWidth = 1)
    {
        Size(w, h); var rx = w / 2; var ry = h / 2; var cx = x + rx; var cy = y + ry; const double k = .5522847498307936;
        Path($"{N(cx + rx)} {N(cy)} m {N(cx + rx)} {N(cy + ry * k)} {N(cx + rx * k)} {N(cy + ry)} {N(cx)} {N(cy + ry)} c " +
             $"{N(cx - rx * k)} {N(cy + ry)} {N(cx - rx)} {N(cy + ry * k)} {N(cx - rx)} {N(cy)} c " +
             $"{N(cx - rx)} {N(cy - ry * k)} {N(cx - rx * k)} {N(cy - ry)} {N(cx)} {N(cy - ry)} c " +
             $"{N(cx + rx * k)} {N(cy - ry)} {N(cx + rx)} {N(cy - ry * k)} {N(cx + rx)} {N(cy)} c h", fill, stroke, lineWidth);
    }
    public void Path(string path, string? fill, string? stroke, double width = 1, bool evenOdd = false)
    {
        ValidatePath(path);
        if (None(fill) && None(stroke)) return;
        StrokeWidth(width); Save(); Paint(fill, stroke); _content.AppendLine(N(width) + " w"); _content.Append(path).Append(' ');
        _content.AppendLine(None(fill) ? "S" : None(stroke) ? (evenOdd ? "f*" : "f") : evenOdd ? "B*" : "B"); Restore();
    }
    public void Image(byte[] bytes, double x, double y, double w, double h)
    {
        Size(w, h); if (w == 0 || h == 0) return;
        var name = document.Image(bytes); Save(); _content.AppendLine($"{N(w)} 0 0 {N(-h)} {N(x)} {N(y + h)} cm /{name} Do"); Restore();
    }
    public static (int Width, int Height) ImageSize(byte[] bytes) { var image = NativePdfImage.Read(bytes); return (image.Width, image.Height); }
    private void Paint(string? fill, string? stroke)
    {
        var f = Color(fill); var s = Color(stroke);
        if (!None(fill)) _content.AppendLine($"{N(f.R)} {N(f.G)} {N(f.B)} rg");
        if (!None(stroke)) _content.AppendLine($"{N(s.R)} {N(s.G)} {N(s.B)} RG");
        _content.Append('/').Append(document.Opacity(f.A, s.A)).AppendLine(" gs");
    }
    private static bool None(string? color) => string.IsNullOrEmpty(color) || color is "none" or "transparent";
    private static (double R, double G, double B, double A) Color(string? color)
    {
        if (None(color)) return (0, 0, 0, 1);
        if (color!.Length is not (7 or 9) || color[0] != '#' || !color.AsSpan(1).ToString().All(Uri.IsHexDigit)) throw new InvalidDataException("PDF color must be an RGB or RGBA hexadecimal value.");
        double C(int n) => byte.Parse(color.AsSpan(n, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        return (C(1), C(3), C(5), color.Length == 9 ? C(7) : 1);
    }
    private static void ValidatePath(string path)
    { if (path.Length > 4_000_000 || !Regex.IsMatch(path, @"\A[0-9.\-+\smlchvy]*\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid PDF vector path."); }
    private static void Size(double w, double h) { N(w); N(h); if (w < 0 || h < 0) throw new InvalidDataException("PDF dimensions cannot be negative."); }
    private static void StrokeWidth(double width) { N(width); if (width < 0) throw new InvalidDataException("PDF line width cannot be negative."); }
    private static void FontSize(double size) { N(size); if (size <= 0 || size > 1000) throw new InvalidDataException("Invalid PDF font size."); }
}
