using System.Globalization;
using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Charts;

// Legacy custom line presentation: inset plot walls, square markers and a plain right-hand key.
internal static class PrintedLineChart
{
    private static readonly string[] Palette = ["#3a6598","#efa252","#009778","#e45520","#a22b64","#ffd35f"];
    internal static ReportVectorImage Draw(ReportAnalysisDefinition definition,IReadOnlyList<ChartSeries> series,double w,double h)
    {
        var image=new ReportVectorImage{Width=w,Height=h};
        var style=definition.LineStyle!;var font=Math.Min(w/54,h/32.8);
        void Box(double x,double y,double width,double height,string fill)=>image.Shapes.Add(new("Rectangle",[x,y,width,height],fill,"none",0));
        void Line(double x,double y,double x2,double y2,string color,double stroke=.5)=>image.Shapes.Add(new("Polyline",[x,y,x2,y2],"none",color,stroke));
        void Text(double x,double y,string s,double size,string anchor="middle",bool bold=false)=>image.Shapes.Add(new("Text",[x,y],"#000000","none",0,Text:new(s,"Arial",size,bold?700:400,Anchor:anchor)));
        Box(0,0,w,h,"#ffffff");Text(w*.5,h*.08,definition.Title,font*2,bold:true);
        var values=series.SelectMany(s=>s.DataPoints).Where(p=>!p.IsEmpty && double.IsFinite(p.Value)).Select(p=>p.Value).ToArray();
        var scale=BarPrintScale.Range(Math.Min(0,values.DefaultIfEmpty().Min()),Math.Max(0,values.DefaultIfEmpty().Max()),targetIntervals:8);
        var min=scale.Minimum;var max=scale.Maximum;var intervals=Math.Max(1,scale.Ticks-1);
        // No rows, or only zeros, leave no value range to scale.
        if(!double.IsFinite(min)||!double.IsFinite(max)||min==max){min=-6;max=6;intervals=6;}
        var left=w*.1235;var right=w*(definition.ShowLegend ? .709 : .955);var top=h*.114;var bottom=h*.853;
        var dx=w*.022;var dy=h*.022;
        Box(left,top,right-left,bottom-top,"#f0f0f4");
        image.Shapes.Add(new("Polygon",[left,top,left,bottom,left-dx,bottom+dy,left-dx,top+dy],"#bfbfc3","none",0));
        image.Shapes.Add(new("Polygon",[left-dx,bottom+dy,right-dx,bottom+dy,right,bottom,left,bottom],"#8f8f92","none",0));
        double Y(double v)=>bottom+dy-(v-min)*(bottom-top)/(max-min);
        for(int i=0;i<=intervals;i++)
        {
            var value=min+(max-min)*i/intervals;var y=Y(value);
            if(style.Grid)Line(left,y-dy,right+font*.5,y-dy,"#9d9db8",.4);
            Text(left-dx-font*.45,y+font*.32,value.ToString(style.ValueFormat,CultureInfo.CurrentCulture),font,"end");
        }
        var labels=series.OrderByDescending(s=>s.DataPoints.Count).FirstOrDefault()?.DataPoints ?? [];
        double X(int i)=>left-dx+(i+.5)*(right-left)/Math.Max(1,labels.Count);
        foreach(var i in ChartAxisLabelLayout.Select(labels.Select(p=>p.Label).ToArray(),X,font,0,"middle",bold:false,axis:null))
            Text(X(i),bottom+dy+font*1.45,labels[i].Label,font);
        var legendExtra=0d;
        for(int s=0;s<series.Count;s++)
        {
            var color=series[s].Color??Palette[s%Palette.Length];var path=new List<double>();
            void Flush(){if(path.Count>=4)image.Shapes.Add(new("Polyline",path.ToArray(),"none",color,1));path.Clear();}
            for(int i=0;i<series[s].DataPoints.Count;i++)
            {
                var p=series[s].DataPoints[i];if(p.IsEmpty||!double.IsFinite(p.Value)){Flush();continue;}
                var x=X(i);var y=Y(p.Value);path.AddRange([x,y]);
                if(style.Markers)Box(x-font*.365,y-font*.365,font*.73,font*.73,color);
            }
            Flush();
            if(!definition.ShowLegend)continue;
            var xkey=right+font*2.3;var ykey=(top+bottom)/2+(s-(series.Count-1)*.5)*font*2.7+legendExtra;
            Line(xkey-font*.3,ykey,xkey+font*1.2,ykey,color,.8);
            if(style.Markers)Box(xkey,ykey-font*.45,font,font*.9,color);
            var caption=series[s].Name;
            var width=Math.Max(font*3,Math.Min(w*.18,w-xkey-font*1.8));var words=caption.Split(' ');var line="";int row=0;
            foreach(var word in words)
            {
                var next=line.Length==0?word:line+" "+word;
                if(line.Length>0&&next.Length*font*.47>width){Text(xkey+font*1.65,ykey+font*(.3+row*1.2),line,font,"start");line=word;row++;}else line=next;
            }
            Text(xkey+font*1.65,ykey+font*(.3+row*1.2),line,font,"start");
            legendExtra+=row*font*.7;
        }
        return image;
    }
}
