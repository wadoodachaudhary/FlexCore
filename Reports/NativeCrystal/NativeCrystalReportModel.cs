namespace Fx.ControlKit.Reports.NativeCrystal;

public sealed class CrystalReportModel
{
    public required string SourcePath { get; init; }

    public string Name { get; set; } = "";

    public CrystalReportCore Core { get; } = new();

    public CrystalDatabaseModel Database { get; set; } = new();

    public CrystalDataDefinitionModel DataDefinition { get; set; } = new();

    public List<CrystalSubreportLinkModel> SubreportLinks { get; } = [];

    public List<CrystalReportModel> Subreports { get; } = [];
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
    public CrystalReportDefinitionModel ReportDefinition { get; } = new();

    public string RecordSelectionFormula { get; set; } = "";

    public string GroupSelectionFormula { get; set; } = "";

    public List<CrystalFormulaFieldModel> FormulaFields { get; } = [];

    public List<CrystalSortFieldModel> SortFields { get; } = [];

    public List<CrystalGroupModel> Groups { get; } = [];

    public List<CrystalParameterFieldModel> Parameters { get; } = [];

    public List<CrystalRunningTotalFieldModel> RunningTotalFields { get; } = [];

    public List<CrystalSummaryFieldModel> SummaryFields { get; } = [];
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
}

public sealed class CrystalGroupModel
{
    public string ConditionField { get; set; } = "";
}

public sealed class CrystalParameterFieldModel
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string PromptText { get; set; } = "";

    public int ValueType { get; set; }

    public int NumberOfBytes { get; set; }

    public bool AllowMultiple { get; set; }

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

    public int ResetConditionType { get; set; }

    public string FormulaName => "{#" + Name + "}";
}

public sealed class CrystalReportDefinitionModel
{
    public List<CrystalReportAreaModel> Areas { get; } = [];

    public List<int> SubreportDocumentIndexes { get; } = [];
}

public sealed class CrystalReportAreaModel
{
    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";

    public int GroupIndex { get; set; }

    public int GroupPairOrder { get; set; }

    public bool EnableRepeatGroupHeader { get; set; } = true;

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

public sealed class CrystalReportObjectModel
{
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

    public CrystalObjectFormatModel Format { get; set; } = new();

    public CrystalBorderModel Border { get; set; } = new();

    public CrystalFontModel Font { get; set; } = new();

    public CrystalColorModel Color { get; set; } = CrystalColorModel.Black();

    public bool HasFont { get; set; }
}

public sealed class CrystalObjectFormatModel
{
    public string CssClass { get; set; } = "";

    public bool EnableCanGrow { get; set; }

    public bool EnableCloseAtPageBreak { get; set; } = true;

    public bool EnableKeepTogether { get; set; } = true;

    public bool EnableSuppress { get; set; }

    public string HorizontalAlignment { get; set; } = "Default";
}

public sealed class CrystalBorderModel
{
    public string BottomLineStyle { get; set; } = "NoLine";

    public bool HasDropShadow { get; set; }

    public string LeftLineStyle { get; set; } = "NoLine";

    public string RightLineStyle { get; set; } = "NoLine";

    public string TopLineStyle { get; set; } = "NoLine";

    public CrystalColorModel BackgroundColor { get; set; } = CrystalColorModel.TransparentWhite();

    public CrystalColorModel BorderColor { get; set; } = CrystalColorModel.Black();
}

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
