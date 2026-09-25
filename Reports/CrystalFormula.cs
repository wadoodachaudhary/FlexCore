using System.Globalization;
using System.Runtime.CompilerServices;

namespace Fx.ControlKit.Reports;

public enum CrystalEvaluationTime { BeforeReadingRecords, WhileReadingRecords, WhilePrintingRecords }

/// <summary>Inert Crystal formula AST (Crystal and Basic syntax). No generated code, reflection, SQL, files, or network access.</summary>
public sealed partial class CrystalFormula
{
    private const int MaxTokens = 4096;
    private const int MaxNesting = 256;
    private const int MaxSteps = 2_000_000;
    private const int MaxLoopIterations = 100_000;
    private const int MaxArrayLength = 1000;
    private const int MaxHostArrayLength = 100_000;
    private const int MaxCallDepth = 32;
    private const int MaxTextLength = 1_048_576;
    private const int MaxArrayText = 1_048_576;
    private readonly Node _root;
    private readonly object? _defaultValue;
    private readonly FunctionSignature? _signature;
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
    /// <summary>True when the formula reads <see cref="CrystalFormulaContext.GroupNumber"/> (a print-time value).</summary>
    public bool UsesGroupNumber { get; }
    /// <summary>Identifiers that are neither declared variables nor Crystal built-ins; evaluating them fails.</summary>
    public IReadOnlyList<string> UnknownSymbols { get; }
    /// <summary>Custom functions (by name) called directly by this formula.</summary>
    public IReadOnlyList<string> CustomFunctions { get; }
    /// <summary>True when the text holds no expression (blank or comments only); it evaluates to the default value.</summary>
    public bool IsEmpty { get; private init; }

    private CrystalFormula(Node root, object? defaultValue, IReadOnlyCollection<Binding> declarations, IReadOnlyList<string> unknown, FunctionSignature? signature = null)
    {
        _root = root; _defaultValue = defaultValue; _signature = signature;
        var nodes = Walk(root).ToArray();
        References = nodes.OfType<Reference>().Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        UsesVariables = declarations.Count > 0 || nodes.Any(n => n is Variable or VarRef or Assign or ElementAssign or Redim or ForLoop);
        UsesPersistentVariables = declarations.Any(b => b.Persistent);
        UsesSharedVariables = declarations.Any(b => b.Scope == "shared");
        WritesPersistentVariables = nodes.Any(n => n switch
        {
            Variable v => v.Binding.Persistent && v.Value is not null,
            Assign a => a.Binding.Persistent, ElementAssign e => e.Binding.Persistent, Redim r => r.Binding.Persistent, ForLoop l => l.Binding.Persistent,
            _ => false
        });
        var symbols = nodes.OfType<Symbol>().Select(n => n.Name.ToLowerInvariant()).ToHashSet();
        var calls = nodes.OfType<Call>().ToArray();
        UsesGroupNumber = symbols.Contains("groupnumber");
        UsesRecordContext = symbols.Overlaps(["recordnumber", "onfirstrecord", "onlastrecord", "currentfieldvalue", "groupnumber"])
            || calls.Any(n => n.Name.ToLowerInvariant() is "previous" or "next" or "previousvalue" or "nextvalue" or "previousisnull" or "nextisnull" or "onfirstrecord" or "onlastrecord" or "counthierarchicalchildren" or "groupname");
        var times = nodes.OfType<Symbol>().Select(n => Enum.TryParse<CrystalEvaluationTime>(n.Name, true, out var time) ? (CrystalEvaluationTime?)time : null)
            .Where(t => t.HasValue).Distinct().ToArray();
        if (times.Length > 1) throw new InvalidDataException("A formula cannot specify conflicting evaluation times.");
        EvaluationTime = times.FirstOrDefault();
        var after = calls.Where(n => n.Name.Equals("EvaluateAfter", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (after.Any(n => n.Args.Length != 1 || n.Args[0] is not Reference r || !r.Name.StartsWith("{@", StringComparison.Ordinal)))
            throw new InvalidDataException("EvaluateAfter requires one formula reference.");
        EvaluateAfter = after.Select(n => ((Reference)n.Args[0]).Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        UsesPageContext = symbols.Overlaps(["pagenumber", "totalpagecount", "pagenofm", "inrepeatedgroupheader"]);
        var aggregates = calls.Where(IsAggregate).ToArray();
        UsesAggregates = aggregates.Length > 0;
        AggregateReferences = aggregates.SelectMany(n => n.Args.OfType<Reference>()).Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        RequiresPrintPass = symbols.Contains("whileprintingrecords");
        UsesEvaluationDirectives = symbols.Overlaps(["whileprintingrecords", "whilereadingrecords", "beforereadingrecords"]) || after.Length > 0;
        UnknownSymbols = unknown;
        CustomFunctions = nodes.OfType<CustomCall>().Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Crystal syntax under each spelling report XML uses for it.</summary>
    public static bool IsCrystalSyntax(string? syntax) =>
        syntax is not null && (syntax.Equals("Crystal", StringComparison.OrdinalIgnoreCase)
            || syntax.Equals("CrystalSyntax", StringComparison.OrdinalIgnoreCase)
            || syntax.Equals("crFormulaSyntaxCrystal", StringComparison.OrdinalIgnoreCase));
    /// <summary>True for the syntaxes <see cref="Compile"/> accepts (Crystal and Basic).</summary>
    public static bool IsSupportedSyntax(string? syntax) => IsCrystalSyntax(syntax) || IsBasicSyntax(syntax);

    public static CrystalFormula Compile(string text, string syntax = "Crystal", object? defaultValue = null, IReadOnlyDictionary<string, CrystalCustomFunction>? customFunctions = null)
    {
        try { return CompileFormula(text, syntax, defaultValue, customFunctions); }
        catch (InsufficientExecutionStackException) { throw new InvalidDataException("Formula nesting limit exceeded."); }
    }
    private static CrystalFormula CompileFormula(string text, string syntax, object? defaultValue, IReadOnlyDictionary<string, CrystalCustomFunction>? customFunctions)
    {
        if (IsBasicSyntax(syntax))
        {
            var basic = new BasicParser(text, customFunctions, false);
            return new(basic.IsEmpty ? new Literal(defaultValue ?? 0m) : basic.ParseFormula(), defaultValue ?? 0m, basic.Declarations, basic.Unknown) { IsEmpty = basic.IsEmpty };
        }
        if (!IsCrystalSyntax(syntax)) throw new NotSupportedException($"Formula syntax '{syntax}' is not implemented.");
        var parser = new Parser(text, customFunctions, false);
        var root = parser.IsEmpty ? new Literal(defaultValue ?? 0m) : parser.ParseFormula();
        return new(root, defaultValue ?? 0m, parser.Declarations, parser.Unknown) { IsEmpty = parser.IsEmpty };
    }

    internal static CrystalCustomFunction CompileFunction(string name, string text, string syntax, IReadOnlyDictionary<string, CrystalCustomFunction>? functions)
    {
        try { return CompileFunctionBody(name, text, syntax, functions); }
        catch (InsufficientExecutionStackException) { throw new InvalidDataException($"Custom function '{name}': formula nesting limit exceeded."); }
    }
    private static CrystalCustomFunction CompileFunctionBody(string name, string text, string syntax, IReadOnlyDictionary<string, CrystalCustomFunction>? functions)
    {
        ParserBase parser; FunctionSignature signature; Node body;
        if (IsBasicSyntax(syntax)) { var basic = new BasicParser(text, functions, true); parser = basic; (signature, body) = basic.ParseFunction(name); }
        else if (IsCrystalSyntax(syntax)) { var crystal = new Parser(text, functions, true); parser = crystal; (signature, body) = crystal.ParseFunction(); }
        else throw new NotSupportedException($"Custom function '{name}' uses {syntax} syntax, which is not implemented.");
        if (parser.Unknown.Count > 0) throw new InvalidDataException($"Custom function '{name}' uses unknown symbol '{parser.Unknown[0]}'.");
        if (Walk(body).Select(n => n switch { Symbol s => s.Name, Call c => c.Name, _ => null }).FirstOrDefault(n => n is not null && PrintStateNames.Contains(n)) is { } printState)
            throw new InvalidDataException($"Custom function '{name}' uses {printState}; custom functions cannot use print-state or evaluation-time functions.");
        var formula = new CrystalFormula(body, Infer(body) ?? 0m, parser.Declarations, parser.Unknown, signature);
        return new(name, text, formula, signature.Parameters.Select(p => new CrystalCustomFunctionParameter(p.Binding.Name, p.Binding.Type, p.Binding.IsArray, p.Binding.IsRange, p.Default is not null)).ToArray());
    }

    public object? Evaluate(CrystalFormulaContext context)
    {
        var frame = new Frame(context, _defaultValue, new Budget());
        try { return _root.Get(frame); }
        catch (NullFormulaValue) { return null; }
        catch (DivideByZeroException) { throw new InvalidDataException("Division by zero."); }
        catch (OverflowException ex) { throw new InvalidDataException("Crystal formula numeric overflow: " + ex.Message, ex); }
        catch (InsufficientExecutionStackException) { throw new InvalidDataException("Formula nesting is too deep to evaluate."); }
    }

    private object? Invoke(Frame caller, string name, object?[] arguments)
    {
        var signature = _signature ?? throw new InvalidOperationException("Not a custom function.");
        var required = signature.Parameters.Count(p => p.Default is null);
        if (arguments.Length < required || arguments.Length > signature.Parameters.Count)
            throw new InvalidDataException($"Custom function '{name}' expects {(required == signature.Parameters.Count ? required.ToString(CultureInfo.InvariantCulture) : $"{required} to {signature.Parameters.Count}")} argument(s).");
        if (++caller.Budget.Depth > MaxCallDepth) throw new InvalidDataException("Custom function call depth limit exceeded.");
        try
        {
            var frame = new Frame(caller.Context, _defaultValue, caller.Budget) { DefaultNulls = caller.DefaultNulls };
            for (var i = 0; i < signature.Parameters.Count; i++)
            {
                var parameter = signature.Parameters[i];
                frame.Write(parameter.Binding, i < arguments.Length ? arguments[i] : parameter.Default!.Get(frame));
            }
            return _root.Get(frame);
        }
        finally { caller.Budget.Depth--; }
    }

    public static bool Boolean(object? value) => value switch { null or DBNull => false, bool b => b, string s => bool.TryParse(s, out var b) ? b : throw new InvalidDataException("Expected a Boolean formula result."), _ => Number(value) != 0 };
    public static decimal Number(object? value) => value switch
    {
        null or DBNull => 0,
        CrystalRange or object?[] => throw new InvalidDataException("A number is required here, not an array or a range."),
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
    };
    public static int Compare(object? left, object? right)
    {
        if (left is null or DBNull) return right is null or DBNull ? 0 : -1;
        if (right is null or DBNull) return 1;
        if (left is TimeSpan time) return time.CompareTo(Time(right));
        if (right is TimeSpan otherTime) return Time(left).CompareTo(otherTime);
        if (left is DateTime date) return date.CompareTo(Date(right));
        if (right is DateTime otherDate) return Date(left).CompareTo(otherDate);
        if (left is string && right is string) return StringComparer.OrdinalIgnoreCase.Compare(Text(left), Text(right));
        if (left is string || right is string)
        {
            if (decimal.TryParse(Text(left), NumberStyles.Number, CultureInfo.CurrentCulture, out var a) && decimal.TryParse(Text(right), NumberStyles.Number, CultureInfo.CurrentCulture, out var b)) return a.CompareTo(b);
            return StringComparer.OrdinalIgnoreCase.Compare(Text(left), Text(right));
        }
        if (left is CrystalRange || right is CrystalRange || AsArray(left) is not null || AsArray(right) is not null)
            throw new InvalidDataException("Crystal ranges and arrays cannot be ordered against other values.");
        return Number(left).CompareTo(Number(right));
    }

    private static readonly ConditionalWeakTable<object, CrystalRandom> SessionRandom = new();
    private static CrystalRandom Random(CrystalFormulaContext context) =>
        context.Random ?? SessionRandom.GetValue(context.SharedVariables, _ => new CrystalRandom());
}

/// <summary>The base type of a Crystal value.</summary>
public enum CrystalBaseType { Number, Currency, Boolean, Date, Time, DateTime, String }

/// <summary>The declared Crystal type of a field, parameter or formula: a multi-value parameter is an array, a range parameter a range.</summary>
public sealed record CrystalValueType(CrystalBaseType BaseType, bool IsArray = false, bool IsRange = false)
{
    /// <summary>The type a report XML names ("Xsd:dateField", "crFieldValueTypeDateTimeField", "Int32sField", "DateParameter", ...), or null when it names none.</summary>
    public static CrystalValueType? FromXml(string? valueType, bool isArray = false, bool isRange = false)
    {
        if (string.IsNullOrWhiteSpace(valueType)) return null;
        var name = valueType.Trim().ToLowerInvariant();
        if (name.StartsWith("crfieldvaluetype", StringComparison.Ordinal)) name = name["crfieldvaluetype".Length..];
        if (name.StartsWith("xsd:", StringComparison.Ordinal)) name = name[4..];
        foreach (var suffix in (string[])["field", "parameter"]) if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        CrystalBaseType? type = name switch
        {
            "date" => CrystalBaseType.Date, "datetime" => CrystalBaseType.DateTime, "time" => CrystalBaseType.Time,
            "boolean" or "bool" => CrystalBaseType.Boolean, "currency" => CrystalBaseType.Currency,
            "string" or "persistentmemo" or "memo" => CrystalBaseType.String,
            "number" or "decimal" or "long" or "short" or "byte" or "float" or "double" or "int8s" or "int8u" or "int16s" or "int16u" or "int32s" or "int32u" or "int64s" => CrystalBaseType.Number,
            _ => null
        };
        return type is null ? null : new(type.Value, isArray, isRange);
    }
}

/// <summary>A Crystal range value. A null endpoint is open (<c>UpTo</c>/<c>UpFrom</c>).</summary>
public sealed record CrystalRange(object? Start, object? End, bool IncludeStart = true, bool IncludeEnd = true)
{
    /// <summary>Whether a bound is a Crystal Date (a whole day against date-times), a DateTime (false), or of unknown type (null).</summary>
    internal bool? StartIsDate { get; init; }
    internal bool? EndIsDate { get; init; }
}

/// <summary>The stored definition of a report custom function; <see cref="Syntax"/> is "Crystal" or "Basic".</summary>
public sealed record CrystalCustomFunctionSource(string Name, string Text, string Syntax = "Crystal");

/// <summary>A parameter of a compiled Crystal custom function.</summary>
public sealed record CrystalCustomFunctionParameter(string Name, string Type, bool IsArray, bool IsRange, bool IsOptional);

/// <summary>A compiled custom function: Crystal syntax (<c>Function (stringVar x, optional numberVar n := 1) body</c>) or Basic syntax (<c>Function name (x As String) ... End Function</c>).</summary>
public sealed class CrystalCustomFunction
{
    internal CrystalCustomFunction(string name, string text, CrystalFormula body, IReadOnlyList<CrystalCustomFunctionParameter> parameters)
    { Name = name; Text = text; Body = body; Parameters = parameters; }
    public string Name { get; }
    public string Text { get; }
    public IReadOnlyList<CrystalCustomFunctionParameter> Parameters { get; }
    /// <summary>Other custom functions this one calls.</summary>
    public IReadOnlyList<string> CalledFunctions => Body.CustomFunctions;
    internal CrystalFormula Body { get; }

    /// <summary>Compiles one custom function. Calls to other custom functions resolve through <paramref name="functions"/> at evaluation time.</summary>
    public static CrystalCustomFunction Compile(string name, string text, string syntax = "Crystal", IReadOnlyDictionary<string, CrystalCustomFunction>? functions = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A custom function requires a name.");
        return CrystalFormula.CompileFunction(name.Trim(), text, syntax, functions);
    }

    /// <summary>Compiles a report's Crystal-syntax custom functions (name to text) so they may call each other; see <see cref="CompileAll(IEnumerable{CrystalCustomFunctionSource})"/>.</summary>
    public static CrystalCustomFunctionLibrary CompileAll(IEnumerable<KeyValuePair<string, string>> definitions, string syntax = "Crystal") =>
        CompileAll(definitions.Select(d => new CrystalCustomFunctionSource(d.Key, d.Value, syntax)));

    /// <summary>Compiles a report's custom functions, each in its own syntax (Crystal or Basic), so they may call each other. A function that does not
    /// compile, is defined twice, is recursive (Crystal rejects recursion) or calls such a function is left out, and the library's
    /// <see cref="CrystalCustomFunctionLibrary.Errors"/> says why; the others stay usable.</summary>
    public static CrystalCustomFunctionLibrary CompileAll(IEnumerable<CrystalCustomFunctionSource> definitions)
    {
        var names = StringComparer.OrdinalIgnoreCase;
        var list = definitions.Select(d => d with { Name = d.Name.Trim() }).ToArray();
        var compiled = new Dictionary<string, CrystalCustomFunction>(names);
        var errors = new Dictionary<string, string>(names);
        foreach (var duplicate in list.GroupBy(d => d.Name, names).Where(g => g.Count() > 1)) errors[duplicate.Key] = "it is defined more than once.";
        var late = new LateFunctions(compiled, list.Select(d => d.Name).ToHashSet(names));
        foreach (var definition in list.Where(d => !errors.ContainsKey(d.Name)))
            try { compiled[definition.Name] = Compile(definition.Name, definition.Text, definition.Syntax, late); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { errors[definition.Name] = ex.Message; }
        // Recursive functions are the strongly connected components with a cycle (Tarjan, iterative: call chains may be thousands deep).
        var index = new Dictionary<string, int>(names); var low = new Dictionary<string, int>(names);
        var open = new Stack<string>(); var onStack = new HashSet<string>(names);
        foreach (var root in compiled.Keys)
        {
            if (index.ContainsKey(root)) continue;
            var work = new Stack<(string Name, int Next)>();
            void Enter(string name) { index[name] = low[name] = index.Count; open.Push(name); onStack.Add(name); work.Push((name, 0)); }
            Enter(root);
            while (work.TryPop(out var frame))
            {
                var calls = compiled[frame.Name].CalledFunctions;
                if (frame.Next < calls.Count)
                {
                    work.Push((frame.Name, frame.Next + 1));
                    var called = calls[frame.Next];
                    if (!compiled.ContainsKey(called)) continue;
                    if (!index.ContainsKey(called)) Enter(called);
                    else if (onStack.Contains(called)) low[frame.Name] = Math.Min(low[frame.Name], index[called]);
                    continue;
                }
                if (work.TryPeek(out var parent)) low[parent.Name] = Math.Min(low[parent.Name], low[frame.Name]);
                if (low[frame.Name] != index[frame.Name]) continue;
                var component = new List<string>();
                string member;
                do { member = open.Pop(); onStack.Remove(member); component.Add(member); } while (!names.Equals(member, frame.Name));
                if (component.Count == 1 && !calls.Contains(frame.Name, names)) continue;
                foreach (var (recursive, cycle) in CycleNames(component, frame.Name))
                    errors[recursive] = (cycle.Names.Count == 0 ? "it calls itself" : $"it calls itself through {string.Join(", ", cycle.Names.Select(c => "'" + c + "'"))}"
                        + (cycle.Length > cycle.Names.Count ? " and others" : "")) + "; Crystal custom functions cannot be recursive.";
            }
        }
        // Each member names its own cycle: none when it calls itself; the root, its shortest calls back to itself; any other member, its shortest calls
        // toward the root (reverse breadth-first distances), then the root's shortest call path back to it, cut short where the two paths meet or at the
        // first listed function that calls the member. Only the first three names of each path are looked at, so each member costs a constant beyond
        // the two searches.
        IEnumerable<(string Name, (List<string> Names, int Length) Cycle)> CycleNames(List<string> component, string root)
        {
            var members = component.ToHashSet(names);
            var toRoot = new Dictionary<string, (int Distance, string Next)>(names) { [root] = (0, root) };
            var from = new Dictionary<string, (int Depth, List<string> First)>(names) { [root] = (0, []) };
            var inward = new Dictionary<string, HashSet<string>>(names);
            foreach (var member in component)
                foreach (var called in compiled[member].CalledFunctions.Where(members.Contains))
                    (inward.TryGetValue(called, out var found) ? found : inward[called] = new(names)).Add(member);
            for (var queue = new Queue<string>([root]); queue.TryDequeue(out var current);)
                foreach (var caller in inward.GetValueOrDefault(current) ?? [])
                    if (toRoot.TryAdd(caller, (toRoot[current].Distance + 1, current))) queue.Enqueue(caller);
            for (var queue = new Queue<string>([root]); queue.TryDequeue(out var current);)
                foreach (var called in compiled[current].CalledFunctions.Where(members.Contains))
                    if (!from.ContainsKey(called))
                    {
                        var (depth, first) = from[current];
                        from[called] = (depth + 1, first.Count < 3 ? [.. first, called] : first);
                        queue.Enqueue(called);
                    }
            foreach (var member in component)
            {
                if (compiled[member].CalledFunctions.Contains(member, names)) { yield return (member, ([], 0)); continue; }
                var cycle = new List<string>();
                int length;
                if (names.Equals(member, root))
                {
                    var start = compiled[root].CalledFunctions.Where(c => members.Contains(c) && !names.Equals(c, root)).MinBy(c => toRoot[c].Distance)!;
                    for (var next = start; cycle.Count < 3 && !names.Equals(next, root); next = toRoot[next].Next) cycle.Add(next);
                    length = toRoot[start].Distance;
                }
                else
                {
                    var down = from[member].First.Where(c => !names.Equals(c, member)).ToList();
                    var between = from[member].Depth - 1;
                    length = toRoot[member].Distance + between;
                    var met = false;
                    for (var next = toRoot[member].Next; cycle.Count < 3 && !met; next = toRoot[next].Next)
                    {
                        cycle.Add(next);
                        if (down.FindIndex(c => names.Equals(c, next)) is >= 0 and var meet) { length = cycle.Count + between - meet - 1; down.RemoveRange(0, meet + 1); met = true; }
                        else met = names.Equals(next, root);
                    }
                    if (met) cycle.AddRange(down.Take(3 - cycle.Count));
                }
                if (cycle.FindIndex(inward[member].Contains) is >= 0 and var close && close + 1 < Math.Max(length, cycle.Count)) { cycle.RemoveRange(close + 1, cycle.Count - close - 1); length = close + 1; }
                yield return (member, (cycle, length));
            }
        }
        // A function calling an unusable one is unusable too; its message names the first function that failed.
        var callers = new Dictionary<string, List<string>>(names);
        foreach (var (name, function) in compiled)
            foreach (var called in function.CalledFunctions.Distinct(names)) (callers.TryGetValue(called, out var found) ? found : callers[called] = []).Add(name);
        var pending = new Queue<(string Name, string Cause)>(errors.Keys.Select(name => (name, name)));
        while (pending.TryDequeue(out var failed))
            foreach (var caller in callers.GetValueOrDefault(failed.Name) ?? [])
                if (errors.TryAdd(caller, $"it calls '{failed.Name}', which cannot be used" + (names.Equals(failed.Name, failed.Cause) ? "." : $" because '{failed.Cause}' cannot be used.")))
                    pending.Enqueue((caller, failed.Cause));
        foreach (var name in errors.Keys) compiled.Remove(name);
        return new(compiled, errors);
    }

    private sealed class LateFunctions(Dictionary<string, CrystalCustomFunction> compiled, HashSet<string> names) : IReadOnlyDictionary<string, CrystalCustomFunction>
    {
        public CrystalCustomFunction this[string key] => compiled[key];
        public IEnumerable<string> Keys => names;
        public IEnumerable<CrystalCustomFunction> Values => compiled.Values;
        public int Count => names.Count;
        public bool ContainsKey(string key) => names.Contains(key);
        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out CrystalCustomFunction value) => compiled.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, CrystalCustomFunction>> GetEnumerator() => compiled.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>A report's compiled custom functions. <see cref="Errors"/> holds, by name, why each left-out function cannot be called; a formula
/// compiled against the library that calls one fails to compile with that reason.</summary>
public sealed class CrystalCustomFunctionLibrary : IReadOnlyDictionary<string, CrystalCustomFunction>
{
    private readonly Dictionary<string, CrystalCustomFunction> _functions;
    internal CrystalCustomFunctionLibrary(Dictionary<string, CrystalCustomFunction> functions, IReadOnlyDictionary<string, string> errors) { _functions = functions; Errors = errors; }
    public IReadOnlyDictionary<string, string> Errors { get; }
    public CrystalCustomFunction this[string key] => _functions[key];
    public IEnumerable<string> Keys => _functions.Keys;
    public IEnumerable<CrystalCustomFunction> Values => _functions.Values;
    public int Count => _functions.Count;
    public bool ContainsKey(string key) => _functions.ContainsKey(key);
    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out CrystalCustomFunction value) => _functions.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, CrystalCustomFunction>> GetEnumerator() => _functions.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Crystal's <c>Rnd</c> generator (minimal-standard LCG with a 32-slot shuffle table). The runtime seeds it from the clock in milliseconds, which its seeding
/// always reduces to 1, so every run yields the same sequence. Keep one per report run.</summary>
public sealed class CrystalRandom
{
    private readonly long[] _table = new long[32];
    private readonly Lock _gate = new();
    private long _state, _last;
    /// <summary>Crystal <c>Rnd(seed)</c>: a negative seed reseeds, zero repeats the last value, otherwise the next value.</summary>
    public double Next(double seed = 1)
    {
        lock (_gate) return seed == 0 ? Current() : seed < 0 ? Seed(seed) : Advance();
    }
    private static long Step(long value)
    {
        var quotient = value / 127773L;
        var next = 16807L * (value - quotient * 127773L) - 2836L * quotient;
        return next < 0 ? next + int.MaxValue : next;
    }
    private double Seed(double seed)
    {
        var value = (long)Math.Abs(seed);
        _state = value == 0 || value >= int.MaxValue ? 1 : value;
        for (var i = 39; i >= 0; i--) { _state = Step(_state); if (i < 32) _table[i] = _state; }
        _last = _state;
        return Advance();
    }
    private double Advance()
    {
        if (_state == 0) Seed(1);
        _last = Step(_last);
        var slot = (int)(_state / 0x4000000L);
        _state = _table[slot]; _table[slot] = _last;
        return Current();
    }
    private double Current() => Math.Min(4.656612875245797E-10 * _state, 0.99999988);
}

public sealed class CrystalFormulaContext
{
    public Func<string, object?> Resolve { get; init; } = name => throw new InvalidDataException($"Unresolved field '{name}'.");
    public Func<string, string, string, object?> Aggregate { get; init; } = (op, field, group) => throw new InvalidDataException("Aggregate context is unavailable.");
    /// <summary>Crystal <c>Sum ({field}, {group field}, "condition")</c>: a summary for the group whose date, time or Boolean condition has that name.</summary>
    public Func<string, string, string, string, object?> ConditionalAggregate { get; init; } = (op, field, group, condition) => throw new NotSupportedException($"{op} with the group condition \"{condition}\" requires the report's groups, which this context does not provide.");
    /// <summary>Crystal <c>GroupName({field})</c> and <c>GroupName({field}, "condition")</c>: the name of the current group on that field (the Others
    /// name, the group-name formula, the specified group or the date period); the condition picks among groups on the same field. Null reads the field value.</summary>
    public Func<string, string?, object?>? GroupName { get; init; }
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
    /// <summary>Crystal <c>GroupNumber</c>: 1-based ordinal, in print order, of the innermost group instance holding the current record.</summary>
    public Func<int> GroupNumber { get; init; } = () => throw new InvalidDataException("Group number context is unavailable.");
    /// <summary>Crystal <c>GroupingLevel({field})</c>: 1-based level of the group whose condition field is the argument, 0 when none.</summary>
    public Func<string, int> GroupingLevel { get; init; } = field => throw new NotSupportedException("GroupingLevel requires the report's group list, which this context does not provide.");
    /// <summary>Crystal <c>CountHierarchicalChildren(level)</c> for the current hierarchical group instance.</summary>
    public Func<int, int> HierarchicalChildren { get; init; } = level => throw new NotSupportedException("CountHierarchicalChildren requires hierarchical grouping (parent/child group trees), which this engine does not build.");
    /// <summary>Crystal document properties by lower-case name: filename, fileauthor, filecreationdate, modificationdate, modificationtime, reporttitle, reportcomments, recordselection, groupselection.</summary>
    public Func<string, object?> DocumentProperty { get; init; } = name => throw new NotSupportedException($"Document property '{name}' is not available to this formula context.");
    /// <summary>The report run's <c>Rnd</c> generator. When null, one generator per <see cref="SharedVariables"/> store is used.</summary>
    public CrystalRandom? Random { get; init; }
    /// <summary>The declared Crystal type of a reference ({T.F}, {?P}, {@F}), or null when unknown. It turns one supplied value of a multi-value or range parameter
    /// into an array or a range, drops the time from Date values, and decides Date against DateTime comparisons: Crystal widens a Date compared with a
    /// date-time to its whole day. When that decision needs a type this returns null for, evaluation fails rather than guess.</summary>
    public Func<string, CrystalValueType?> ReferenceType { get; init; } = _ => null;
}
