using Fx.ControlKit.Reports;
using System.Globalization;

namespace Fx.ControlKit.Charts;

internal static partial class PrintedSpatialChart
{
    private static ReportVectorImage Columns(ReportAnalysisDefinition d,IReadOnlyList<ChartSeries> series,double w,double h)
    {
        var s=d.SpatialStyle!;var g=new Canvas(w,h,s.Palette);var f=g.Font;
        if (s.ColumnView is { } view && DrawRoom(g, d, series, view)) return g.Image;
        g.Text(w*.5,h*.075,d.Title,f*2.1,bold:true);
        var matrix=s.CameraValues;
        var volume=s.VolumeValues is { Length:3 } size ? size : new[]{1000d,1000d,1000d};
        if(matrix.Length!=12) throw new InvalidDataException("A spatial column chart requires its saved camera transform.");
        // The graph coordinate system is X=depth, Y=value and Z=category. The saved
        // affine camera is column-major; the final division supplies perspective.
        (double X,double Y) Project(double depth,double value,double category)
        {
            var x=depth*volume[0];var y=value*volume[1];var z=category*volume[2];
            var distance=matrix[2]*x+matrix[5]*y+matrix[8]*z+matrix[11];
            if(Math.Abs(distance)<1)throw new InvalidDataException("Chart camera intersects its plotting volume.");
            return ((matrix[0]*x+matrix[3]*y+matrix[6]*z+matrix[9])/distance,
                -(matrix[1]*x+matrix[4]*y+matrix[7]*z+matrix[10])/distance);
        }
        var corners=(from x in new[]{-1d,1d} from y in new[]{-1d,1d} from z in new[]{-1d,1d} select Project(x,y,z)).ToArray();
        var x0=corners.Min(p=>p.X);var x1=corners.Max(p=>p.X);var y0=corners.Min(p=>p.Y);var y1=corners.Max(p=>p.Y);
        var aspect=s.VolumeValues.Length==3 ? Math.Clamp(Math.Sqrt(volume[2]/volume[1]),.6,1) : 1;
        var scaleX=w*.80*aspect/(x1-x0);var scaleY=h*.72/(y1-y0);var offsetX=w*.50-(x0+x1)*scaleX/2;var offsetY=h*.52-(y0+y1)*scaleY/2;
        (double X,double Y) P(double depth,double value,double category){var p=Project(depth,value,category);return(p.X*scaleX+offsetX,p.Y*scaleY+offsetY);}
        double[] Face(params (double X,double Y)[] pts)=>pts.SelectMany(p=>new[]{p.X,p.Y}).ToArray();
        void Edge((double X,double Y)a,(double X,double Y)b)=>g.Line(a.X,a.Y,b.X,b.Y);
        g.Poly(Face(P(-1,-1,-1),P(-1,1,-1),P(-1,1,1),P(-1,-1,1)),"#999999");
        g.Poly(Face(P(-1,-1,1),P(-1,1,1),P(1,1,1),P(1,-1,1)),"#c8c8c8");
        g.Poly(Face(P(-1,-1,-1),P(-1,-1,1),P(1,-1,1),P(1,-1,-1)),"#ffffff");
        var range = ColumnRange(series);
        var tickCount=(int)Math.Round((range.Max-range.Min)/range.Step);
        for(var i=0;i<=tickCount;i++)
        {
            var y=-1+2d*i/tickCount;var a=P(-1,y,-1);var b=P(-1,y,1);var c=P(1,y,1);Edge(a,b);Edge(b,c);
            var label=(range.Min+i*range.Step).ToString("0.##",CultureInfo.CurrentCulture);
            g.Text(a.X-f*.7,a.Y+f*.3,label,f,"end");g.Text(c.X+f*.5,c.Y+f*.3,label,f,"start");
        }
        var count=series.Select(v=>v.DataPoints.Count).DefaultIfEmpty().Max();
        for(var group=0;group<series.Count;group++)
        for(var i=0;i<series[group].DataPoints.Count;i++)
        {
            var point=series[group].DataPoints[i];if(point.IsEmpty)continue;
            var z0=-1+2d*(i+.10)/count;var z1=-1+2d*(i+.91)/count;
            // Spread the second grouping over the depth axis in the same units as
            // the projected floor. A single series remains centered on the floor.
            var x0b=series.Count==1 ? -.12 : -1+2*(group+.12)/series.Count;
            var x1b=series.Count==1 ? .12 : -1+2*(group+.88)/series.Count;
            var y=-1+2*(Math.Clamp(point.Value,range.Min,range.Max)-range.Min)/(range.Max-range.Min);
            var color=g.Color(series.Count==1?i:group);
            g.Poly(Face(P(x1b,-1,z0),P(x1b,y,z0),P(x1b,y,z1),P(x1b,-1,z1)),Shade(color,.72),"none");
            g.Poly(Face(P(x0b,-1,z0),P(x0b,y,z0),P(x1b,y,z0),P(x1b,-1,z0)),Shade(color,.9),"none");
            g.Poly(Face(P(x0b,y,z0),P(x0b,y,z1),P(x1b,y,z1),P(x1b,y,z0)),color,"none");
        }
        var labels=series.FirstOrDefault()?.DataPoints??[];
        var step=series.Count>1 ? Math.Max(1,(int)Math.Ceiling(labels.Count*f*3/(w*.41))) : 1;
        for(var i=0;i<labels.Count;i+=step)
        {
            var p=P(1,-1,-1+2d*(i+.5)/Math.Max(1,count));g.Text(p.X,p.Y+f*1.5,labels[i].Label,f*.92);
        }
        if(series.Count>1)
        {
            var stride=Math.Max(1,(int)Math.Ceiling(series.Count*f*3/(w*.36)));
            for(var j=0;j<series.Count;j+=stride)
            {
                var p=P(-1+2d*(j+.5)/series.Count,-1,-1);
                var name=double.TryParse(series[j].Name,out var v)?v.ToString("F2",CultureInfo.CurrentCulture):series[j].Name??"";
                g.Text(p.X,p.Y+f*1.5,name,f*.92);
            }
            g.Text(w*.12,h*.96,s.DepthTitle,f,bold:true);
        }
        var left=P(-1,1,-1);var right=P(1,1,1);
        g.Text(left.X,left.Y-f*1.4,s.ValueTitle,f,bold:true);g.Text(right.X,right.Y-f*1.4,s.ValueTitle,f,bold:true);
        g.Text(w*.89,h*.96,s.CategoryTitle,f,bold:true);
        return g.Image;
    }
    private static (double Min, double Max, double Step) ColumnRange(IReadOnlyList<ChartSeries> series)
    {
        var values=series.SelectMany(v=>v.DataPoints).Where(p=>!p.IsEmpty && double.IsFinite(p.Value)).Select(p=>p.Value).ToArray();
        var range = Range(values.DefaultIfEmpty(0).Min(), values.DefaultIfEmpty(1).Max(), true);
        if (series.Count > 1)
        {
            var bounds = BarPrintScale.Range(Math.Min(0, values.DefaultIfEmpty().Min()), Math.Max(1, values.DefaultIfEmpty().Max()));
            range = (bounds.Minimum, bounds.Maximum, (bounds.Maximum - bounds.Minimum) / (bounds.Ticks - 1));
        }
        else if ((range.Max-range.Min)/range.Step < 7) range=(range.Min,range.Max,range.Step/2);
        return range;
    }
    private static string Shade(string color,double fraction)=>"#"+string.Concat(new[]{1,3,5}.Select(i=>((int)(Convert.ToInt32(color.Substring(i,2),16)*fraction)).ToString("x2")));
}
