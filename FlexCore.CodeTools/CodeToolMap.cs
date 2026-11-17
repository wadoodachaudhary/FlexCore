using Fx.ControlKit.Llm.CodeTools;

namespace Fx.ControlKit.CodeTools;

/// <summary>Maps a shell catalog id onto a <see cref="CodeToolKind"/>. Converter and runner return false.</summary>
public static class CodeToolMap
{
    public static bool TryGetKind(string? id, out CodeToolKind kind)
    {
        if (CodeToolCatalog.TryGet(id, out var descriptor) && descriptor is not null)
        {
            kind = descriptor.Kind;
            return true;
        }

        kind = default;
        return false;
    }
}
