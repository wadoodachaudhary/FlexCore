using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Migration;

internal sealed class RazorEmit
{
    public required string Markup { get; init; }
    public List<ControlSymbol> Controls { get; init; } = [];
    public List<MigrationTodo> Todos { get; init; } = [];
}

internal static class WebFormsRazorEmitter
{
    public static RazorEmit EmitPage(AspxDocument document, string relativePath, string? codeBehind, IReadOnlyDictionary<string, string> routes)
    {
        var controls = new List<ControlSymbol>();
        Collect(document.Root, inTemplate: false, controls, codeBehind ?? "");
        var todos = new List<MigrationTodo>();
        var builder = new StringBuilder();
        var context = new EmitScope(builder, controls, routes, todos, relativePath, inTemplate: false, layout: false);
        var contents = document.Root.Children.Where(child => Is(child, "asp:Content")).ToList();
        if (contents.Count == 0) EmitChildren(document.Root.Children, context);
        else
        {
            foreach (var content in contents)
                EmitChildren(content.Children, context);
        }
        return new RazorEmit { Markup = builder.ToString(), Controls = controls, Todos = todos };
    }

    public static (string HeadLinks, string Body, List<MigrationTodo> Todos) EmitMaster(
        AspxDocument document,
        string relativePath,
        IReadOnlyDictionary<string, string> routes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> bundles)
    {
        var todos = new List<MigrationTodo>();
        var head = new StringBuilder();
        var body = new StringBuilder();
        var headNode = Find(document.Root, "head");
        var bodyNode = Find(document.Root, "body") ?? document.Root;
        if (headNode is not null)
            EmitHead(headNode, head, bundles, routes, todos, relativePath);
        var controls = new List<ControlSymbol>();
        var scope = new EmitScope(body, controls, routes, todos, relativePath, inTemplate: false, layout: true) { Bundles = bundles };
        EmitChildren(UnwrapForm(bodyNode.Children), scope);
        return (head.ToString(), body.ToString(), todos);
    }

    private static void EmitHead(AspxNode head, StringBuilder output, IReadOnlyDictionary<string, IReadOnlyList<string>> bundles, IReadOnlyDictionary<string, string> routes, List<MigrationTodo> todos, string path)
    {
        foreach (var child in head.Children)
        {
            if (child.Kind == AspxKind.Expression)
            {
                output.Append(BundleTags(child.Text, bundles, script: true));
                continue;
            }
            if (child.Kind != AspxKind.Element) continue;
            if (Is(child, "title") || Is(child, "asp:PlaceHolder") || Is(child, "asp:ContentPlaceHolder"))
            {
                var nested = child.Children.FirstOrDefault(item => item.Kind == AspxKind.Expression);
                if (nested is not null) output.Append(BundleTags(nested.Text, bundles, script: true));
                continue;
            }
            if (Is(child, "webopt:BundleReference"))
            {
                var bundlePath = child.Attribute("Path") ?? "";
                output.Append(TagsForVirtual(bundlePath, bundles, script: false));
                continue;
            }
            if (Is(child, "link"))
            {
                var href = child.Attribute("href");
                if (href is null) continue;
                output.Append("    <link rel=\"stylesheet\" href=\"").Append(Virtual(href)).AppendLine("\" />");
            }
        }
        if (output.Length == 0)
            todos.Add(new MigrationTodo(path, "The master page head did not contain a style bundle. Pages use the browser default stylesheet."));
    }

    private static void EmitChildren(IEnumerable<AspxNode> nodes, EmitScope scope)
    {
        foreach (var node in nodes) Emit(node, scope);
    }

    private static void Emit(AspxNode node, EmitScope scope)
    {
        if (node.Kind == AspxKind.Text)
        {
            scope.Output.Append(node.Text.Replace("@", "@@", StringComparison.Ordinal));
            return;
        }
        if (node.Kind == AspxKind.Comment)
        {
            scope.Output.Append(node.Text);
            return;
        }
        if (node.Kind == AspxKind.Expression)
        {
            scope.Output.Append(RenderExpression(node.Text, scope));
            return;
        }
        if (Is(node, "asp:ContentPlaceHolder") && scope.Layout)
        {
            scope.Output.AppendLine("@Body");
            return;
        }
        if (Is(node, "asp:Content"))
        {
            EmitChildren(node.Children, scope);
            return;
        }
        if (Is(node, "asp:ScriptManager") || Is(node, "asp:ScriptManagerProxy"))
        {
            scope.Todos.Add(new MigrationTodo(scope.Path, "ScriptManager and the Web Forms script libraries (WebForms.js, validators, MsAjax) were not copied into the Blazor page. Site script bundles are included when BundleConfig names a file that exists."));
            foreach (var reference in node.Descendants().Where(child => Is(child, "asp:ScriptReference")))
            {
                var virtualPath = reference.Attribute("Path");
                if (!string.IsNullOrEmpty(virtualPath))
                    scope.Output.Append(TagsForVirtual(virtualPath, scope.Bundles, script: true));
                else
                    scope.Todos.Add(new MigrationTodo(scope.Path, "ScriptReference '" + (reference.Attribute("Name") ?? "") + "' is a framework script and was not emitted."));
            }
            return;
        }
        if (Is(node, "webopt:BundleReference"))
        {
            scope.Output.Append(TagsForVirtual(node.Attribute("Path") ?? "", scope.Bundles, script: false));
            return;
        }
        if (IsPrefix(node, "asp:"))
        {
            EmitServerControl(node, scope);
            return;
        }
        if (node.Name.Contains(':', StringComparison.Ordinal))
        {
            var directive = node.Name;
            scope.Todos.Add(new MigrationTodo(scope.Path, "Custom control <" + directive + "> was not mapped to a Blazor component. Its inner markup was kept."));
            scope.Output.Append("<!-- TODO: ").Append(directive).AppendLine(" was not converted. -->");
            EmitChildren(node.Children, scope);
            return;
        }
        EmitHtml(node, scope);
    }

    private static void EmitServerControl(AspxNode node, EmitScope scope)
    {
        var kind = KindOf(node.Name);
        var id = node.Attribute("ID") ?? node.Attribute("Id");
        var control = id is null ? null : scope.Controls.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        switch (kind)
        {
            case "List":
            case "Repeater":
                EmitRepeater(node, scope, control);
                return;
            case "Grid":
                EmitGrid(node, scope, control);
                return;
            case "Form":
            case "Details":
                EmitDetails(node, scope, control);
                return;
            case "TextBox":
                EmitInput(node, scope, control, area: node.Attribute("TextMode")?.Equals("MultiLine", StringComparison.OrdinalIgnoreCase) == true);
                return;
            case "DropDown":
                EmitSelect(node, scope, control);
                return;
            case "Button":
            case "LinkButton":
                EmitButton(node, scope, control);
                return;
            case "HyperLink":
                EmitAnchor(node, scope, control, node.Attribute("NavigateUrl"), node.Children, node.Attribute("Text"));
                return;
            case "Label":
            case "Literal":
                scope.Output.Append("<span");
                AppendClass(node, scope.Output);
                scope.Output.Append('>');
                if (node.Attribute("Text") is { } labelText) scope.Output.Append(RenderMixed(labelText, scope));
                else EmitChildren(node.Children, scope);
                scope.Output.Append("</span>");
                return;
            case "Image":
                scope.Output.Append("<img");
                AppendClass(node, scope.Output);
                var image = node.Attribute("ImageUrl") ?? "";
                scope.Output.Append(" src=\"").Append(RenderMixed(image, scope)).Append("\" alt=\"\" />");
                return;
            case "CheckBox":
                scope.Output.Append("<input type=\"checkbox\"");
                if (control is not null) scope.Output.Append(" @bind=\"").Append(control.Field).Append('"');
                AppendClass(node, scope.Output);
                scope.Output.Append(" />");
                scope.Todos.Add(new MigrationTodo(scope.Path, "CheckBox " + (id ?? "") + " is a checkbox. Its postback event was not wired."));
                return;
            case "Radio":
                scope.Output.Append("<input type=\"radio\"");
                AppendClass(node, scope.Output);
                scope.Output.Append(" />");
                scope.Todos.Add(new MigrationTodo(scope.Path, "RadioButton " + (id ?? "") + " was emitted as a radio input without a group binding."));
                return;
            case "Validator":
                return;
            case "PlaceHolder":
            case "Panel":
                EmitChildren(node.Children, scope);
                return;
            default:
                scope.Todos.Add(new MigrationTodo(scope.Path, node.Name + (id is null ? "" : " #" + id) + " has no Blazor mapping. Inner markup was kept."));
                scope.Output.Append("<!-- TODO: ").Append(node.Name).AppendLine(" was not converted. -->");
                EmitChildren(node.Children, scope);
                return;
        }
    }

    private static void EmitRepeater(AspxNode node, EmitScope scope, ControlSymbol? control)
    {
        var field = control?.Field ?? "items";
        var empty = Child(node, "EmptyDataTemplate", "EmptyItemTemplate");
        var layout = Child(node, "LayoutTemplate");
        var item = Child(node, "ItemTemplate");
        var placeholder = node.Attribute("ItemPlaceholderID") ?? "itemPlaceholder";
        scope.Output.Append("@if (!(").Append(field).Append("?.Any() ?? false))").AppendLine();
        scope.Output.AppendLine("{");
        if (empty is not null) EmitChildren(empty.Children, scope);
        else scope.Output.AppendLine("    <p>No data was returned.</p>");
        scope.Output.AppendLine("}");
        scope.Output.AppendLine("else");
        scope.Output.AppendLine("{");
        if (layout is not null) EmitLayout(layout, item, placeholder, field, scope);
        else
        {
            scope.Output.Append("@foreach (var item in ").Append(field).AppendLine(")");
            scope.Output.AppendLine("{");
            if (item is not null) EmitChildren(item.Children, scope.ForTemplate());
            scope.Output.AppendLine("}");
        }
        var alternating = Child(node, "AlternatingItemTemplate");
        var edit = Child(node, "EditItemTemplate");
        var insert = Child(node, "InsertItemTemplate");
        if (alternating is not null)
            scope.Todos.Add(new MigrationTodo(scope.Path, (control?.Id ?? node.Name) + " AlternatingItemTemplate was not converted; ItemTemplate is used for every row."));
        if (edit is not null || insert is not null)
            scope.Todos.Add(new MigrationTodo(scope.Path, (control?.Id ?? node.Name) + " edit/insert templates were not converted."));
        scope.Output.AppendLine("}");
    }

    private static void EmitLayout(AspxNode layout, AspxNode? item, string placeholder, string field, EmitScope scope)
    {
        foreach (var child in layout.Children)
        {
            if (child.Kind == AspxKind.Element && Is(child, "asp:PlaceHolder") && (child.Attribute("ID") ?? "").Equals(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                scope.Output.Append("@foreach (var item in ").Append(field).AppendLine(")");
                scope.Output.AppendLine("{");
                if (item is not null) EmitChildren(item.Children, scope.ForTemplate());
                scope.Output.AppendLine("}");
                continue;
            }
            if (child.Kind == AspxKind.Element && child.Children.Count > 0 && child.Descendants().Any(node => Is(node, "asp:PlaceHolder")))
            {
                scope.Output.Append('<').Append(child.Name);
                EmitHtmlAttributes(child, scope);
                scope.Output.Append('>');
                EmitLayout(child, item, placeholder, field, scope);
                scope.Output.Append("</").Append(child.Name).Append('>');
                continue;
            }
            Emit(child, scope);
        }
    }

    private static void EmitGrid(AspxNode node, EmitScope scope, ControlSymbol? control)
    {
        var field = control?.Field ?? "rows";
        var columns = node.Descendants().Where(child => Is(child, "asp:BoundField") || Is(child, "asp:TemplateField") || Is(child, "asp:CommandField") || Is(child, "asp:HyperLinkField")).ToList();
        if (columns.Count == 0)
        {
            scope.Todos.Add(new MigrationTodo(scope.Path, (control?.Id ?? "GridView") + " has no columns. Auto-generated columns were not inferred."));
            scope.Output.Append("<!-- TODO: ").Append(node.Name).AppendLine(" columns were not converted. -->");
            return;
        }
        scope.Output.AppendLine("<table class=\"table\">");
        scope.Output.AppendLine("<thead><tr>");
        foreach (var column in columns)
        {
            var header = column.Attribute("HeaderText") ?? "";
            scope.Output.Append("<th>").Append(header.Replace("@", "@@", StringComparison.Ordinal)).AppendLine("</th>");
        }
        scope.Output.AppendLine("</tr></thead>");
        scope.Output.Append("<tbody>@foreach (var item in ").Append(field).AppendLine("){<tr>");
        var row = scope.ForTemplate();
        foreach (var column in columns)
        {
            scope.Output.Append("<td>");
            if (Is(column, "asp:BoundField"))
            {
                var data = column.Attribute("DataField") ?? "";
                scope.Output.Append("@item.").Append(data);
            }
            else if (Is(column, "asp:TemplateField"))
            {
                var template = Child(column, "ItemTemplate");
                if (template is not null) EmitChildren(template.Children, row);
            }
            else if (Is(column, "asp:HyperLinkField"))
            {
                scope.Output.Append("<a href=\"@item.").Append(column.Attribute("DataNavigateUrlFields") ?? "Id").Append("\">")
                    .Append(column.Attribute("Text") ?? "Open").Append("</a>");
                scope.Todos.Add(new MigrationTodo(scope.Path, "HyperLinkField DataNavigateUrlFormatString was simplified to the raw field."));
            }
            else
            {
                scope.Output.Append("<!-- TODO: CommandField was not converted. -->");
                scope.Todos.Add(new MigrationTodo(scope.Path, "GridView CommandField was not converted."));
            }
            scope.Output.Append("</td>");
        }
        scope.Output.AppendLine("</tr>}</tbody></table>");
    }

    private static void EmitDetails(AspxNode node, EmitScope scope, ControlSymbol? control)
    {
        var template = Child(node, "ItemTemplate");
        if (template is not null)
        {
            scope.Output.AppendLine("@{ var item = " + (control?.Field ?? "item") + "; }");
            EmitChildren(template.Children, scope.ForTemplate());
            return;
        }
        var fields = node.Descendants().Where(child => Is(child, "asp:BoundField")).ToList();
        if (fields.Count == 0)
        {
            scope.Todos.Add(new MigrationTodo(scope.Path, node.Name + " without ItemTemplate or BoundField was left empty."));
            scope.Output.Append("<!-- TODO: ").Append(node.Name).AppendLine(" was not converted. -->");
            return;
        }
        scope.Output.AppendLine("<dl>");
        foreach (var field in fields)
        {
            scope.Output.Append("<dt>").Append(field.Attribute("HeaderText") ?? field.Attribute("DataField")).Append("</dt><dd>@")
                .Append(control?.Field ?? "item").Append('.').Append(field.Attribute("DataField")).AppendLine("</dd>");
        }
        scope.Output.AppendLine("</dl>");
    }

    private static void EmitInput(AspxNode node, EmitScope scope, ControlSymbol? control, bool area)
    {
        var tag = area ? "textarea" : "input";
        scope.Output.Append('<').Append(tag);
        if (!area) scope.Output.Append(" type=\"text\"");
        if (control is not null) scope.Output.Append(" @bind=\"").Append(control.Field).Append('"');
        AppendClass(node, scope.Output);
        if (node.Attribute("ReadOnly")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) scope.Output.Append(" readonly");
        if (node.Attribute("ToolTip") is { } tip) scope.Output.Append(" title=\"").Append(Attr(tip)).Append('"');
        if (area)
        {
            scope.Output.Append('>');
            if (control is null && node.Attribute("Text") is { } text) scope.Output.Append(Attr(text));
            scope.Output.Append("</textarea>");
        }
        else scope.Output.Append(" />");
    }

    private static void EmitSelect(AspxNode node, EmitScope scope, ControlSymbol? control)
    {
        scope.Output.Append("<select");
        if (control is not null) scope.Output.Append(" @bind=\"").Append(control.Field).Append('"');
        AppendClass(node, scope.Output);
        scope.Output.Append('>');
        if (control is not null)
        {
            var source = control.SelectMethod is not null ? control.SelectMethod + "()" : control.ItemsField;
            var value = control.DataValueField ?? "Id";
            var text = control.DataTextField ?? value;
            scope.Output.Append("@foreach (var option in ").Append(source).Append("){<option value=\"@option.").Append(value).Append("\">@option.").Append(text).Append("</option>}");
        }
        else
        {
            scope.Todos.Add(new MigrationTodo(scope.Path, "DropDownList without an ID was not given options."));
        }
        scope.Output.Append("</select>");
    }

    private static void EmitButton(AspxNode node, EmitScope scope, ControlSymbol? control)
    {
        var handler = node.Attribute("OnClick") ?? control?.OnClick;
        scope.Output.Append("<button type=\"button\"");
        if (!string.IsNullOrEmpty(handler)) scope.Output.Append(" @onclick=\"").Append(handler).Append('"');
        else scope.Todos.Add(new MigrationTodo(scope.Path, "Button " + (node.Attribute("ID") ?? "") + " has no OnClick handler."));
        AppendClass(node, scope.Output);
        scope.Output.Append('>');
        scope.Output.Append((node.Attribute("Text") ?? "Submit").Replace("@", "@@", StringComparison.Ordinal));
        scope.Output.Append("</button>");
    }

    private static void EmitAnchor(AspxNode node, EmitScope scope, ControlSymbol? control, string? navigate, List<AspxNode> children, string? text)
    {
        scope.Output.Append("<a");
        if (control?.HrefMutated == true)
            scope.Output.Append(" href=\"@").Append(control.HrefField).Append('"');
        else if (!string.IsNullOrEmpty(navigate))
            scope.Output.Append(" href=\"").Append(RenderMixed(navigate, scope)).Append('"');
        else scope.Output.Append(" href=\"#\"");
        if (control?.ClassMutated == true)
            scope.Output.Append(" class=\"@").Append(control.ClassField).Append('"');
        else AppendClass(node, scope.Output);
        scope.Output.Append('>');
        if (children.Count > 0) EmitChildren(children, scope);
        else scope.Output.Append((text ?? "").Replace("@", "@@", StringComparison.Ordinal));
        scope.Output.Append("</a>");
    }

    private static void EmitHtml(AspxNode node, EmitScope scope)
    {
        if (node.Name.StartsWith('/')) return;
        var name = node.Name.Equals("image", StringComparison.OrdinalIgnoreCase) ? "img" : node.Name;
        scope.Output.Append('<').Append(name);
        EmitHtmlAttributes(node, scope);
        var self = node.SelfClosing || AspxSyntaxIsVoid(node.Name);
        if (self && node.Children.Count == 0)
        {
            scope.Output.Append(" />");
            return;
        }
        scope.Output.Append('>');
        EmitChildren(node.Children, scope);
        scope.Output.Append("</").Append(name).Append('>');
    }

    private static void EmitHtmlAttributes(AspxNode node, EmitScope scope)
    {
        foreach (var attribute in node.Attributes)
        {
            if (attribute.Name.Equals("runat", StringComparison.OrdinalIgnoreCase)) continue;
            var value = attribute.Value.Replace("~/", "/", StringComparison.Ordinal);
            if (attribute.Name.Equals("href", StringComparison.OrdinalIgnoreCase) && value is "~" or "~/")
                value = "/";
            if (attribute.Name.Equals("href", StringComparison.OrdinalIgnoreCase) && value == "~")
                value = "/";
            scope.Output.Append(' ').Append(attribute.Name).Append("=\"").Append(RenderMixed(value, scope)).Append('"');
        }
    }

    private static string RenderMixed(string value, EmitScope scope)
    {
        if (!value.Contains("<%", StringComparison.Ordinal))
        {
            if (value is "~" or "~/") return "/";
            return Attr(value.Replace("~/", "/", StringComparison.Ordinal));
        }
        var parts = new List<string>();
        var index = 0;
        while (index < value.Length)
        {
            var start = value.IndexOf("<%", index, StringComparison.Ordinal);
            if (start < 0)
            {
                if (index < value.Length) parts.Add(CSharpIds.Quote(Tilde(value[index..])));
                break;
            }
            if (start > index) parts.Add(CSharpIds.Quote(Tilde(value[index..start])));
            var end = value.IndexOf("%>", start + 2, StringComparison.Ordinal);
            if (end < 0) break;
            parts.Add("(" + WebFormsCodeBehind.ExpressionToCSharp(value[(start + 2)..end], scope.InTemplate, scope.Routes) + ")");
            index = end + 2;
        }
        return "@(" + string.Join(" + ", parts) + ")";
    }

    private static string RenderExpression(string raw, EmitScope scope)
    {
        if (raw.Contains("Scripts.Render", StringComparison.Ordinal) || raw.Contains("Styles.Render", StringComparison.Ordinal))
            return BundleTags(raw, scope.Bundles, raw.Contains("Scripts.Render", StringComparison.Ordinal));
        var code = WebFormsCodeBehind.ExpressionToCSharp(raw, scope.InTemplate, scope.Routes);
        if (Regex.IsMatch(code, @"^[A-Za-z_][\w\.]*$")) return "@" + code;
        return "@(" + code + ")";
    }

    private static string BundleTags(string expression, IReadOnlyDictionary<string, IReadOnlyList<string>> bundles, bool script)
    {
        var match = Regex.Match(expression, @"Render\(\s*""([^""]+)""\s*\)");
        return match.Success ? TagsForVirtual(match.Groups[1].Value, bundles, script) : "";
    }

    private static string TagsForVirtual(string virtualPath, IReadOnlyDictionary<string, IReadOnlyList<string>> bundles, bool script)
    {
        if (virtualPath.Contains("/WebForms/", StringComparison.OrdinalIgnoreCase) || virtualPath.Contains("MsAjax", StringComparison.OrdinalIgnoreCase))
            return "";
        if (virtualPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || virtualPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            var href = virtualPath.StartsWith('/') ? virtualPath : "/" + virtualPath.TrimStart('~', '/');
            return script || href.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                ? "    <script src=\"" + href + "\"></script>\n"
                : "    <link rel=\"stylesheet\" href=\"" + href + "\" />\n";
        }
        if (!bundles.TryGetValue(virtualPath, out var files) && !bundles.TryGetValue(virtualPath.TrimStart('~'), out files))
            return "<!-- TODO: bundle " + virtualPath + " was not found. -->";
        var builder = new StringBuilder();
        foreach (var file in files)
        {
            var href = file.StartsWith('/') ? file : "/" + file.TrimStart('~', '/');
            if (script || href.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                builder.Append("    <script src=\"").Append(href).AppendLine("\"></script>");
            else
                builder.Append("    <link rel=\"stylesheet\" href=\"").Append(href).AppendLine("\" />");
        }
        return builder.ToString();
    }

    private static void Collect(AspxNode node, bool inTemplate, List<ControlSymbol> controls, string codeBehind)
    {
        if (node.Kind != AspxKind.Element)
        {
            return;
        }
        var template = inTemplate || IsTemplate(node.Name);
        if (IsPrefix(node, "asp:") && !template && node.Attribute("ID") is { } id && KindOf(node.Name) is { } kind && kind is not "Validator" and not "PlaceHolder" and not "Panel" and not "Content")
        {
            var symbol = new ControlSymbol
            {
                Id = id,
                Field = CSharpIds.Field(id),
                Kind = kind,
                ItemType = node.Attribute("ItemType"),
                DataTextField = node.Attribute("DataTextField"),
                DataValueField = node.Attribute("DataValueField"),
                SelectMethod = node.Attribute("SelectMethod"),
                CssClass = node.Attribute("CssClass"),
                OnClick = node.Attribute("OnClick"),
                ReadOnly = node.Attribute("ReadOnly")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
                ToolTip = node.Attribute("ToolTip"),
                NavigateExpression = node.Attribute("NavigateUrl"),
                HrefMutated = codeBehind.Contains(id + ".NavigateUrl", StringComparison.Ordinal),
                ClassMutated = codeBehind.Contains(id + ".CssClass", StringComparison.Ordinal),
                ItemsMutated = codeBehind.Contains(id + ".DataSource", StringComparison.Ordinal)
            };
            var text = node.Attribute("Text");
            if (text is not null && text.Contains("<%", StringComparison.Ordinal)) symbol = CopyText(symbol, text);
            else if (node.Children.Count == 1 && node.Children[0].Kind == AspxKind.Text && kind == "TextBox")
                symbol = CopyLiteral(symbol, node.Children[0].Text.Trim());
            else if (text is not null && kind == "TextBox") symbol = CopyLiteral(symbol, text);
            controls.Add(symbol);
        }
        if (Is(node.Name, "asp:RequiredFieldValidator") || Is(node.Name, "asp:RangeValidator") || Is(node.Name, "asp:CompareValidator") || Is(node.Name, "asp:RegularExpressionValidator"))
        {
            var target = node.Attribute("ControlToValidate");
            var owner = controls.LastOrDefault(control => control.Id.Equals(target, StringComparison.OrdinalIgnoreCase));
            owner?.Validators.Add(new ValidatorSymbol
            {
                Kind = node.Name.Split(':').Last(),
                Message = node.Attribute("ErrorMessage") ?? node.Attribute("Text"),
                Type = node.Attribute("Type"),
                Minimum = node.Attribute("MinimumValue"),
                Maximum = node.Attribute("MaximumValue"),
                Pattern = node.Attribute("ValidationExpression"),
                Control = target
            });
        }
        foreach (var child in node.Children)
            Collect(child, template && !Is(node, "asp:ListView") && !Is(node, "asp:Repeater") && !Is(node, "asp:GridView") && !Is(node, "asp:FormView") && !Is(node, "asp:DetailsView") || IsTemplate(node.Name), controls, codeBehind);
    }

    private static ControlSymbol CopyText(ControlSymbol symbol, string expression) => new()
    {
        Id = symbol.Id, Field = symbol.Field, Kind = symbol.Kind, ItemType = symbol.ItemType, DataTextField = symbol.DataTextField,
        DataValueField = symbol.DataValueField, SelectMethod = symbol.SelectMethod, CssClass = symbol.CssClass, OnClick = symbol.OnClick,
        ReadOnly = symbol.ReadOnly, ToolTip = symbol.ToolTip, NavigateExpression = symbol.NavigateExpression, HrefMutated = symbol.HrefMutated,
        ClassMutated = symbol.ClassMutated, ItemsMutated = symbol.ItemsMutated, TextExpression = expression
    };

    private static ControlSymbol CopyLiteral(ControlSymbol symbol, string literal) => new()
    {
        Id = symbol.Id, Field = symbol.Field, Kind = symbol.Kind, ItemType = symbol.ItemType, DataTextField = symbol.DataTextField,
        DataValueField = symbol.DataValueField, SelectMethod = symbol.SelectMethod, CssClass = symbol.CssClass, OnClick = symbol.OnClick,
        ReadOnly = symbol.ReadOnly, ToolTip = symbol.ToolTip, NavigateExpression = symbol.NavigateExpression, HrefMutated = symbol.HrefMutated,
        ClassMutated = symbol.ClassMutated, ItemsMutated = symbol.ItemsMutated, LiteralText = literal
    };

    private static bool IsTemplate(string name) => name.EndsWith("Template", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<AspxNode> UnwrapForm(List<AspxNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == AspxKind.Element && node.Name.Equals("form", StringComparison.OrdinalIgnoreCase) && node.HasAttribute("runat"))
            {
                foreach (var child in UnwrapForm(node.Children)) yield return child;
            }
            else yield return node;
        }
    }

    private static AspxNode? Find(AspxNode node, string name)
    {
        if (node.Kind == AspxKind.Element && node.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return node;
        foreach (var child in node.Children)
        {
            var found = Find(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    private static AspxNode? Child(AspxNode node, params string[] names) =>
        node.Children.FirstOrDefault(child => names.Any(name => Is(child, name)));

    private static void AppendClass(AspxNode node, StringBuilder output)
    {
        var css = node.Attribute("CssClass") ?? node.Attribute("class");
        if (!string.IsNullOrEmpty(css)) output.Append(" class=\"").Append(Attr(css)).Append('"');
    }

    private static string KindOf(string name) => name.ToLowerInvariant() switch
    {
        "asp:listview" => "List",
        "asp:repeater" => "Repeater",
        "asp:gridview" => "Grid",
        "asp:datagrid" => "Grid",
        "asp:formview" => "Form",
        "asp:detailsview" => "Details",
        "asp:textbox" => "TextBox",
        "asp:dropdownlist" => "DropDown",
        "asp:button" => "Button",
        "asp:linkbutton" => "LinkButton",
        "asp:hyperlink" => "HyperLink",
        "asp:label" => "Label",
        "asp:literal" => "Literal",
        "asp:image" => "Image",
        "asp:checkbox" => "CheckBox",
        "asp:radiobutton" => "Radio",
        "asp:requiredfieldvalidator" or "asp:rangevalidator" or "asp:comparevalidator" or "asp:regularexpressionvalidator" or "asp:validationsummary" => "Validator",
        "asp:placeholder" => "PlaceHolder",
        "asp:panel" => "Panel",
        "asp:content" => "Content",
        _ => "Other"
    };

    private static bool Is(AspxNode node, string name) => node.Kind == AspxKind.Element && node.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    private static bool Is(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static bool IsPrefix(AspxNode node, string prefix) => node.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    private static string Attr(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("@", "@@", StringComparison.Ordinal);
    private static string Virtual(string href) => href.Replace("~/", "/", StringComparison.Ordinal).Replace("@", "@@", StringComparison.Ordinal);
    private static string Tilde(string value) => value.Replace("~/", "/", StringComparison.Ordinal) is "~" or "~/" ? "/" : value.Replace("~/", "/", StringComparison.Ordinal);
    private static bool AspxSyntaxIsVoid(string name) => name.Equals("br", StringComparison.OrdinalIgnoreCase) || name.Equals("img", StringComparison.OrdinalIgnoreCase) || name.Equals("image", StringComparison.OrdinalIgnoreCase) || name.Equals("hr", StringComparison.OrdinalIgnoreCase) || name.Equals("input", StringComparison.OrdinalIgnoreCase) || name.Equals("meta", StringComparison.OrdinalIgnoreCase) || name.Equals("link", StringComparison.OrdinalIgnoreCase);

    private sealed class EmitScope
    {
        public EmitScope(StringBuilder output, List<ControlSymbol> controls, IReadOnlyDictionary<string, string> routes, List<MigrationTodo> todos, string path, bool inTemplate, bool layout)
        {
            Output = output;
            Controls = controls;
            Routes = routes;
            Todos = todos;
            Path = path;
            InTemplate = inTemplate;
            Layout = layout;
        }

        public StringBuilder Output { get; }
        public List<ControlSymbol> Controls { get; }
        public IReadOnlyDictionary<string, string> Routes { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Bundles { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
        public List<MigrationTodo> Todos { get; }
        public string Path { get; }
        public bool InTemplate { get; }
        public bool Layout { get; }
        public EmitScope ForTemplate() => new(Output, Controls, Routes, Todos, Path, true, Layout) { Bundles = Bundles };
    }
}

internal static class AspxNodeExtensions
{
    public static IEnumerable<AspxNode> Descendants(this AspxNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var nested in child.Descendants()) yield return nested;
        }
    }
}
