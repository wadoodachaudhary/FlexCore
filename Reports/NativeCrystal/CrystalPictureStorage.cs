using System.Buffers.Binary;

namespace Fx.ControlKit.Reports.NativeCrystal;

/// <summary>The image-only subset of Crystal OleObjectStorage, scoped to the owning report.</summary>
internal static class CrystalPictureStorage
{
    public static void Apply(CrystalReportModel model, IReadOnlyList<CrystalRptStream> streams, string prefix, Action<string>? progress)
    {
        foreach (var picture in model.DataDefinition.ReportDefinition.Areas.SelectMany(a => a.Sections)
                     .SelectMany(s => s.ReportObjects).Where(o => o.Kind == "Picture"))
        {
            var storage = prefix + "Embedding " + picture.PictureStorageIndex + "/";
            var entries = streams.Where(s => s.FullPath.StartsWith(storage, StringComparison.OrdinalIgnoreCase) &&
                !s.FullPath[storage.Length..].Contains('/')).ToList();
            var content = entries.FirstOrDefault(s => s.Name.Equals("CONTENTS", StringComparison.OrdinalIgnoreCase));
            try
            {
                if (content is not null) picture.ImageDataUrl = ReportObjectVisual.EmbedImage(content.Bytes);
                else if (entries.FirstOrDefault(s => s.Name == "\u0001Ole10Native") is { Bytes.Length: > 4 } native)
                {
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(native.Bytes);
                    if (size != native.Bytes.Length - 4) throw new InvalidDataException("Invalid Ole10Native image length.");
                    picture.ImageDataUrl = ReportObjectVisual.EmbedImage(native.Bytes.AsSpan(4));
                }
                else throw new InvalidDataException(entries.Count == 0 ? "Linked picture storage is missing." : "OLE presentation/metafile rasterization is not supported.");
            }
            catch (InvalidDataException ex)
            {
                picture.PictureDiagnostic = $"{picture.Name} ({storage}): {ex.Message}";
                progress?.Invoke(picture.PictureDiagnostic);
            }
        }
    }
}
