using Fx.ControlKit.Artifacts;

namespace Fx.ControlKit.Artifacts.Crystal;

/// <summary>Viewer registration for Crystal XML produced by <see cref="Fx.ControlKit.Reports.CrystalRptToXml"/>.</summary>
public sealed class CrystalXmlViewerDescriptor : ITranslatedFileViewer
{
    /// <summary><see cref="TranslatedFile.Format"/> for a converted Crystal report.</summary>
    public const string FormatKey = "crystal-xml";

    /// <summary>
    /// Extension callers pass to <see cref="Fx.ControlKit.Reports.CrystalRptToXml"/>.
    /// The converter writes the path it is given and does not invent an extension.
    /// </summary>
    public const string XmlExtension = ".xml";

    public string Format => FormatKey;
    public string Title => "Crystal report";
    public Type Component => typeof(CrystalXmlViewer);
}
