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
            var candidates = entries.Where(s => s.Name.Equals("CONTENTS", StringComparison.OrdinalIgnoreCase) || s.Name == "\u0001Ole10Native" || IsPresentation(s.Name))
                .OrderBy(s => IsPresentation(s.Name) ? 2 : s.Name == "\u0001Ole10Native" ? 1 : 0).ThenBy(s => s.Name, StringComparer.Ordinal);
            var failures = new List<string>();
            foreach (var content in candidates)
            {
                try
                {
                    if (IsPresentation(content.Name))
                    {
                        try { picture.ImageDataUrl = CrystalMetafileImage.Presentation(content.Bytes, picture.PictureAspect); }
                        catch (InvalidDataException) { picture.VectorImage = CrystalVectorMetafile.Presentation(content.Bytes, picture.PictureAspect); }
                    }
                    else if (content.Name == "\u0001Ole10Native")
                    {
                        if (content.Bytes.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(content.Bytes) != content.Bytes.Length - 4)
                            throw new InvalidDataException("Invalid Ole10Native image length.");
                        Image(content.Bytes.AsSpan(4));
                    }
                    else Image(content.Bytes);
                    picture.PictureDiagnostic = "";
                    break;
                }
                catch (InvalidDataException ex) { failures.Add($"{content.Name.TrimStart('\u0001', '\u0002')}: {ex.Message}"); }
            }
            if (string.IsNullOrEmpty(picture.ImageDataUrl) && picture.VectorImage is null)
            {
                var reason = failures.Count > 0 ? string.Join("; ", failures) : entries.Count == 0 ? "Linked picture storage is missing." : "No supported image presentation was found.";
                picture.PictureDiagnostic = $"{picture.Name} ({storage}): {reason}";
                progress?.Invoke(picture.PictureDiagnostic);
            }
            void Image(ReadOnlySpan<byte> bytes)
            {
                try { picture.ImageDataUrl = CrystalMetafileImage.Embed(bytes); }
                catch (InvalidDataException) { picture.VectorImage = CrystalVectorMetafile.Read(bytes); }
            }
        }
    }

    private static bool IsPresentation(string name) => name.Length == 11 && name.StartsWith("\u0002OlePres", StringComparison.Ordinal)
        && name[8..].All(c => c is >= '0' and <= '9');
}
