using System.Buffers.Binary;
using System.Numerics;

namespace Fx.ControlKit.Reports.NativeCrystal;

// Only records needed by this geometry vocabulary are played. Unknown records invalidate the whole image.
internal sealed partial class CrystalVectorMetafile
{
    private sealed record DrawingObject(bool Pen, string Color, double Width = 0, MetafileFont? Font = null);
    private sealed record Context(double X = 0, double Y = 0, double WX = 0, double WY = 0, double WW = 1, double WH = 1,
        double VX = 0, double VY = 0, double VW = 1, double VH = 1, int Mode = 1,
        DrawingObject? Pen = null, DrawingObject? Brush = null, bool Winding = false, double[]? Clip = null, bool WindowSet = false, bool ViewportSet = false,
        Matrix3x2 World = default, DrawingObject? Font = null, string TextColor = "#000000", int TextAlign = 0, bool Transparent = false,
        string BackgroundColor = "#ffffff", double[][]? ClipPolygons = null);
    private readonly ReportVectorImage _scene = new();
    private readonly Dictionary<uint, DrawingObject> _objects = [];
    private readonly List<Context> _saved = [];
    private Context _context = new(Pen: new(true, "#000000"), Brush: new(false, "#ffffff"), World: Matrix3x2.Identity);
    private int _handles;
    private bool _wmf;
    private int _pointCount;

    public static ReportVectorImage Read(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length <= 5 * 1024 * 1024 && bytes.Length >= 18, "Invalid vector metafile size.");
        var reader = new CrystalVectorMetafile();
        if (bytes.Length >= 88 && U32(bytes, 0) == 1 && U32(bytes, 40) == 0x464D4520) reader.Emf(bytes);
        else reader.Wmf(bytes);
        reader._scene.Validate(); return reader._scene;
    }

    public static ReportVectorImage Presentation(ReadOnlySpan<byte> bytes, int aspect)
    {
        var payload = CrystalMetafileImage.PresentationPayload(bytes, aspect, out var format);
        Require(format is 3 or 14, "OLE presentation has no supported vector metafile.");
        return Read(payload);
    }

    private void Wmf(ReadOnlySpan<byte> bytes)
    {
        _wmf = true;
        if (U32(bytes, 0) == 0x9AC6CDD7)
        {
            Require(bytes.Length >= 40, "Truncated placeable WMF header.");
            ushort sum = 0; for (var i = 0; i < 20; i += 2) sum ^= U16(bytes, i);
            Require(sum == U16(bytes, 20) && U16(bytes, 14) > 0, "Invalid placeable WMF checksum or units.");
            Frame(I16(bytes, 6), I16(bytes, 8), I16(bytes, 10) - I16(bytes, 6), I16(bytes, 12) - I16(bytes, 8));
            _context = _context with { WX = _scene.Left, WY = _scene.Top, WW = _scene.Width, WH = _scene.Height,
                VX = _scene.Left, VY = _scene.Top, VW = _scene.Width, VH = _scene.Height };
            bytes = bytes[22..];
        }
        Require(U16(bytes, 0) is 1 or 2 && U16(bytes, 2) == 9 && U16(bytes, 4) is 0x100 or 0x300 && U32(bytes, 6) * 2L == bytes.Length,
            "Invalid WMF header or length.");
        _handles = U16(bytes, 10); Require(_handles <= 4096, "WMF object table exceeds the limit.");
        var offset = 18; var records = 0;
        while (offset < bytes.Length)
        {
            Require(++records <= 10000 && bytes.Length - offset >= 6, "Invalid WMF record header/count.");
            var size = U32(bytes, offset) * 2L; Require(size >= 6 && size <= bytes.Length - offset, "Invalid WMF record size.");
            var record = bytes.Slice(offset, (int)size); offset += (int)size;
            var type = U16(record, 4); var p = record[6..];
            switch (type)
            {
                case 0: Size(p, 0); Require(offset == bytes.Length, "WMF data follows EOF."); return;
                case 0x0103: Size(p, 2); MapMode(I16(p, 0)); break;
                case 0x020B: Size(p, 4); _context = _context with { WX = I16(p, 2), WY = I16(p, 0) }; break;
                case 0x020C: Size(p, 4); Window(I16(p, 2), I16(p, 0)); break;
                case 0x020D: Size(p, 4); _context = _context with { VX = I16(p, 2), VY = I16(p, 0) }; break;
                case 0x020E: Size(p, 4); Viewport(I16(p, 2), I16(p, 0)); break;
                case 0x0106: Size(p, 2); FillMode(I16(p, 0)); break;
                case 0x0104: Size(p, 2); Require(I16(p, 0) == 13, "Unsupported WMF binary raster operation."); break;
                case 0x001E: Size(p, 0); Save(); break;
                case 0x0127: Size(p, 2); Restore(I16(p, 0)); break;
                case 0x02FA: Size(p, 10); Create(Free(), Pen(U16(p, 0), I16(p, 2), U32(p, 6))); break;
                case 0x02FC: Size(p, 8); Create(Free(), Brush(U16(p, 0), U32(p, 2))); break;
                case 0x02FB: Create(Free(), Font(p, false)); break;
                case 0x0209: Size(p, 4); _context = _context with { TextColor = Color(U32(p, 0)) }; break;
                case 0x0201: Size(p, 4); _context = _context with { BackgroundColor = Color(U32(p, 0)) }; break;
                case 0x0102: Size(p, 2); Background(I16(p, 0)); break;
                case 0x012E: Size(p, 2); Alignment(U16(p, 0)); break;
                case 0x0521: WmfText(p, false); break;
                case 0x0A32: WmfText(p, true); break;
                case 0x012D: Size(p, 2); Select(U16(p, 0), 0x8000); break;
                case 0x01F0: Size(p, 2); Delete(U16(p, 0)); break;
                case 0x0214: Size(p, 4); _context = _context with { X = I16(p, 2), Y = I16(p, 0) }; break;
                case 0x0213: Size(p, 4); Line(I16(p, 2), I16(p, 0)); break;
                case 0x041B: case 0x0418: case 0x0416:
                    Size(p, 8); Rectangle(type == 0x041B ? "Rectangle" : type == 0x0418 ? "Ellipse" : "Clip", I16(p, 6), I16(p, 4), I16(p, 2), I16(p, 0)); break;
                case 0x0324: case 0x0325:
                    Require(p.Length >= 2, "Truncated WMF polygon.");
                    Points(p, 2, U16(p, 0), true, type == 0x0324); break;
                default: throw new InvalidDataException($"WMF vector record 0x{type:X4} is not supported.");
            }
        }
        throw new InvalidDataException("Missing WMF EOF.");
    }

    private void Emf(ReadOnlySpan<byte> bytes)
    {
        var header = U32(bytes, 4);
        Require(header >= 88 && header <= bytes.Length && header % 4 == 0 && U32(bytes, 48) == bytes.Length, "Invalid EMF header size.");
        var dx = I32(bytes, 72); var dy = I32(bytes, 76); var mx = I32(bytes, 80); var my = I32(bytes, 84);
        Require(dx > 0 && dy > 0 && mx > 0 && my > 0, "Invalid EMF reference device.");
        Frame(I32(bytes, 24) * (double)dx / (mx * 100d), I32(bytes, 28) * (double)dy / (my * 100d),
            ((long)I32(bytes, 32) - I32(bytes, 24) + 1) * dx / (mx * 100d), ((long)I32(bytes, 36) - I32(bytes, 28) + 1) * dy / (my * 100d));
        _handles = U16(bytes, 56); Require(_handles <= 4096, "EMF object table exceeds the limit.");
        var offset = (int)header; var records = 1;
        while (offset < bytes.Length)
        {
            Require(++records <= 10000 && bytes.Length - offset >= 8, "Invalid EMF record header/count.");
            var size = U32(bytes, offset + 4); Require(size >= 8 && size % 4 == 0 && size <= bytes.Length - offset, "Invalid EMF record size.");
            var record = bytes.Slice(offset, (int)size); offset += (int)size;
            var type = U32(record, 0); var p = record[8..];
            switch (type)
            {
                case 14: Require(p.Length >= 12 && offset == bytes.Length && records == U32(bytes, 52), "Invalid EMF EOF/count."); return;
                case 17: Size(p, 4); MapMode(I32(p, 0)); break;
                case 9: Size(p, 8); Window(I32(p, 0), I32(p, 4)); break;
                case 10: Size(p, 8); _context = _context with { WX = I32(p, 0), WY = I32(p, 4) }; break;
                case 11: Size(p, 8); Viewport(I32(p, 0), I32(p, 4)); break;
                case 12: Size(p, 8); _context = _context with { VX = I32(p, 0), VY = I32(p, 4) }; break;
                case 19: Size(p, 4); FillMode(I32(p, 0)); break;
                case 20: Size(p, 4); Require(I32(p, 0) == 13, "Unsupported EMF binary raster operation."); break;
                case 33: Size(p, 0); Save(); break;
                case 34: Size(p, 4); Restore(I32(p, 0)); break;
                case 38: Size(p, 20); Create(U32(p, 0), Pen(U32(p, 4), I32(p, 8), U32(p, 16))); break;
                case 39: Size(p, 16); Create(U32(p, 0), Brush(U32(p, 4), U32(p, 8))); break;
                case 82: Require(p.Length >= 96, "Truncated EMF font."); Create(U32(p, 0), Font(p[4..], true)); break;
                case 24: Size(p, 4); _context = _context with { TextColor = Color(U32(p, 0)) }; break;
                case 25: Size(p, 4); _context = _context with { BackgroundColor = Color(U32(p, 0)) }; break;
                case 18: Size(p, 4); Background(I32(p, 0)); break;
                case 22: Size(p, 4); Alignment(U32(p, 0)); break;
                case 83: case 84: EmfText(record, type == 84); break;
                case 35: Size(p, 24); Transform(p, 4); break;
                case 36: Size(p, 28); Transform(p[..24], U32(p, 24)); break;
                case 37: Size(p, 4); Select(U32(p, 0), 0x80000000); break;
                case 40: Size(p, 4); Delete(U32(p, 0)); break;
                case 27: Size(p, 8); _context = _context with { X = I32(p, 0), Y = I32(p, 4) }; break;
                case 54: Size(p, 8); Line(I32(p, 0), I32(p, 4)); break;
                case 43: case 42: case 30:
                    Size(p, 16); Rectangle(type == 43 ? "Rectangle" : type == 42 ? "Ellipse" : "Clip", I32(p, 0), I32(p, 4), I32(p, 8), I32(p, 12)); break;
                case 3: case 4: case 86: case 87:
                    Require(p.Length >= 20, "Truncated EMF polygon.");
                    Points(p, 20, U32(p, 16), type is 86 or 87, type is 3 or 86); break;
                case 2: case 5: case 85: case 88:
                    Require(p.Length >= 20, "Truncated EMF Bezier curve.");
                    Bezier(p, U32(p, 16), type is 85 or 88, type is 5 or 88); break;
                default: throw new InvalidDataException($"EMF vector record {type} is not supported.");
            }
        }
        throw new InvalidDataException("Missing EMF EOF.");
    }

    private void Points(ReadOnlySpan<byte> bytes, int offset, uint count, bool shorts, bool closed)
    {
        var stride = shorts ? 4 : 8;
        Require(count is >= 2 and <= 16000 && offset + count * (long)stride == bytes.Length, "Invalid vector point count.");
        var points = new double[count * 2];
        for (var i = 0; i < count; i++)
        {
            var p = offset + i * stride;
            points[i * 2] = shorts ? I16(bytes, p) : I32(bytes, p);
            points[i * 2 + 1] = shorts ? I16(bytes, p + 2) : I32(bytes, p + 4);
        }
        Draw(closed ? "Polygon" : "Polyline", points);
    }

    private void Line(double x, double y)
    {
        Draw("Polyline", [_context.X, _context.Y, x, y]); _context = _context with { X = x, Y = y };
    }

    private void Bezier(ReadOnlySpan<byte> bytes, uint count, bool shorts, bool fromCurrent)
    {
        var stride = shorts ? 4 : 8;
        Require(count is >= 3 and <= 16000 && 20 + count * (long)stride == bytes.Length, "Invalid Bezier point count.");
        var usable = fromCurrent ? count / 3 * 3 : (count - 1) / 3 * 3 + 1;
        Require(fromCurrent || usable >= 4, "Bezier curve has no complete segment.");
        var points = new double[(usable + (fromCurrent ? 1 : 0)) * 2];
        var start = fromCurrent ? 2 : 0;
        if (fromCurrent) { points[0] = _context.X; points[1] = _context.Y; }
        for (var i = 0; i < usable; i++)
        {
            points[start + i * 2] = shorts ? I16(bytes, 20 + i * stride) : I32(bytes, 20 + i * stride);
            points[start + i * 2 + 1] = shorts ? I16(bytes, 22 + i * stride) : I32(bytes, 24 + i * stride);
        }
        var endX = points[^2]; var endY = points[^1];
        Draw("Bezier", points);
        if (fromCurrent) _context = _context with { X = endX, Y = endY };
    }

    private double[] MappedRectangle(double left, double top, double right, double bottom)
    {
        var a = Map(left, top); var b = Map(right, top); var c = Map(right, bottom); var d = Map(left, bottom);
        return [a.X, a.Y, b.X, b.Y, c.X, c.Y, d.X, d.Y];
    }

    private void Rectangle(string kind, double left, double top, double right, double bottom)
    {
        if (kind != "Clip") { Draw(kind, [Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top)]); return; }
        var matrix = Mapping();
        if (matrix.M12 != 0 || matrix.M21 != 0)
        {
            Require((_context.ClipPolygons?.Length ?? 0) < 64, "Metafile clip nesting exceeds the limit.");
            _context = _context with { ClipPolygons = [.. _context.ClipPolygons ?? [], MappedRectangle(left, top, right, bottom)] };
            return;
        }
        var a = Map(left, top); var b = Map(right, bottom);
        double[] rect = [Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)];
        if (_context.Clip is { } old)
        {
            var x = Math.Max(old[0], rect[0]); var y = Math.Max(old[1], rect[1]);
            rect = [x, y, Math.Max(0, Math.Min(old[0] + old[2], rect[0] + rect[2]) - x), Math.Max(0, Math.Min(old[1] + old[3], rect[1] + rect[3]) - y)];
        }
        _context = _context with { Clip = rect };
    }

    private void Draw(string kind, double[] points)
    {
        Require(_scene.Shapes.Count < 10000 && (_pointCount += points.Length) <= 200000, "Vector shape/point limit exceeded.");
        var matrix = Mapping();
        var pen = _context.Pen!; var sx = _context.Mode == 8 ? _context.VW / _context.WW : 1;
        var sy = _context.Mode == 8 ? _context.VH / _context.WH : 1;
        if (!_context.World.IsIdentity)
        {
            var x = new Vector2(matrix.M11, matrix.M12); var y = new Vector2(matrix.M21, matrix.M22);
            Require(pen.Color == "none" || Math.Abs(x.Length() - y.Length()) < .00001 && Math.Abs(Vector2.Dot(x, y)) < .00001,
                "Nonuniform world-transformed pen strokes are not supported.");
            _scene.Shapes.Add(new(kind, points, kind is "Polyline" or "Bezier" ? "none" : _context.Brush!.Color, pen.Color,
                pen.Width == 0 ? 1 / x.Length() : pen.Width, _context.Winding, _context.Clip?.ToArray(), MatrixValues(matrix), ClipPolygons: _context.ClipPolygons));
            return;
        }
        if (kind is "Rectangle" or "Ellipse")
        {
            var a = Map(points[0], points[1]); var b = Map(points[0] + points[2], points[1] + points[3]);
            points = [Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)];
        }
        else for (var i = 0; i < points.Length; i += 2) { var mapped = Map(points[i], points[i + 1]); points[i] = mapped.X; points[i + 1] = mapped.Y; }
        Require(pen.Color == "none" || pen.Width == 0 || Math.Abs(Math.Abs(sx) - Math.Abs(sy)) < 0.000001, "Anisotropic wide-pen strokes are not supported.");
        _scene.Shapes.Add(new(kind, points, kind is "Polyline" or "Bezier" ? "none" : _context.Brush!.Color, pen.Color,
            pen.Width == 0 ? 1 : Math.Abs(sx * pen.Width), _context.Winding, _context.Clip?.ToArray(), ClipPolygons: _context.ClipPolygons));
    }

    private (double X, double Y) Map(double x, double y)
    {
        var matrix = Mapping();
        return (x * matrix.M11 + y * matrix.M21 + matrix.M31, x * matrix.M12 + y * matrix.M22 + matrix.M32);
    }

    private Matrix3x2 Mapping()
    {
        if (_scene.Width == 0)
        {
            Require(_wmf && _context.WindowSet && _context.Mode == 8 && _context.WW > 0 && _context.WH > 0, "Unframed WMF requires a positive explicit window.");
            if (_context.ViewportSet)
                Frame(Math.Min(_context.VX, _context.VX + _context.VW), Math.Min(_context.VY, _context.VY + _context.VH), Math.Abs(_context.VW), Math.Abs(_context.VH));
            else
            {
                Frame(_context.WX, _context.WY, _context.WW, _context.WH);
                _context = _context with { VX = _scene.Left, VY = _scene.Top, VW = _scene.Width, VH = _scene.Height };
            }
        }
        var sx = _context.Mode == 8 ? _context.VW / _context.WW : 1;
        var sy = _context.Mode == 8 ? _context.VH / _context.WH : 1;
        var matrix = _context.World * new Matrix3x2((float)sx, 0, 0, (float)sy, (float)(_context.VX - _context.WX * sx), (float)(_context.VY - _context.WY * sy));
        ValidateMatrix(matrix); return matrix;
    }
    private void Frame(double left, double top, double width, double height)
    {
        Require(width > 0 && height > 0 && width <= 100000000 && height <= 100000000, "Invalid vector frame.");
        _scene.Left = left; _scene.Top = top; _scene.Width = width; _scene.Height = height;
    }
    private void MapMode(int mode) { Require(mode is 1 or 8, "Unsupported vector mapping mode."); _context = _context with { Mode = mode }; }
    private void Window(double x, double y) { Require(x != 0 && y != 0, "Zero vector window extent."); _context = _context with { WW = x, WH = y, WindowSet = true }; }
    private void Viewport(double x, double y) { Require(x != 0 && y != 0, "Zero vector viewport extent."); _context = _context with { VW = x, VH = y, ViewportSet = true }; }
    private void FillMode(int mode) { Require(mode is 1 or 2, "Invalid polygon fill mode."); _context = _context with { Winding = mode == 2 }; }
    private void Save() { Require(_saved.Count < 64, "Vector saved-state limit exceeded."); _saved.Add(_context); }
    private void Restore(int relative)
    {
        Require(relative < 0 && -(long)relative <= _saved.Count, "Invalid vector restore state.");
        var index = _saved.Count + relative; _context = _saved[index]; _saved.RemoveRange(index, _saved.Count - index);
    }
    private uint Free() { for (uint i = 0; i < _handles; i++) if (!_objects.ContainsKey(i)) return i; throw new InvalidDataException("WMF object table is full."); }
    private void Create(uint id, DrawingObject value) { Require(id < _handles && (_wmf || id > 0) && _objects.TryAdd(id, value), "Invalid vector object handle."); }
    private void Delete(uint id)
    {
        Require(_objects.TryGetValue(id, out var value), "Unknown vector object handle.");
        Require(!ReferenceEquals(_context.Pen, value) && !ReferenceEquals(_context.Brush, value) && !ReferenceEquals(_context.Font, value)
            && !_saved.Any(c => ReferenceEquals(c.Pen, value) || ReferenceEquals(c.Brush, value) || ReferenceEquals(c.Font, value)), "Deleting a selected vector object is not supported.");
        _objects.Remove(id);
    }
    private void Select(uint id, uint stockBit)
    {
        DrawingObject? value;
        if ((id & stockBit) != 0) value = (id & ~stockBit) switch
        {
            0 => new(false, "#ffffff"), 1 => new(false, "#c0c0c0"), 2 => new(false, "#808080"), 3 => new(false, "#404040"),
            4 => new(false, "#000000"), 5 => new(false, "none"), 6 => new(true, "#ffffff"), 7 => new(true, "#000000"), 8 => new(true, "none"),
            _ => throw new InvalidDataException("Unsupported vector stock object.")
        };
        else if (!_objects.TryGetValue(id, out value)) throw new InvalidDataException("Unknown vector object handle.");
        _context = value.Font is not null ? _context with { Font = value } : value.Pen ? _context with { Pen = value } : _context with { Brush = value };
    }
    private static DrawingObject Pen(uint style, double width, uint color)
    { Require(style is 0 or 5 && width >= 0, "Only solid/null metafile pens are supported."); return new(true, style == 5 ? "none" : Color(color), width); }
    private static DrawingObject Brush(uint style, uint color)
    { Require(style is 0 or 1, "Only solid/null metafile brushes are supported."); return new(false, style == 1 ? "none" : Color(color)); }
    private static string Color(uint color) { Require(color >> 24 == 0, "Palette-indexed metafile colors are not supported."); return $"#{color & 255:x2}{(color >> 8) & 255:x2}{(color >> 16) & 255:x2}"; }
    private static void Size(ReadOnlySpan<byte> bytes, int expected) => Require(bytes.Length == expected, "Invalid vector record payload size.");
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static int I32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static short I16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]);
}
