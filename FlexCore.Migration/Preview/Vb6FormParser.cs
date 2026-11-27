using System.Globalization;

namespace Fx.ControlKit.Preview;

/// <summary>One control from a VB6 <c>.frm</c> designer block. Positions are twips.</summary>
public sealed record Vb6FormControl(
    string Kind,
    string Name,
    string Caption,
    string Text,
    int Left,
    int Top,
    int Width,
    int Height,
    int TabIndex,
    int Value);

/// <summary>The designer surface of one VB6 form. Positions are twips (15 twips = 1 pixel).</summary>
public sealed record Vb6FormLayout(
    string Name,
    string Caption,
    int ClientWidth,
    int ClientHeight,
    IReadOnlyList<Vb6FormControl> Controls)
{
    public const int TwipsPerPixel = 15;

    public int ClientWidthPixels => Math.Max(1, ClientWidth / TwipsPerPixel);
    public int ClientHeightPixels => Math.Max(1, ClientHeight / TwipsPerPixel);
}

/// <summary>
/// Reads the designer block of a VB6 form. Code after the form's closing <c>End</c> is ignored.
/// Enough for labels, text boxes, check boxes and command buttons.
/// </summary>
public static class Vb6FormParser
{
    public static Vb6FormLayout Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        Vb6FormLayout? form = null;
        var stack = new Stack<Builder>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("Begin ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                stack.Push(new Builder(KindOf(parts[1]), parts[2].Trim()));
                continue;
            }
            if (line == "End")
            {
                if (stack.Count == 0) break;
                var done = stack.Pop();
                if (stack.Count == 0)
                {
                    form = new Vb6FormLayout(done.Name, done.Caption, done.ClientWidth, done.ClientHeight, done.Children);
                    break;
                }
                stack.Peek().Children.Add(done.ToControl());
                continue;
            }
            if (stack.Count == 0 || !line.Contains('=', StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            var name = line[..eq].Trim();
            var value = Unquote(line[(eq + 1)..].Trim());
            var current = stack.Peek();
            switch (name)
            {
                case "Caption": current.Caption = value; break;
                case "Text": current.Text = value; break;
                case "Left": current.Left = Int(value); break;
                case "Top": current.Top = Int(value); break;
                case "Width": current.Width = Int(value); break;
                case "Height": current.Height = Int(value); break;
                case "ClientWidth": current.ClientWidth = Int(value); break;
                case "ClientHeight": current.ClientHeight = Int(value); break;
                case "TabIndex": current.TabIndex = Int(value); break;
                case "Value": current.Value = Int(value); break;
            }
        }
        if (form is null) throw new FormatException("The file has no VB6 form designer block.");
        return form;
    }

    private static string KindOf(string token) => token switch
    {
        "VB.Form" => "Form",
        "VB.Label" => "Label",
        "VB.TextBox" => "TextBox",
        "VB.CheckBox" => "CheckBox",
        "VB.CommandButton" => "CommandButton",
        _ => token.StartsWith("VB.", StringComparison.Ordinal) ? token[3..] : token
    };

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        return value;
    }

    private static int Int(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private sealed class Builder(string kind, string name)
    {
        public string Kind { get; } = kind;
        public string Name { get; } = name;
        public string Caption { get; set; } = "";
        public string Text { get; set; } = "";
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ClientWidth { get; set; }
        public int ClientHeight { get; set; }
        public int TabIndex { get; set; }
        public int Value { get; set; }
        public List<Vb6FormControl> Children { get; } = [];

        public Vb6FormControl ToControl()
            => new(Kind, Name, Caption, Text, Left, Top, Width, Height, TabIndex, Value);
    }
}
