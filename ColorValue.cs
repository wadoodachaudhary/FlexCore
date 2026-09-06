using System.Globalization;
namespace Fx.ControlKit;
public sealed record ColorValue(double Hue,double Saturation,double Brightness,double Alpha=1)
{
 public string ToHex(){double h=((Hue%360)+360)%360/60,s=Math.Clamp(Saturation,0,1),v=Math.Clamp(Brightness,0,1),c=v*s,x=c*(1-Math.Abs(h%2-1)),m=v-c;var rgb=h switch{<1=>(c,x,0d),<2=>(x,c,0d),<3=>(0d,c,x),<4=>(0d,x,c),<5=>(x,0d,c),_=>(c,0d,x)};return $"#{(int)Math.Round((rgb.Item1+m)*255):X2}{(int)Math.Round((rgb.Item2+m)*255):X2}{(int)Math.Round((rgb.Item3+m)*255):X2}"+(Alpha<1?$"{(int)Math.Round(Math.Clamp(Alpha,0,1)*255):X2}":"");}
 public static bool TryParse(string? text,out ColorValue color){color=new(0,0,0);if(text is null)return false;var value=text.Trim().TrimStart('#');if(value.Length is 3 or 4)value=string.Concat(value.Select(c=>$"{c}{c}"));if(value.Length is not (6 or 8)||!uint.TryParse(value,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var bits))return false;double a=1;if(value.Length==8){a=(bits&255)/255d;bits>>=8;}double r=((bits>>16)&255)/255d,g=((bits>>8)&255)/255d,b=(bits&255)/255d,max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),delta=max-min;double h=delta==0?0:max==r?60*((g-b)/delta%6):max==g?60*((b-r)/delta+2):60*((r-g)/delta+4);color=new((h+360)%360,max==0?0:delta/max,max,a);return true;}
}
