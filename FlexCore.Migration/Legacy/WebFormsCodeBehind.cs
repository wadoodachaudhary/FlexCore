using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Migration;

/// <summary>Turns a Web Forms code-behind into the <c>@code</c> block of a Razor component.</summary>
internal static class WebFormsCodeBehind
{
    private static readonly Regex MethodRegex = new(
        @"^(?<indent>\s*)(?:(?:public|protected|private|internal)\s+)?(?:(?:static|virtual|override|async|new|sealed|partial|unsafe)\s+)*(?<ret>[\w<>,\.\[\]\?]+(?:\s+[\w<>,\.\[\]\?]+)*)\s+(?<name>\w+)\s*\((?<args>[^)]*)\)\s*\{",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex FieldRegex = new(
        @"^\s*(?<vis>public|protected|private)\s+(?<mods>(?:static|readonly|const)\s+)*(?<type>[\w<>,\.\[\]\?]+)\s+(?<name>\w+)\s*(?:=\s*(?<init>[^;]+))?;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PropertyRegex = new(
        @"^\s*(?<vis>public|protected|private)\s+(?:static\s+)?(?<type>[\w<>,\.\[\]\?]+)\s+(?<name>\w+)\s*\{\s*get;\s*(?:private\s+|protected\s+)?set;\s*\}",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static TranslatedPage Translate(
        string? source,
        string relativePath,
        IReadOnlyList<ControlSymbol> controls,
        IReadOnlyDictionary<string, string> routes,
        IReadOnlyList<string> pageRouteTemplates,
        List<MigrationTodo> todos)
    {
        source ??= "";
        var parameters = RouteParameters(source, pageRouteTemplates);
        var services = new List<(string Type, string Name)>();
        var state = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in PropertyRegex.Matches(source).Cast<Match>())
        {
            var type = property.Groups["type"].Value;
            var name = property.Groups["name"].Value;
            if (!seen.Add(name) || controls.Any(control => control.Id == name)) continue;
            if (IsService(type))
            {
                services.Add((type, name));
                continue;
            }
            if (type.Contains("WebControls", StringComparison.Ordinal) || type is "ILog" or "Page") continue;
            state.Append("    private ").Append(type).Append(' ').Append(name).Append(" { get; set; }").AppendLine();
        }
        foreach (var field in FieldRegex.Matches(source).Cast<Match>())
        {
            var type = field.Groups["type"].Value;
            var name = field.Groups["name"].Value;
            if (!seen.Add(name) || controls.Any(control => control.Id == name)) continue;
            if (type is "ILog" || type.Contains("WebControls", StringComparison.Ordinal) || type.Contains("HtmlControls", StringComparison.Ordinal))
                continue;
            if (field.Groups["mods"].Value.Contains("const", StringComparison.Ordinal))
            {
                state.Append("    private const ").Append(type).Append(' ').Append(name).Append(" = ").Append(field.Groups["init"].Value.Trim()).Append(';').AppendLine();
                continue;
            }
            var init = field.Groups["init"].Success ? " = " + field.Groups["init"].Value.Trim() : "";
            state.Append("    private ").Append(type).Append(' ').Append(name).Append(init).Append(';').AppendLine();
        }

        foreach (var control in controls)
        {
            switch (control.Kind)
            {
                case "TextBox":
                    var initial = string.IsNullOrEmpty(control.LiteralText) ? "\"\"" : CSharpIds.Quote(control.LiteralText);
                    state.Append("    private string ").Append(control.Field).Append(" = ").Append(initial).Append(';').AppendLine();
                    break;
                case "DropDown":
                    state.Append("    private string ").Append(control.Field).Append(" = \"\";").AppendLine();
                    var itemType = ShortType(control.ItemType) ?? "object";
                    state.Append("    private IEnumerable<").Append(itemType).Append("> ").Append(control.ItemsField)
                        .Append(" = Array.Empty<").Append(itemType).Append(">();").AppendLine();
                    break;
                case "List":
                case "Grid":
                case "Repeater":
                    var rowType = ShortType(control.ItemType) ?? "object";
                    state.Append("    private IEnumerable<").Append(rowType).Append("> ").Append(control.Field)
                        .Append(" = Array.Empty<").Append(rowType).Append(">();").AppendLine();
                    break;
                case "HyperLink":
                    if (control.HrefMutated)
                        state.Append("    private string ").Append(control.HrefField).Append(" = \"\";").AppendLine();
                    if (control.ClassMutated)
                        state.Append("    private string ").Append(control.ClassField).Append(" = ").Append(CSharpIds.Quote(control.CssClass ?? "")).Append(';').AppendLine();
                    break;
                case "Label":
                    state.Append("    private string ").Append(control.Field).Append(" = \"\";").AppendLine();
                    break;
            }
        }
        if (controls.Any(control => control.Validators.Count > 0))
            state.AppendLine("    private readonly List<string> migrationErrors = new();");

        var methods = ExtractMethods(source);
        var needsNavigation = false;
        var body = new StringBuilder();
        if (methods.Count == 0 && source.Contains("class ", StringComparison.Ordinal))
        {
            todos.Add(new MigrationTodo(relativePath, "Code-behind methods could not be parsed. The page renders converted markup only."));
        }
        foreach (var method in methods)
        {
            if (method.Name is "Dispose") continue;
            var translated = TranslateBody(method.Body, controls, routes, parameters, relativePath, todos, ref needsNavigation);
            var returnType = method.Name == "Page_Load" ? "void" : method.ReturnType;
            var name = method.Name == "Page_Load" ? "OnParametersSet" : method.Name;
            var signature = method.Name is "Page_Load" || method.Name.EndsWith("_Click", StringComparison.Ordinal)
                ? ""
                : method.Arguments;
            if (method.Name == "Page_Load")
            {
                var reset = new StringBuilder();
                foreach (var control in controls.Where(control => control.ClassMutated))
                    reset.Append("        ").Append(control.ClassField).Append(" = ").Append(CSharpIds.Quote(control.CssClass ?? "")).Append(';').AppendLine();
                translated = reset + translated.TrimEnd() + Environment.NewLine + BindingsAndDefaults(controls, source);
                body.Append("    protected override void OnParametersSet()").AppendLine();
            }
            else
            {
                body.Append("    private ").Append(returnType).Append(' ').Append(name).Append('(').Append(signature).Append(')').AppendLine();
            }
            body.AppendLine("    {");
            body.Append(translated);
            if (!translated.EndsWith('\n')) body.AppendLine();
            body.AppendLine("    }");
            body.AppendLine();
        }
        if (!methods.Any(method => method.Name == "Page_Load") && controls.Any(control => control.Kind == "DropDown" && control.SelectMethod is not null || control.TextExpression is not null))
        {
            body.AppendLine("    protected override void OnParametersSet()");
            body.AppendLine("    {");
            body.Append(BindingsAndDefaults(controls, source));
            body.AppendLine("    }");
        }
        if (controls.Any(control => control.Validators.Count > 0))
        {
            body.AppendLine("    private bool ValidateFields()");
            body.AppendLine("    {");
            body.AppendLine("        migrationErrors.Clear();");
            foreach (var control in controls)
            {
                foreach (var validator in control.Validators)
                    body.Append(ValidatorCheck(control, validator, relativePath, todos));
            }
            body.AppendLine("        return migrationErrors.Count == 0;");
            body.AppendLine("    }");
        }

        return new TranslatedPage
        {
            Code = state.ToString() + body,
            Todos = todos,
            Services = services,
            Parameters = parameters,
            NeedsNavigation = needsNavigation,
            NeedsValidation = controls.Any(control => control.Validators.Count > 0)
        };
    }

    private static string BindingsAndDefaults(IReadOnlyList<ControlSymbol> controls, string source)
    {
        var builder = new StringBuilder();
        foreach (var control in controls.Where(control => control.Kind == "TextBox" && control.TextExpression is not null))
        {
            var expression = ExpressionToCSharp(control.TextExpression ?? "", inTemplate: false, new Dictionary<string, string>());
            builder.Append("        ").Append(control.Field).Append(" = System.Convert.ToString(").Append(expression).Append(") ?? \"\";").AppendLine();
        }
        foreach (var control in controls.Where(control => control.Kind == "DropDown" && control.SelectMethod is not null))
        {
            var value = control.DataValueField ?? "Id";
            builder.Append("        if (string.IsNullOrEmpty(").Append(control.Field).Append("))").AppendLine();
            builder.Append("        {").AppendLine();
            builder.Append("            var first").Append(control.Field).Append(" = ").Append(control.SelectMethod).Append("().FirstOrDefault();").AppendLine();
            builder.Append("            if (first").Append(control.Field).Append(" != null) ").Append(control.Field).Append(" = first").Append(control.Field).Append('.').Append(value).Append(".ToString();").AppendLine();
            builder.Append("        }").AppendLine();
        }
        return builder.ToString();
    }

    private static string ValidatorCheck(ControlSymbol control, ValidatorSymbol validator, string path, List<MigrationTodo> todos)
    {
        var message = CSharpIds.Quote(validator.Message ?? "Invalid value.");
        var field = control.Field;
        if (validator.Kind.Equals("RequiredFieldValidator", StringComparison.OrdinalIgnoreCase))
            return $"        if (string.IsNullOrWhiteSpace({field})) migrationErrors.Add({message});{Environment.NewLine}";
        if (validator.Kind.Equals("RangeValidator", StringComparison.OrdinalIgnoreCase))
        {
            var type = validator.Type ?? "String";
            var min = validator.Minimum ?? "0";
            var max = validator.Maximum ?? "0";
            if (type.Equals("Integer", StringComparison.OrdinalIgnoreCase))
                return $"        if (!int.TryParse({field}, out var {field}Number) || {field}Number < {min} || {field}Number > {max}) migrationErrors.Add({message});{Environment.NewLine}";
            if (type.Equals("Double", StringComparison.OrdinalIgnoreCase) || type.Equals("Currency", StringComparison.OrdinalIgnoreCase))
                return $"        if (!decimal.TryParse({field}, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var {field}Number) || {field}Number < {min}m || {field}Number > {max}m) migrationErrors.Add({message});{Environment.NewLine}";
        }
        if (validator.Kind.Equals("RegularExpressionValidator", StringComparison.OrdinalIgnoreCase) && validator.Pattern is not null)
            return $"        if (!System.Text.RegularExpressions.Regex.IsMatch({field} ?? \"\", {CSharpIds.Quote(validator.Pattern)})) migrationErrors.Add({message});{Environment.NewLine}";
        todos.Add(new MigrationTodo(path, $"{validator.Kind} on {control.Id} was not converted to a check."));
        return $"        // TODO: {validator.Kind} on {control.Id} was not converted.{Environment.NewLine}";
    }

    private static List<(string Name, string ClrType, bool Optional)> RouteParameters(string source, IReadOnlyList<string> templates)
    {
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in templates)
        {
            var tokens = Regex.Matches(template, @"\{(\w+)\}").Select(match => match.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
                names.TryAdd(token, false);
        }
        if (templates.Count > 0)
        {
            foreach (var name in names.Keys.ToList())
            {
                var onAll = templates.All(template => Regex.IsMatch(template, @"\{" + Regex.Escape(name) + @"\}", RegexOptions.IgnoreCase));
                names[name] = !onAll || Regex.IsMatch(source, @"RouteData\.Values\.Keys\.Contains\(\s*""" + Regex.Escape(name) + @"""", RegexOptions.IgnoreCase);
            }
        }
        foreach (Match match in Regex.Matches(source, @"RouteData\.Values\[\s*""(\w+)""\s*\]"))
            names.TryAdd(match.Groups[1].Value, false);
        return names.Select(pair =>
        {
            var asInt = pair.Key.Equals("id", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("index", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("size", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(source, @"ToInt32\(\s*Page\.RouteData\.Values\[\s*""" + Regex.Escape(pair.Key) + @"""", RegexOptions.IgnoreCase);
            var clr = asInt ? pair.Value ? "int?" : "int" : pair.Value ? "string?" : "string";
            return (Pascal(pair.Key), clr, pair.Value);
        }).ToList();
    }

    private static string TranslateBody(
        string body,
        IReadOnlyList<ControlSymbol> controls,
        IReadOnlyDictionary<string, string> routes,
        List<(string Name, string ClrType, bool Optional)> parameters,
        string relativePath,
        List<MigrationTodo> todos,
        ref bool needsNavigation)
    {
        body = UnwrapPostBack(body, relativePath, todos);
        body = Regex.Replace(body, @"^\s*_log\.[^;]*;\s*$", "", RegexOptions.Multiline);
        body = Regex.Replace(body, @"^\s*LogManager\.[^;]*;\s*$", "", RegexOptions.Multiline);
        body = Regex.Replace(body, @"^\s*(?:this\.)?[A-Za-z_][\w]*\.DataBind\(\s*\)\s*;\s*$", "", RegexOptions.Multiline);
        body = Regex.Replace(body, @"^\s*this\.DataBind\(\s*\)\s*;\s*$", "", RegexOptions.Multiline);
        foreach (var parameter in parameters)
        {
            var key = parameter.Name;
            var read = parameter.Optional && parameter.ClrType.StartsWith("int", StringComparison.Ordinal)
                ? key + ".GetValueOrDefault()"
                : key;
            body = Regex.Replace(body, @"Convert\.ToInt32\(\s*Page\.RouteData\.Values\[\s*""" + Regex.Escape(key) + @"""\s*\]\s*\)", read, RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"Page\.RouteData\.Values\.Keys\.Contains\(\s*""" + Regex.Escape(key) + @"""\s*\)", parameter.Optional ? key + ".HasValue" : "true", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"Page\.RouteData\.Values\[\s*""" + Regex.Escape(key) + @"""\s*\]", read, RegexOptions.IgnoreCase);
        }
        body = ReplaceGetRouteUrl(body, routes);
        body = body.Replace("this.ModelState.IsValid", "ValidateFields()", StringComparison.Ordinal)
            .Replace("ModelState.IsValid", "ValidateFields()", StringComparison.Ordinal);
        foreach (var control in controls.OrderByDescending(control => control.Id.Length))
        {
            if (control.Kind is "TextBox")
                body = body.Replace(control.Id + ".Text", control.Field, StringComparison.Ordinal);
            if (control.Kind is "DropDown")
            {
                body = body.Replace(control.Id + ".SelectedValue", control.Field, StringComparison.Ordinal);
                body = body.Replace(control.Id + ".DataSource", control.ItemsField, StringComparison.Ordinal);
            }
            if (control.Kind is "List" or "Grid" or "Repeater")
                body = body.Replace(control.Id + ".DataSource", control.Field, StringComparison.Ordinal);
            if (control.Kind is "HyperLink")
            {
                body = body.Replace(control.Id + ".NavigateUrl", control.HrefField, StringComparison.Ordinal);
                body = body.Replace(control.Id + ".CssClass", control.ClassField, StringComparison.Ordinal);
            }
            if (control.Kind is "Label")
                body = body.Replace(control.Id + ".Text", control.Field, StringComparison.Ordinal);
        }
        body = Regex.Replace(body, @"Response\.Redirect\(\s*""~/?""\s*\)\s*;", "Navigation.NavigateTo(Navigation.BaseUri);");
        if (body.Contains("Navigation.NavigateTo", StringComparison.Ordinal)) needsNavigation = true;
        body = Regex.Replace(body, @"Response\.Redirect\(\s*([^)]+)\)\s*;", "Navigation.NavigateTo($1);");
        if (body.Contains("Navigation.NavigateTo", StringComparison.Ordinal)) needsNavigation = true;

        var kept = new List<string>();
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                kept.Add("");
                continue;
            }
            if (ContainsForbidden(raw))
            {
                var trimmed = raw.Trim();
                todos.Add(new MigrationTodo(relativePath, "Not converted: " + TrimNote(trimmed)));
                RememberDeclarations(trimmed, dropped);
                kept.Add("        // TODO: " + trimmed);
                continue;
            }
            kept.Add(raw);
        }
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = 0; index < kept.Count; index++)
            {
                var line = kept[index];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line)) continue;
                if (!dropped.Any(name => Regex.IsMatch(line, @"\b" + Regex.Escape(name) + @"\b"))) continue;
                RememberDeclarations(line.Trim(), dropped);
                kept[index] = "        // TODO: " + line.Trim();
                todos.Add(new MigrationTodo(relativePath, "Not converted because it uses a Web Forms local: " + TrimNote(line.Trim())));
                changed = true;
            }
        }
        return string.Join(Environment.NewLine, kept) + Environment.NewLine;
    }

    private static string UnwrapPostBack(string body, string path, List<MigrationTodo> todos)
    {
        body = TakeConditional(body, "!Page.IsPostBack", unwrap: true, path, todos);
        body = TakeConditional(body, "Page.IsPostBack", unwrap: false, path, todos);
        body = body.Replace("!Page.IsPostBack", "true", StringComparison.Ordinal).Replace("Page.IsPostBack", "false", StringComparison.Ordinal);
        return body;
    }

    private static string TakeConditional(string body, string condition, bool unwrap, string path, List<MigrationTodo> todos)
    {
        var pattern = @"if\s*\(\s*" + Regex.Escape(condition) + @"\s*\)\s*\{";
        var match = Regex.Match(body, pattern);
        if (!match.Success) return body;
        var open = match.Index + match.Length - 1;
        var close = MatchBrace(body, open);
        if (close < 0) return body;
        var inner = body[(open + 1)..close];
        var replacement = unwrap
            ? inner
            : "        // TODO: postback-only logic was not converted." + Environment.NewLine;
        if (!unwrap)
            todos.Add(new MigrationTodo(path, "A Page.IsPostBack block was not converted. Blazor has no postback."));
        return body[..match.Index] + replacement + body[(close + 1)..];
    }

    private static void RememberDeclarations(string line, HashSet<string> names)
    {
        foreach (Match match in Regex.Matches(line, @"\b(?:var|[A-Z][\w<>,\.]*)\s+(\w+)\s*="))
            names.Add(match.Groups[1].Value);
    }

    private static bool ContainsForbidden(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return false;
        string[] tokens = ["Page.", "Response.", "Server.", "ViewState", "RouteData", "ModelState", "HttpContext", "Session[", "log4net", "LogManager", ".DataBind", ".DataSource", ".SelectedValue", ".NavigateUrl", "System.Web", ".Visible", "Request.", "RouteTable", "HttpUtility", "ScriptManager", "HostingEnvironment", "ConfigurationManager"];
        return tokens.Any(token => line.Contains(token, StringComparison.Ordinal));
    }

    private static string TrimNote(string text) => text.Length <= 180 ? text : text[..180] + "...";

    private static List<MethodChunk> ExtractMethods(string source)
    {
        var methods = new List<MethodChunk>();
        foreach (Match match in MethodRegex.Matches(source))
        {
            var open = match.Index + match.Length - 1;
            var close = MatchBrace(source, open);
            if (close < 0) continue;
            var returnType = match.Groups["ret"].Value.Trim();
            var parts = returnType.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1) returnType = parts[^1];
            methods.Add(new MethodChunk(returnType, match.Groups["name"].Value, match.Groups["args"].Value.Trim(), source[(open + 1)..close]));
        }
        return methods;
    }

    private static int MatchBrace(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '{') depth++;
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0) return index;
            }
        }
        return -1;
    }

    internal static string ReplaceGetRouteUrl(string text, IReadOnlyDictionary<string, string> routes)
    {
        var start = 0;
        var output = new StringBuilder();
        while (true)
        {
            var at = text.IndexOf("GetRouteUrl(", start, StringComparison.Ordinal);
            if (at < 0)
            {
                output.Append(text, start, text.Length - start);
                break;
            }
            output.Append(text, start, at - start);
            var open = at + "GetRouteUrl(".Length - 1;
            var close = MatchParen(text, open);
            if (close < 0)
            {
                output.Append(text[at..]);
                break;
            }
            var args = text[(open + 1)..close];
            output.Append(RenderRouteCall(args, routes));
            start = close + 1;
        }
        return output.ToString();
    }

    private static string RenderRouteCall(string args, IReadOnlyDictionary<string, string> routes)
    {
        var comma = args.IndexOf(',');
        var name = (comma < 0 ? args : args[..comma]).Trim().Trim('"');
        if (!routes.TryGetValue(name, out var template))
            return "\"/\"";
        var values = comma < 0 ? "" : args[(comma + 1)..];
        var anonymous = Regex.Match(values, @"new\s*\{(?<body>.*)\}\s*$", RegexOptions.Singleline);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (anonymous.Success)
        {
            foreach (var part in SplitArgs(anonymous.Groups["body"].Value))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;
                map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }
        var rendered = Regex.Replace(template, @"\{(\w+)\}", match =>
        {
            var token = match.Groups[1].Value;
            return map.TryGetValue(token, out var expr) ? "\" + (" + expr + ") + \"" : "";
        });
        return "\"/" + rendered.TrimStart('/') + "\"";
    }

    internal static string ExpressionToCSharp(string raw, bool inTemplate, IReadOnlyDictionary<string, string> routes)
    {
        var text = raw.Trim();
        if (text.StartsWith("<%", StringComparison.Ordinal)) text = text[2..].Trim();
        if (text.EndsWith("%>", StringComparison.Ordinal)) text = text[..^2].Trim();
        if (text.StartsWith("#:", StringComparison.Ordinal)) text = text[2..].Trim();
        else if (text.StartsWith('#')) text = text[1..].Trim();
        else if (text.StartsWith(':')) text = text[1..].Trim();
        else if (text.StartsWith('=')) text = text[1..].Trim();
        else if (text.StartsWith('$'))
        {
            var route = Regex.Match(text, @"RouteName\s*=\s*(\w+)");
            if (route.Success && routes.TryGetValue(route.Groups[1].Value, out var template))
                return CSharpIds.Quote("/" + template.TrimStart('/'));
            return "\"/\"";
        }
        if (inTemplate) text = Regex.Replace(text, @"\bItem\b", "item");
        text = ReplaceGetRouteUrl(text, routes);
        text = text.Replace("~/", "/", StringComparison.Ordinal).Replace("\"~\"", "\"/\"", StringComparison.Ordinal);
        return text;
    }

    private static int MatchParen(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '(') depth++;
            else if (text[index] == ')')
            {
                depth--;
                if (depth == 0) return index;
            }
        }
        return -1;
    }

    private static IEnumerable<string> SplitArgs(string body)
    {
        var start = 0;
        var depth = 0;
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] is '(' or '{') depth++;
            else if (body[index] is ')' or '}') depth--;
            else if (body[index] == ',' && depth == 0)
            {
                yield return body[start..index];
                start = index + 1;
            }
        }
        if (start < body.Length) yield return body[start..];
    }

    private static bool IsService(string type) =>
        type.Length > 2 && type[0] == 'I' && char.IsUpper(type[1]) && !type.StartsWith("IEnumerable", StringComparison.Ordinal)
        && !type.StartsWith("IList", StringComparison.Ordinal) && !type.StartsWith("ICollection", StringComparison.Ordinal)
        && !type.StartsWith("IDictionary", StringComparison.Ordinal);

    private static string? ShortType(string? full)
    {
        if (string.IsNullOrWhiteSpace(full)) return null;
        var leaf = full.Split('.').Last();
        return leaf.Length == 0 ? null : leaf;
    }

    private static string Pascal(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private sealed record MethodChunk(string ReturnType, string Name, string Arguments, string Body);
}
