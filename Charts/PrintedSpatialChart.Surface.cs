using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Charts;

internal static partial class PrintedSpatialChart
{
    private static (double Min,double Max,double Step) SurfaceRange(IReadOnlyList<ChartSeries> rows)
    {
        var values=rows.SelectMany(row=>row.DataPoints).Where(p=>!p.IsEmpty && double.IsFinite(p.Value)).Select(p=>p.Value);
        var bounds=BarPrintScale.Range(Math.Min(0,values.DefaultIfEmpty().Min()),Math.Max(1,values.DefaultIfEmpty().Max()),targetIntervals:8);
        return (bounds.Minimum,bounds.Maximum,(bounds.Maximum-bounds.Minimum)/(bounds.Ticks-1));
    }

    // Connect adjacent observations only: a missing corner is a hole, not a measurement of zero.
    private static void DrawSurfaceMesh(Canvas g, IReadOnlyList<ChartSeries> rows, Func<int,double> x,
        Func<int,double> z, Func<double,double> level, Func<double,double,double,(double X,double Y)> project,
        Func<double,double,double,double> distance)
    {
        var faces = new List<(double Distance, ReportVectorShape Face)>();
        for (int row=0; row+1<rows.Count; row++)
        {
            var a=rows[row].DataPoints; var b=rows[row+1].DataPoints;
            for (int col=0; col+1<Math.Min(a.Count,b.Count); col++)
            {
                if (!Present(a[col]) || !Present(a[col+1]) || !Present(b[col+1]) || !Present(b[col])) continue;
                double x0=x(col), x1=x(col+1), z0=z(row), z1=z(row+1);
                double ya=level(a[col].Value), yb=level(a[col+1].Value), yc=level(b[col+1].Value), yd=level(b[col].Value);
                var p=project(x0,ya,z0); var q=project(x1,yb,z0); var r=project(x1,yc,z1); var s=project(x0,yd,z1);
                faces.Add((distance((x0+x1)/2,(ya+yb+yc+yd)/4,(z0+z1)/2),
                    new("Polygon",[p.X,p.Y,q.X,q.Y,r.X,r.Y,s.X,s.Y],g.Color(row),"#000000",g.Font/16)));
            }
        }
        g.Image.Shapes.AddRange(faces.OrderByDescending(f=>f.Distance).Select(f=>f.Face));
        static bool Present(ChartDataPoint p) => !p.IsEmpty && double.IsFinite(p.Value);
    }
}
