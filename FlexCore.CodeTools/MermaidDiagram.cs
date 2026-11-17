using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.CodeTools;

/// <summary>
/// Draws the Mermaid subset the code tools emit: classDiagram, sequenceDiagram, and flowchart.
/// The SVG is produced in C# so a browser can show it without a script.
/// </summary>
public static class MermaidDiagram
{
    public static bool TryRenderSvg(string? mermaid, out string svg)
    {
        svg = "";
        if (!TryParse(mermaid, out var diagram)) return false;
        svg = diagram.Kind switch
        {
            "sequence" => RenderSequence(diagram),
            "flow" => RenderFlow(diagram),
            _ => RenderClass(diagram)
        };
        return svg.Length > 0;
    }

    private static bool TryParse(string? mermaid, out Diagram diagram)
    {
        diagram = new Diagram();
        if (string.IsNullOrWhiteSpace(mermaid)) return false;
        var text = mermaid.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start) text = text[(start + 1)..end];
        }

        var lines = text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("%%")).ToList();
        if (lines.Count == 0) return false;
        var head = lines[0];
        if (head.StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase)) diagram.Kind = "sequence";
        else if (head.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) || head.StartsWith("graph", StringComparison.OrdinalIgnoreCase))
        {
            diagram.Kind = "flow";
            diagram.TopDown = !head.Contains("LR", StringComparison.OrdinalIgnoreCase);
        }
        else diagram.Kind = "class";

        if (diagram.Kind == "class") return ParseClass(lines, diagram);
        if (diagram.Kind == "sequence") return ParseSequence(lines, diagram);
        return ParseFlow(lines, diagram);
    }

    private static bool ParseClass(List<string> lines, Diagram diagram)
    {
        ClassBox? current = null;
        foreach (var line in lines.Skip(1))
        {
            if (line == "{") continue;
            if (current is not null)
            {
                if (line.StartsWith('}')) { current = null; continue; }
                current.Members.Add(line.Trim());
                continue;
            }

            var open = Regex.Match(line, @"^class\s+(\w+)\s*\{?$");
            if (open.Success)
            {
                current = new ClassBox(open.Groups[1].Value);
                diagram.Classes.Add(current);
                if (!line.EndsWith('{')) current = line.Contains('{') ? current : null;
                if (!line.Contains('{')) current = null;
                else if (line.Contains('}')) current = null;
                continue;
            }

            var relation = Regex.Match(line, @"^(\w+)\s+(--\|>|<\|--|-->|\*--|o--|--+)\s+(\w+)(?:\s*:\s*(.*))?$");
            if (relation.Success)
            {
                diagram.Relations.Add(new Relation(relation.Groups[1].Value, relation.Groups[3].Value, relation.Groups[2].Value, relation.Groups[4].Value.Trim()));
                Ensure(diagram, relation.Groups[1].Value);
                Ensure(diagram, relation.Groups[3].Value);
            }
        }

        return diagram.Classes.Count > 0;
    }

    private static void Ensure(Diagram diagram, string name)
    {
        if (diagram.Classes.All(item => item.Name != name)) diagram.Classes.Add(new ClassBox(name));
    }

    private static bool ParseSequence(List<string> lines, Diagram diagram)
    {
        foreach (var line in lines.Skip(1))
        {
            var participant = Regex.Match(line, @"^participant\s+(\w+)(?:\s+as\s+(.+))?$", RegexOptions.IgnoreCase);
            if (participant.Success)
            {
                diagram.Actors.Add(new Actor(participant.Groups[1].Value, participant.Groups[2].Success && participant.Groups[2].Value.Length > 0 ? participant.Groups[2].Value.Trim() : participant.Groups[1].Value));
                continue;
            }

            var message = Regex.Match(line, @"^(\w+)\s*(-+>>?|--+\)|-->>)\s*(\w+)\s*:\s*(.*)$");
            if (!message.Success) continue;
            Touch(diagram, message.Groups[1].Value);
            Touch(diagram, message.Groups[3].Value);
            diagram.Messages.Add(new Message(message.Groups[1].Value, message.Groups[3].Value, message.Groups[4].Value.Trim(), message.Groups[2].Value.Contains("--")));
        }

        return diagram.Actors.Count > 0 && diagram.Messages.Count > 0;
    }

    private static void Touch(Diagram diagram, string id)
    {
        if (diagram.Actors.All(actor => actor.Id != id)) diagram.Actors.Add(new Actor(id, id));
    }

    private static bool ParseFlow(List<string> lines, Diagram diagram)
    {
        foreach (var line in lines.Skip(1))
        {
            var edge = Regex.Match(line, @"^(\w+)\s*(\[[^\]]*\]|\([^)]*\)|\{[^}]*\})?\s*-->\s*(?:\|([^|]*)\|\s*)?(\w+)\s*(\[[^\]]*\]|\([^)]*\)|\{[^}]*\})?\s*$");
            if (edge.Success)
            {
                var from = Node(diagram, edge.Groups[1].Value, edge.Groups[2].Value);
                var to = Node(diagram, edge.Groups[4].Value, edge.Groups[5].Value);
                diagram.Edges.Add(new Edge(from.Id, to.Id, edge.Groups[3].Value.Trim()));
                continue;
            }

            var alone = Regex.Match(line, @"^(\w+)\s*(\[[^\]]*\]|\([^)]*\)|\{[^}]*\})\s*$");
            if (alone.Success) Node(diagram, alone.Groups[1].Value, alone.Groups[2].Value);
        }

        return diagram.Nodes.Count > 0;
    }

    private static FlowNode Node(Diagram diagram, string id, string shape)
    {
        var existing = diagram.Nodes.FirstOrDefault(node => node.Id == id);
        var label = LabelOf(shape, id);
        var kind = shape.StartsWith('{') ? "diamond" : shape.StartsWith('(') ? "round" : "rect";
        if (existing is not null)
        {
            if (shape.Length > 0) existing.Label = label;
            if (shape.Length > 0) existing.Shape = kind;
            return existing;
        }

        var node = new FlowNode(id, label, kind);
        diagram.Nodes.Add(node);
        return node;
    }

    private static string LabelOf(string shape, string id)
    {
        if (string.IsNullOrEmpty(shape) || shape.Length < 2) return id;
        return shape[1..^1].Trim();
    }

    private static string RenderClass(Diagram diagram)
    {
        var boxes = diagram.Classes.Select(Measure).ToList();
        var columns = Math.Min(3, Math.Max(1, boxes.Count));
        var gapX = 96;
        var gapY = 64;
        var colWidth = boxes.Max(box => box.Width);
        var rowHeight = boxes.Max(box => box.Height);
        for (var i = 0; i < boxes.Count; i++)
        {
            boxes[i].X = 28 + (i % columns) * (colWidth + gapX);
            boxes[i].Y = 28 + (i / columns) * (rowHeight + gapY);
        }

        var width = 28 + columns * colWidth + (columns - 1) * gapX + 28;
        var rows = (boxes.Count + columns - 1) / columns;
        var height = 28 + rows * rowHeight + Math.Max(0, rows - 1) * gapY + 36;
        var svg = new StringBuilder();
        Open(svg, width, height, "Class diagram");
        foreach (var relation in diagram.Relations)
        {
            var from = boxes.First(box => box.Name == relation.From);
            var to = boxes.First(box => box.Name == relation.To);
            Line(svg, from, to, relation.Label);
        }

        foreach (var box in boxes)
        {
            svg.Append("<rect x=\"").Append(box.X).Append("\" y=\"").Append(box.Y).Append("\" width=\"").Append(box.Width).Append("\" height=\"").Append(box.Height).Append("\" rx=\"8\" class=\"box\"/>");
            svg.Append("<rect x=\"").Append(box.X).Append("\" y=\"").Append(box.Y).Append("\" width=\"").Append(box.Width).Append("\" height=\"28\" rx=\"8\" class=\"head\"/>");
            Text(svg, box.X + 12, box.Y + 19, box.Name, "name");
            var y = box.Y + 46;
            foreach (var member in box.Members)
            {
                Text(svg, box.X + 12, y, Pretty(member), "member");
                y += 18;
            }
        }

        Close(svg);
        return svg.ToString();
    }

    private static string RenderSequence(Diagram diagram)
    {
        var width = Math.Max(420, 40 + diagram.Actors.Count * 160);
        var height = 90 + diagram.Messages.Count * 42 + 30;
        var svg = new StringBuilder();
        Open(svg, width, height, "Sequence diagram");
        var xs = new Dictionary<string, int>();
        for (var i = 0; i < diagram.Actors.Count; i++)
        {
            var x = 80 + i * ((width - 80) / Math.Max(1, diagram.Actors.Count));
            xs[diagram.Actors[i].Id] = x;
            svg.Append("<rect x=\"").Append(x - 46).Append("\" y=\"16\" width=\"92\" height=\"32\" rx=\"8\" class=\"box\"/>");
            Text(svg, x - 40, 36, diagram.Actors[i].Label, "name");
            svg.Append("<line x1=\"").Append(x).Append("\" y1=\"48\" x2=\"").Append(x).Append("\" y2=\"").Append(height - 16).Append("\" class=\"life\"/>");
        }

        var y = 78;
        foreach (var message in diagram.Messages)
        {
            var x1 = xs[message.From];
            var x2 = xs[message.To];
            svg.Append("<line x1=\"").Append(x1).Append("\" y1=\"").Append(y).Append("\" x2=\"").Append(x2).Append("\" y2=\"").Append(y).Append("\" class=\"").Append(message.Dashed ? "dashed" : "arrow").Append("\" marker-end=\"url(#arrow)\"/>");
            Text(svg, Math.Min(x1, x2) + 8, y - 6, message.Label, "member");
            y += 42;
        }

        Close(svg);
        return svg.ToString();
    }

    private static string RenderFlow(Diagram diagram)
    {
        var rank = new Dictionary<string, int>();
        foreach (var node in diagram.Nodes) rank[node.Id] = 0;
        for (var pass = 0; pass < diagram.Nodes.Count; pass++)
        {
            foreach (var edge in diagram.Edges)
            {
                if (rank.TryGetValue(edge.From, out var from) && rank.TryGetValue(edge.To, out var to) && to < from + 1)
                    rank[edge.To] = from + 1;
            }
        }

        var groups = diagram.Nodes.GroupBy(node => rank[node.Id]).OrderBy(group => group.Key).ToList();
        var svg = new StringBuilder();
        var positions = new Dictionary<string, (int X, int Y, int W, int H)>();
        var gap = diagram.TopDown ? 36 : 48;
        var cursor = 24;
        var maxCross = 24;
        foreach (var group in groups)
        {
            var along = cursor;
            var local = 24;
            foreach (var node in group)
            {
                var w = Math.Max(96, node.Label.Length * 8 + 28);
                var h = node.Shape == "diamond" ? 64 : 40;
                int x, y;
                if (diagram.TopDown) { x = local; y = along; }
                else { x = along; y = local; }
                positions[node.Id] = (x, y, w, h);
                local += (diagram.TopDown ? w : h) + gap;
            }

            maxCross = Math.Max(maxCross, local);
            var thick = group.Max(node => node.Shape == "diamond" ? 64 : 40);
            cursor += thick + 56;
        }

        var width = (diagram.TopDown ? maxCross : cursor) + 24;
        var height = (diagram.TopDown ? cursor : maxCross) + 24;
        Open(svg, width, height, "Flow diagram");
        foreach (var edge in diagram.Edges)
        {
            if (!positions.TryGetValue(edge.From, out var from) || !positions.TryGetValue(edge.To, out var to)) continue;
            var x1 = from.X + from.W / 2;
            var y1 = from.Y + from.H;
            var x2 = to.X + to.W / 2;
            var y2 = to.Y;
            if (!diagram.TopDown)
            {
                x1 = from.X + from.W;
                y1 = from.Y + from.H / 2;
                x2 = to.X;
                y2 = to.Y + to.H / 2;
            }

            svg.Append("<line x1=\"").Append(x1).Append("\" y1=\"").Append(y1).Append("\" x2=\"").Append(x2).Append("\" y2=\"").Append(y2).Append("\" class=\"arrow\" marker-end=\"url(#arrow)\"/>");
            if (edge.Label.Length > 0) Text(svg, (x1 + x2) / 2 + 4, (y1 + y2) / 2 - 4, edge.Label, "member");
        }

        foreach (var node in diagram.Nodes)
        {
            var box = positions[node.Id];
            if (node.Shape == "diamond")
            {
                var cx = box.X + box.W / 2;
                var cy = box.Y + box.H / 2;
                svg.Append("<polygon points=\"").Append(cx).Append(',').Append(box.Y).Append(' ').Append(box.X + box.W).Append(',').Append(cy).Append(' ').Append(cx).Append(',').Append(box.Y + box.H).Append(' ').Append(box.X).Append(',').Append(cy).Append("\" class=\"box\"/>");
            }
            else
            {
                svg.Append("<rect x=\"").Append(box.X).Append("\" y=\"").Append(box.Y).Append("\" width=\"").Append(box.W).Append("\" height=\"").Append(box.H).Append("\" rx=\"").Append(node.Shape == "round" ? 20 : 8).Append("\" class=\"box\"/>");
            }

            Text(svg, box.X + 12, box.Y + box.H / 2 + 4, node.Label, "name");
        }

        Close(svg);
        return svg.ToString();
    }

    private static Placed Measure(ClassBox box)
    {
        var longest = box.Name.Length;
        foreach (var member in box.Members) longest = Math.Max(longest, member.Length);
        return new Placed
        {
            Name = box.Name,
            Members = box.Members,
            Width = Math.Max(160, longest * 7 + 28),
            Height = 36 + Math.Max(1, box.Members.Count) * 18 + 12
        };
    }

    private static string Pretty(string member) =>
        System.Text.RegularExpressions.Regex.Replace(member, "~([^~]+)~", "<$1>");

    private static void Line(StringBuilder svg, Placed from, Placed to, string label)
    {
        int x1, y1, x2, y2;
        if (Math.Abs(from.Y - to.Y) < 8)
        {
            var left = from.X < to.X ? from : to;
            var right = from.X < to.X ? to : from;
            x1 = left.X + left.Width;
            y1 = left.Y + left.Height / 2;
            x2 = right.X;
            y2 = right.Y + right.Height / 2;
            if (from.X > to.X) (x1, x2, y1, y2) = (from.X, to.X + to.Width, from.Y + from.Height / 2, to.Y + to.Height / 2);
        }
        else
        {
            x1 = from.X + from.Width / 2;
            y1 = from.Y + from.Height;
            x2 = to.X + to.Width / 2;
            y2 = to.Y;
        }

        svg.Append("<line x1=\"").Append(x1).Append("\" y1=\"").Append(y1).Append("\" x2=\"").Append(x2).Append("\" y2=\"").Append(y2).Append("\" class=\"arrow\" marker-end=\"url(#arrow)\"/>");
        if (label.Length > 0) Text(svg, (x1 + x2) / 2 + 6, (y1 + y2) / 2 - 6, label, "member");
    }

    private static void Open(StringBuilder svg, int width, int height, string label)
    {
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height).Append("\" role=\"img\" class=\"fx-mermaid-svg\" aria-label=\"").Append(Escape(label)).Append("\">");
        svg.Append("<title>").Append(Escape(label)).Append("</title>");
        svg.Append("<defs><marker id=\"arrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path d=\"M 0 0 L 10 5 L 0 10 z\" class=\"arrow-head\"/></marker></defs>");
        svg.Append("<style>.box{fill:#fff;stroke:#d9d3c7;stroke-width:1.2}.head{fill:#f3faf6;stroke:#d9d3c7}.name{font:600 13px ui-monospace,Consolas,monospace;fill:#178a49}.member{font:12px ui-monospace,Consolas,monospace;fill:#1c1c1c}.arrow{stroke:#5f6b66;stroke-width:1.4;fill:none}.dashed{stroke:#5f6b66;stroke-width:1.4;stroke-dasharray:4 3;fill:none}.life{stroke:#e4dfd6;stroke-dasharray:3 3}.arrow-head{fill:#5f6b66}</style>");
    }

    private static void Close(StringBuilder svg) => svg.Append("</svg>");

    private static void Text(StringBuilder svg, int x, int y, string text, string cls) =>
        svg.Append("<text x=\"").Append(x).Append("\" y=\"").Append(y).Append("\" class=\"").Append(cls).Append("\">").Append(Escape(text)).Append("</text>");

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private sealed class Diagram
    {
        public string Kind { get; set; } = "class";
        public bool TopDown { get; set; } = true;
        public List<ClassBox> Classes { get; } = [];
        public List<Relation> Relations { get; } = [];
        public List<Actor> Actors { get; } = [];
        public List<Message> Messages { get; } = [];
        public List<FlowNode> Nodes { get; } = [];
        public List<Edge> Edges { get; } = [];
    }

    private sealed class ClassBox(string name)
    {
        public string Name { get; } = name;
        public List<string> Members { get; } = [];
    }

    private sealed record Relation(string From, string To, string Kind, string Label);
    private sealed record Actor(string Id, string Label);
    private sealed record Message(string From, string To, string Label, bool Dashed);
    private sealed class FlowNode(string id, string label, string shape)
    {
        public string Id { get; } = id;
        public string Label { get; set; } = label;
        public string Shape { get; set; } = shape;
    }

    private sealed record Edge(string From, string To, string Label);

    private sealed class Placed
    {
        public string Name { get; set; } = "";
        public List<string> Members { get; set; } = [];
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
