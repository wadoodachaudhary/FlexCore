using System.Numerics;

namespace Fx.ControlKit.Reports.NativeCrystal;

internal sealed partial class CrystalVectorMetafile
{
    private List<ReportVectorPathCommand>? _path;
    private bool _pathOpen;
    private bool _figureOpen;
    private bool _figureUsesCurrent;

    private void BeginPath()
    {
        Require(!_pathOpen, "Nested metafile path brackets are invalid.");
        _path = []; _pathOpen = true; _figureOpen = false;
    }

    private void EndPath()
    {
        Require(_pathOpen, "Metafile EndPath has no open bracket.");
        _pathOpen = false;
    }

    private void MoveTo(double x, double y)
    {
        _context = _context with { X = x, Y = y };
        if (_pathOpen) AddPath("Move", [x, y]);
    }

    private void ContinuePathFigure()
    {
        if (!_figureOpen || !_figureUsesCurrent) AddPath("Move", [_context.X, _context.Y]);
    }

    private void ClosePathFigure()
    {
        Require(_pathOpen, "Metafile CloseFigure has no open bracket.");
        if (_figureOpen) AddPath("Close", []);
    }

    private void AddPath(string kind, double[] points)
    {
        Require(_path is not null && _path.Count < 10000 && (_pointCount += Math.Max(1, points.Length)) <= 200000,
            "Metafile path command/coordinate limit exceeded.");
        // GDI records the geometry in device space; a later transform changes the pen, not the stored path.
        for (var i = 0; i < points.Length; i += 2)
        {
            var mapped = Map(points[i], points[i + 1]); points[i] = mapped.X; points[i + 1] = mapped.Y;
        }
        _path!.Add(new(kind, points)); _figureOpen = kind != "Close"; _figureUsesCurrent = true;
    }

    private void CapturePath(string kind, double[] points)
    {
        Require(kind is "Rectangle" or "Polygon" or "Polyline" or "Bezier", $"Metafile {kind} inside a path bracket is not supported.");
        if (kind == "Rectangle")
            points = [points[0], points[1], points[0] + points[2], points[1], points[0] + points[2], points[1] + points[3], points[0], points[1] + points[3]];
        AddPath("Move", points[..2]);
        var stride = kind == "Bezier" ? 6 : 2;
        for (var i = 2; i < points.Length; i += stride) AddPath(kind == "Bezier" ? "Cubic" : "Line", points[i..(i + stride)]);
        if (kind is "Polygon" or "Rectangle") AddPath("Close", []);
        // Standalone Polyline/PolyBezier do not update the DC current position.
        _figureUsesCurrent = false;
    }

    private void PaintPath(bool fill, bool stroke)
    {
        Require(_path is not null && !_pathOpen, "Metafile path painting requires a completed path.");
        var commands = new List<ReportVectorPathCommand>();
        var open = false;
        foreach (var command in _path!)
        {
            if (fill && open && command.Kind == "Move") commands.Add(new("Close", []));
            commands.Add(command); open = command.Kind != "Close";
        }
        if (fill && open) commands.Add(new("Close", []));
        if (commands.Any(c => c.Kind is "Line" or "Cubic"))
        {
            var pen = _context.Pen!; var width = 1d;
            if (stroke && pen.Color != "none" && pen.Width != 0)
            {
                var matrix = Mapping();
                var x = new Vector2(matrix.M11, matrix.M12); var y = new Vector2(matrix.M21, matrix.M22);
                Require(Math.Abs(x.Length() - y.Length()) < .00001 && Math.Abs(Vector2.Dot(x, y)) < .00001,
                    "Nonuniform wide-pen path strokes are not supported.");
                width = pen.Width * x.Length();
            }
            Require(_scene.Shapes.Count < 10000, "Vector shape limit exceeded.");
            _scene.Shapes.Add(new("Path", [], fill ? _context.Brush!.Color : "none", stroke ? pen.Color : "none", width,
                _context.Winding, _context.Clip?.ToArray(), ClipPolygons: _context.ClipPolygons, Commands: commands.ToArray()));
        }
        _path = null; _figureOpen = false;
    }
}
