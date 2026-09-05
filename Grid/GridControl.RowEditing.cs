using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using System.Globalization;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    // Both generated row editors use the same FlexCore controls and validation
    // callbacks. An EditTemplate remains entirely owned by its caller.
    private RenderFragment RenderGeneratedRowEditor(GridColumn column) => builder =>
    {
        var field = column.Field;
        var label = HeaderColumnDisplay(column);
        var enabled = column.AllowEditing && !column.IsPrimaryKey;
        if (column.Type is ColumnType.Boolean or ColumnType.CheckBox)
        {
            builder.OpenComponent<CheckBoxControl>(0);
            builder.SetKey((_editItem, field));
            builder.AddAttribute(1, "Checked", GetBoolValue(_editItem, field));
            builder.AddAttribute(2, "CheckedChanged", EventCallback.Factory.Create<bool>(this, value => SetRowEditProperty(field, value)));
            builder.AddAttribute(3, "Disabled", !enabled);
            builder.AddAttribute(4, "Label", label);
            builder.AddAttribute(5, "CssClass", GetRowEditorCss(field, "fx-row-editor-checkbox"));
            builder.AddAttribute(6, "OnKeyDown", EventCallback.Factory.Create<KeyboardEventArgs>(this, HandleRowEditorKeyDown));
            builder.AddAttribute(7, "StopKeyDownPropagation", true);
            builder.CloseComponent();
        }
        else if (column.Type == ColumnType.Date)
        {
            var raw = GetPropertyValue(_editItem, field);
            var date = raw switch
            {
                DateTime value => value,
                DateOnly value => value.ToDateTime(TimeOnly.MinValue),
                DateTimeOffset value => value.DateTime,
                _ => ParseBatchEditDateValue(Convert.ToString(raw, CultureInfo.CurrentCulture))
            };
            builder.OpenComponent<DatePickerControl>(10);
            builder.SetKey((_editItem, field));
            builder.AddAttribute(11, "Value", date);
            builder.AddAttribute(12, "ValueChanged", EventCallback.Factory.Create<DateTime?>(this, value => SetRowEditProperty(field, value)));
            builder.AddAttribute(13, "Enabled", enabled);
            builder.AddAttribute(14, "Format", string.IsNullOrWhiteSpace(column.Format) ? "MM/dd/yyyy" : column.Format);
            builder.AddAttribute(15, "AriaLabel", label);
            builder.AddAttribute(16, "CssClass", GetRowEditorCss(field, "fx-row-editor-date"));
            builder.AddAttribute(17, "Style", "width:100%;");
            builder.AddAttribute(18, "OnKeyDown", EventCallback.Factory.Create<KeyboardEventArgs>(this, HandleRowEditorKeyDown));
            // Keep invalid typed dates visible to row validation as well as the
            // picker, so Enter/Save cannot silently save the previous date.
            builder.AddAttribute(19, "InputValueChanged", (Action<string?>)(value => SetRowEditProperty(field, value)));
            builder.CloseComponent();
        }
        else
        {
            builder.OpenComponent<TextBoxControl>(20);
            builder.SetKey((_editItem, field));
            builder.AddAttribute(21, "Value", GetGeneratedEditorValue(column));
            builder.AddAttribute(22, "ValueChanged", EventCallback.Factory.Create<string?>(this, value => SetRowEditProperty(field, value)));
            builder.AddAttribute(23, "Enabled", enabled);
            builder.AddAttribute(24, "InputType", GetEditorInputType(column));
            builder.AddAttribute(25, "MaxLength", column.MaxLength);
            builder.AddAttribute(26, "UpdateOnInput", true);
            builder.AddAttribute(27, "Uncontrolled", true);
            builder.AddAttribute(28, "CssClass", GetRowEditorCss(field, "fx-row-editor-text"));
            builder.AddAttribute(29, "HtmlAttributes", new Dictionary<string, object>
            {
                ["aria-label"] = label,
                ["style"] = $"width:100%;{GetEditorInputStyle(column)}",
                ["inputmode"] = column.Type == ColumnType.Number ? "decimal" : "text"
            });
            builder.AddAttribute(30, "OnKeyDown", EventCallback.Factory.Create<KeyboardEventArgs>(this, HandleRowEditorKeyDown));
            builder.CloseComponent();
        }
    };

    private async Task HandleRowEditorKeyDown(KeyboardEventArgs args)
    {
        if (!_isEditing)
            return;
        if (args.Key is "Enter" or "NumpadEnter")
            await SaveEdit();
        else if (args.Key == "Escape")
            CancelEdit();
    }
}
