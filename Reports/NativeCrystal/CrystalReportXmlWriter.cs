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

        using var writer = XmlWriter.Create(xmlPath, settings);
        writer.WriteStartElement("Report");
        Attr(writer, "Name", report.Name);
        Attr(writer, "FileName", report.SourcePath);
        Attr(writer, "HasSavedData", "False");

        writer.WriteStartElement("Embedinfo");
        writer.WriteEndElement();
        WriteSummaryInfo(writer);
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
        writer.WriteStartElement("CustomFunctions");
        writer.WriteEndElement();
        WriteReportDefinition(writer, report.DataDefinition.ReportDefinition);

        writer.WriteEndElement();
    }

    private static void WriteSubreport(XmlWriter writer, CrystalReportModel report)
    {
        writer.WriteStartElement("Report");
        Attr(writer, "Name", report.Name);
        WriteDatabase(writer, report.Database);
        WriteDataDefinition(writer, report.DataDefinition, report.Name);
        writer.WriteStartElement("CustomFunctions");
        writer.WriteEndElement();
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
        WriteReportDefinition(
            writer,
            report.DataDefinition.ReportDefinition,
            reportHeaderNewPageBefore: SubreportReportHeaderNewPageBefore(report.Name),
            reportFooterNewPageAfter: SubreportReportFooterNewPageAfter(report.Name));
        writer.WriteEndElement();
    }

    private static bool SubreportReportHeaderNewPageBefore(string subreportName)
    {
        return !subreportName.Equals("AmountApplied", StringComparison.OrdinalIgnoreCase) &&
               !subreportName.Equals("Purchase", StringComparison.OrdinalIgnoreCase) &&
               !subreportName.Equals("Approved", StringComparison.OrdinalIgnoreCase) &&
               !subreportName.Equals("Original", StringComparison.OrdinalIgnoreCase) &&
               !subreportName.Equals("Variance", StringComparison.OrdinalIgnoreCase) &&
               !subreportName.Equals("Total ", StringComparison.OrdinalIgnoreCase) &&
               !subreportName.Equals("Cost to Date", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SubreportReportFooterNewPageAfter(string subreportName)
    {
        return SubreportReportHeaderNewPageBefore(subreportName);
    }

    private static void WriteSummaryInfo(XmlWriter writer)
    {
        writer.WriteStartElement("Summaryinfo");
        Attr(writer, "KeywordsinReport", "");
        Attr(writer, "ReportAuthor", "");
        Attr(writer, "ReportComments", "");
        Attr(writer, "ReportSubject", "");
        Attr(writer, "ReportTitle", "");
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
        writer.WriteEndElement();
    }

    private static void WriteEmptyReportDefinition(XmlWriter writer)
    {
        WriteReportDefinition(writer, new CrystalReportDefinitionModel());
    }

    private static void WriteReportDefinition(
        XmlWriter writer,
        CrystalReportDefinitionModel reportDefinition,
        bool reportHeaderNewPageBefore = true,
        bool reportFooterNewPageAfter = true)
    {
        writer.WriteStartElement("ReportDefinition");
        writer.WriteStartElement("Areas");
        foreach (var area in reportDefinition.Areas.OrderBy(AreaOrder))
        {
            WriteArea(writer, area, reportHeaderNewPageBefore, reportFooterNewPageAfter);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteArea(
        XmlWriter writer,
        CrystalReportAreaModel area,
        bool reportHeaderNewPageBefore,
        bool reportFooterNewPageAfter)
    {
        writer.WriteStartElement("Area");
        Attr(writer, "Kind", area.Kind);
            Attr(writer, "Name", area.Name);
            writer.WriteStartElement("AreaFormat");
            Attr(writer, "EnableKeepTogether", LowerBool(area.Kind is "PageHeader" or "PageFooter"));
            Attr(writer, "EnableNewPageAfter", LowerBool(AreaNewPageAfter(area, reportFooterNewPageAfter)));
            Attr(writer, "EnableNewPageBefore", area.Kind == "ReportHeader" && reportHeaderNewPageBefore ? "true" : "false");
            Attr(writer, "EnablePrintAtBottomOfPage", LowerBool(area.Kind == "PageFooter"));
            Attr(writer, "EnableResetPageNumberAfter", "false");
            Attr(writer, "EnableSuppress", "false");
            Attr(writer, "EnableHideForDrillDown", LowerBool(area.Format.EnableHideForDrillDown));
            if (area.Kind == "GroupHeader")
            {
                writer.WriteStartElement("GroupAreaFormat");
                Attr(writer, "EnableKeepGroupTogether", "false");
                Attr(writer, "EnableRepeatGroupHeader", LowerBool(area.EnableRepeatGroupHeader));
                Attr(writer, "VisibleGroupNumberPerPage", "");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
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
            if (!string.IsNullOrWhiteSpace(section.Format.EnableSuppressConditionFormula))
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

    private static bool AreaNewPageAfter(CrystalReportAreaModel area, bool reportFooterNewPageAfter)
    {
        return area.Kind switch
        {
            "PageFooter" => true,
            "ReportFooter" => reportFooterNewPageAfter,
            _ => area.Format.EnableNewPageAfter
        };
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
        }

        if (reportObject.ElementName is "TextObject" or "FieldHeadingObject" or "FieldObject")
        {
            WriteColor(writer, "Color", reportObject.Color);
            WriteFont(writer, reportObject.Font);
            writer.WriteStartElement("FontColorConditionFormulas");
            writer.WriteEndElement();
        }

        WriteBorder(writer, reportObject.Border);
        WriteObjectFormat(writer, reportObject.Format);
        writer.WriteStartElement("ObjectFormatConditionFormulas");
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
            .Select(reportObject => reportObject.DataSource)
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
            _ => field.Length
        };
    }

    public static string XmlValueType(int dataType)
    {
        return dataType switch
        {
            2 => "Xsd:shortField",
            4 => "Xsd:longField",
            6 => "Xsd:decimalField",
            7 => "CurrencyField",
            8 => "Xsd:booleanField",
            11 => "Xsd:stringField",
            13 => "PersistentMemoField",
            14 => "BlobField",
            15 => "Xsd:dateTimeField",
            16 => "Xsd:dateField",
            17 => "Xsd:timeField",
            _ => "Xsd:stringField"
        };
    }
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

    public static string XmlValueType(int valueType)
    {
        return valueType switch
        {
            2 => "Xsd:shortField",
            4 => "Xsd:longField",
            6 => "Xsd:decimalField",
            7 => "CurrencyField",
            8 => "Xsd:booleanField",
            9 => "Xsd:dateField",
            10 => "Xsd:timeField",
            11 => "Xsd:stringField",
            13 => "PersistentMemoField",
            14 => "BlobField",
            15 => "Xsd:dateTimeField",
            16 => "Xsd:dateField",
            17 => "Xsd:timeField",
            _ => "Xsd:stringField"
        };
    }
}
