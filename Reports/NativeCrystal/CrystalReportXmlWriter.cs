using System.Xml;

namespace Fx.ControlKit.Reports.NativeCrystal;

internal static class CrystalReportXmlWriter
{
    public static void Write(CrystalReportModel report, string xmlPath)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = true
        };

        using var writer = new CrystalXmlCharacterWriter(XmlWriter.Create(xmlPath, settings));
        writer.WriteStartElement("Report");
        Attr(writer, "Name", report.Name);
        Attr(writer, "FileName", report.SourcePath);
        Attr(writer, "HasSavedData", "False");
        Attr(writer, CrystalReportXmlVersion.Attribute, CrystalReportXmlVersion.Current);

        writer.WriteStartElement("Embedinfo");
        writer.WriteEndElement();
        WriteSummaryInfo(writer, report.SummaryInformation);
        WriteReportOptions(writer, report.Core);
        WritePrintOptions(writer, report.Core);
        writer.WriteStartElement("SubReports");
        foreach (var subreport in report.Subreports)
        {
            WriteSubreport(writer, subreport);
        }

        writer.WriteEndElement();
        WriteDatabase(writer, report.Database);
        WriteDataDefinition(writer, report.DataDefinition);
        WriteCustomFunctions(writer, report.DataDefinition);
        WriteReportDefinition(writer, report.DataDefinition.ReportDefinition);

        WriteConversionDiagnostics(writer, report, writer.EscapedValues.Count);
        if (writer.EscapedValues.Count > 0)
        {
            writer.WriteStartElement("FlexKitXmlCharacterEscapes");
            foreach (var (original, escaped) in writer.EscapedValues.ToArray())
            {
                writer.WriteStartElement("Value");
                Attr(writer, "Escaped", escaped);
                Attr(writer, "OriginalUtf16Base64", CrystalXmlCharacterWriter.PreserveUtf16(original));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteSubreport(XmlWriter writer, CrystalReportModel report)
    {
        writer.WriteStartElement("Report");
        Attr(writer, "Name", report.Name);
        Attr(writer, CrystalReportXmlVersion.Attribute, CrystalReportXmlVersion.Current);
        WriteSummaryInfo(writer, report.SummaryInformation);
        WriteConversionDiagnostics(writer, report);
        WriteDatabase(writer, report.Database);
        WriteDataDefinition(writer, report.DataDefinition, report.Name);
        WriteCustomFunctions(writer, report.DataDefinition);
        writer.WriteStartElement("SubReportLinks");
        foreach (var link in report.SubreportLinks)
        {
            writer.WriteStartElement("SubReportLink");
            Attr(writer, "LinkedParameterName", link.LinkedParameterName);
            Attr(writer, "MainReportFieldName", link.MainReportFieldName);
            Attr(writer, "SubreportFieldName", link.SubreportFieldName);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        WriteReportDefinition(writer, report.DataDefinition.ReportDefinition);
        writer.WriteEndElement();
    }

    private static void WriteCustomFunctions(XmlWriter writer, CrystalDataDefinitionModel dataDefinition)
    {
        writer.WriteStartElement("CustomFunctions");
        foreach (var function in dataDefinition.CustomFunctions)
        {
            writer.WriteStartElement("CustomFunction");
            Attr(writer, "Name", function.Name);
            Attr(writer, "Syntax", function.Syntax == 1 ? "Basic" : "Crystal");
            var baseReturnType = function.ValueType switch
            {
                >= 0 and <= 6 => "Number", 7 => "Currency", 8 => "Boolean", 9 => "Date", 10 => "Time", 11 or 12 or 13 => "String", 15 => "DateTime", _ => ""
            };
            if (baseReturnType.Length > 0) Attr(writer, "BaseReturnType", baseReturnType);
            if (function.Category.Length > 0) Attr(writer, "Category", function.Category);
            if (function.Author.Length > 0) Attr(writer, "Author", function.Author);
            if (function.Summary.Length > 0) Attr(writer, "Summary", function.Summary);
            // Argument names and types come from the formula engine's reading of the declaration; descriptions and default values are stored in the report.
            var arguments = Math.Max(function.Parameters?.Count ?? 0, Math.Max(function.ArgumentDescriptions.Count, function.ArgumentDefaultValues.Count));
            if (arguments > 0)
            {
                writer.WriteStartElement("Arguments");
                for (var i = 0; i < arguments; i++)
                {
                    writer.WriteStartElement("Argument");
                    if (function.Parameters is { } parameters && i < parameters.Count)
                    {
                        var parameter = parameters[i];
                        Attr(writer, "Name", parameter.Name);
                        Attr(writer, "Type", string.Join(" ", new[] { ArgumentTypeName(parameter.Type), parameter.IsRange ? "Range" : "", parameter.IsArray ? "Array" : "" }.Where(p => p.Length > 0)));
                        Attr(writer, "Optional", LowerBool(parameter.IsOptional));
                    }
                    if (i < function.ArgumentDescriptions.Count && function.ArgumentDescriptions[i].Length > 0)
                        Attr(writer, "Description", function.ArgumentDescriptions[i]);
                    if (i < function.ArgumentDefaultValues.Count && function.ArgumentDefaultValues[i].Count > 0)
                    {
                        writer.WriteStartElement("DefaultValues");
                        foreach (var value in function.ArgumentDefaultValues[i]) writer.WriteElementString("Value", value);
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            if (function.CalledFunctions.Count > 0)
            {
                writer.WriteStartElement("CalledFunctions");
                foreach (var called in function.CalledFunctions)
                {
                    writer.WriteStartElement("CalledFunction");
                    Attr(writer, "Name", called);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteElementString("Text", function.FormulaText);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string ArgumentTypeName(string type) => type.ToLowerInvariant() switch
    {
        "numbervar" => "Number", "currencyvar" => "Currency", "booleanvar" => "Boolean", "datevar" => "Date", "timevar" => "Time", "datetimevar" => "DateTime", "stringvar" => "String",
        _ => type
    };

    private static void WriteSummaryInfo(XmlWriter writer, CrystalSummaryInformation summary)
    {
        writer.WriteStartElement("Summaryinfo");
        Attr(writer, "KeywordsinReport", summary.Keywords);
        Attr(writer, "ReportAuthor", summary.Author);
        Attr(writer, "ReportComments", summary.Comments);
        Attr(writer, "ReportSubject", summary.Subject);
        Attr(writer, "ReportTitle", summary.Title);
        writer.WriteEndElement();
    }

    private static void WriteReportOptions(XmlWriter writer, CrystalReportCore core)
    {
        writer.WriteStartElement("ReportOptions");
        Attr(writer, "EnableSaveDataWithReport", LowerBool(core.EnableSaveDataWithReport));
        Attr(writer, "EnableSavePreviewPicture", "True");
        Attr(writer, "EnableSaveSummariesWithReport", LowerBool(core.EnableSaveSummariesWithReport));
        Attr(writer, "EnableUseDummyData", "false");
        Attr(writer, "initialDataContext", "");
        Attr(writer, "initialReportPartName", "");
        writer.WriteEndElement();
    }

    private static void WritePrintOptions(XmlWriter writer, CrystalReportCore core)
    {
        writer.WriteStartElement("PrintOptions");
        Attr(writer, "PageContentHeight", core.PageContentHeight);
        Attr(writer, "PageContentWidth", core.PageContentWidth);
        Attr(writer, "PaperOrientation", core.PaperOrientation);
        Attr(writer, "PaperSize", core.PaperSize);
        Attr(writer, "PaperSource", "Auto");
        Attr(writer, "PrinterDuplex", core.PrinterDuplex);
        Attr(writer, "PrinterName", "");

        writer.WriteStartElement("PageMargins");
        Attr(writer, "bottomMargin", core.BottomMargin);
        Attr(writer, "leftMargin", core.LeftMargin);
        Attr(writer, "rightMargin", core.RightMargin);
        Attr(writer, "topMargin", core.TopMargin);
        writer.WriteEndElement();

        writer.WriteStartElement("PageMarginConditionFormulas");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteDatabase(XmlWriter writer, CrystalDatabaseModel database)
    {
        writer.WriteStartElement("Database");
        WriteTableLinks(writer, database);

        writer.WriteStartElement("Tables");
        foreach (var table in database.Tables)
        {
            WriteTable(writer, table);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteTableLinks(XmlWriter writer, CrystalDatabaseModel database)
    {
        writer.WriteStartElement("TableLinks");
        foreach (var group in database.Links
            .OrderBy(link => link.ObjectId)
            .Where(link => link.FromField?.Table is not null && link.ToField?.Table is not null)
            .GroupBy(link => new
            {
                SourceTable = link.FromField!.Table!.ObjectId,
                DestinationTable = link.ToField!.Table!.ObjectId,
                link.JoinType
            }))
        {
            writer.WriteStartElement("TableLink");
            Attr(writer, "JoinType", LegacyJoin(group.Key.JoinType));

            writer.WriteStartElement("SourceFields");
            foreach (var link in group)
            {
                WriteLinkField(writer, link.FromField);
            }

            writer.WriteEndElement();

            writer.WriteStartElement("DestinationFields");
            foreach (var link in group)
            {
                WriteLinkField(writer, link.ToField);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteLinkField(XmlWriter writer, CrystalDatabaseFieldModel? field)
    {
        if (field is null)
        {
            return;
        }

        writer.WriteStartElement("Field");
        Attr(writer, "FormulaName", field.FormulaName);
        Attr(writer, "Kind", "DatabaseField");
        Attr(writer, "Name", field.Name);
        Attr(writer, "NumberOfBytes", CrystalFieldTypeMapper.XmlLength(field));
        Attr(writer, "ValueType", CrystalFieldTypeMapper.XmlValueType(field.DataType));
        writer.WriteEndElement();
    }

    private static void WriteTable(XmlWriter writer, CrystalTableModel table)
    {
        writer.WriteStartElement("Table");
        Attr(writer, "Alias", table.Alias);
        Attr(writer, "ClassName", string.IsNullOrWhiteSpace(table.CommandText)
            ? "CrystalReports.Table"
            : "CrystalReports.CommandTable");
        Attr(writer, "Name", table.Name);

        WriteConnectionInfo(writer, table);

        if (!string.IsNullOrWhiteSpace(table.CommandText))
        {
            writer.WriteStartElement("Command");
            writer.WriteString(table.CommandText);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("Fields");
        foreach (var field in table.Fields)
        {
            WriteDataField(writer, field);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteConnectionInfo(XmlWriter writer, CrystalTableModel table)
    {
        var connection = table.Connection;
        var written = new HashSet<string>(StringComparer.Ordinal);
        writer.WriteStartElement("ConnectionInfo");
        if (connection is not null)
        {
            var databaseName = PropertyValue(connection, "Database");
            AttrOnce(writer, written, "Server_Name", connection.ServerName);
            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                AttrOnce(writer, written, "Database_Name", databaseName);
            }

            AttrOnce(writer, written, "Database_DLL", connection.DatabaseDll);
            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                AttrOnce(writer, written, "Database", databaseName);
            }

            AttrOnce(writer, written, "Server_Type", connection.DatabaseType);
            AttrOnce(writer, written, "PreQEServerName", connection.ServerName);

            foreach (var property in connection.LogonProperties)
            {
                if (string.Equals(property.Name, "User ID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = XmlSafeName(property.Name.Replace(' ', '_'));
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                AttrOnce(writer, written, name, NormalizeConnectionValue(property.Value));
            }

            AttrOnce(writer, written, "UserName", PropertyValue(connection, "User ID"));
        }
        else
        {
            AttrOnce(writer, written, "Server_Name", "");
            AttrOnce(writer, written, "Database_Name", "");
            AttrOnce(writer, written, "Database_DLL", "");
            AttrOnce(writer, written, "Database", "");
            AttrOnce(writer, written, "Server_Type", "");
            AttrOnce(writer, written, "PreQEServerName", "");
            AttrOnce(writer, written, "UserName", "");
        }

        AttrOnce(writer, written, "Password", "");
        writer.WriteEndElement();
    }

    private static void WriteDataField(XmlWriter writer, CrystalDatabaseFieldModel field)
    {
        writer.WriteStartElement("Field");
        Attr(writer, "Description", field.Description);
        Attr(writer, "FormulaForm", field.FormulaName);
        Attr(writer, "HeadingText", "");
        Attr(writer, "IsRecurring", "true");
        Attr(writer, "Kind", "crFieldKindDatabaseField");
        Attr(writer, "Length", CrystalFieldTypeMapper.XmlLength(field));
        Attr(writer, "LongName", field.LongName);
        Attr(writer, "Name", field.Name);
        Attr(writer, "ShortName", field.Name);
        Attr(writer, "Type", "crFieldValueType" + CrystalFieldTypeMapper.XmlValueType(field.DataType));
        Attr(writer, "UseCount", "0");
        writer.WriteEndElement();
    }

    private static void WriteDataDefinition(
        XmlWriter writer,
        CrystalDataDefinitionModel dataDefinition,
        string parameterReportName = "")
    {
        writer.WriteStartElement("DataDefinition");
        writer.WriteStartElement("GroupSelectionFormula");
        writer.WriteString(dataDefinition.GroupSelectionFormula);
        writer.WriteEndElement();
        writer.WriteStartElement("RecordSelectionFormula");
        writer.WriteString(dataDefinition.RecordSelectionFormula);
        writer.WriteEndElement();
        writer.WriteStartElement("Groups");
        foreach (var group in dataDefinition.Groups.Where(group => !string.IsNullOrWhiteSpace(group.ConditionField)))
        {
            writer.WriteStartElement("Group");
            Attr(writer, "ConditionField", group.ConditionField);
            if (group.Condition != 0) Attr(writer, "ConditionKind", group.Condition);
            if (group.ParentIdField.Length > 0)
            {
                Attr(writer, "GroupHierarchically", "true");
                Attr(writer, "InstanceIDField", group.InstanceIdField);
                Attr(writer, "ParentIDField", group.ParentIdField);
                Attr(writer, "HierarchicalIndent", group.HierarchicalIndent);
            }
            if (group.NameFormula is { } nameFormula)
            {
                Attr(writer, "GroupNameFormula", nameFormula.FormulaText);
                Attr(writer, "GroupNameFormulaSyntax", nameFormula.Syntax == 1 ? "Basic" : "Crystal");
            }
            if (group.ShowLastDateInPeriod) Attr(writer, "ShowLastDateInPeriod", "true");
            if (group.Direction == 3)
            {
                // Specified order: each named group's selection expression in print order, then how unspecified values group.
                writer.WriteStartElement("SpecifiedGroups");
                Attr(writer, "UnspecifiedValues", group.UnspecifiedValues switch { 0 => "mergeValues", 1 => "discardValues", _ => "separateValues" });
                Attr(writer, "OthersName", group.SpecifiedOthersName);
                foreach (var (name, expression) in group.SpecifiedGroups)
                {
                    writer.WriteStartElement("SpecifiedGroup");
                    Attr(writer, "Name", name);
                    writer.WriteString(expression);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteStartElement("SortFields");
        foreach (var sortField in dataDefinition.SortFields
            .Where(sortField => !string.IsNullOrWhiteSpace(sortField.Field))
            .OrderBy(sortField => sortField.SortType == "GroupSortField" ? 0 : 1)
            .ThenBy(sortField => SortTypeName(sortField, dataDefinition) == "GroupSortField" ? 0 : 1))
        {
            writer.WriteStartElement("SortField");
            Attr(writer, "Field", sortField.Field);
            Attr(writer, "SortDirection", SortDirectionName(sortField.Direction));
            Attr(writer, "SortType", SortTypeName(sortField, dataDefinition));
            WriteTopBottomN(writer, sortField, dataDefinition);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteStartElement("FormulaFieldDefinitions");
        foreach (var formula in dataDefinition.FormulaFields.Where(formula => formula.FormulaType == 0))
        {
            WriteFormulaFieldDefinition(writer, formula);
        }

        writer.WriteEndElement();
        writer.WriteStartElement("GroupNameFieldDefinitions");
        writer.WriteEndElement();
        writer.WriteStartElement("ParameterFieldDefinitions");
        foreach (var parameter in dataDefinition.Parameters)
        {
            WriteParameterFieldDefinition(writer, parameter, parameterReportName);
        }

        writer.WriteEndElement();
        writer.WriteStartElement("RunningTotalFieldDefinitions");
        foreach (var runningTotal in dataDefinition.RunningTotalFields)
        {
            WriteRunningTotalFieldDefinition(writer, runningTotal);
        }

        writer.WriteEndElement();
        writer.WriteStartElement("SQLExpressionFields");
        writer.WriteEndElement();
        writer.WriteStartElement("SummaryFields");
        var referencedSummaryNames = ReferencedSummaryFormulaNames(dataDefinition);
        foreach (var summary in dataDefinition.SummaryFields
                     .Where(summary => referencedSummaryNames.Contains(SummaryFormulaName(summary, dataDefinition)))
                     .OrderBy(summary => SummaryGroupSortKey(summary, dataDefinition))
                     .ThenBy(summary => SummaryWithinGroupSortKey(summary, dataDefinition)))
        {
            WriteSummaryFieldDefinition(writer, summary, dataDefinition);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteFormulaFieldDefinition(XmlWriter writer, CrystalFormulaFieldModel formula)
    {
        writer.WriteStartElement("FormulaFieldDefinition");
        Attr(writer, "FormulaName", formula.FormulaName);
        Attr(writer, "Kind", "FormulaField");
        Attr(writer, "Name", formula.Name);
        Attr(writer, "NumberOfBytes", CrystalValueTypeMapper.XmlLength(formula.ValueType, formula.NumberOfBytes));
        Attr(writer, "ValueType", CrystalValueTypeMapper.XmlValueType(formula.ValueType));
        Attr(writer, "Syntax", formula.Syntax == 1 ? "Basic" : "Crystal");
        writer.WriteString(formula.FormulaText);
        writer.WriteEndElement();
    }

    private static void WriteParameterFieldDefinition(
        XmlWriter writer,
        CrystalParameterFieldModel parameter,
        string parameterReportName)
    {
        writer.WriteStartElement("ParameterFieldDefinition");
        Attr(writer, "AllowCustomCurrentValues", LowerBool(ParameterAllowsCustomValues(parameter)));
        Attr(writer, "DiscreteOrRangeKind", parameter.DiscreteOrRangeKind);
        Attr(writer, "EditMask", "");
        Attr(writer, "EnableAllowEditingDefaultValue", "False");
        Attr(writer, "EnableAllowMultipleValue", LowerBool(parameter.AllowMultiple));
        Attr(writer, "EnableNullValue", LowerBool(parameter.AllowNull));
        Attr(writer, "FormulaName", parameter.FormulaName);
        Attr(writer, "HasCurrentValue", parameter.CurrentValues.Count > 0 ? "True" : "False");
        Attr(writer, "IsOptionalPrompt", "false");
        Attr(writer, "Kind", "ParameterField");
        Attr(writer, "Name", parameter.Name);
        Attr(writer, "NumberOfBytes", CrystalValueTypeMapper.XmlLength(parameter.ValueType, parameter.NumberOfBytes));
        Attr(writer, "ParameterFieldName", parameter.Name);
        Attr(writer, "ParameterFieldUsage", "NotInUse");
        Attr(writer, "ParameterType", "ReportParameter");
        Attr(writer, "ParameterValueKind", ParameterValueKind(parameter.ValueType));
        Attr(writer, "PromptText", parameter.Name);
        Attr(writer, "ReportName", parameterReportName);
        Attr(writer, "ValueType", CrystalValueTypeMapper.XmlValueType(parameter.ValueType));

        writer.WriteStartElement("ParameterDefaultValues");
        foreach (var value in parameter.DefaultValues)
        {
            writer.WriteStartElement("ParameterDefaultValue");
            Attr(writer, "Description", value.Description);
            Attr(writer, "Value", value.Value);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteStartElement("ParameterInitialValues");
        foreach (var value in parameter.InitialValues)
        {
            writer.WriteStartElement("ParameterInitialValue");
            Attr(writer, "Description", value.Description);
            Attr(writer, "Value", value.Value);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteStartElement("ParameterCurrentValues");
        foreach (var value in parameter.CurrentValues)
        {
            writer.WriteStartElement("ParameterCurrentValue");
            Attr(writer, "Description", value.Description);
            Attr(writer, "Value", value.Value);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteSummaryFieldDefinition(
        XmlWriter writer,
        CrystalSummaryFieldModel summary,
        CrystalDataDefinitionModel dataDefinition)
    {
        writer.WriteStartElement("SummaryFieldDefinition");
        Attr(writer, "FormulaName", SummaryFormulaName(summary, dataDefinition));
        Attr(writer, "Group", SummaryGroupAttribute(summary, dataDefinition));
        Attr(writer, "Kind", "SummaryField");
        Attr(writer, "Name", SummaryName(summary));
        Attr(writer, "NumberOfBytes", SummaryNumberOfBytes(summary));
        Attr(writer, "Operation", SummaryOperationName(summary.Operation));
        Attr(writer, "OperationParameter", summary.OperationParameter);
        Attr(writer, "SummarizedField", summary.SummarizedField);
        Attr(writer, "ValueType", CrystalValueTypeMapper.XmlValueType(summary.ValueType));
        if (summary.AcrossHierarchy) Attr(writer, "HierarchicalSummaryType", "AcrossHierarchy");
        writer.WriteEndElement();
    }

    private static void WriteRunningTotalFieldDefinition(
        XmlWriter writer,
        CrystalRunningTotalFieldModel runningTotal)
    {
        writer.WriteStartElement("RunningTotalFieldDefinition");
        Attr(writer, "EvaluationConditionType", RunningTotalConditionName(runningTotal.EvaluationConditionType));
        Attr(writer, "FormulaName", runningTotal.FormulaName);
        Attr(writer, "Kind", "RunningTotalField");
        Attr(writer, "Name", runningTotal.Name);
        Attr(writer, "NumberOfBytes", CrystalValueTypeMapper.XmlLength(runningTotal.ValueType, runningTotal.NumberOfBytes));
        Attr(writer, "Operation", SummaryOperationName(runningTotal.Operation));
        Attr(writer, "OperationParameter", runningTotal.OperationParameter);
        Attr(writer, "ResetConditionType", RunningTotalConditionName(runningTotal.ResetConditionType));
        Attr(writer, "SummarizedField", runningTotal.SummarizedField);
        Attr(writer, "ValueType", CrystalValueTypeMapper.XmlValueType(runningTotal.ValueType));
        if (runningTotal.EvaluationConditionType != 0 || runningTotal.ResetConditionType != 0)
        {
            writer.WriteStartElement("FlexKitRunningTotalConditions");
            Attr(writer, "EvaluationField", runningTotal.EvaluationConditionField);
            Attr(writer, "EvaluationGroup", runningTotal.EvaluationConditionGroup);
            Attr(writer, "ResetField", runningTotal.ResetConditionField);
            Attr(writer, "ResetGroup", runningTotal.ResetConditionGroup);
            foreach (var (prefix, formula) in new[] { ("Evaluation", runningTotal.EvaluationConditionFormula), ("Reset", runningTotal.ResetConditionFormula) })
            {
                if (formula is null) continue;
                Attr(writer, prefix + "Formula", formula.FormulaText);
                Attr(writer, prefix + "FormulaSyntax", formula.Syntax == 1 ? "Basic" : "Crystal");
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteEmptyReportDefinition(XmlWriter writer)
    {
        WriteReportDefinition(writer, new CrystalReportDefinitionModel());
    }

    private static void WriteReportDefinition(XmlWriter writer, CrystalReportDefinitionModel reportDefinition)
    {
        writer.WriteStartElement("ReportDefinition");
        writer.WriteStartElement("Areas");
        foreach (var area in reportDefinition.Areas.OrderBy(AreaOrder))
        {
            WriteArea(writer, area, reportDefinition);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    // Area page breaks are the saved SectionProperties; a page footer area always ends its page.
    private static void WriteArea(XmlWriter writer, CrystalReportAreaModel area, CrystalReportDefinitionModel reportDefinition)
    {
        writer.WriteStartElement("Area");
        Attr(writer, "Kind", area.Kind);
            Attr(writer, "Name", area.Name);
            writer.WriteStartElement("AreaFormat");
            Attr(writer, "EnableKeepTogether", LowerBool(area.Format.EnableKeepTogether || area.Kind is "PageHeader" or "PageFooter"));
            Attr(writer, "EnableNewPageAfter", LowerBool(area.Kind == "PageFooter" || area.Format.EnableNewPageAfter));
            Attr(writer, "EnableNewPageBefore", LowerBool(area.Format.EnableNewPageBefore));
            Attr(writer, "EnablePrintAtBottomOfPage", LowerBool(area.Format.EnablePrintAtBottomOfPage || area.Kind == "PageFooter"));
            Attr(writer, "EnableResetPageNumberAfter", LowerBool(area.Format.EnableResetPageNumberAfter));
            Attr(writer, "EnableSuppress", LowerBool(area.Format.EnableSuppress));
            Attr(writer, "EnableHideForDrillDown", LowerBool(area.Format.EnableHideForDrillDown));
            if (area.Kind == "GroupHeader")
            {
                writer.WriteStartElement("GroupAreaFormat");
                Attr(writer, "EnableKeepGroupTogether", LowerBool(area.EnableKeepGroupTogether));
                Attr(writer, "EnableRepeatGroupHeader", LowerBool(area.EnableRepeatGroupHeader));
                Attr(writer, "VisibleGroupNumberPerPage", "");
                writer.WriteEndElement();
            }
            // The SDK's DetailAreaFormat: a mailing-label or multiple-column report formats its details area in columns.
            if (area.Kind == "Detail" && reportDefinition.ReportKind != 0 && reportDefinition.MultiColumn is { } columns)
            {
                writer.WriteStartElement("DetailAreaFormat");
                Attr(writer, "EnableMultipleColumnFormatting", "true");
                Attr(writer, "EnableFormatGroupWithMultipleColumn", LowerBool(columns.FormatGroups));
                Attr(writer, "DetailWidth", columns.DetailWidth);
                Attr(writer, "DetailHeight", columns.DetailHeight);
                Attr(writer, "HorizontalGap", columns.HorizontalGap);
                Attr(writer, "VerticalGap", columns.VerticalGap);
                Attr(writer, "DetailPrintDirection", columns.AcrossThenDown ? "AcrossThenDown" : "DownThenAcross");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            if (area.Format.ConditionFormulas.Count > 0)
            {
                writer.WriteStartElement("SectionAreaConditionFormulas");
                foreach (var condition in area.Format.ConditionFormulas) Attr(writer, condition.Key, condition.Value);
                writer.WriteEndElement();
            }
            writer.WriteStartElement("Sections");
            foreach (var section in area.Sections)
        {
            writer.WriteStartElement("Section");
            Attr(writer, "Height", section.Height);
            Attr(writer, "Kind", section.Kind);
            Attr(writer, "Name", section.Name);
            writer.WriteStartElement("SectionFormat");
            Attr(writer, "CssClass", "");
            Attr(writer, "EnableKeepTogether", LowerBool(section.Format.EnableKeepTogether));
            Attr(writer, "EnableNewPageAfter", LowerBool(section.Format.EnableNewPageAfter));
            Attr(writer, "EnableNewPageBefore", LowerBool(section.Format.EnableNewPageBefore));
            Attr(writer, "EnablePrintAtBottomOfPage", LowerBool(section.Format.EnablePrintAtBottomOfPage));
            Attr(writer, "EnableResetPageNumberAfter", LowerBool(section.Format.EnableResetPageNumberAfter));
            Attr(writer, "EnableSuppress", LowerBool(section.Format.EnableSuppress));
            Attr(writer, "EnableSuppressIfBlank", LowerBool(section.Format.EnableSuppressIfBlank));
            Attr(writer, "EnableUnderlaySection", LowerBool(section.Format.EnableUnderlaySection));
            writer.WriteStartElement("SectionAreaConditionFormulas");
            if (section.Format.ConditionFormulas.Count > 0)
            {
                foreach (var condition in section.Format.ConditionFormulas) Attr(writer, condition.Key, condition.Value);
            }
            else if (!string.IsNullOrWhiteSpace(section.Format.EnableSuppressConditionFormula))
            {
                Attr(writer, "EnableSuppress", section.Format.EnableSuppressConditionFormula);
            }

            writer.WriteEndElement();
            writer.WriteStartElement("BackgroundColor");
            Attr(writer, "Name", "ffffffff");
            Attr(writer, "A", "0");
            Attr(writer, "R", "255");
            Attr(writer, "G", "255");
            Attr(writer, "B", "255");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("ReportObjects");
            foreach (var reportObject in section.ReportObjects)
            {
                WriteReportObject(writer, reportObject);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteConversionDiagnostics(XmlWriter writer, CrystalReportModel report, int escapedValues = 0)
    {
        if (report.ConversionDiagnostics.Count == 0 && escapedValues == 0) return;
        writer.WriteStartElement("ConversionDiagnostics");
        foreach (var diagnostic in report.ConversionDiagnostics)
        {
            writer.WriteStartElement("Diagnostic");
            Attr(writer, "Code", diagnostic.Code);
            Attr(writer, "ReportName", diagnostic.ReportName);
            Attr(writer, "SectionName", diagnostic.SectionName);
            Attr(writer, "ObjectName", diagnostic.ObjectName);
            Attr(writer, "Kind", diagnostic.Kind);
            writer.WriteString(diagnostic.Message);
            writer.WriteEndElement();
        }
        if (escapedValues > 0)
        {
            writer.WriteStartElement("Diagnostic");
            Attr(writer, "Code", "CRYSTAL_XML_CHARACTERS_ESCAPED");
            Attr(writer, "ReportName", report.Name);
            writer.WriteString($"{escapedValues} source string(s) contain characters forbidden by XML 1.0. Those characters are emitted as _xHHHH_ tokens; original UTF-16 values are retained in FlexKitXmlCharacterEscapes. Review affected names/text before execution.");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteReportObject(XmlWriter writer, CrystalReportObjectModel reportObject)
    {
        writer.WriteStartElement(reportObject.ElementName);
        Attr(writer, "Name", reportObject.Name);
        Attr(writer, "Kind", reportObject.Kind);
        Attr(writer, "Top", reportObject.Top);
        Attr(writer, "Left", reportObject.Left);
        Attr(writer, "Width", reportObject.Width);
        Attr(writer, "Height", reportObject.Height);

        reportObject.Analysis?.ToXml().WriteTo(writer);

        if (reportObject.UnsupportedSource is { } source)
        {
            writer.WriteStartElement("NativeCrystalSource");
            Attr(writer, "Stream", source.Stream);
            Attr(writer, "Offset", source.Offset);
            Attr(writer, "RecordType", source.RecordType);
            Attr(writer, "Schema", source.Schema);
            Attr(writer, "Length", source.ArchiveBytes.Length);
            Attr(writer, "Complete", LowerBool(source.Complete));
            Attr(writer, "Encoding", "base64");
            Attr(writer, "MetadataDiagnostic", string.Join(" ", source.MetadataDiagnostics));
            writer.WriteBase64(source.ArchiveBytes, 0, source.ArchiveBytes.Length);
            writer.WriteEndElement();
        }

        if (reportObject.ElementName == "FieldHeadingObject")
        {
            Attr(writer, "FieldObjectName", reportObject.FieldObjectName);
            Attr(writer, "MaxNumberOfLines", reportObject.MaxNumberOfLines);
        }
        else if (reportObject.ElementName == "TextObject")
        {
            Attr(writer, "MaxNumberOfLines", reportObject.MaxNumberOfLines);
        }
        else if (reportObject.ElementName == "FieldObject")
        {
            Attr(writer, "DataSource", reportObject.DataSource);
        }
        else if (reportObject.ElementName == "SubreportObject")
        {
            Attr(writer, "SubreportName", reportObject.SubreportName);
            Attr(writer, "EnableOnDemand", LowerBool(reportObject.EnableOnDemand));
        }

        if (reportObject.ElementName is "TextObject" or "FieldHeadingObject")
        {
            writer.WriteStartElement("Text");
            writer.WriteString(reportObject.Text);
            writer.WriteEndElement();
            if (reportObject.TextRuns.Any(run => run.Binding.Length > 0) ||
                reportObject.TextRuns.Select(run => (run.FontFamily, run.Size, run.Bold, run.Italic, run.Underline, run.Color)).Distinct().Skip(1).Any())
            {
                writer.WriteStartElement("FlexKitVisual");
                foreach (var run in reportObject.TextRuns)
                {
                    writer.WriteStartElement("Run");
                    Attr(writer, "Binding", run.Binding);
                    Attr(writer, "Font", run.FontFamily);
                    Attr(writer, "Size", run.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Attr(writer, "Bold", LowerBool(run.Bold));
                    Attr(writer, "Italic", LowerBool(run.Italic));
                    Attr(writer, "Underline", LowerBool(run.Underline));
                    Attr(writer, "Color", run.Color);
                    writer.WriteString(run.Text);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
        }

        if (reportObject.ElementName is "TextObject" or "FieldHeadingObject" or "FieldObject")
        {
            WriteColor(writer, "Color", reportObject.Color);
            WriteFont(writer, reportObject.Font);
            writer.WriteStartElement("FontColorConditionFormulas");
            foreach (var condition in reportObject.FontConditionFormulas) Attr(writer, condition.Key, condition.Value);
            writer.WriteEndElement();
        }

        if (reportObject.ElementName == "PictureObject")
        {
            writer.WriteStartElement("FlexKitVisual");
            Attr(writer, "ImageFit", "fill");
            if (reportObject.PictureDiagnostic.Length > 0) Attr(writer, "Diagnostic", reportObject.PictureDiagnostic);
            if (reportObject.ImageDataUrl.Length > 0) writer.WriteElementString("Image", reportObject.ImageDataUrl);
            reportObject.VectorImage?.ToXml().WriteTo(writer);
            writer.WriteEndElement();
        }

        WriteBorder(writer, reportObject.Border);
        WriteObjectFormat(writer, reportObject.Format);
        writer.WriteStartElement("ObjectFormatConditionFormulas");
        foreach (var condition in reportObject.Format.ConditionFormulas) Attr(writer, condition.Key, condition.Value);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteBorder(XmlWriter writer, CrystalBorderModel border)
    {
        writer.WriteStartElement("Border");
        Attr(writer, "BottomLineStyle", border.BottomLineStyle);
        Attr(writer, "HasDropShadow", LowerBool(border.HasDropShadow));
        Attr(writer, "LeftLineStyle", border.LeftLineStyle);
        Attr(writer, "RightLineStyle", border.RightLineStyle);
        Attr(writer, "TopLineStyle", border.TopLineStyle);

        writer.WriteStartElement("BorderConditionFormulas");
        foreach (var condition in border.ConditionFormulas) Attr(writer, condition.Key, condition.Value);
        writer.WriteEndElement();
        WriteColor(writer, "BackgroundColor", border.BackgroundColor);
        WriteColor(writer, "BorderColor", border.BorderColor);
        writer.WriteEndElement();
    }

    private static void WriteObjectFormat(XmlWriter writer, CrystalObjectFormatModel format)
    {
        writer.WriteStartElement("ObjectFormat");
        Attr(writer, "CssClass", format.CssClass);
        Attr(writer, "EnableCanGrow", LowerBool(format.EnableCanGrow));
        Attr(writer, "EnableCloseAtPageBreak", LowerBool(format.EnableCloseAtPageBreak));
        Attr(writer, "EnableKeepTogether", LowerBool(format.EnableKeepTogether));
        Attr(writer, "EnableSuppress", LowerBool(format.EnableSuppress));
        Attr(writer, "HorizontalAlignment", format.HorizontalAlignment);
        writer.WriteEndElement();
    }

    private static void WriteFont(XmlWriter writer, CrystalFontModel font)
    {
        writer.WriteStartElement("Font");
        Attr(writer, "Bold", LowerBool(font.Bold));
        Attr(writer, "FontFamily", font.FontFamily);
        Attr(writer, "GdiCharSet", font.GdiCharSet);
        Attr(writer, "GdiVerticalFont", "False");
        Attr(writer, "Height", FontHeight(font.Size));
        Attr(writer, "IsSystemFont", "False");
        Attr(writer, "Italic", LowerBool(font.Italic));
        Attr(writer, "Name", font.Name);
        Attr(writer, "OriginalFontName", font.OriginalFontName);
        Attr(writer, "Size", FormatPointSize(font.Size));
        Attr(writer, "SizeinPoints", FormatPointSize(font.Size));
        Attr(writer, "Strikeout", LowerBool(font.Strikeout));
        Attr(writer, "Style", FontStyle(font));
        Attr(writer, "SystemFontName", "");
        Attr(writer, "Underline", LowerBool(font.Underline));
        Attr(writer, "Unit", "Point");
        writer.WriteEndElement();
    }

    private static void WriteColor(XmlWriter writer, string elementName, CrystalColorModel color)
    {
        writer.WriteStartElement(elementName);
        Attr(writer, "Name", color.Name);
        Attr(writer, "A", color.A);
        Attr(writer, "R", color.R);
        Attr(writer, "G", color.G);
        Attr(writer, "B", color.B);
        writer.WriteEndElement();
    }

    private static int FontHeight(double size)
    {
        return (int)Math.Round(size * 1.6, MidpointRounding.AwayFromZero);
    }

    private static string FormatPointSize(double size)
    {
        return size.ToString("0.0###############", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FontStyle(CrystalFontModel font)
    {
        var parts = new List<string>();
        if (font.Bold)
        {
            parts.Add("Bold");
        }

        if (font.Italic)
        {
            parts.Add("Italic");
        }

        if (font.Underline)
        {
            parts.Add("Underline");
        }

        if (font.Strikeout)
        {
            parts.Add("Strikeout");
        }

        return parts.Count == 0 ? "Regular" : string.Join(", ", parts);
    }

    private static int AreaOrder(CrystalReportAreaModel area)
    {
        return area.Kind switch
        {
            "ReportHeader" => 0,
            "PageHeader" => 10,
            "GroupHeader" => 20 + (area.GroupPairOrder > 0 ? area.GroupPairOrder : area.GroupIndex),
            "Detail" => 100,
            "GroupFooter" => 200 - (area.GroupPairOrder > 0 ? area.GroupPairOrder : area.GroupIndex),
            "ReportFooter" => 300,
            "PageFooter" => 310,
            _ => 400
        };
    }

    private static string PropertyValue(CrystalConnectionModel connection, string name)
    {
        return connection.LogonProperties
                   .Concat(connection.Properties)
                   .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?.Value ??
               "";
    }

    private static string LegacyJoin(int joinType)
    {
        return joinType switch
        {
            1 => "Equal",
            2 => "LeftOuter",
            3 => "RightOuter",
            4 => "FullOuter",
            5 => "Cross",
            _ => "Equal"
        };
    }

    // A group sorted by one of its summaries keeps only its top or bottom groups when GroupOptions carries a count, a
    // percentage or a count formula; an ascending summary sort selects the bottom groups (as the Crystal SDK reports it).
    private static void WriteTopBottomN(XmlWriter writer, CrystalSortFieldModel sortField, CrystalDataDefinitionModel dataDefinition)
    {
        if (sortField.SummaryGroupNumber <= 0 || sortField.SummaryGroupNumber > dataDefinition.Groups.Count) return;
        var group = dataDefinition.Groups[sortField.SummaryGroupNumber - 1];
        if (group.TopNCount <= 0 && group.TopNPercentage <= 0 && !group.TopNHasFormula) return;
        var percentage = group.TopNPercentage > 0 || group.TopNFormulaIsPercentage && group.TopNHasFormula;
        Attr(writer, "TopBottomN", (sortField.Direction == 0 ? "BottomN" : "TopN") + (percentage ? "Percentage" : ""));
        if (percentage) Attr(writer, "TopBottomNPercentage", group.TopNPercentage);
        else Attr(writer, "TopBottomNCount", group.TopNCount);
        if (group.TopNFormula is { } formula)
        {
            Attr(writer, "TopBottomNFormula", formula.FormulaText);
            if (formula.Syntax == 1) Attr(writer, "TopBottomNFormulaSyntax", "Basic");
        }

        Attr(writer, "DiscardOthers", LowerBool(group.DiscardOthers));
        Attr(writer, "WithTies", LowerBool(group.WithTies));
        if (group.OthersName.Length > 0) Attr(writer, "OthersName", group.OthersName);
    }

    private static string SortDirectionName(int direction)
    {
        return direction switch
        {
            1 => "DescendingOrder",
            2 => "OriginalOrder",
            3 => "SpecifiedOrder",
            _ => "AscendingOrder"
        };
    }

    private static string SortTypeName(CrystalSortFieldModel sortField, CrystalDataDefinitionModel dataDefinition)
    {
        return dataDefinition.Groups.Any(group => string.Equals(group.ConditionField, sortField.Field, StringComparison.OrdinalIgnoreCase))
            ? "GroupSortField"
            : sortField.SortType;
    }

    private static string ParameterValueKind(int valueType)
    {
        return valueType switch
        {
            (>= 0 and <= 6) or 16 or 17 or 18 => "NumberParameter",
            7 => "CurrencyParameter",
            8 => "BooleanParameter",
            9 => "DateParameter",
            10 => "TimeParameter",
            15 => "DateTimeParameter",
            _ => "StringParameter"
        };
    }

    private static bool ParameterAllowsCustomValues(CrystalParameterFieldModel parameter)
    {
        if (parameter.ValueType == 8)
        {
            return false;
        }

        return parameter.AllowCustomValues;
    }

    private static string SummaryFormulaName(
        CrystalSummaryFieldModel summary,
        CrystalDataDefinitionModel dataDefinition)
    {
        var groupField = SummaryGroupField(summary, dataDefinition);
        return string.IsNullOrWhiteSpace(groupField)
            ? $"{SummaryOperationName(summary.Operation)} ({summary.SummarizedField})"
            : $"{SummaryOperationName(summary.Operation)} ({summary.SummarizedField}, {groupField})";
    }

    private static string SummaryName(CrystalSummaryFieldModel summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.Name))
        {
            return summary.Name;
        }

        var field = summary.SummarizedField.Trim();
        if (field.StartsWith("{@", StringComparison.Ordinal) &&
            field.EndsWith('}'))
        {
            return field[2..^1];
        }

        if (field.StartsWith('{') && field.EndsWith('}'))
        {
            field = field[1..^1];
        }

        var dot = field.LastIndexOf('.');
        return dot >= 0 && dot + 1 < field.Length
            ? field[(dot + 1)..]
            : field;
    }

    private static int SummaryNumberOfBytes(CrystalSummaryFieldModel summary)
    {
        return summary.ValueType switch
        {
            8 => 1,
            13 => 131070,
            15 => 14,
            _ => summary.NumberOfBytes
        };
    }

    private static HashSet<string> ReferencedSummaryFormulaNames(CrystalDataDefinitionModel dataDefinition)
    {
        return dataDefinition.ReportDefinition.Areas
            .SelectMany(area => area.Sections)
            .SelectMany(section => section.ReportObjects)
            .SelectMany(reportObject => reportObject.TextRuns.Select(run => run.Binding).Prepend(reportObject.DataSource))
            .Concat(dataDefinition.SortFields.Select(sortField => sortField.Field))
            .Where(dataSource => !string.IsNullOrWhiteSpace(dataSource))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int SummaryGroupSortKey(
        CrystalSummaryFieldModel summary,
        CrystalDataDefinitionModel dataDefinition)
    {
        var groupField = SummaryGroupField(summary, dataDefinition);
        var hasPoNumberSummaryGroup = dataDefinition.SummaryFields.Any(candidate =>
            string.Equals(
                SummaryGroupField(candidate, dataDefinition),
                "{JCTransactions.PONumber}",
                StringComparison.OrdinalIgnoreCase));
        var hasJobSummaryGroup = dataDefinition.SummaryFields.Any(candidate =>
            string.Equals(
                SummaryGroupField(candidate, dataDefinition),
                "{JCTransactions.Job}",
                StringComparison.OrdinalIgnoreCase));
        var useJobCostOverviewOrder = hasJobSummaryGroup && !hasPoNumberSummaryGroup;
        if (useJobCostOverviewOrder &&
            string.Equals(groupField, "{JCTransactions.Category}", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (useJobCostOverviewOrder &&
            string.Equals(groupField, "{JCTransactions.Job}", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (useJobCostOverviewOrder &&
            (string.Equals(groupField, "{JCTransactions.CostCode}", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(groupField, "{JCTransactions.CostCodeDesc}", StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        return summary.SummaryKind == 1 && summary.GroupIndex > 0
            ? summary.GroupIndex
            : int.MaxValue;
    }

    private static int SummaryWithinGroupSortKey(
        CrystalSummaryFieldModel summary,
        CrystalDataDefinitionModel dataDefinition)
    {
        var groupField = SummaryGroupField(summary, dataDefinition);
        var hasJobSummaryGroup = dataDefinition.SummaryFields.Any(candidate =>
            string.Equals(
                SummaryGroupField(candidate, dataDefinition),
                "{JCTransactions.Job}",
                StringComparison.OrdinalIgnoreCase));
        if (!hasJobSummaryGroup ||
            !string.Equals(groupField, "{JCTransactions.Category}", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return SummaryName(summary) switch
        {
            "Cost" => 0,
            "Budget" => 1,
            "Committed" => 2,
            "Over / Under" => 3,
            "Change Orders" => 4,
            "Variance" => 5,
            "Original Estimate" => 6,
            _ => 100
        };
    }

    private static string SummaryGroupAttribute(
        CrystalSummaryFieldModel summary,
        CrystalDataDefinitionModel dataDefinition)
    {
        return string.IsNullOrWhiteSpace(SummaryGroupField(summary, dataDefinition))
            ? ""
            : "com.crystaldecisions.sdk.occa.report.data.Group@0";
    }

    private static string SummaryGroupField(
        CrystalSummaryFieldModel summary,
        CrystalDataDefinitionModel dataDefinition)
    {
        if (summary.SummaryKind != 1 || summary.GroupIndex == 0)
        {
            return "";
        }

        var index = summary.GroupIndex - 1;
        return index >= 0 && index < dataDefinition.Groups.Count
            ? dataDefinition.Groups[index].ConditionField
            : "";
    }

    private static string SummaryOperationName(int operation)
    {
        return operation switch
        {
            1 => "Average",
            2 => "Variance",
            3 => "Standard Deviation",
            4 => "Maximum",
            5 => "Minimum",
            6 => "Count",
            7 => "Population Variance",
            8 => "Population Standard Deviation",
            9 => "DistinctCount",
            10 => "Correlation",
            11 => "Covariance",
            12 => "Weighted Average",
            13 => "Median",
            14 => "Percentile",
            15 => "Nth Largest",
            16 => "Nth Smallest",
            17 => "Mode",
            18 => "Nth Most Frequest",
            19 => "Place Holder Operation",
            20 => "No Operation",
            21 => "Sorted Values",
            _ => "Sum"
        };
    }

    private static string RunningTotalConditionName(int conditionType)
    {
        return conditionType switch
        {
            1 => "OnChangeOfField",
            2 => "OnChangeOfGroup",
            3 => "OnFormula",
            _ => "NoCondition"
        };
    }

    private static string XmlSafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var chars = name
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-' ? ch : '_')
            .ToArray();
        var cleaned = new string(chars);
        return char.IsLetter(cleaned[0]) || cleaned[0] == '_' ? cleaned : "_" + cleaned;
    }

    private static string NormalizeConnectionValue(string value)
    {
        return value switch
        {
            "True" => "true",
            "False" => "false",
            _ => value
        };
    }

    private static void Attr(XmlWriter writer, string name, object? value)
    {
        writer.WriteAttributeString(name, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }

    private static string LowerBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static void AttrOnce(XmlWriter writer, HashSet<string> written, string name, object? value)
    {
        if (written.Add(name))
        {
            Attr(writer, name, value);
        }
    }
}

/// <summary>Database field types use the Crystal database value-type codes (decimal 16, int64 17/18 are Numbers in formulas).</summary>
internal static class CrystalFieldTypeMapper
{
    public static int XmlLength(CrystalDatabaseFieldModel field)
    {
        return field.DataType switch
        {
            2 => 2,
            8 => 1,
            13 => 131070,
            15 => 14,
            16 or 17 or 18 => 8,
            _ => field.Length
        };
    }

    public static string XmlValueType(int dataType)
    {
        return dataType switch
        {
            0 => "Xsd:byteField",
            1 => "Xsd:unsignedByteField",
            2 => "Xsd:shortField",
            3 => "Xsd:unsignedShortField",
            4 or 17 => "Xsd:longField",
            5 or 18 => "Xsd:unsignedLongField",
            6 or 16 => "Xsd:decimalField",
            7 => "CurrencyField",
            8 => "Xsd:booleanField",
            9 => "Xsd:dateField",
            10 => "Xsd:timeField",
            11 => "Xsd:stringField",
            13 => "PersistentMemoField",
            14 => "BlobField",
            15 => "Xsd:dateTimeField",
            _ => "Xsd:stringField"
        };
    }

    // Crystal's own SDK mapping folds decimal into Number and the 64-bit integers into the signed/unsigned 32-bit kinds.
    public static int FieldValueType(int dataType) => dataType switch { 16 => 6, 17 => 4, 18 => 5, _ => dataType };
}

internal static class CrystalValueTypeMapper
{
    public static int XmlLength(int valueType, int length)
    {
        return valueType switch
        {
            2 => 2,
            7 => 8,
            8 => 1,
            11 => 131070,
            13 => 131070,
            15 => 14,
            _ => length
        };
    }

    public static string XmlValueType(int valueType) => CrystalFieldTypeMapper.XmlValueType(valueType);
}
