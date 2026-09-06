using System.Text.Json;
namespace Fx.ControlKit.Maps;
public sealed record GeoPoint(double Latitude,double Longitude);
public sealed record MapPixel(double X,double Y);
public sealed record MapMarker(string Id,GeoPoint Location,string Title,double Value=1,string Color="#2268B8");
public sealed record MapShape(string Id,string Title,IReadOnlyList<IReadOnlyList<GeoPoint>> Paths,bool Closed=true,string Fill="#b6d4ed",string Stroke="#537c9f");
public sealed record MapView(GeoPoint Center,int Zoom);
public static class MapProjection
{
 public const double MaxLatitude=85.05112878;
 public static MapPixel Project(GeoPoint point,int zoom)
 {
  if(!double.IsFinite(point.Latitude)||!double.IsFinite(point.Longitude)||zoom is <0 or >22)throw new ArgumentOutOfRangeException(nameof(point));
  var size=256*Math.Pow(2,zoom);var sin=Math.Sin(Math.Clamp(point.Latitude,-MaxLatitude,MaxLatitude)*Math.PI/180);
  return new((point.Longitude+180)/360*size,(.5-Math.Log((1+sin)/(1-sin))/(4*Math.PI))*size);
 }
 public static GeoPoint Unproject(MapPixel point,int zoom){var size=256*Math.Pow(2,zoom);return new(Math.Atan(Math.Sinh(Math.PI*(1-2*point.Y/size)))*180/Math.PI,((point.X/size*360)%360+360)%360-180);}
 public static IReadOnlyList<MapShape> ReadGeoJson(string json)
 {
  using var document=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=64});var shapes=new List<MapShape>();int points=0;
  GeoPoint Point(JsonElement node){if(++points>200000)throw new ArgumentException("GeoJSON exceeds 200,000 coordinates.");var coordinate=node.EnumerateArray().Select(n=>n.GetDouble()).ToArray();if(coordinate.Length<2||!double.IsFinite(coordinate[0])||!double.IsFinite(coordinate[1]))throw new ArgumentException("Invalid GeoJSON coordinate.");return new(coordinate[1],coordinate[0]);}
  void Geometry(JsonElement geometry,string id,string title){var type=geometry.GetProperty("type").GetString();if(type=="GeometryCollection"){foreach(var child in geometry.GetProperty("geometries").EnumerateArray())Geometry(child,id+"-"+shapes.Count,title);return;}var c=geometry.GetProperty("coordinates");switch(type){case "Polygon":shapes.Add(new(id,title,c.EnumerateArray().Select(r=>(IReadOnlyList<GeoPoint>)r.EnumerateArray().Select(Point).ToArray()).ToArray()));break;case "MultiPolygon":foreach(var polygon in c.EnumerateArray())shapes.Add(new(id+"-"+shapes.Count,title,polygon.EnumerateArray().Select(r=>(IReadOnlyList<GeoPoint>)r.EnumerateArray().Select(Point).ToArray()).ToArray()));break;case "LineString":shapes.Add(new(id,title,[c.EnumerateArray().Select(Point).ToArray()],false,"none"));break;case "MultiLineString":shapes.Add(new(id,title,c.EnumerateArray().Select(r=>(IReadOnlyList<GeoPoint>)r.EnumerateArray().Select(Point).ToArray()).ToArray(),false,"none"));break;default:throw new ArgumentException($"Geometry type {type} is not a shape layer. Use markers for point features.");}}
  void Feature(JsonElement feature){var id=feature.TryGetProperty("id",out var key)?key.ToString():Guid.NewGuid().ToString("N");var title=feature.TryGetProperty("properties",out var props)&&props.ValueKind==JsonValueKind.Object&&props.TryGetProperty("name",out var name)?name.ToString():id;var geometry=feature.GetProperty("geometry");if(geometry.ValueKind!=JsonValueKind.Null)Geometry(geometry,id,title);}
  var root=document.RootElement;switch(root.GetProperty("type").GetString()){case "FeatureCollection":foreach(var feature in root.GetProperty("features").EnumerateArray())Feature(feature);break;case "Feature":Feature(root);break;default:Geometry(root,"shape","Shape");break;}return shapes;
 }
}
