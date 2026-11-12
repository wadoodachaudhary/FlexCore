using Fx.ControlKit.Reports;
using System.Globalization;

namespace Fx.ControlKit.Charts;

// Native perspective pies can have independent slice labels and a legend.
internal static class PrintedPerspectivePie
{
    private static readonly string[] Palette = ["#3a6598", "#efa252", "#009778", "#e45520", "#a22b64", "#ffd35f"];
    internal static ReportVectorImage Draw(ReportAnalysisDefinition definition, IReadOnlyList<ChartSeries> series, double w, double h)
    {
        var image = new ReportVectorImage { Width=w, Height=h };
        var layout = definition.SpatialStyle?.BottomPie;
        if (layout?.TransparentBackground != true)
            image.Shapes.Add(new("Rectangle",[0,0,w,h],"#ffffff","none",0));
        var f = Math.Max(6, Math.Min(w/58, h/30));
        void Text(double x,double y,string label,double size,string anchor="middle",bool bold=false) =>
            image.Shapes.Add(new("Text",[x,y],"#000000","none",0,Text:new(label,"Arial",size,bold?700:400,Anchor:anchor)));
        var titleSize = Math.Max(10, Math.Min(w/58, h/30)*1.92);
        var titleLines = ReportNativeCrossTabText.Wrap(definition.Title,
            new("Arial", (decimal)titleSize, true, false, false, "#000000", "transparent", "center"), (int)(w*.92*20)).Split('\n');
        for (var line = 0; line < titleLines.Length; line++)
            Text(w*.5,h*.08 + line*titleSize*1.05,titleLines[line],titleSize,bold:true);
        var source = series.FirstOrDefault()?.DataPoints;
        var indices = source?.Select((p, i) => (p, i)).Where(item => !item.p.IsEmpty && double.IsFinite(item.p.Value) && item.p.Value > 0)
            .Select(item => item.i).ToArray() ?? [];
        var points = indices.Select(i => source![i]).ToArray();
        var total=points.Sum(p=>p.Value);
        if(total<=0) return image;
        var palette = definition.PiePalette ??
            (definition.SpatialStyle?.Palette.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } saved ? saved : Palette);
        var detach=definition.SpatialStyle?.PieDetachment??0;
        var cx=w*(definition.ShowLegend ? (detach>0?.385:.35) : .495); var cy=h*(w<260 && !definition.ShowLegend ? .57 : .538);
        var rx=Math.Min(w*(w<260 && !definition.ShowLegend ? .28 : .22),h*(detach>0?.36:.301));var ry=rx*(detach>0?.70:.853);var depth=ry*(detach>0?.14:.0714);
        // A single full circle with automatic callouts uses the compact one-label layout.
        if (points.Length == 1 && !definition.ShowLegend && layout?.LabelOffsets is null) layout = null;
        LegendBox? key = null;
        if (layout is not null && definition.SpatialStyle?.Frame is { } frame)
        {
            key = definition.ShowLegend ? MeasureLegend(layout, points, total, w, h) : null;
            f = PrintFont(layout.LabelFont, h);
            var left = frame.Left * w;
            var right = frame.Right * w;
            var top = frame.Top * h;
            var bottom = frame.Bottom * h;
            if (key is not null && key.Left < right && key.Left + key.Width > left) bottom = Math.Min(bottom, key.Top - f * 1.1);
            if (bottom <= top) bottom = frame.Bottom * h;
            cx = (left + right) / 2;
            // Pie depth uses four virtual units per saved depth unit, on a 32768-unit canvas.
            depth = Math.Min(h * layout.Depth * 4 / 32768, (bottom - top) * .95);
            cy = (top + bottom - depth) / 2;
            rx = .5 * Math.Min((right - left) * (1 - layout.HorizontalInset / 100d),
                (bottom - top) * (1 - layout.VerticalInset / 100d));
            ry = rx * (1 - layout.Tilt / 90d);
        }
        var faces=new List<ReportVectorShape>();var callouts=new List<(double X,double Y,string Label,bool Right)>();
        var angle=layout is null ? -Math.PI/2 : -layout.Rotation*Math.PI/180;
        for(var i=0;i<points.Length;i++)
        {
            var p=points[i]; var sweep=p.Value/total*Math.PI*2*(layout?.Clockwise == false ? -1 : 1);var end=angle+sweep;
            var color=p.Color ?? palette[(layout is null ? i : indices[i])%palette.Length];var samples=Math.Max(3,(int)Math.Ceiling(Math.Abs(sweep)*30));
            var separate=detach==3 || detach==2 && i==Array.FindIndex(points,p=>p.Value==points.Max(v=>v.Value)) || detach==1 && i==Array.FindIndex(points,p=>p.Value==points.Min(v=>v.Value));
            var slice = layout?.Slices?.GetValueOrDefault(indices[i]);
            var shift = points.Length == 1 ? 0 : slice is { ExplodePercent: > 0 } ? slice.ExplodePercent / 100d : separate ? .45 : 0;
            var sx = cx + rx * shift * Math.Cos(angle + sweep / 2);
            var sy = cy + ry * shift * Math.Sin(angle + sweep / 2);
            var top=new List<double>();
            if(points.Length>1)top.AddRange([sx,sy]);
            for(int k=0;k<=samples;k++){var a=angle+sweep*k/samples;top.AddRange([sx+rx*Math.Cos(a),sy+ry*Math.Sin(a)]);}
            // Only the front half of the rim is visible; paint it before all top faces.
            for(int turn=(int)Math.Floor(Math.Min(angle,end)/(2*Math.PI)); turn<=Math.Floor(Math.Max(angle,end)/(2*Math.PI)); turn++)
            {
                var lo=Math.Max(Math.Min(angle,end),turn*2*Math.PI);var hi=Math.Min(Math.Max(angle,end),(turn*2+1)*Math.PI);if(hi<=lo)continue;
                var rim=new List<double>();
                for(int k=0;k<=samples;k++){var a=lo+(hi-lo)*k/samples;rim.AddRange([sx+rx*Math.Cos(a),sy+ry*Math.Sin(a)]);}
                for(int k=samples;k>=0;k--){var a=lo+(hi-lo)*k/samples;rim.AddRange([sx+rx*Math.Cos(a),sy+ry*Math.Sin(a)+depth]);}
                image.Shapes.Add(new("Polygon",rim.ToArray(),Shade(color),"none",0));
            }
            if (shift > 0 && depth > 0)
            {
                // Separating sectors exposes the two radial walls as well as the circular rim.
                Wall(angle, -Math.Sign(sweep));
                Wall(end, Math.Sign(sweep));
                void Wall(double boundary, int outward)
                {
                    if (outward * Math.Cos(boundary) <= 0) return;
                    var bx = sx + rx * Math.Cos(boundary);
                    var by = sy + ry * Math.Sin(boundary);
                    image.Shapes.Add(new("Polygon", [sx,sy,bx,by,bx,by+depth,sx,sy+depth], Shade(color), "none", 0));
                }
            }
            faces.Add(new("Polygon",top.ToArray(),color,color,.05));
            // A full circle starts its callout at the rear rim, not at its bottom midpoint.
            var mid=points.Length==1 ? -Math.PI/2+.16 : angle+sweep/2;
            callouts.Add((sx+rx*Math.Cos(mid),sy+ry*Math.Sin(mid),p.Label,Math.Cos(mid)>=0));
            angle=end;
        }
        image.Shapes.AddRange(faces);
        if (layout is not null)
        {
            DrawSliceLabels(image, callouts, points, indices, layout, w, h, cx, cy, rx, ry, depth, f);
            if (key is not null) DrawLegend(image, key, points, total, indices, palette);
        }
        else if(!definition.ShowLegend)
        {
            var used=new List<double>();
            foreach(var c in callouts.OrderBy(c=>c.Y))
            {
                var y=c.Y-f*.6;while(used.Any(v=>Math.Abs(v-y)<f*1.4))y+=f*1.4;
                y=Math.Clamp(y,h*.15,h-f);used.Add(y);
                var labelWidth = c.Label.Length*f*.52;
                var end=c.Right ? Math.Min(w-labelWidth-f*.5,cx+rx*1.3) : Math.Max(labelWidth+f*.5,cx-rx*1.3);
                image.Shapes.Add(new("Polyline",[c.X,c.Y,c.X,y,end,y],"none","#000000",.6));
                Text(end,y+f*.35,c.Label,f,c.Right?"start":"end");
            }
        }
        else
        {
            var x=w*.70;
            var leading=detach>0?1.05:1.45;
            f=Math.Min(f,Math.Min(h*.68/((points.Length+1.6)*leading),w*.27/Math.Max(1,points.Max(p=>p.Label.Length+9)*.56)));
            var legendWidth=detach>0?Math.Min(w*.29,(points.Max(p=>p.Label.Length)+9)*f*.52+f*2.2):w*.29;
            var y=cy-(points.Length+1.6)*f*leading/2;
            image.Shapes.Add(new("Rectangle",[x,y,legendWidth,(points.Length+1.6)*f*leading],"#ffffff","#000000",.6));
            for(int i=0;i<points.Length;i++)
            {
                image.Shapes.Add(new("Rectangle",[x+f*.4,y+f*(.6+i*leading),f*.8,f*.8],points[i].Color??palette[i%palette.Length],"none",0));
                Text(x+f*1.6,y+f*(1.35+i*leading),$"{points[i].Label}  {points[i].Value/total:P1}",f,"start");
            }
            Text(x+f*1.6,y+f*(1.35+points.Length*leading),"Total: 100.0%",f,"start");
        }
        return image;
    }
    private sealed record LegendBox(double Left, double Top, double Width, double Height, double Font, double Leading);

    // Font heights use the drawing's virtual coordinate system and precision scale.
    private static double PrintFont(int units, double height) => units * (1000d / 1024) * height / 32768;
    private static string Percent(double fraction) => fraction.ToString("P1", CultureInfo.CurrentCulture);

    private static LegendBox MeasureLegend(ReportBottomPieLayout layout, ChartDataPoint[] points, double total, double w, double h)
    {
        // If the designer enables a formerly hidden legend, give it a centered bottom frame.
        var frame = layout.Legend ?? new ReportPlotFrame(.15, .72, .85, .98);
        var rows = points.Length + 1;
        var font = Math.Min(PrintFont(layout.LegendFont, h), (frame.Bottom - frame.Top) * h / (rows * 1.2 + .65));
        double Width(string text) => ChartAxisLabelLayout.MeasureWidth(text, font);
        var width = points.Select(p => Width(p.Label)).Append(Width("Total:")).Max()
            + points.Select(p => Width(Percent(p.Value / total))).Append(Width(Percent(1))).Max() + font * 3.9;
        var scale = Math.Min(1, (frame.Right - frame.Left) * w / width);
        width *= scale;
        font *= scale;
        var leading = font * 1.2;
        var height = leading * rows + font * .65;
        return new((frame.Left + frame.Right) * w / 2 - width / 2, frame.Bottom * h - height, width, height, font, leading);
    }

    private static void DrawLegend(ReportVectorImage image, LegendBox box, ChartDataPoint[] points, double total, int[] indices, string[] palette)
    {
        image.Shapes.Add(new("Rectangle", [box.Left, box.Top, box.Width, box.Height], "#ffffff", "#000000", .6));
        for (var i = 0; i <= points.Length; i++)
        {
            var y = box.Top + box.Font * .4 + i * box.Leading;
            var name = "Total:";
            var share = 1d;
            if (i < points.Length)
            {
                var point = points[i];
                name = point.Label;
                share = point.Value / total;
                var swatch = point.Color ?? palette[indices[i] % palette.Length];
                image.Shapes.Add(new("Rectangle", [box.Left + box.Font * .55, y, box.Font * .94, box.Font * .94], swatch, "none", 0));
            }
            var baseline = y + box.Font * .86;
            Label(image, box.Left + box.Font * 2.15, baseline, name, box.Font, "start");
            Label(image, box.Left + box.Width - box.Font * .75, baseline, Percent(share), box.Font, "end");
        }
    }

    private static void DrawSliceLabels(ReportVectorImage image, List<(double X, double Y, string Label, bool Right)> callouts,
        ChartDataPoint[] points, int[] indices, ReportBottomPieLayout layout, double w, double h, double cx, double cy, double rx, double ry, double depth, double font)
    {
        var visible = callouts.Select((call, i) => (Call: call, Index: indices[i], Flag: layout.SliceLabels.ElementAtOrDefault(indices[i])))
            .Where(item => item.Flag is '1' or '2');
        foreach (var quadrant in visible.GroupBy(item => (item.Call.Right, Upper: item.Call.Y < cy)))
        {
            var sorted = quadrant.OrderBy(item => item.Call.Y).ToArray();
            var step = font * 1.15;
            var y = quadrant.Key.Upper ? cy - ry - font * .3 : cy + ry + depth + font - (sorted.Length - 1) * step;
            foreach (var (call, index, flag) in sorted)
            {
                var format = layout.Slices?.GetValueOrDefault(index)?.ValueFormat;
                var amount = string.IsNullOrEmpty(format) ? "" : points[Array.IndexOf(indices, index)].Value.ToString(format, CultureInfo.CurrentCulture);
                var width = Math.Max(ChartAxisLabelLayout.MeasureWidth(call.Label, font), ChartAxisLabelLayout.MeasureWidth(amount, font));
                var x = call.Right ? w - font * .1 - width : font * .1 + width;
                if (layout.Legend is null && layout.LabelOffsets is null)
                    x = call.Right ? Math.Min(x, cx + 1.3 * rx) : Math.Max(x, cx - 1.3 * rx);
                var labelY = y;
                if (layout.LabelOffsets?.TryGetValue(index, out var move) == true)
                {
                    // Crystal saves dragged callouts as deltas from their automatic positions.
                    x += move.X * w / 32768;
                    labelY -= move.Y * h / 32768;
                }
                var left = Math.Clamp(call.Right ? x : x - width, 0, Math.Max(0, w - width));
                var end = call.X < left ? left : call.X > left + width ? left + width : call.Right ? left : left + width;
                if (flag == '2')
                {
                    // When a dragged label moves toward the middle, lead outward first to avoid crossing the face.
                    var inward = call.Y < cy ? labelY > call.Y : labelY < call.Y;
                    double[] line = inward && layout.LabelOffsets?.ContainsKey(index) == true
                        ? [call.X, call.Y, end, call.Y, end, labelY] : [call.X, call.Y, call.X, labelY, end, labelY];
                    image.Shapes.Add(new("Polyline", line, "none", "#000000", .6));
                }
                Label(image, call.Right ? left : left + width, labelY + font * .35, call.Label, font, call.Right ? "start" : "end");
                if (amount.Length > 0)
                    Label(image, call.Right ? left : left + width, labelY + font * 1.5, amount, font, call.Right ? "start" : "end");
                y += amount.Length > 0 ? step * 2 : step;
            }
        }
    }

    private static void Label(ReportVectorImage image, double x, double y, string value, double font, string anchor) =>
        image.Shapes.Add(new("Text", [x, y], "#000000", "none", 0, Text: new(value, "Arial", font, Anchor: anchor)));

    private static string Shade(string color)
    {
        if(color.Length!=7 || !int.TryParse(color.AsSpan(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var rgb))return color;
        return $"#{(int)((rgb>>16)*.78):x2}{(int)((rgb>>8&255)*.78):x2}{(int)((rgb&255)*.78):x2}";
    }
}
