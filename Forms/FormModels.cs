using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Fx.ControlKit;

public enum FormEditorType { Auto, TextBox, TextArea, CheckBox, Switch, DatePicker, TimePicker, DropDownList }
public sealed record FormFieldChangedArgs(object Model, string Field, object? Value);
public sealed class FormFieldContext
{
    public required object Model { get; init; }
    public required PropertyInfo Property { get; init; }
    public required FieldIdentifier Field { get; init; }
    public required Func<object?, Task> SetValueAsync { get; init; }
    public object? Value => Property.GetValue(Model);
}
internal static class FormFields
{
    internal static bool Supports(Type type)
    {
        type=Nullable.GetUnderlyingType(type)??type;
        return type.IsEnum || type==typeof(string) || type==typeof(bool) || type==typeof(Guid)
            || type==typeof(DateTime) || type==typeof(DateOnly) || type==typeof(TimeOnly)
            || type==typeof(decimal) || type==typeof(double) || type==typeof(float)
            || type==typeof(int) || type==typeof(long) || type==typeof(short) || type==typeof(byte)
            || type==typeof(uint) || type==typeof(ulong) || type==typeof(ushort) || type==typeof(sbyte);
    }
    internal static (object Owner, PropertyInfo Property) Resolve(object model, string field)
    {
        var parts=field.Split('.');
        for(var i=0;i<parts.Length;i++)
        {
            var property=model.GetType().GetProperty(parts[i],BindingFlags.Public|BindingFlags.Instance)
                ?? throw new ArgumentException($"Unknown form field '{field}'.");
            if(property.GetIndexParameters().Length!=0 || !property.CanRead) throw new ArgumentException($"Field '{field}' is not readable.");
            if(i==parts.Length-1) return(model,property);
            model=property.GetValue(model) ?? throw new ArgumentException($"Initialize '{parts[i]}' before editing '{field}'.");
        }
        throw new ArgumentException("A form field is required.");
    }
}
