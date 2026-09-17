using System.Globalization;
using System.Text.RegularExpressions;
using Sprache;

namespace Fx.ControlKit.Reports;

public enum CrystalEvaluationTime { BeforeReadingRecords, WhileReadingRecords, WhilePrintingRecords }

/// <summary>Inert Crystal-syntax AST. No generated code, reflection, SQL, files, or network access.</summary>
public sealed class CrystalFormula
{
    private readonly Node _root;
    private readonly object? _defaultValue;
    public IReadOnlyList<string> References { get; }
    public bool UsesVariables { get; }
    public bool UsesPersistentVariables { get; }
    public bool UsesSharedVariables { get; }
    public bool WritesPersistentVariables { get; }
    public bool UsesRecordContext { get; }
    public CrystalEvaluationTime? EvaluationTime { get; }
    public IReadOnlyList<string> EvaluateAfter { get; }
    public bool UsesPageContext { get; }
    public bool UsesAggregates { get; }
    public IReadOnlyList<string> AggregateReferences { get; }
    public bool UsesEvaluationDirectives { get; }
    public bool RequiresPrintPass { get; }
    private CrystalFormula(Node root, object? defaultValue)
    {
        _root = root; _defaultValue = defaultValue;
        var nodes = Walk(root).ToArray();
        References = nodes.OfType<Reference>().Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        UsesVariables = nodes.Any(n => n is Variable or Assign);
        UsesPersistentVariables = nodes.OfType<Variable>().Any(n => !n.Scope.Equals("local", StringComparison.OrdinalIgnoreCase));
        UsesSharedVariables = nodes.OfType<Variable>().Any(n => n.Scope.Equals("shared", StringComparison.OrdinalIgnoreCase));
        var persistentNames = nodes.OfType<Variable>().Where(n => !n.Scope.Equals("local", StringComparison.OrdinalIgnoreCase)).Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        WritesPersistentVariables = nodes.OfType<Variable>().Any(n => persistentNames.Contains(n.Name) && n.Value is not null)
            || nodes.OfType<Assign>().Any(n => persistentNames.Contains(n.Name));
        UsesRecordContext = nodes.OfType<Symbol>().Any(n => n.Name.ToLowerInvariant() is "recordnumber" or "onfirstrecord" or "onlastrecord" or "currentfieldvalue")
            || nodes.OfType<Call>().Any(n => n.Name.ToLowerInvariant() is "previous" or "next" or "onfirstrecord" or "onlastrecord");
        var times = nodes.OfType<Symbol>().Select(n => Enum.TryParse<CrystalEvaluationTime>(n.Name, true, out var time) ? (CrystalEvaluationTime?)time : null)
            .Where(t => t.HasValue).Distinct().ToArray();
        if (times.Length > 1) throw new InvalidDataException("A formula cannot specify conflicting evaluation times.");
        EvaluationTime = times.FirstOrDefault();
        var after = nodes.OfType<Call>().Where(n => n.Name.Equals("EvaluateAfter", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (after.Any(n => n.Args.Length != 1 || n.Args[0] is not Reference r || !r.Name.StartsWith("{@", StringComparison.Ordinal)))
            throw new InvalidDataException("EvaluateAfter requires one formula reference.");
        EvaluateAfter = after.Select(n => ((Reference)n.Args[0]).Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        UsesPageContext = nodes.OfType<Symbol>().Any(n => n.Name.ToLowerInvariant() is "pagenumber" or "totalpagecount" or "inrepeatedgroupheader");
        UsesAggregates = nodes.OfType<Call>().Any(n => AggregateNames.Contains(n.Name));
        AggregateReferences = nodes.OfType<Call>().Where(n => AggregateNames.Contains(n.Name)).SelectMany(n => n.Args.OfType<Reference>())
            .Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        RequiresPrintPass = nodes.OfType<Symbol>().Any(n => n.Name.Equals("WhilePrintingRecords", StringComparison.OrdinalIgnoreCase));
        UsesEvaluationDirectives = nodes.OfType<Symbol>().Any(n => n.Name.ToLowerInvariant() is "whileprintingrecords" or "whilereadingrecords" or "beforereadingrecords")
            || nodes.OfType<Call>().Any(n => n.Name.Equals("EvaluateAfter", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Crystal syntax under each spelling report XML uses for it.</summary>
    public static bool IsCrystalSyntax(string? syntax) =>
        syntax is not null && (syntax.Equals("Crystal", StringComparison.OrdinalIgnoreCase)
            || syntax.Equals("CrystalSyntax", StringComparison.OrdinalIgnoreCase)
            || syntax.Equals("crFormulaSyntaxCrystal", StringComparison.OrdinalIgnoreCase));

    public static CrystalFormula Compile(string text, string syntax = "Crystal", object? defaultValue = null)
    {
        if (!IsCrystalSyntax(syntax)) throw new NotSupportedException($"Formula syntax '{syntax}' is not implemented.");
        if (text.Length > 32768) throw new InvalidDataException("Formula exceeds the 32 KB limit.");
        // Bound recursive grammar work before parsing untrusted report definitions.
        string[] tokens;
        try { tokens = Token(Lexemes).End().Parse(text).ToArray(); }
        catch (ParseException ex) { throw new InvalidDataException("Invalid Crystal formula: " + ex.Message, ex); }
        if (tokens.Length > 2048) throw new InvalidDataException("Formula token limit exceeded.");
        var depth = 0; var branches = 0;
        foreach (var token in tokens)
        {
            if ((token is "(" or "[") && ++depth > 64 || token.Equals("if", StringComparison.OrdinalIgnoreCase) && ++branches > 128)
                throw new InvalidDataException("Formula nesting limit exceeded.");
            if (token is ")" or "]") depth--;
        }
        try
        {
            var root = string.IsNullOrWhiteSpace(string.Concat(tokens)) ? new Literal(defaultValue ?? 0m) : Program.End().Parse(text);
            foreach (var call in Walk(root).OfType<Call>())
                if (!Functions.Contains(call.Name)) throw new NotSupportedException($"Crystal function '{call.Name}' is not implemented.");
            return new(root, defaultValue ?? 0m);
        }
        catch (ParseException ex) { throw new InvalidDataException("Invalid Crystal formula: " + ex.Message, ex); }
    }

    public object? Evaluate(CrystalFormulaContext context)
    {
        var frame = new Frame(context, _defaultValue);
        try { return _root.Get(frame); }
        catch (NullFormulaValue) { return null; }
    }

    public static bool Boolean(object? value) => value switch { null or DBNull => false, bool b => b, string s => bool.TryParse(s, out var b) ? b : throw new InvalidDataException("Expected a Boolean formula result."), _ => Number(value) != 0 };
    public static decimal Number(object? value) => value is null or DBNull ? 0 : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    public static int Compare(object? left, object? right)
    {
        if (left is null or DBNull) return right is null or DBNull ? 0 : -1;
        if (right is null or DBNull) return 1;
        if (left is DateTime date) return date.CompareTo(Date(right));
        if (right is DateTime otherDate) return Date(left).CompareTo(otherDate);
        if (left is string && right is string) return StringComparer.OrdinalIgnoreCase.Compare(Text(left), Text(right));
        if (left is string || right is string)
        {
            if (decimal.TryParse(Text(left), NumberStyles.Number, CultureInfo.CurrentCulture, out var a) && decimal.TryParse(Text(right), NumberStyles.Number, CultureInfo.CurrentCulture, out var b)) return a.CompareTo(b);
            return StringComparer.OrdinalIgnoreCase.Compare(Text(left), Text(right));
        }
        return Number(left).CompareTo(Number(right));
    }
    private static string Text(object? value) => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
    private static DateTime Date(object? value) => value is DateTime date ? date : Convert.ToDateTime(value, CultureInfo.CurrentCulture);
    private static readonly HashSet<string> AggregateNames = new("sum average avg count distinctcount minimum maximum".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Functions = new(("sum average avg count distinctcount minimum maximum iif switch choose isnull hasvalue previous next onfirstrecord onlastrecord " +
        "totext cstr tonumber cdbl cint val isnumber todate cdate date datetime dateadd datediff year month day hour minute second dayofweek " +
        "left right mid len length trim ltrim rtrim uppercase lowercase ucase lcase replace instr chr chrw asc space replicate round truncate int abs sgn ceiling floor remainder mod " +
        "groupname rgb color defaultvaluesfornulls exceptionsfornulls evaluateafter").Split(' '), StringComparer.OrdinalIgnoreCase);

    private sealed class Frame(CrystalFormulaContext context, object? defaultValue)
    {
        public CrystalFormulaContext Context { get; } = context;
        public object? Default { get; } = defaultValue;
        public Dictionary<string, object?> Locals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Scopes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Steps { get; private set; }
        public bool DefaultNulls { get; set; } = context.DefaultValuesForNulls;
        public object? NonNull(object? value) => value is null or DBNull && !DefaultNulls ? throw new NullFormulaValue() : value;
        public void Tick() { if (++Steps > 10000) throw new InvalidDataException("Formula evaluation step limit exceeded."); }
        public IDictionary<string, object?> Variables(string name, string? scope = null)
        {
            scope ??= Scopes.GetValueOrDefault(name, "local");
            return scope.ToLowerInvariant() switch { "shared" => Context.SharedVariables, "global" => Context.GlobalVariables, _ => Locals };
        }
    }
    private sealed class NullFormulaValue : Exception;
    private abstract record Node
    {
        public object? Get(Frame f)
        {
            f.Tick(); var value = Eval(f);
            if (value is string text && text.Length > 1_048_576) throw new InvalidDataException("Formula text result exceeds 1 MB.");
            return value;
        }
        protected abstract object? Eval(Frame f);
        public virtual IEnumerable<Node> Children => [];
    }
    private sealed record Literal(object? Value) : Node { protected override object? Eval(Frame f) => Value; }
    private sealed record Default : Node { protected override object? Eval(Frame f) => f.Default; }
    private sealed record Reference(string Name) : Node { protected override object? Eval(Frame f) => f.Context.Resolve(Name); }
    private sealed record Symbol(string Name) : Node
    {
        protected override object? Eval(Frame f) => Name.ToLowerInvariant() switch
        {
            "true" => true, "false" => false, "null" => null,
            "currentdate" => f.Context.Now.Date, "currentdatetime" => f.Context.Now, "currenttime" => f.Context.Now.TimeOfDay,
            "pagenumber" => f.Context.PageNumber, "totalpagecount" => f.Context.TotalPageCount,
            "recordnumber" => f.Context.RecordIndex + 1, "onfirstrecord" => f.Context.RecordIndex == 0,
            "onlastrecord" => f.Context.RecordIndex == f.Context.RecordCount - 1,
            "drilldowngrouplevel" => f.Context.DrillDownGroupLevel, "inrepeatedgroupheader" => f.Context.InRepeatedGroupHeader,
            "currentfieldvalue" => f.Context.CurrentFieldValue(),
            "whileprintingrecords" or "whilereadingrecords" or "beforereadingrecords" => null,
            "defaultvaluesfornulls" => f.DefaultNulls = true, "exceptionsfornulls" => f.DefaultNulls = false,
            "crred" => 255m, "crgreen" => 32768m, "crblue" => 16711680m, "crblack" => 0m, "crwhite" => 16777215m,
            "crmaroon" => 128m, "crolive" => 32896m, "crnavy" => 8388608m, "crpurple" => 8388736m,
            "crteal" => 8421376m, "crgray" => 8421504m, "crsilver" => 12632256m, "crlime" => 65280m,
            "cryellow" => 65535m, "crfuchsia" => 16711935m, "craqua" => 16776960m, "crnocolor" => -1m,
            "crregular" => 0m, "crbold" => 1m, "critalic" => 2m, "crbolditalic" => 3m,
            _ => f.Variables(Name).TryGetValue(Name, out var value) ? value : throw new InvalidDataException($"Unknown Crystal symbol '{Name}'.")
        };
    }
    private sealed record Variable(string Scope, string Type, string Name, Node? Value) : Node
    {
        public override IEnumerable<Node> Children => Value is null ? [] : [Value];
        protected override object? Eval(Frame f)
        {
            f.Scopes[Name] = Scope;
            var vars = f.Variables(Name, Scope);
            if (Value is not null) vars[Name] = Value.Get(f);
            else if (!vars.ContainsKey(Name)) vars[Name] = Type.ToLowerInvariant() switch { "stringvar" => "", "booleanvar" => false, "datevar" or "datetimevar" => DateTime.MinValue, _ => 0m };
            return vars[Name];
        }
    }
    private sealed record Assign(string Name, Node Value) : Node
    {
        public override IEnumerable<Node> Children => [Value];
        protected override object? Eval(Frame f) => f.Variables(Name)[Name] = Value.Get(f);
    }
    private sealed record Sequence(Node[] Nodes) : Node
    {
        public override IEnumerable<Node> Children => Nodes;
        protected override object? Eval(Frame f) { object? value = null; foreach (var node in Nodes) value = node.Get(f); return value; }
    }
    private sealed record Conditional(Node Test, Node Yes, Node No) : Node
    {
        public override IEnumerable<Node> Children => [Test, Yes, No];
        protected override object? Eval(Frame f) => Boolean(f.NonNull(Test.Get(f))) ? Yes.Get(f) : No.Get(f);
    }
    private sealed record Unary(string Op, Node Value) : Node
    {
        public override IEnumerable<Node> Children => [Value];
        protected override object? Eval(Frame f) => Op.ToLowerInvariant() switch { "not" => !Boolean(f.NonNull(Value.Get(f))), "-" => -Number(f.NonNull(Value.Get(f))), _ => Number(f.NonNull(Value.Get(f))) };
    }
    private sealed record Values(Node[] Nodes) : Node
    {
        public override IEnumerable<Node> Children => Nodes;
        protected override object? Eval(Frame f) => Nodes.Select(n => n.Get(f)).ToArray();
    }
    private sealed record Range(object? Start, object? End);
    private sealed record Binary(string Op, Node Left, Node Right) : Node
    {
        public override IEnumerable<Node> Children => [Left, Right];
        protected override object? Eval(Frame f)
        {
            var a = f.NonNull(Left.Get(f)); var op = Op.ToLowerInvariant();
            if (op == "and") return Boolean(a) && Boolean(f.NonNull(Right.Get(f)));
            if (op == "or") return Boolean(a) || Boolean(f.NonNull(Right.Get(f)));
            var b = f.NonNull(Right.Get(f));
            return op switch
            {
                "=" => Compare(a, b) == 0, "<>" or "!=" => Compare(a, b) != 0, "<" => Compare(a, b) < 0,
                ">" => Compare(a, b) > 0, "<=" => Compare(a, b) <= 0, ">=" => Compare(a, b) >= 0,
                "xor" => Boolean(a) ^ Boolean(b), "&" => Text(a) + Text(b),
                "+" when a is string || b is string => Text(a) + Text(b),
                "+" when a is DateTime date => date.AddDays((double)Number(b)),
                "-" when a is DateTime date && b is DateTime other => (decimal)(date - other).TotalDays,
                "-" when a is DateTime date => date.AddDays(-(double)Number(b)),
                "+" => Number(a) + Number(b), "-" => Number(a) - Number(b), "*" => Number(a) * Number(b),
                "/" => Number(a) / Number(b), "mod" or "%" => Number(a) % Number(b), "^" => (decimal)Math.Pow((double)Number(a), (double)Number(b)),
                "to" => new Range(a, b), "in" => b is Range range ? Compare(a, range.Start) >= 0 && Compare(a, range.End) <= 0 : b is object?[] values ? values.Any(v => Compare(a, v) == 0) : Text(b).Contains(Text(a), StringComparison.OrdinalIgnoreCase),
                "like" => Regex.IsMatch(Text(a), "^" + Regex.Escape(Text(b)).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100)),
                _ => throw new NotSupportedException($"Crystal operator '{Op}' is not implemented.")
            };
        }
    }
    private sealed record Call(string Name, Node[] Args) : Node
    {
        public override IEnumerable<Node> Children => Args;
        protected override object? Eval(Frame f)
        {
            var name = Name.ToLowerInvariant();
            var values = new Dictionary<int, object?>();
            object? Arg(int i)
            {
                if (!values.TryGetValue(i, out var value)) values[i] = value = i < Args.Length ? Args[i].Get(f) : throw new InvalidDataException($"{Name}: missing argument {i + 1}.");
                return name is "isnull" or "hasvalue" ? value : f.NonNull(value);
            }
            int Int(int i) => checked((int)Number(Arg(i)));
            string Str(int i) => Text(Arg(i));
            if (name == "iif") { var test = Boolean(Arg(0)); var yes = Arg(1); var no = Arg(2); return test ? yes : no; }
            if (name == "switch")
            {
                if (Args.Length % 2 != 0) throw new InvalidDataException("Switch requires condition/value pairs.");
                for (var i = 0; i < Args.Length; i++) _ = Arg(i);
                for (var i = 0; i < Args.Length; i += 2) if (Boolean(Arg(i))) return Arg(i + 1);
                return f.Default;
            }
            if (name == "choose") { for (var index = 0; index < Args.Length; index++) _ = Arg(index); var i = Int(0); return i >= 1 && i < Args.Length ? Arg(i) : f.Default; }
            if (AggregateNames.Contains(name))
            {
                if (Args.Length is < 1 or > 2 || Args[0] is not Reference reference || Args.Length == 2 && Args[1] is not Reference)
                    throw new NotSupportedException($"{Name} requires a field and optional group reference.");
                return f.Context.Aggregate(name, reference.Name, Args.Length == 2 ? ((Reference)Args[1]).Name : "");
            }
            if (name is "previous" or "next")
            {
                if (Args.Length != 1 || Args[0] is not Reference reference) throw new NotSupportedException($"{Name} requires a field reference.");
                return f.Context.Relative(reference.Name, name == "previous" ? -1 : 1);
            }
            return name switch
            {
                "isnull" => Arg(0) is null or DBNull, "hasvalue" => Arg(0) is not (null or DBNull),
                "onfirstrecord" => f.Context.RecordIndex == 0, "onlastrecord" => f.Context.RecordIndex == f.Context.RecordCount - 1,
                "groupname" => Text(Arg(0)), "totext" or "cstr" => ToText(Arg(0), Args.Skip(1).Select(n => n.Get(f)).ToArray()),
                "tonumber" or "cdbl" => Number(Arg(0)), "cint" => decimal.Round(Number(Arg(0)), 0, MidpointRounding.AwayFromZero),
                "val" => decimal.TryParse(Regex.Match(Str(0).TrimStart(), @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : 0m,
                "isnumber" => decimal.TryParse(Str(0), NumberStyles.Number, CultureInfo.CurrentCulture, out _),
                "date" or "datetime" => Args.Length == 1 ? Date(Arg(0)) : new DateTime(Int(0), Int(1), Int(2), Args.Length > 3 ? Int(3) : 0, Args.Length > 4 ? Int(4) : 0, Args.Length > 5 ? Int(5) : 0),
                "todate" or "cdate" => Date(Arg(0)).Date,
                "datediff" => DateDiff(Str(0), Date(Arg(1)), Date(Arg(2))),
                "dateadd" => DateAdd(Str(0), Number(Arg(1)), Date(Arg(2))),
                "year" => Date(Arg(0)).Year, "month" => Date(Arg(0)).Month, "day" => Date(Arg(0)).Day,
                "hour" => Date(Arg(0)).Hour, "minute" => Date(Arg(0)).Minute, "second" => Date(Arg(0)).Second, "dayofweek" => (int)Date(Arg(0)).DayOfWeek + 1,
                "left" => Str(0)[..Math.Clamp(Int(1), 0, Str(0).Length)], "right" => Str(0)[(Str(0).Length - Math.Clamp(Int(1), 0, Str(0).Length))..],
                "mid" => Str(0).Substring(Math.Clamp(Int(1) - 1, 0, Str(0).Length), Args.Length > 2 ? Math.Clamp(Int(2), 0, Str(0).Length - Math.Clamp(Int(1) - 1, 0, Str(0).Length)) : Str(0).Length - Math.Clamp(Int(1) - 1, 0, Str(0).Length)),
                "len" or "length" => Str(0).Length, "trim" => Str(0).Trim(), "ltrim" => Str(0).TrimStart(), "rtrim" => Str(0).TrimEnd(),
                "uppercase" or "ucase" => Str(0).ToUpper(CultureInfo.CurrentCulture), "lowercase" or "lcase" => Str(0).ToLower(CultureInfo.CurrentCulture),
                "replace" => Str(0).Replace(Str(1), Str(2), StringComparison.Ordinal),
                "instr" => Args.Length == 2 ? Str(0).IndexOf(Str(1), StringComparison.Ordinal) + 1 : Str(1).IndexOf(Str(2), Math.Clamp(Int(0) - 1, 0, Str(1).Length), StringComparison.Ordinal) + 1,
                "chr" or "chrw" => char.ConvertFromUtf32(Int(0)), "asc" => Str(0).Length == 0 ? 0 : char.ConvertToUtf32(Str(0), 0),
                "space" => new string(' ', Math.Clamp(Int(0), 0, 32768)),
                "replicate" => string.Concat(Enumerable.Repeat(Str(0), Math.Clamp(Int(1), 0, 32768 / Math.Max(1, Str(0).Length)))),
                "round" => decimal.Round(Number(Arg(0)), Args.Length > 1 ? Math.Clamp(Int(1), 0, 28) : 0, MidpointRounding.AwayFromZero),
                "truncate" => Truncate(Number(Arg(0)), Args.Length > 1 ? Int(1) : 0), "int" or "floor" => decimal.Floor(Number(Arg(0))),
                "ceiling" => decimal.Ceiling(Number(Arg(0))), "abs" => Math.Abs(Number(Arg(0))), "sgn" => Math.Sign(Number(Arg(0))),
                "remainder" or "mod" => Number(Arg(0)) % Number(Arg(1)),
                "rgb" or "color" => Math.Clamp(Int(0), 0, 255) + Math.Clamp(Int(1), 0, 255) * 256 + Math.Clamp(Int(2), 0, 255) * 65536,
                "evaluateafter" => Arg(0),
                _ => throw new NotSupportedException($"Crystal function '{Name}' is not implemented.")
            };
        }
    }
    private static decimal Truncate(decimal number, int places)
    {
        var factor = (decimal)Math.Pow(10, Math.Clamp(places, -28, 28));
        return decimal.Truncate(number * factor) / factor;
    }
    private static string ToText(object? value, object?[] args)
    {
        if (value is string text) return text;
        if (args.Length > 0 && args[0] is string format)
            return value is IFormattable formattable ? formattable.ToString(format, CultureInfo.CurrentCulture) : Text(value);
        if (value is DateTime date) return date.ToShortDateString();
        if (value is null) return "";
        if (value is bool boolean) return boolean ? "True" : "False";
        var digits = args.Length == 0 ? 2 : Math.Clamp((int)Number(args[0]), 0, 28);
        var culture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
        if (args.Length > 1) culture.NumberFormat.NumberGroupSeparator = Text(args[1]);
        if (args.Length > 2) culture.NumberFormat.NumberDecimalSeparator = Text(args[2]);
        return Number(value).ToString("N" + digits, culture);
    }
    private static decimal DateDiff(string interval, DateTime start, DateTime end) => interval.ToLowerInvariant() switch
    {
        "yyyy" => end.Year - start.Year, "q" => (end.Year - start.Year) * 4 + (end.Month - 1) / 3 - (start.Month - 1) / 3,
        "m" => (end.Year - start.Year) * 12 + end.Month - start.Month, "d" or "y" => (end.Date - start.Date).Days,
        "h" => end.Ticks / TimeSpan.TicksPerHour - start.Ticks / TimeSpan.TicksPerHour,
        "n" => end.Ticks / TimeSpan.TicksPerMinute - start.Ticks / TimeSpan.TicksPerMinute,
        "s" => end.Ticks / TimeSpan.TicksPerSecond - start.Ticks / TimeSpan.TicksPerSecond,
        _ => throw new NotSupportedException($"DateDiff interval '{interval}' is not implemented.")
    };
    private static DateTime DateAdd(string interval, decimal amount, DateTime date) => interval.ToLowerInvariant() switch
    {
        "yyyy" => date.AddYears((int)amount), "q" => date.AddMonths((int)amount * 3), "m" => date.AddMonths((int)amount),
        "d" or "y" => date.AddDays((double)amount), "h" => date.AddHours((double)amount), "n" => date.AddMinutes((double)amount),
        "s" => date.AddSeconds((double)amount), "ww" => date.AddDays((double)amount * 7),
        _ => throw new NotSupportedException($"DateAdd interval '{interval}' is not implemented.")
    };
    private static IEnumerable<Node> Walk(Node node) { yield return node; foreach (var child in node.Children) foreach (var descendant in Walk(child)) yield return descendant; }

    private static readonly Parser<string> Trivia = Parse.WhiteSpace.AtLeastOnce().Text()
        .Or(Parse.String("//").Then(_ => Parse.CharExcept("\r\n").Many().Text()))
        .Or(Parse.String("/*").Then(_ => Parse.AnyChar.Until(Parse.String("*/")).Text()));
    private static Parser<T> Token<T>(Parser<T> parser) => from before in Trivia.Many() from value in parser from after in Trivia.Many() select value;
    private static Parser<string> Word(string word) => Token(Parse.IgnoreCase(word).Text().Then(value => Parse.LetterOrDigit.Or(Parse.Char('_')).Not().Return(value.ToLowerInvariant())));
    private static Parser<string> Punctuation(string value) => Token(Parse.String(value).Text());
    private static Parser<string> Quoted(char quote) => from open in Parse.Char(quote)
        from chars in Parse.String(new string(quote, 2)).Return(quote.ToString()).Or(Parse.CharExcept(quote).Select(c => c.ToString())).Many()
        from close in Parse.Char(quote) select string.Concat(chars);
    private static readonly Parser<string> Identifier = Token(from first in Parse.Letter.Or(Parse.Char('_')) from rest in Parse.LetterOrDigit.Or(Parse.Char('_')).Many() select first + new string(rest.ToArray()));
    private static readonly Parser<IEnumerable<string>> Lexemes = Token(Quoted('"').Or(Quoted('\'')).Select(_ => "literal")
        .Or(Parse.Char('{').Then(_ => Parse.CharExcept('}').Many()).Then(_ => Parse.Char('}')).Return("reference"))
        .Or(Parse.Letter.AtLeastOnce().Text()).Or(Parse.AnyChar.Select(c => c.ToString()))).Many();
    private static readonly Parser<Node> Constant = Token(Quoted('"').Or(Quoted('\''))).Select(s => (Node)new Literal(s))
        .Or(Token(Parse.DecimalInvariant).Select(s => (Node)new Literal(decimal.Parse(s, CultureInfo.InvariantCulture))))
        .Or(Token(from open in Parse.Char('#') from value in Parse.CharExcept('#').AtLeastOnce().Text() from close in Parse.Char('#') select (Node)new Literal(DateTime.Parse(value, CultureInfo.InvariantCulture))));
    private static readonly Parser<Node> Field = Token(from open in Parse.Char('{') from name in Parse.CharExcept('}').AtLeastOnce().Text() from close in Parse.Char('}') select (Node)new Reference("{" + name + "}"));
    private static readonly Parser<Node> Declaration = from scope in Word("shared").Or(Word("global")).Or(Word("local")).Optional()
        from type in Word("numbervar").Or(Word("currencyvar")).Or(Word("stringvar")).Or(Word("booleanvar")).Or(Word("datevar")).Or(Word("datetimevar"))
        from name in Identifier from value in Punctuation(":=").Then(_ => Parse.Ref(() => Expression)).Optional()
        select (Node)new Variable(scope.GetOrDefault() ?? "global", type, name, value.GetOrDefault());
    private static readonly Parser<Node> Assignment = from name in Identifier from op in Punctuation(":=") from value in Parse.Ref(() => Expression) select (Node)new Assign(name, value);
    private static readonly Parser<Node> If = from keyword in Word("if") from test in Parse.Ref(() => Expression) from then in Word("then")
        from yes in Parse.Ref(() => Expression) from no in Word("else").Then(_ => Parse.Ref(() => Expression)).Optional()
        select (Node)new Conditional(test, yes, no.GetOrDefault() ?? new Default());
    private static readonly Parser<Node> Function = from name in Identifier from open in Punctuation("(")
        from args in Parse.Ref(() => Expression).DelimitedBy(Punctuation(",")).Optional() from close in Punctuation(")")
        select (Node)new Call(name, (args.GetOrDefault() ?? []).ToArray());
    private static readonly Parser<Node> Atom = If.Or(Declaration).Or(Assignment).Or(Constant).Or(Field).Or(Function)
        .Or(from open in Punctuation("(") from value in Parse.Ref(() => Program) from close in Punctuation(")") select value)
        .Or(from open in Punctuation("[") from values in Parse.Ref(() => Expression).DelimitedBy(Punctuation(",")) from close in Punctuation("]") select (Node)new Values(values.ToArray()))
        .Or(Identifier.Where(name => !new[] { "then", "else", "and", "or", "xor", "mod", "in", "like", "to" }.Contains(name.ToLowerInvariant())).Select(name => (Node)new Symbol(name)));
    private static readonly Parser<Node> UnaryExpression = (from op in Punctuation("-").Or(Punctuation("+")) from value in Parse.Ref(() => UnaryExpression) select (Node)new Unary(op, value)).Or(Atom);
    private static Parser<Node> Chain(Parser<Node> operand, params string[] operators) => Parse.ChainOperator(operators.Select(op => char.IsLetter(op[0]) ? Word(op) : Punctuation(op)).Aggregate((a, b) => a.Or(b)), operand, (op, a, b) => new Binary(op, a, b));
    private static readonly Parser<Node> Power = Chain(UnaryExpression, "^");
    private static readonly Parser<Node> Product = Chain(Power, "*", "/", "%", "mod");
    private static readonly Parser<Node> Sum = Chain(Product, "+", "-", "&");
    private static readonly Parser<Node> Ranges = Chain(Sum, "to");
    private static readonly Parser<Node> Comparison = Chain(Ranges, "<=", ">=", "<>", "!=", "=", "<", ">", "in", "like");
    private static readonly Parser<Node> Not = Word("not").Then(_ => Parse.Ref(() => Not)).Select(value => (Node)new Unary("not", value)).Or(Comparison);
    private static readonly Parser<Node> And = Chain(Not, "and");
    private static readonly Parser<Node> Expression = Chain(And, "or", "xor");
    private static readonly Parser<Node> Program = from nodes in Expression.DelimitedBy(Punctuation(";")) from tail in Punctuation(";").Many() select (Node)new Sequence(nodes.ToArray());
}

public sealed class CrystalFormulaContext
{
    public Func<string, object?> Resolve { get; init; } = name => throw new InvalidDataException($"Unresolved field '{name}'.");
    public Func<string, string, string, object?> Aggregate { get; init; } = (op, field, group) => throw new InvalidDataException("Aggregate context is unavailable.");
    public Func<string, int, object?> Relative { get; init; } = (field, offset) => throw new InvalidDataException("Relative record context is unavailable.");
    public IDictionary<string, object?> SharedVariables { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public IDictionary<string, object?> GlobalVariables { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public DateTime Now { get; init; } = DateTime.Now;
    public int RecordIndex { get; init; }
    public int RecordCount { get; init; }
    public object PageNumber { get; init; } = 1;
    public object TotalPageCount { get; init; } = 1;
    public int DrillDownGroupLevel { get; init; }
    public bool InRepeatedGroupHeader { get; init; }
    public bool DefaultValuesForNulls { get; init; }
    public Func<object?> CurrentFieldValue { get; init; } = () => null;
}
