namespace Fx.ControlKit.Reports.NativeCrystal;

/// <summary>Version of the report XML the native converter writes, stamped on every <c>Report</c> element as <see cref="Attribute"/>. A stored conversion
/// without the stamp, or with an older one, came from an earlier converter: a host that keeps converted XML should convert the RPT again.</summary>
public static class CrystalReportXmlVersion
{
    public const int Current = 2;
    public const string Attribute = "FlexKitXmlVersion";

    /// <summary>The converter version stamped on a report XML file, or 0 when it has none (an earlier FlexKit conversion or another exporter).</summary>
    public static int Stored(string xmlPath)
    {
        using var reader = System.Xml.XmlReader.Create(xmlPath, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        return reader.MoveToContent() == System.Xml.XmlNodeType.Element && int.TryParse(reader.GetAttribute(Attribute), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var version) ? version : 0;
    }
}

public sealed class CrystalReportModel
{
    public required string SourcePath { get; init; }

    public string Name { get; set; } = "";

    public CrystalReportCore Core { get; } = new();

    public CrystalSummaryInformation SummaryInformation { get; set; } = new();

    public CrystalDatabaseModel Database { get; set; } = new();

    public CrystalDataDefinitionModel DataDefinition { get; set; } = new();

    public List<CrystalSubreportLinkModel> SubreportLinks { get; } = [];

    public List<CrystalReportModel> Subreports { get; } = [];

    /// <summary>Capability warnings for this report; embedded reports have their own list.</summary>
    public List<CrystalConversionDiagnostic> ConversionDiagnostics { get; } = [];
}

/// <summary>
/// One ParameterLinkItem as Crystal stores it: the subreport parameter id, the main-report field that feeds it and,
/// when the link was made with "select data in subreport based on field", that subreport field's field-manager slot.
/// </summary>
internal sealed class CrystalStoredSubreportLink
{
    public int ParameterId { get; init; }

    public string MainReportFieldName { get; set; } = "";

    /// <summary>Field-definition type of the subreport field, or -1 when the link only feeds the parameter.</summary>
    public int SubreportFieldType { get; init; } = -1;

    public int SubreportFieldIndex { get; init; } = -1;
}

public sealed class CrystalSubreportLinkModel
{
    public string LinkedParameterName { get; set; } = "";

    public string MainReportFieldName { get; set; } = "";

    public string SubreportFieldName { get; set; } = "";
}

public sealed class CrystalReportCore
{
    public int VersionMajor { get; set; }

    public int VersionMinor { get; set; }

    public int VersionPatch { get; set; }

    public string ReportName { get; set; } = "";

    public bool HasSavedData { get; set; }

    public bool EnableSaveDataWithReport { get; set; } = true;

    public bool EnableSaveSummariesWithReport { get; set; } = true;

    public int LeftMargin { get; set; } = 360;

    public int RightMargin { get; set; } = 360;

    public int TopMargin { get; set; } = 360;

    public int BottomMargin { get; set; } = 360;

    public int PageContentWidth { get; set; } = 11520;

    public int PageContentHeight { get; set; } = 15120;

    public string PaperOrientation { get; set; } = "Portrait";

    public string PaperSize { get; set; } = "PaperLetter";

    public string PaperSource { get; set; } = "Auto";

    public string PrinterDuplex { get; set; } = "Simplex";

    public string PrinterName { get; set; } = "";
}

public sealed class CrystalDatabaseModel
{
    internal bool UsesLegacyFieldNames { get; init; }

    internal List<string> ParseWarnings { get; } = [];

    public List<CrystalConnectionModel> Connections { get; } = [];

    public List<CrystalTableModel> Tables { get; } = [];

    public List<CrystalTableLinkModel> Links { get; } = [];

    public Dictionary<int, CrystalDatabaseFieldModel> FieldsByObjectId { get; } = [];

    public Dictionary<int, CrystalTableModel> TablesByObjectId { get; } = [];
}

public sealed class CrystalConnectionModel
{
    public int ObjectId { get; set; }

    public string DatabaseDll { get; set; } = "";

    public string DatabaseType { get; set; } = "";

    public string ServerName { get; set; } = "";

    public List<CrystalQePropertyModel> LogonProperties { get; } = [];

    public List<CrystalQePropertyModel> Properties { get; } = [];
}

public sealed class CrystalQePropertyModel
{
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    public List<CrystalQePropertyModel> Children { get; } = [];
}

public sealed class CrystalTableModel
{
    public int ObjectId { get; set; }

    public CrystalConnectionModel? Connection { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string QualifiedName { get; set; } = "";

    public int TableType { get; set; }

    public string Alias { get; set; } = "";

    public bool IsFlat { get; set; }

    public bool IsLinkable { get; set; }

    public string CommandText { get; set; } = "";

    public string ExternalIndexes { get; set; } = "";

    public string OverriddenName { get; set; } = "";

    public List<string> Qualifiers { get; } = [];

    public List<CrystalDatabaseFieldModel> Fields { get; } = [];
}

public sealed class CrystalDatabaseFieldModel
{
    public int ObjectId { get; set; }

    public CrystalTableModel? Table { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public int DataType { get; set; }

    public int Length { get; set; }

    public int Attributes { get; set; }

    public int Precision { get; set; }

    public string TableAlias => Table?.Alias ?? "";

    public string FormulaName => string.IsNullOrWhiteSpace(TableAlias)
        ? Name
        : "{" + TableAlias + "." + Name + "}";

    public string LongName => string.IsNullOrWhiteSpace(TableAlias)
        ? Name
        : TableAlias + "." + Name;
}

public sealed class CrystalTableLinkModel
{
    public int ObjectId { get; set; }

    public CrystalDatabaseFieldModel? FromField { get; set; }

    public CrystalDatabaseFieldModel? ToField { get; set; }

    public int LinkOperator { get; set; }

    public int JoinType { get; set; }

    public int Enforced { get; set; }
}

public sealed class CrystalDataDefinitionModel
{
    internal List<string> ParseWarnings { get; } = [];

    public CrystalReportDefinitionModel ReportDefinition { get; } = new();

    public string RecordSelectionFormula { get; set; } = "";

    public string GroupSelectionFormula { get; set; } = "";

    public List<CrystalFormulaFieldModel> FormulaFields { get; } = [];

    public List<CrystalSortFieldModel> SortFields { get; } = [];

    public List<CrystalGroupModel> Groups { get; } = [];

    public List<CrystalParameterFieldModel> Parameters { get; } = [];

    public List<CrystalRunningTotalFieldModel> RunningTotalFields { get; } = [];

    public List<CrystalSummaryFieldModel> SummaryFields { get; } = [];

    public List<CrystalCustomFunctionModel> CustomFunctions { get; } = [];

    /// <summary>Field-manager references by Crystal field-definition type (0 database, 1 formula, 2 summary, ...), in slot order.</summary>
    internal Dictionary<int, List<string>> FieldManagerReferences { get; } = [];

    internal int RecordSelectionFormulaSyntax { get; set; }
}

/// <summary>A report custom function (legacy Contents record 335): formula text plus its repository metadata.</summary>
public sealed class CrystalCustomFunctionModel
{
    public string Name { get; set; } = "";

    public string FormulaText { get; set; } = "";

    /// <summary>Saved base value type of the result (Crystal stores no range/array flag); 255 means the function was not compiled when saved.</summary>
    public int ValueType { get; set; } = 255;

    public int Syntax { get; set; }

    public string Category { get; set; } = "";

    public string Author { get; set; } = "";

    public string Summary { get; set; } = "";

    public List<string> ArgumentDescriptions { get; } = [];

    public List<List<string>> ArgumentDefaultValues { get; } = [];

    public List<string> CalledFunctions { get; } = [];

    /// <summary>The arguments the formula engine read from the declaration; null when it cannot compile the function.</summary>
    internal IReadOnlyList<CrystalCustomFunctionParameter>? Parameters { get; set; }
}

public sealed class CrystalFormulaFieldModel
{
    public string Name { get; set; } = "";

    public string FormulaText { get; set; } = "";

    public int ValueType { get; set; }

    public int NumberOfBytes { get; set; }

    public int Syntax { get; set; }

    public int FormulaType { get; set; }

    public string FormulaName => "{@" + Name + "}";
}

public sealed class CrystalSortFieldModel
{
    public string Field { get; set; } = "";

    public int Direction { get; set; }

    public string SortType { get; set; } = "RecordSortField";

    /// <summary>For a group sort by a summary, the 1-based number of the group whose summary it sorts; otherwise 0.</summary>
    internal int SummaryGroupNumber { get; set; }
}

public sealed class CrystalGroupModel
{
    public string ConditionField { get; set; } = "";

    /// <summary>Hierarchical grouping (GroupOptions): records whose parent ID equals another group's instance ID nest under it.</summary>
    public string ParentIdField { get; set; } = "";

    public string InstanceIdField { get; set; } = "";

    public int HierarchicalIndent { get; set; }

    /// <summary>The group-name formula (GroupNameFormat useFormulaField/useFormulaValue); null when the condition field names the group.</summary>
    public CrystalFormulaFieldModel? NameFormula { get; set; }

    /// <summary>GroupOptions condition: date/date-time period (DateCondition 0-11), time period (0-3) or Boolean change (0-6); 0 groups on every value change.</summary>
    public int Condition { get; set; }

    /// <summary>GroupOptions sort direction: 0 ascending, 1 descending, 2 original order, 3 specified order.</summary>
    public int Direction { get; set; }

    /// <summary>Date-period groups are named by the last date of the period instead of the first (showLastDateInPeriod).</summary>
    public bool ShowLastDateInPeriod { get; set; }

    /// <summary>Specified-order named groups (ValueRangeList), in print order: the group name and its Crystal selection expression.</summary>
    public List<(string Name, string Expression)> SpecifiedGroups { get; } = [];

    /// <summary>UnspecifiedValuesType for records outside every named group: 0 merge into one group, 1 discard, 2 keep their own groups
    /// (Crystal's default when no ValueRangeList is saved).</summary>
    public int UnspecifiedValues { get; set; } = 2;

    /// <summary>Name of the merged group of unspecified values.</summary>
    public string SpecifiedOthersName { get; set; } = "Others";

    /// <summary>Top/Bottom N group count of the group's summary sort (TopNGroupInfo); 0 when not limited by count.</summary>
    internal int TopNCount { get; set; }

    /// <summary>Top/Bottom N percentage of the group's summary sort; 0 when not limited by percentage.</summary>
    internal double TopNPercentage { get; set; }

    internal bool TopNHasFormula { get; set; }

    /// <summary>The conditional formula that computes the Top/Bottom N count or percentage, when resolved.</summary>
    internal CrystalFormulaFieldModel? TopNFormula { get; set; }

    internal bool TopNFormulaIsPercentage { get; set; }

    internal bool DiscardOthers { get; set; }

    internal bool WithTies { get; set; }

    internal string OthersName { get; set; } = "";
}

public sealed class CrystalParameterFieldModel
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string PromptText { get; set; } = "";

    public int ValueType { get; set; }

    public int NumberOfBytes { get; set; }

    public bool AllowMultiple { get; set; }

    /// <summary>The parameter accepts range values (discrete-and-range or range only).</summary>
    internal bool AllowRange { get; set; }

    internal bool AllowDiscrete { get; set; }

    /// <summary>Crystal's DiscreteOrRangeKind; a range-capable parameter's formula value is a range (ParameterFieldDefinition).</summary>
    public string DiscreteOrRangeKind => !AllowRange ? "DiscreteValue" : AllowDiscrete ? "DiscreteAndRangeValue" : "RangeValue";

    public bool AllowCustomValues { get; set; } = true;

    public bool AllowNull { get; set; }

    public bool HasPromptMetadata { get; set; }

    public bool HasBrowseField { get; set; }

    public List<CrystalParameterDefaultValueModel> DefaultValues { get; } = [];

    public List<CrystalParameterDefaultValueModel> InitialValues { get; } = [];

    public List<CrystalParameterDefaultValueModel> CurrentValues { get; } = [];

    public string FormulaName => "{?" + Name + "}";
}

public sealed class CrystalParameterDefaultValueModel
{
    public string Value { get; set; } = "";

    public string Description { get; set; } = "";
}

public sealed class CrystalSummaryFieldModel
{
    public string Name { get; set; } = "";

    public int ValueType { get; set; }

    public int NumberOfBytes { get; set; }

    public string SummarizedField { get; set; } = "";

    public int Operation { get; set; }

    public int OperationParameter { get; set; }

    public int SummaryKind { get; set; }

    public int GroupIndex { get; set; }

    /// <summary>HierarchicalSummaryType "Across Hierarchy": a group's total includes its hierarchical descendants.</summary>
    public bool AcrossHierarchy { get; set; }
}

public sealed class CrystalRunningTotalFieldModel
{
    public string Name { get; set; } = "";

    public int ValueType { get; set; }

    public int NumberOfBytes { get; set; }

    public string SummarizedField { get; set; } = "";

    public int Operation { get; set; }

    public int OperationParameter { get; set; }

    public int EvaluationConditionType { get; set; }
    public string EvaluationConditionField { get; set; } = "";
    public int EvaluationConditionGroup { get; set; }
    public CrystalFormulaFieldModel? EvaluationConditionFormula { get; set; }

    public int ResetConditionType { get; set; }
    public string ResetConditionField { get; set; } = "";
    public int ResetConditionGroup { get; set; }
    public CrystalFormulaFieldModel? ResetConditionFormula { get; set; }

    public string FormulaName => "{#" + Name + "}";
}

public sealed class CrystalReportDefinitionModel
{
    public List<CrystalReportAreaModel> Areas { get; } = [];

    public List<int> SubreportDocumentIndexes { get; } = [];

    /// <summary>Crystal ReportKind: 0 standard, 1 mailing labels, 2 multiple columns. Any kind but 0 formats the details area in <see cref="MultiColumn"/>'s columns.</summary>
    public int ReportKind { get; set; }

    public CrystalMultiColumnModel? MultiColumn { get; set; }
}

/// <summary>Crystal MultiColumnInfo: the detail size, the gaps between details (twips), the printing direction and whether group sections share the columns.</summary>
public sealed record CrystalMultiColumnModel(int DetailWidth, int DetailHeight, int HorizontalGap, int VerticalGap, bool AcrossThenDown, bool FormatGroups);

public sealed class CrystalReportAreaModel
{
    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";

    public int GroupIndex { get; set; }

    public int GroupPairOrder { get; set; }

    public bool EnableRepeatGroupHeader { get; set; } = true;

    public bool EnableKeepGroupTogether { get; set; }

    public CrystalSectionFormatModel Format { get; set; } = new() { EnableKeepTogether = false };

    public List<CrystalReportSectionModel> Sections { get; } = [];
}

public sealed class CrystalReportSectionModel
{
    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";

    public int Height { get; set; }

    public CrystalSectionFormatModel Format { get; set; } = new();

    public List<CrystalReportObjectModel> ReportObjects { get; } = [];
}

public sealed class CrystalSectionFormatModel
{
    public Dictionary<string, string> ConditionFormulas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool EnableKeepTogether { get; set; } = true;

    public bool EnableNewPageAfter { get; set; }

    public bool EnableNewPageBefore { get; set; }

    public bool EnablePrintAtBottomOfPage { get; set; }

    public bool EnableResetPageNumberAfter { get; set; }

    public bool EnableSuppress { get; set; }

    public bool EnableHideForDrillDown { get; set; }

    public bool EnableSuppressIfBlank { get; set; }

    public bool EnableUnderlaySection { get; set; }

    public string EnableSuppressConditionFormula { get; set; } = "";
}

public sealed record CrystalConversionDiagnostic(string Code, string ReportName, string SectionName,
    string ObjectName, string Kind, string Message);

/// <summary>Opaque bytes from the decoded Contents stream, including TSLV record headers.</summary>
public sealed class CrystalUnsupportedObjectSource
{
    public string Stream { get; set; } = "";
    public int Offset { get; init; }
    public int RecordType { get; init; }
    public int Schema { get; init; }
    public byte[] ArchiveBytes { get; set; } = [];
    public bool Complete { get; set; }
    public List<string> MetadataDiagnostics { get; } = [];
}

public sealed class CrystalReportObjectModel
{
    public ReportAnalysisDefinition? Analysis { get; set; }
    public string AnalysisDiagnostic { get; set; } = "";
    public CrystalUnsupportedObjectSource? UnsupportedSource { get; set; }

    public int PictureStorageIndex { get; set; } = -1;
    public int PictureAspect { get; set; } = 1;
    public string ImageDataUrl { get; set; } = "";
    public ReportVectorImage? VectorImage { get; set; }
    public string PictureDiagnostic { get; set; } = "";
    public List<CrystalTextRunModel> TextRuns { get; } = [];
    public string ElementName { get; set; } = "";

    public string Name { get; set; } = "";

    public string Kind { get; set; } = "";

    public int Top { get; set; }

    public int Left { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int MaxNumberOfLines { get; set; }

    public string Text { get; set; } = "";

    public string DataSource { get; set; } = "";

    public string FieldObjectName { get; set; } = "";

    public int SubreportDocumentIndex { get; set; } = -1;

    public string SubreportName { get; set; } = "";

    public bool EnableOnDemand { get; set; }

    /// <summary>The subreport links stored with this subreport object (legacy Contents records 260/262).</summary>
    internal List<CrystalStoredSubreportLink> StoredSubreportLinks { get; } = [];

    public CrystalObjectFormatModel Format { get; set; } = new();

    public CrystalBorderModel Border { get; set; } = new();

    public CrystalFontModel Font { get; set; } = new();

    public CrystalColorModel Color { get; set; } = CrystalColorModel.Black();

    public bool HasFont { get; set; }
    internal List<(string Property, string Name, int Type, int Index)> FontConditionReferences { get; } = [];
    public Dictionary<string, string> FontConditionFormulas { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CrystalObjectFormatModel
{
    internal List<(string Property, string Name, int Type, int Index)> ConditionReferences { get; } = [];
    public Dictionary<string, string> ConditionFormulas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string CssClass { get; set; } = "";

    public bool EnableCanGrow { get; set; }

    public bool EnableCloseAtPageBreak { get; set; } = true;

    public bool EnableKeepTogether { get; set; } = true;

    public bool EnableSuppress { get; set; }

    public string HorizontalAlignment { get; set; } = "Default";
}

public sealed class CrystalBorderModel
{
    internal List<(string Property, string Name, int Type, int Index)> ConditionReferences { get; } = [];
    public Dictionary<string, string> ConditionFormulas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string BottomLineStyle { get; set; } = "NoLine";

    public bool HasDropShadow { get; set; }

    public string LeftLineStyle { get; set; } = "NoLine";

    public string RightLineStyle { get; set; } = "NoLine";

    public string TopLineStyle { get; set; } = "NoLine";

    public CrystalColorModel BackgroundColor { get; set; } = CrystalColorModel.TransparentWhite();

    public CrystalColorModel BorderColor { get; set; } = CrystalColorModel.Black();
}

public sealed record CrystalTextRunModel(string Text, string Binding, string FontFamily, double Size, bool Bold, bool Italic, bool Underline, string Color);

public sealed class CrystalFontModel
{
    public bool Bold { get; set; }

    public string FontFamily { get; set; } = "Arial";

    public int GdiCharSet { get; set; }

    public bool Italic { get; set; }

    public string Name { get; set; } = "Arial";

    public string OriginalFontName { get; set; } = "Arial";

    public double Size { get; set; } = 10.0;

    public bool Strikeout { get; set; }

    public bool Underline { get; set; }

    public int Weight { get; set; } = 400;
}

public sealed class CrystalColorModel
{
    public string Name { get; set; } = "Black";

    public int A { get; set; } = 255;

    public int R { get; set; }

    public int G { get; set; }

    public int B { get; set; }

    public static CrystalColorModel Black() => new();

    public static CrystalColorModel TransparentWhite() => new()
    {
        Name = "ffffffff",
        A = 0,
        R = 255,
        G = 255,
        B = 255
    };
}
