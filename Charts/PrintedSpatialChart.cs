using Fx.ControlKit.Reports;
using System.Globalization;

namespace Fx.ControlKit.Charts;

/// <summary>Vector plots for analytical chart families with numeric or spatial axes.</summary>
internal static partial class PrintedSpatialChart
{
    internal static ReportVectorImage Draw(ReportAnalysisDefinition d, IReadOnlyList<ChartSeries> series, double w, double h)
    {
        var s=d.SpatialStyle!;
        if(s.Family==5) return Columns(d,series,w,h);
        var g=new Canvas(w,h,s.Palette);
        if (s.Family == 6)
        {
            if (s.ColumnView is { } view && DrawRoom(g, d, series, view)) return g.Image;
            return PrintedStageChart.Draw(ChartType.Surface, series, d.Title, w, h, d.ShowLegend,
                d.PiePalette ?? ReportAnalysisGraphic.CrystalBarPalette);
        }
        g.Text(w*.5,h*.085,d.Title,g.Font * (s.Family == 12 ? 2 : 2.1),bold:true);
        if (s.Family == 4) { MultipleRings(g, d, series); return g.Image; }
        if (s.Family == 1) { StatusLines(g, d, series); return g.Image; }
        if (s.Family == 2) { DepthArea(g, d, series); return g.Image; }
        if (s.Family == 12 && d.Measures.Count > 1) { SummaryGauge(g, d, series); return g.Image; }
        if (s.Family == 12) { CategoryGauges(g, d, series); return g.Image; }
        if (s.Family == 11) { DualAxisLines(g, d, series); return g.Image; }
        if(s.Family==8) { Radar(g,d,series);return g.Image; }
        var f=g.Font;var frame=s.Frame;
        double l=(frame?.Left??.10)*w, r=(frame?.Right??(d.ShowLegend?.82:.98))*w,
            top=(frame?.Top??.14)*h, bottom=(frame?.Bottom??.84)*h;
        if(s.Family==15){l=w*.10;r=w*.96;top=h*.14;bottom=h*.84;}
        g.Box(l,top,r-l,bottom-top,s.Family==10?"#d8d8d8":"#ffffff","#000000");
        var raw=series.SelectMany(p=>p.DataPoints).Where(p=>!p.IsEmpty&&double.IsFinite(p.Value)).ToArray();
        var yRange=(Min:0d, Max:1d, Step:.2);
        var xRange=(Min:.4, Max:1.6, Step:.2);
        var points=new List<(double X,double Y,string Color)>();
        var bins=new List<(double Low,double High,int Count)>();
        if(s.Family==7)
        {
            points=series.SelectMany((v,i)=>v.DataPoints.Where(p=>!p.IsEmpty&&double.IsFinite(p.Value)&&double.TryParse(p.Label,NumberStyles.Float,CultureInfo.InvariantCulture,out _))
                .Select(p=>(double.Parse(p.Label,CultureInfo.InvariantCulture),p.Value,g.Color(i)))).ToList();
            if(points.Count>0){xRange=Range(points.Min(p=>p.X),points.Max(p=>p.X),false);yRange=Range(points.Min(p=>p.Y),points.Max(p=>p.Y),true);}
        }
        else if(s.Family==15 && raw.Length>0)
        {
            xRange=Range(raw.Min(p=>p.Value),raw.Max(p=>p.Value),false);
            var count=Math.Clamp((int)Math.Ceiling(Math.Sqrt(raw.Length)),8,64);var step=(xRange.Max-xRange.Min)/count;
            for(var i=0;i<count;i++) bins.Add((xRange.Min+i*step,xRange.Min+(i+1)*step,raw.Count(p=>(int)Math.Min(count-1,Math.Floor((p.Value-xRange.Min)/step+1e-9))==i)));
            yRange=Range(0,bins.Max(b=>b.Count),true);
        }
        else if(s.Family==10 && raw.Length>0) yRange=Range(0,raw.Max(p=>p.Value),true);
        if (s.Family==10 && d.PrintAxis is {} retained)
            yRange=(retained.Minimum,retained.Maximum,(retained.Maximum-retained.Minimum)/retained.Intervals);
        double X(double v)=>l+(v-xRange.Min)/(xRange.Max-xRange.Min)*(r-l);
        double Y(double v)=>bottom-(v-yRange.Min)/(yRange.Max-yRange.Min)*(bottom-top);
        for(var i=0;i<=Math.Round((yRange.Max-yRange.Min)/yRange.Step);i++)
        {
            var v=yRange.Min+i*yRange.Step;var y=Y(v);g.Line(l,y,r,y);
            g.Text(l-f*.5,y+f*.34,s.Family==10?v.ToString("C0",CultureInfo.CurrentCulture):v.ToString("0.##",CultureInfo.CurrentCulture),f,"end");
        }
        if(s.Family is 7 or 15)
        {
            for(var i=0;i<=Math.Round((xRange.Max-xRange.Min)/xRange.Step);i++)
            {
                var v=xRange.Min+i*xRange.Step;
                if(s.Family==7)g.Line(X(v),top,X(v),bottom);
                g.Text(X(v),bottom+f*1.6,v.ToString("0.##",CultureInfo.CurrentCulture),f*(s.Family==15?1.44:1));
            }
            foreach(var p in points)g.Box(X(p.X)-f*.45,Y(p.Y)-f*.45,f*.9,f*.9,p.Color);
            foreach(var b in bins.Where(b=>b.Count>0))g.Box(X(b.Low),Y(b.Count),X(b.High)-X(b.Low),bottom-Y(b.Count),g.Color(3));
        }
        else
        {
            var categories=series.FirstOrDefault()?.DataPoints??[];var slot=(r-l)/Math.Max(1,categories.Count);
            for(var i=0;i<categories.Count;i++)g.Text(l+(i+.5)*slot,bottom+f*1.8,categories[i].Label,f);
            // Incomplete saved stock bindings still have axes and a legend, but cannot supply an OHLC glyph.
            var required=d.ChartType==ChartType.HighLow?3:4;
            if(series.Count==required)
                for(var i=0;i<categories.Count;i++)
                {
                    var values=series.Select(v=>v.DataPoints.ElementAtOrDefault(i)).ToArray();
                    if(values.Any(v=>v is null||v.IsEmpty))continue;
                    var x=l+(i+.5)*slot;var vs=values.Select(v=>v!.Value).ToArray();
                    var high=d.ChartType==ChartType.HighLow?vs[0]:vs[1];var low=d.ChartType==ChartType.HighLow?vs[1]:vs[2];
                    g.Line(x,Y(low),x,Y(high),g.Color(0));
                    g.Line(x,Y(vs[^1]),x+Math.Min(f,slot*.2),Y(vs[^1]),g.Color(0));
                    if(required==4)g.Line(x-Math.Min(f,slot*.2),Y(vs[0]),x,Y(vs[0]),g.Color(0));
                }
        }
        g.Text((l+r)/2,h-f*.3,s.CategoryTitle,f,bold:true);
        g.Text(f*1.2,(top+bottom)/2,s.ValueTitle,f,bold:true,angle:-90);
        // A saved frame that reaches the right edge leaves no room for the right-hand key.
        if(d.ShowLegend && s.Family!=15 && w-r-f*1.5>=f*3)
        {
            var names=s.Family==10 ? new[]{Caption(d.Measures[0])}:series.Select(v=>v.Name??"").ToArray();
            g.Legend(names,r+f*1.2,(top+bottom)/2,w-r-f*1.5,h*.7);
        }
        return g.Image;
    }

    private static string Caption(ReportAnalysisMeasure m)=>string.IsNullOrEmpty(m.Caption)?m.Aggregate+" of "+m.Field.Trim('{','}').Split('.').Last():m.Caption;
    private static (double Min,double Max,double Step) Range(double low,double high,bool zero)
    {
        if(zero){low=Math.Min(0,low);high=Math.Max(0,high);if(low==high)high=1;}
        else if(low==high){var span=Math.Max(.5,Math.Abs(low)*.5);low-=span;high+=span;}
        var magnitude=Math.Pow(10,Math.Floor(Math.Log10((high-low)/6)));var ratio=(high-low)/6/magnitude;
        var step=magnitude*(ratio<=1?1:ratio<=2?2:ratio<=5?5:10);
        return(Math.Floor(low/step)*step,Math.Ceiling(high/step)*step,step);
    }
    private static void Radar(Canvas g,ReportAnalysisDefinition d,IReadOnlyList<ChartSeries> series)
    {
        var w=g.Width;var h=g.Height;var f=g.Font;var count=series.Select(v=>v.DataPoints.Count).DefaultIfEmpty().Max();
        if(count==0)return;
        var cx=w*.43;var cy=h*.52;var radius=Math.Min(w*.26,h*.40);
        var max=series.SelectMany(v=>v.DataPoints).Where(v=>!v.IsEmpty).Select(v=>v.Value).DefaultIfEmpty(1).Max();var range=Range(0,max,true);
        var high=range.Max;var rings=(int)Math.Round(high/range.Step);
        (double X,double Y) Point(int i,double distance){var a=(i+.5)*2*Math.PI/count;return(cx+Math.Cos(a)*distance,cy+Math.Sin(a)*distance);}
        for(var i=1;i<=rings;i++)g.Image.Shapes.Add(new("Ellipse",[cx-radius*i/rings,cy-radius*i/rings,2*radius*i/rings,2*radius*i/rings],"none","#000000",.45));
        g.Line(cx-radius,cy,cx+radius,cy);
        for(var i=0;i<=rings;i++)g.Text(cx+radius*i/rings,cy+f*.5,(i*range.Step).ToString("0.##",CultureInfo.CurrentCulture),f,"start",angle:90);
        for(var i=0;i<count;i++)
        {
            var pt=Point(i,radius);g.Line(cx,cy,pt.X,pt.Y);
            var text=Point(i,radius+f*.65);g.Text(text.X,text.Y+f*.3,series[0].DataPoints[i].Label,f,pt.X<cx?"end":"start");
        }
        foreach(var (v,k) in series.Select((v,k)=>(v,k)))
        {
            var poly=new List<double>();
            for(var i=0;i<v.DataPoints.Count;i++)
            {
                var p=v.DataPoints[i];if(p.IsEmpty)continue;var pt=Point(i,Math.Clamp(p.Value/high,0,1)*radius);
                poly.AddRange([pt.X,pt.Y]);g.Box(pt.X-f*.5,pt.Y-f*.5,f,f,g.Color(k));
            }
            if(poly.Count>2){poly.AddRange(poly.Take(2).ToArray());g.Image.Shapes.Add(new("Polyline",poly.ToArray(),"none",g.Color(k),.65));}
        }
        g.Text(cx,h*.96,d.SpatialStyle!.CategoryTitle,f,bold:true);
        g.Text(w*.02,cy,d.SpatialStyle.ValueTitle,f,bold:true,angle:-90);
        if(d.ShowLegend)g.Legend(d.Measures.Select(Caption).ToArray(),w*.81,cy,w*.18,h*.65);
    }

    private static void DualAxisLines(Canvas canvas, ReportAnalysisDefinition definition, IReadOnlyList<ChartSeries> series)
    {
        var style = definition.SpatialStyle!;
        var w = canvas.Width;
        var h = canvas.Height;
        var font = canvas.Font;
        var frame = style.Frame ?? new ReportPlotFrame(.09, .08, .90, .76);
        var left = frame.Left * w;
        var right = frame.Right * w;
        var top = frame.Top * h;
        var bottom = frame.Bottom * h;
        var all = series.SelectMany(s => s.DataPoints).Where(p => !p.IsEmpty && double.IsFinite(p.Value)).ToArray();
        var auto = BarPrintScale.Range(Math.Min(0, all.Select(p => p.Value).DefaultIfEmpty().Min()),
            Math.Max(1, all.Select(p => p.Value).DefaultIfEmpty().Max()), targetIntervals: 6);
        var primary = style.PrimaryScale is { IsValid: true } saved ? saved : new ReportChartScale(auto.Minimum, auto.Maximum, auto.Ticks - 1);
        var secondary = style.SecondaryScale is { IsValid: true } savedRight ? savedRight : primary;
        void Ticks(ReportChartScale scale, bool onRight)
        {
            for (var i = 0; i <= scale.Intervals; i++)
            {
                var v = scale.Minimum + (scale.Maximum - scale.Minimum) * i / scale.Intervals;
                var y = bottom - (bottom - top) * i / scale.Intervals;
                if (!onRight) canvas.Line(left, y, right, y, "#a59483");
                var text = Math.Abs(v) >= 1000 ? (v / 1000).ToString("0.##", CultureInfo.CurrentCulture) + "K" : v.ToString("0.##", CultureInfo.CurrentCulture);
                canvas.Text(onRight ? right + font * .4 : left - font * .4, y + font * .36, text, font,
                    onRight ? "start" : "end", color: onRight ? "#8000ff" : "#000000");
            }
        }
        canvas.Box(left, top, right-left, bottom-top, "#ffffff", "#333333");
        Ticks(primary, false);
        Ticks(secondary, true);
        var categories = series.OrderByDescending(s => s.DataPoints.Count).FirstOrDefault()?.DataPoints ?? [];
        double X(int i) => left + (i + .5) * (right - left) / Math.Max(1, categories.Count);
        var labels = categories.Select(p => DateTime.TryParse(p.Label, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)
            ? date.ToString("M/d", CultureInfo.CurrentCulture) : p.Label).ToArray();
        foreach (var i in ChartAxisLabelLayout.Select(labels, X, font, -45, "end", bold: false, axis: null))
            canvas.Text(X(i), bottom + font, labels[i], font, "end", angle: -45);
        for (var index = 0; index < series.Count; index++)
        {
            var scale = index == series.Count - 1 && index > 0 ? secondary : primary;
            var color = canvas.Color(index);
            var path = new List<double>();
            void EndPath()
            {
                if (path.Count >= 4) canvas.Image.Shapes.Add(new("Polyline", path.ToArray(), "none", color, 1));
                else if (path.Count == 2)
                {
                    // A single category is drawn as a marker inside the plot, even at a fixed scale boundary.
                    var radius = font * .48;
                    var y = Math.Clamp(path[1], top + radius, bottom - radius);
                    canvas.Box(path[0] - radius, y - radius, radius * 2, radius * 2, color);
                }
                path.Clear();
            }
            for (var i = 0; i < series[index].DataPoints.Count; i++)
            {
                var p = series[index].DataPoints[i];
                if (p.IsEmpty || !double.IsFinite(p.Value)) { EndPath(); continue; }
                path.Add(X(i));
                path.Add(bottom - (p.Value - scale.Minimum) / (scale.Maximum - scale.Minimum) * (bottom - top));
            }
            EndPath();
        }
        if (definition.ShowLegend && series.Count > 0)
        {
            var names = series.Select(s => s.Name.Trim('{', '}').Split('.').Last()).ToArray();
            var slot = w / series.Count;
            var fs = Math.Min(font, slot / Math.Max(4, names.Max(n => n.Length) * .55 + 2));
            for (var i = 0; i < names.Length; i++)
            {
                var x = (i + .5) * slot - names[i].Length * fs * .24;
                canvas.Line(x-fs*1.5, h-fs*.7, x-fs*.3, h-fs*.7, canvas.Color(i));
                canvas.Text(x, h-fs*.38, names[i], fs, "start");
            }
        }
    }

    private sealed class Canvas(double w,double h,string palette)
    {
        public double Width=>w;public double Height=>h;
        public double Font=>Math.Min(w/53,h/32);
        public ReportVectorImage Image {get;}=new(){Width=w,Height=h};
        private readonly string[] colors=palette.Split(' ',StringSplitOptions.RemoveEmptyEntries) is {Length:>0} c?c:ReportAnalysisGraphic.CrystalBarPalette;
        public string Color(int i)=>colors[i%colors.Length];
        public void Box(double x,double y,double width,double height,string fill,string border="none")=>Image.Shapes.Add(new("Rectangle",[x,y,width,height],fill,border,.5));
        public void Poly(double[] xy,string fill,string border="#000000")=>Image.Shapes.Add(new("Polygon",xy,fill,border,.5));
        public void Line(double x,double y,double x2,double y2,string color="#000000")=>Image.Shapes.Add(new("Polyline",[x,y,x2,y2],"none",color,.45));
        public void Text(double x,double y,string text,double size,string anchor="middle",bool bold=false,double angle=0,string color="#000000")
        {
            var a=angle*Math.PI/180;Image.Shapes.Add(new("Text",[0,0],color,"none",0,
                Transform:[Math.Cos(a),Math.Sin(a),-Math.Sin(a),Math.Cos(a),x,y],Text:new(text,"Arial",size,bold?700:400,Anchor:anchor)));
        }
        public void Legend(IReadOnlyList<string> names,double x,double cy,double width,double height)
        {
            if(names.Count==0)return;var f=Math.Min(Font,Math.Min(height/((names.Count+.8)*1.3),width/Math.Max(8,names.Max(n=>n.Length)*.55+3)));
            width=Math.Min(width,(names.Max(n=>n.Length)*.52+3)*f);
            var hh=(names.Count+.8)*1.3*f;var y=cy-hh/2;Box(x,y,width,hh,"#ffffff","#000000");
            for(var i=0;i<names.Count;i++){Box(x+f*.7,y+f*(.6+i*1.3),f*.8,f*.8,Color(i));Text(x+f*2,y+f*(1.3+i*1.3),names[i],f,"start");}
        }
    }
}
