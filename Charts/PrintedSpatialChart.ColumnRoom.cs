using System.Globalization;
using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Charts;

internal static partial class PrintedSpatialChart
{
    // Square risers and the three slabs share the same world coordinates. Apply the saved eye
    // after the affine camera, then map signed graph coordinates to the output viewport.
    private static bool DrawRoom(Canvas g, ReportAnalysisDefinition d, IReadOnlyList<ChartSeries> series, ReportSpatialColumnView view)
    {
        var style = d.SpatialStyle!;
        var m = style.CameraValues;
        var volume = style.VolumeValues;
        if (m.Length != 12 || volume.Length != 3 || !view.IsValid || view.EyeZ >= 0) return false;
        var width = volume[0] * 4; var height = volume[1] * 4; var depth = volume[2] * 4;
        var left = view.WallX / width; var floor = view.FloorY / height; var back = 1 - view.WallZ / depth;
        if (left >= 1 || floor >= 1 || back <= 0) return false;
        double Distance(double x, double y, double z) => (x-.5)*width*m[2]+(y-.5)*height*m[5]+(z-.5)*depth*m[8]+m[11]-view.EyeZ;
        if ((from x in new[] {0, 1} from y in new[] {0, 1} from z in new[] {0, 1} select Distance(x,y,z)).Min() <= 1) return false;
        (double X,double Y) P(double x,double y,double z)
        {
            var scale = -view.EyeZ / Distance(x,y,z);
            x=(x-.5)*width; y=(y-.5)*height; z=(z-.5)*depth;
            var u=(x*m[0]+y*m[3]+z*m[6]+m[9]-view.EyeX)*scale+view.PanX;
            var v=(x*m[1]+y*m[4]+z*m[7]+m[10]-view.EyeY)*scale+view.PanY;
            return (g.Width*(.5+u/32768),g.Height*(.5-v/32768));
        }

        // This wall arrangement is for a camera looking into the open front/right/top of the room.
        // Keep the existing projection for a drawing viewed through the back of a wall.
        static double Cross((double X,double Y) a,(double X,double Y) b,(double X,double Y) c) =>
            (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        if (Cross(P(0,0,0),P(1,0,0),P(1,1,0)) >= 0 || Cross(P(1,0,0),P(1,0,1),P(1,1,1)) >= 0 ||
            Cross(P(0,1,0),P(1,1,0),P(1,1,1)) >= 0) return false;

        void Face((double X,double Y) a,(double X,double Y) b,(double X,double Y) c,(double X,double Y) e,string fill,string stroke)
        {
            if (Cross(a,b,c) < 0) g.Image.Shapes.Add(new("Polygon",[a.X,a.Y,b.X,b.Y,c.X,c.Y,e.X,e.Y],fill,stroke,g.Font/144));
        }
        void Solid(double x,double xx,double y,double yy,double z,double zz,string color,bool wall)
        {
            string front=Shade(color,.8), side=Shade(color,.6), outline=wall?"#000000":"none";
            Face(P(x,y,z),P(xx,y,z),P(xx,yy,z),P(x,yy,z),front,outline);
            Face(P(x,yy,z),P(xx,yy,z),P(xx,yy,zz),P(x,yy,zz),color,outline);
            Face(P(xx,y,z),P(xx,y,zz),P(xx,yy,zz),P(xx,yy,z),side,outline);
        }
        void Edge((double X,double Y) a,(double X,double Y) b)=>
            g.Image.Shapes.Add(new("Polyline",[a.X,a.Y,b.X,b.Y],"none","#000000",g.Font/9));
        bool surface = style.Family == 6;
        var range=surface ? SurfaceRange(series) : ColumnRange(series);
        int ticks=(int)Math.Round((range.Max-range.Min)/range.Step);
        double Level(double value)=>floor+(1-floor)*(Math.Clamp(value,range.Min,range.Max)-range.Min)/(range.Max-range.Min);
        double font=g.Font;
        g.Text(g.Width*.5,g.Height*.083,d.Title,font*2,bold:true);
        if (view.ShowFloor) Solid(0,1,0,floor,0,1,"#ffffff",true);
        if (view.ShowLeft)
        {
            Solid(0,left,floor,1,0,1,"#ffffff",true);
            for(int j=0;j<=ticks;j++)
            {
                double y=floor+(1-floor)*j/ticks;
                Edge(P(0,y,0),P(left,y,0)); Edge(P(left,y,0),P(left,y,1));
            }
        }
        if (view.ShowBack)
        {
            Solid(left,1,floor,1,back,1,"#ffffff",true);
            for(int j=0;j<=ticks;j++)
            {
                double y=floor+(1-floor)*j/ticks;
                Edge(P(left,y,back),P(1,y,back));Edge(P(1,y,back),P(1,y,1));
            }
        }

        // The surface binding stores depth groups in the points and category groups in the
        // series; risers store the opposite. Keep the report bindings and matrix holes intact.
        var points=(surface?series.MaxBy(s=>s.DataPoints.Count):series.FirstOrDefault())?.DataPoints;
        int count=series.Select(s=>s.DataPoints.Count).DefaultIfEmpty().Max();
        double slotX=(1-left)/Math.Max(1,surface?count:series.Count),slotZ=back/Math.Max(1,surface?series.Count:count);
        double DepthCenter(int s)=>left+(s+.5)*slotX;
        double CategoryCenter(int i)=>(i+.5)*slotZ;
        if (surface)
        {
            DrawSurfaceMesh(g,series,DepthCenter,CategoryCenter,Level,P,Distance);
        }
        else
        {
            double sideWorld=Math.Min(slotX*width,slotZ*depth)*(1-view.GapPercent/100d);
            double halfX=sideWorld/width/2, halfZ=sideWorld/depth/2;
            var zero=Level(0);
            var bars=from s in Enumerable.Range(0,series.Count) from i in Enumerable.Range(0,series[s].DataPoints.Count)
                     let point=series[s].DataPoints[i] where !point.IsEmpty && double.IsFinite(point.Value)
                     let x=DepthCenter(s) let z=CategoryCenter(i)
                     orderby Distance(x,zero,z) descending
                     select (x,z,point.Value,Color:g.Color(series.Count==1?i:s));
            foreach(var bar in bars)
            {
                var y=Level(bar.Value);
                Solid(bar.x-halfX,bar.x+halfX,Math.Min(zero,y),Math.Max(zero,y),bar.z-halfZ,bar.z+halfZ,bar.Color,false);
            }
        }

        for(int j=0;j<=ticks;j++)
        {
            var y=floor+(1-floor)*j/ticks;var a=P(0,y,0);var b=P(1,y,1);
            var label=(range.Min+j*range.Step).ToString("0.##",CultureInfo.CurrentCulture);
            g.Text(a.X-font*.7,a.Y+font*.12,label,font,"end");g.Text(b.X+font*.5,b.Y+font*.12,label,font,"start");
        }
        void Captions(string[] names,Func<int,(double X,double Y)> at,bool category)
        {
            if(names.Length==0)return;
            var first=at(0);var last=at(names.Length-1);
            double dx=Math.Abs(last.X-first.X)/Math.Max(1,names.Length-1),dy=Math.Abs(last.Y-first.Y)/Math.Max(1,names.Length-1);
            var widths=names.Select(s=>ChartAxisLabelLayout.MeasureWidth(s,font));
            var advance=category?widths.Average():widths.Max();
            double stride=Math.Min(advance*.73/Math.Max(.01,dx),font*.9/Math.Max(.01,dy));
            int step=Math.Max(1,(int)Math.Ceiling(Math.Min(stride,names.Length)));
            for(int i=0;i<names.Length;i+=step)
            {
                var p=at(i);g.Text(p.X+(category?font*.3:-font*.3),p.Y+font*1.15,names[i],font,category?"start":"end");
            }
        }
        var groupNames=series.Select(s=>s.Name??"").ToArray();
        var pointNames=points?.Select(p=>p.Label).ToArray()??[];
        Captions(surface?groupNames:pointNames,i=>P(1,0,CategoryCenter(i)),true);
        var depthLabels=(surface?pointNames:groupNames).Select(name=>double.TryParse(name,out var val)?val.ToString("F2",CultureInfo.CurrentCulture):name).ToArray();
        if(!surface && d.SeriesField.Length==0 && series.Count==1 && d.Measures.Count==1)
            depthLabels[0]=d.Measures[0].Caption.Length>0?d.Measures[0].Caption:style.ValueTitle;
        Captions(depthLabels,i=>P(DepthCenter(i),0,0),false);
        var l=P(left,1,0);var r=P(1,1,back);
        g.Text(l.X,l.Y-font*1.55,style.ValueTitle,font*1.11,bold:true);g.Text(r.X,r.Y-font*1.55,style.ValueTitle,font*1.11,bold:true);
        g.Text(g.Width-font*.35,g.Height-font*2.25,style.CategoryTitle,font*1.11,"end",true);
        if(style.DepthTitle.Length>0)g.Text(font*.4,g.Height-font*2,style.DepthTitle,font*1.11,"start",true);
        return true;
    }
}
