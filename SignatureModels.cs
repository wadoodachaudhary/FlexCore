using System.Globalization;
using System.Security;
using System.Text;
namespace Fx.ControlKit;
public sealed record SignaturePoint(double X,double Y);
public sealed record SignatureStroke(IReadOnlyList<SignaturePoint> Points,string Color="#202938",double Width=2);
public sealed record SignatureValue(IReadOnlyList<SignatureStroke> Strokes)
{
 public static SignatureValue Empty => new(Array.Empty<SignatureStroke>());
 public string ToSvg(double width=600,double height=180,string background="#FFFFFF")
 {
  if(!double.IsFinite(width)||!double.IsFinite(height)||width<=0||height<=0)throw new ArgumentOutOfRangeException(nameof(width));
  var text=new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {F(width)} {F(height)}\" width=\"{F(width)}\" height=\"{F(height)}\"><rect width=\"100%\" height=\"100%\" fill=\"{SecurityElement.Escape(background)}\"/>");
  foreach(var stroke in Strokes){if(!double.IsFinite(stroke.Width)||stroke.Width<=0||stroke.Points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)))throw new ArgumentException("Signature geometry must be finite.");text.Append($"<polyline fill=\"none\" stroke=\"{SecurityElement.Escape(stroke.Color)}\" stroke-width=\"{F(stroke.Width)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" points=\"{string.Join(" ",stroke.Points.Select(p=>$"{F(p.X)},{F(p.Y)}"))}\"/>");}
  return text.Append("</svg>").ToString();
 }
 private static string F(double n)=>n.ToString("0.###",CultureInfo.InvariantCulture);
}
