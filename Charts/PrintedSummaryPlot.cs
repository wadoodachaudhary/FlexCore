using Fx.ControlKit.Reports;
using System.Globalization;

namespace Fx.ControlKit.Charts;

// Saved-axis charts and charts whose categories are the grand summaries themselves.
internal static class PrintedSummaryPlot
{
    private static readonly string[] Palette = ["#0066ff","#ff0000","#ffff00","#00cc00","#00ffff","#ff00ff"];
    // A flat pie (even with a detached slice) and a 3D riser without its saved camera print as ordinary charts.
    private static bool Spatial(ReportAnalysisDefinition d) => d.SpatialStyle is { Family: not (3 or 5) } or { Family: 5, CameraValues.Length: 12 };
    internal static bool Supports(ReportAnalysisDefinition d) => d.ChartType == ChartType.StackedBar100 && !d.ChartThreeDimensional || Spatial(d) || d.LineStyle is not null && d.ChartType == ChartType.Line || d.PerspectivePie && d.ChartType == ChartType.Pie
        || d.HasFixedSeriesBarAxis || d.PrintAxis is not null && d.Measures.Count == 1 && d.SeriesField.Length == 0 && d.ChartType is (ChartType.Bar or ChartType.Area)
        || d.SummariesAsCategories && (d.PerspectivePie || d.ChartType == ChartType.Bar);

    internal static ReportVectorImage Draw(ReportAnalysisDefinition d, IReadOnlyList<ChartSeries> series, double w, double h)
    {
        if (d.ChartType == ChartType.StackedBar100 && !d.ChartThreeDimensional)
            return PrintedCategoryBarChart.Draw(d.ChartType, series, d.Title, w, h, d.ShowLegend,
                yAxis: new ChartAxis { TickCount = 11, Format = "0'%'" },
                rightKey: d.RightCategoryLegend ?? new("", "", false, true) { KeyBySeries = true });
        if (Spatial(d)) return PrintedSpatialChart.Draw(d, series, w, h);
        if (d.LineStyle is not null && d.ChartType == ChartType.Line) return PrintedLineChart.Draw(d, series, w, h);
        if (d.PerspectivePie && !d.SummariesAsCategories) return PrintedPerspectivePie.Draw(d, series, w, h);
        if (d.PerspectivePie && series is [{ DataPoints: { Count: > 0 } points0 }] && points0.All(p => p is not null && (p.IsEmpty || p.Value == 0)))
            return PrintedStageChart.DrawZeroPie(series[0], d.Title, w, h, d.ShowLegend);
        if (d.HasFixedSeriesBarAxis && d.PrintAxis is { } fixedAxis)
        {
            var key = d.RightCategoryLegend ?? new(fixedAxis.CategoryTitle, fixedAxis.ValueTitle, false, fixedAxis.Grid);
            var paint = key.Drawing ?? new ReportBarDrawing("", "#ffffff", null, null, null, fixedAxis.Frame);
            var valueAxis = new ChartAxis { Min = fixedAxis.Minimum, Max = fixedAxis.Maximum, TickCount = fixedAxis.Intervals + 1,
                Format = fixedAxis.Format, Title = fixedAxis.ValueTitle, ShowGridLines = fixedAxis.Grid };
            return PrintedCategoryBarChart.Draw(d.ChartType, series, d.Title, w, h, d.ShowLegend, yAxis: valueAxis,
                rightKey: key with { Drawing = paint, KeyBySeries = true });
        }
        var image = new ReportVectorImage { Width=w, Height=h };
        var points = series.FirstOrDefault()?.DataPoints ?? [];
        var summaries = d.SummariesAsCategories;
        var f = Math.Clamp(Math.Min(h*.036,w*.017),5,12);
        void Box(double x,double y,double width,double height,string fill,string border="none") =>
            image.Shapes.Add(new("Rectangle",[x,y,width,height],fill,border,.65));
        void Line(double x,double y,double x2,double y2,string c="#000000") =>
            image.Shapes.Add(new("Polyline",[x,y,x2,y2],"none",c,.6));
        void Label(double x,double y,string text,double size,string anchor="middle",bool bold=false,string color="#000000",double angle=0)
        {
            var a=angle*Math.PI/180;
            image.Shapes.Add(new("Text",angle==0?[x,y]:[0,0],color,"none",0,Transform:angle==0?null:[Math.Cos(a),Math.Sin(a),-Math.Sin(a),Math.Cos(a),x,y],
                Text:new(text,"Arial",size,bold?700:400,Anchor:anchor)));
        }
        var pieColors = d.PiePalette ?? Palette;
        void Key(IReadOnlyList<string> labels)
        {
            if(!d.ShowLegend || labels.Count==0)return;
            var fs=Math.Clamp(Math.Min(w*.015,h*.033),5,10); var x=w*.688; var y=h*.54-(labels.Count+1)*fs*.67;
            Box(x,y,w*.304,(labels.Count+.7)*fs*1.42,"#ffffff","#000000");
            for(var i=0;i<labels.Count;i++){
                if(i<points.Count)Box(x+fs*.65,y+fs*(.65+i*1.42),fs*.83,fs*.83,pieColors[i % pieColors.Length],"#000000");
                Label(x+fs*2.1,y+fs*(1.4+i*1.42),labels[i],fs,"start");
            }
        }
        Box(0,0,w,h,"#ffffff");
        if(d.PerspectivePie){
            Label(w*.5,h*.085,d.Title,f*1.8,bold:true,color:"#ff0000");
            var total=points.Where(p=>!p.IsEmpty).Sum(p=>p.Value);
            var cx=w*(d.ShowLegend ? .365 : .5); var cy=h*.51;var rx=Math.Min(w*.23,h*.30);var ry=rx*.78;var depth=ry*.26;
            var start=-Math.PI/2;var faces=new List<ReportVectorShape>();var keys=new List<string>();
            for(var i=0;i<points.Count;i++){
                var p=points[i]; var fraction=total>0&&!p.IsEmpty?p.Value/total:0;
                var end=start+Math.PI*2*fraction;var poly=new List<double>{cx,cy};
                int steps=Math.Max(1,(int)Math.Ceiling(120*fraction));
                for(int k=0;k<=steps;k++){var t=start+(end-start)*k/steps; poly.Add(cx+rx*Math.Cos(t));poly.Add(cy+ry*Math.Sin(t));}
                if(fraction>0){
                    faces.Add(new("Polygon",poly.ToArray(),pieColors[i % pieColors.Length],"#000000",.55));
                    var front=new List<double>();
                    for(int turn=0;turn<2;turn++){
                        var lo=Math.Max(start,turn*2*Math.PI);var hi=Math.Min(end,(turn*2+1)*Math.PI);if(hi<=lo)continue;
                        front.Clear();for(int k=0;k<=steps;k++){var t=lo+(hi-lo)*k/steps;front.Add(cx+rx*Math.Cos(t));front.Add(cy+ry*Math.Sin(t));}
                        for(int k=steps;k>=0;k--){var t=lo+(hi-lo)*k/steps;front.Add(cx+rx*Math.Cos(t));front.Add(cy+ry*Math.Sin(t)+depth);}
                        var rgb=Convert.ToInt32(pieColors[i % pieColors.Length][1..],16);
                        image.Shapes.Add(new("Polygon",front.ToArray(),$"#{(rgb>>16)*3/4:x2}{(rgb>>8&255)*3/4:x2}{(rgb&255)*3/4:x2}","#000000",.5));
                    }
                }
                keys.Add($"{p.Label}    {fraction:P1}".Replace(" %","%"));start=end;
            }
            image.Shapes.AddRange(faces);
            start=-Math.PI/2;
            foreach(var p in points.Where(p=>!p.IsEmpty&&p.Value>0)){
                var delta=2*Math.PI*p.Value/total; var mid=start+delta/2;var up=Math.Sin(mid)<0;
                var x=cx+rx*Math.Cos(mid);var y=cy+ry*Math.Sin(mid)+(up?0:depth);
                Line(x,y,x,y+(up?-1:1)*f*2.2);Label(x,y+(up?-2.4:3)*f,p.Label,f*.79);
                start+=delta;
            }
            keys.Add("Total:                 "+(total>0?"100.0%":"0.0%"));Key(keys);return image;
        }
        var axis=d.PrintAxis;
        var max=axis?.Maximum??BarPrintScale.Range(0,Math.Max(1,points.Select(p=>p.Value).DefaultIfEmpty().Max()),targetIntervals:7).Maximum;
        var min=axis?.Minimum??0; var n=axis?.Intervals??7;
        var left=w*(summaries ? .095:.163);var right=w*(d.ShowLegend ? .66 : .966);
        var top=h*(summaries ? .20:.17);var bottom=h*(summaries ? .88:.80);
        if(axis?.ValueTitle.Length>0 && (axis.Grid || d.ChartType!=ChartType.Area))left=w*.156;
        if (axis?.Frame is { IsValid: true } frame)
        {
            left = frame.Left*w; right = frame.Right*w; top = frame.Top*h; bottom = frame.Bottom*h;
        }
        // A lone point has no filled area. The classic fixed-axis print profile
        // emits its frame, ticks and category only; the editable title is retained.
        bool axesOnly = axis is not null && d.ChartType == ChartType.Area && points.Count(p=>!p.IsEmpty) < 2;
        var titleVisible = !axesOnly && d.Title.Length != 0;
        if(titleVisible) Label(w*.50,h*.09,d.Title,summaries ? f*1.7 : f*2.1,bold:true,color:summaries?"#ff0000":"#000000");
        Box(left,top,right-left,bottom-top,"#ffffff","#000000");
        double Y(double v)=>bottom-(v-min)*(bottom-top)/(max-min);
        for(int i=0;i<=n;i++){
            var value=min+(max-min)*i/n;var y=Y(value);
            if(i>0&&i<n&&(axis?.Grid??true)) Line(left,y,right,y);
            Label(left-f*.6,y+f*.36,value.ToString(axis?.Format??"0.##",CultureInfo.CurrentCulture),f*.94,"end");
        }
        var step=(right-left)/Math.Max(1,points.Count);
        if (d.ChartType == ChartType.Area)
        {
            // Missing points delimit separate areas instead of being interpreted as zero.
            for (int start = 0; start < points.Count;)
            {
                if (points[start].IsEmpty) { start++; continue; }
                int end = start + 1;
                while (end < points.Count && !points[end].IsEmpty) end++;
                if (end-start >= 2)
                {
                    var floor = Y(Math.Clamp(0,min,max));
                    var polygon = new List<double>{left+(start+.5)*step,floor};
                    for (int i=start;i<end;i++){polygon.Add(left+(i+.5)*step);polygon.Add(Y(Math.Clamp(points[i].Value,min,max)));}
                    polygon.Add(left+(end-.5)*step);polygon.Add(floor);
                    image.Shapes.Add(new("Polygon",polygon.ToArray(),Palette[0],"none",0));
                }
                start=end;
            }
        }
        // Crystal turns the category captions of a bar chart only when they do not fit their slots.
        var rotate=d.ChartType==ChartType.Bar&&points.Any(p=>ChartAxisLabelLayout.MeasureWidth(p.Label,f)>step-f*.3);
        for(int i=0;i<points.Count;i++){
            var p=points[i];var x=left+(i+.5)*step;
            if(!p.IsEmpty&&d.ChartType==ChartType.Bar){
                var baseline=Y(Math.Clamp(0,min,max));var y=Y(Math.Clamp(p.Value,min,max));
                if(Math.Abs(baseline-y)>.001)Box(x-step*.44,Math.Min(y,baseline),step*.88,Math.Abs(baseline-y),Palette[summaries?i%Palette.Length:0],summaries?"#000000":"none");
            }
            if(!summaries)Label(x,bottom+(axesOnly ? .82*f : rotate ? f : f*1.4),p.Label,axesOnly ? f*.62 : f,rotate?"end":"middle",angle:rotate?-45:0);
        }
        if(axis is not null && !axesOnly){
            Label(w*.51,h*.99,axis.CategoryTitle,f*1.35,bold:true);
            Label(left-f*8,(top+bottom)/2,axis.ValueTitle,f*1.35,bold:true,angle:-90);
        }
        if(summaries)Key(points.Select(p=>p.Label).ToArray());else SideKey(points.Select(p=>p.Label).ToArray());return image;
        // The key of a saved-axis chart stands beside the plot, centred on it and fitted to its captions, never over the plot.
        void SideKey(IReadOnlyList<string> labels)
        {
            if(!d.ShowLegend || labels.Count==0)return;
            var boxWidth=Math.Min(w-right-f*2.4,f*4.05+labels.Max(l=>ChartAxisLabelLayout.MeasureWidth(l,f)));
            if(boxWidth<f*3)return;
            var boxHeight=(labels.Count*1.42+1.12)*f;
            var x=right+Math.Min(w*.0395,Math.Max(f*.5,w-right-boxWidth-f*.5)); var y=(top+bottom)/2-boxHeight/2;
            Box(x,y,boxWidth,boxHeight,"#ffffff","#000000");
            for(var i=0;i<labels.Count;i++){
                Box(x+f*.85,y+f*(.8+i*1.42),f*.83,f*.83,pieColors[i % pieColors.Length],"#000000");
                Label(x+f*2.5,y+f*(1.55+i*1.42),labels[i],f,"start");
            }
        }
    }
}
