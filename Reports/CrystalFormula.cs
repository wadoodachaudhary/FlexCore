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
    /// <summary>The global and shared variables the formula declares, as "scope:name" (name in lower case).</summary>
    public IReadOnlyCollection<string> PersistentVariables { get; }
    /// <summary>The global and shared variables the formula assigns, as "scope:name" (name in lower case).</summary>
    public IReadOnlyCollection<string> AssignedVariables { get; }
    /// <summary>True when every global or shared variable the formula assigns is a temporary: one it sets before it reads it,
    /// at its top level (OnlyTemporaries). True for a formula that assigns none.</summary>
    internal bool AssignsOnlyTemporaries { get; }
    /// <summary>The global and shared variables the formula sets before it uses them, as "scope:name": the first use of each,
    /// in the order of its top-level statements, is one of those statements setting it (Temporaries). A run of the formula
    /// never reads what another formula left in such a variable.</summary>
    internal IReadOnlyCollection<string> TemporaryVariables { get; }
    /// <summary>The global and shared variables the formula may read before it sets them, as "scope:name" (Reads): one it
    /// refers to, declares inside an expression or as its result (Crystal's `WhilePrintingRecords; Shared NumberVar x;`
    /// prints x), or changes an element of, less its temporaries. What another formula left in any other variable it
    /// declares never reaches what it prints or assigns.</summary>
    internal IReadOnlyCollection<string> ReadVariables { get; }
    /// <summary>For each global or shared variable the formula assigns, as "scope:name", what the value it leaves there may
    /// come from (AssignmentFlow) when it runs to its end: the global and shared variables as they were when the formula
    /// began, and the formulas it refers to, as "{@name}". A variable the formula may leave as it was (set under a condition
    /// or in a loop, or an array element) comes from itself too. A formula that fails, or stops at a null value
    /// (Evaluate's stopped), may leave some of its variables set and not the rest.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyCollection<string>> AssignmentReads => _assignmentReads ??= AssignmentFlow(_root, AssignedVariables);
    private IReadOnlyDictionary<string, IReadOnlyCollection<string>>? _assignmentReads;
    public bool UsesRecordContext { get; }
    public bool UsesRecordSequenceContext { get; }
    public CrystalEvaluationTime? EvaluationTime { get; }
    public IReadOnlyList<string> EvaluateAfter { get; }
    public bool UsesPageContext { get; }
    public bool UsesGridContext { get; }
    public bool UsesAggregates { get; }
    /// <summary>True when an aggregate names a group (Sum ({x}, {group}) or a group condition); one with the field alone is over the whole report.</summary>
    public bool UsesGroupedAggregates { get; }
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
        WritesPersistentVariables = nodes.Any(n => SetBinding(n) is { Persistent: true });
        PersistentVariables = declarations.Where(b => b.Persistent).Select(VariableKey).ToHashSet();
        AssignedVariables = nodes.Select(SetBinding).Where(b => b is { Persistent: true }).Select(b => VariableKey(b!)).ToHashSet();
        AssignsOnlyTemporaries = !WritesPersistentVariables || OnlyTemporaries(root, AssignedVariables);
        TemporaryVariables = WritesPersistentVariables ? Temporaries(root) : [];
        ReadVariables = Reads(root).Except(TemporaryVariables).ToHashSet();
        var symbols = nodes.OfType<Symbol>().Select(n => n.Name.ToLowerInvariant()).ToHashSet();
        var calls = nodes.OfType<Call>().ToArray();
        UsesGridContext = symbols.Overlaps(GridSymbols) || calls.Any(call => GridFunctions.Contains(call.Name));
        UsesGroupNumber = symbols.Contains("groupnumber");
        UsesRecordSequenceContext = symbols.Overlaps(["recordnumber", "onfirstrecord", "onlastrecord", "groupnumber"])
            || calls.Any(n => n.Name.ToLowerInvariant() is "previous" or "next" or "previousvalue" or "nextvalue" or "previousisnull" or "nextisnull" or "onfirstrecord" or "onlastrecord" or "counthierarchicalchildren" or "groupname");
        UsesRecordContext = UsesRecordSequenceContext || symbols.Contains("currentfieldvalue");
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
        UsesGroupedAggregates = aggregates.Any(n => n.Args.Length > 1);
        AggregateReferences = aggregates.SelectMany(n => n.Args.OfType<Reference>()).Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        RequiresPrintPass = symbols.Contains("whileprintingrecords");
        UsesEvaluationDirectives = symbols.Overlaps(["whileprintingrecords", "whilereadingrecords", "beforereadingrecords"]) || after.Length > 0;
        UnknownSymbols = unknown;
        CustomFunctions = nodes.OfType<CustomCall>().Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string VariableKey(Binding binding) => binding.Scope + ":" + binding.Name.ToLowerInvariant();

    // The variable a node sets: a declaration with a value, an assignment (of an array element too), a ReDim or a For loop.
    private static Binding? SetBinding(Node node) => node switch
    {
        Variable { Value: not null } v => v.Binding, Assign a => a.Binding, ElementAssign e => e.Binding, Redim r => r.Binding, ForLoop l => l.Binding,
        _ => null
    };

    // The variable a node of the statement reads: a reference, a declaration used as a value (`(Global NumberVar n) + 1`), an
    // assignment to an array element or a ReDim Preserve (both keep the array's other elements). A declaration that is the
    // statement itself reads nothing.
    private static Binding? ReadBinding(Node node, Node statement) => node switch
    {
        VarRef reference => reference.Binding,
        Variable { Value: null } declaration when !ReferenceEquals(declaration, statement) => declaration.Binding,
        ElementAssign element => element.Binding, Redim { Preserve: true } redim => redim.Binding,
        _ => null
    };

    // The formula's top-level statements in order, through Basic's function body (the statements no condition or loop holds),
    // each with the global or shared variable it sets (a declaration with a value, or an assignment) and the expression that
    // gives the value; a statement that sets none has no target and is that expression itself.
    private static List<(Node Statement, Binding? Target, Node Value)> TopStatements(Node root)
    {
        var statements = new List<(Node Statement, Binding? Target, Node Value)>();
        void Add(Node node)
        {
            if (node is Sequence sequence) foreach (var child in sequence.Nodes) Add(child);
            else if (node is FunctionBody body) Add(body.Body);
            else statements.Add(node switch
            {
                Variable { Binding.Persistent: true, Value: { } initial } declaration => (node, declaration.Binding, initial),
                Assign { Binding.Persistent: true } assignment => (node, assignment.Binding, assignment.Value),
                _ => (node, null, node)
            });
        }
        Add(root);
        return statements;
    }

    // True when each global or shared variable the formula assigns is set before the formula reads it, by a statement of its
    // top level (no condition or loop around it): a declaration with a value (`NumberVar d := PageNumber - 1`), or a
    // declaration and then an assignment (`NumberVar d; d := ...`; Basic's `Global d As Number` and `d = ...`). A second run
    // of such a formula computes nothing from its first. Variables it only reads (one the page header sets) and local
    // variables (Basic's formula result among them) do not count. A counter (`x := x + 1`, `x := (Global NumberVar x) + 1`),
    // an assignment under a condition or in a loop, an array element, a ReDim, or a For loop over such a variable makes it
    // no temporary.
    private static bool OnlyTemporaries(Node root, IReadOnlyCollection<string> assigned)
    {
        var initialized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (statement, target, value) in TopStatements(root))
        {
            foreach (var node in Walk(value))
            {
                if (ReadBinding(node, statement) is { Persistent: true } read && VariableKey(read) is var key
                    && assigned.Contains(key) && !initialized.Contains(key)) return false;
                if (SetBinding(node) is { Persistent: true }) return false;
            }
            if (target is not null) initialized.Add(VariableKey(target));
        }
        return true;
    }

    // The global and shared variables whose first use, in the order of the top-level statements, is one of those statements
    // setting it (TemporaryVariables). A variable read, or set under a condition, in a loop or inside an expression, before
    // that is none; so is one only declared.
    private static HashSet<string> Temporaries(Node root)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var temporaries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (statement, target, value) in TopStatements(root))
        {
            foreach (var node in Walk(value))
                if ((ReadBinding(node, statement) ?? SetBinding(node)) is { Persistent: true } binding) used.Add(VariableKey(binding));
            if (target is not null && used.Add(VariableKey(target))) temporaries.Add(VariableKey(target));
        }
        return temporaries;
    }

    internal bool? ConstantGridSuppression => UsesGridContext && WritesPersistentVariables && !UsesSharedVariables
        && _root is Sequence { Nodes: [.., var last] } ? last switch
        {
            Literal { Value: bool value } => value,
            Symbol symbol when bool.TryParse(symbol.Name, out var value) => value,
            _ => null
        } : null;

    // The global and shared variables a node of a top-level statement reads (ReadBinding), and in Crystal syntax the last
    // statement when it is a declaration of its own: it is the formula's result (a Basic formula's result is its `formula`
    // variable). ReadVariables takes the temporaries out.
    private static HashSet<string> Reads(Node root)
    {
        var reads = new HashSet<string>(StringComparer.Ordinal);
        var statements = TopStatements(root);
        foreach (var (statement, _, value) in statements)
            foreach (var node in Walk(value))
                if (ReadBinding(node, statement) is { Persistent: true } read) reads.Add(VariableKey(read));
        if (root is not FunctionBody && statements.Count > 0 && statements[^1].Statement is Variable { Value: null, Binding.Persistent: true } result)
            reads.Add(VariableKey(result.Binding));
        return reads;
    }

    // AssignmentReads: follows the formula in order, with what each variable it has set so far comes from. The branches of a
    // condition join, each variable one sets coming from what the condition reads too, and from itself when another branch
    // leaves it; anything else that sets a variable inside it (a loop, an assignment inside an expression, an array element,
    // a ReDim) has each such variable come from everything it reads, itself among them. A formula too deep to follow has each
    // variable it assigns come from everything it reads and assigns.
    private static Dictionary<string, IReadOnlyCollection<string>> AssignmentFlow(Node root, IReadOnlyCollection<string> assigned)
    {
        var order = Walk(root).ToList();
        var setting = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        for (var index = order.Count - 1; index >= 0; index--)
            if (SetBinding(order[index]) is not null || order[index].Children.Any(setting.Contains)) setting.Add(order[index]);
        var env = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        try { Flow(root); }
        catch (InsufficientExecutionStackException)
        {
            var all = Reads(root);
            all.UnionWith(assigned);
            all.UnionWith(order.OfType<Reference>().Select(r => r.Name).Where(n => n.StartsWith("{@", StringComparison.Ordinal)));
            return assigned.ToDictionary(v => v, _ => (IReadOnlyCollection<string>)all, StringComparer.Ordinal);
        }
        return assigned.ToDictionary(v => v, v => (IReadOnlyCollection<string>)(env.TryGetValue(v, out var from) ? from : new HashSet<string>(StringComparer.Ordinal) { v }), StringComparer.Ordinal);

        HashSet<string> Of(string key) => env.TryGetValue(key, out var from) ? new(from, StringComparer.Ordinal)
            : key.StartsWith("local:", StringComparison.Ordinal) ? new(StringComparer.Ordinal) : new(StringComparer.Ordinal) { key };
        HashSet<string>? Read(Node node) => node switch
        {
            VarRef reference => Of(VariableKey(reference.Binding)),
            Variable { Value: null } declaration => Of(VariableKey(declaration.Binding)),
            ElementAssign element => Of(VariableKey(element.Binding)),
            Redim { Preserve: true } redim => Of(VariableKey(redim.Binding)),
            Reference { Name: var name } when name.StartsWith("{@", StringComparison.Ordinal) => new(StringComparer.Ordinal) { name },
            _ => null
        };
        HashSet<string> Uses(Node node)
        {
            var from = new HashSet<string>(StringComparer.Ordinal);
            foreach (var inner in Walk(node)) if (Read(inner) is { } read) from.UnionWith(read);
            return from;
        }
        HashSet<string> Set(Binding binding, HashSet<string> from)
        {
            env[VariableKey(binding)] = from;
            return new(from, StringComparer.Ordinal);
        }
        HashSet<string> Branches(HashSet<string> test, IEnumerable<Node> branches)
        {
            var start = env;
            var value = new HashSet<string>(test, StringComparer.Ordinal);
            var ends = new List<Dictionary<string, HashSet<string>>>();
            foreach (var branch in branches)
            {
                env = new(start, StringComparer.Ordinal);
                value.UnionWith(Flow(branch));
                ends.Add(env);
            }
            env = start;
            foreach (var key in ends.SelectMany(end => end.Where(e => !start.TryGetValue(e.Key, out var was) || !ReferenceEquals(was, e.Value)).Select(e => e.Key))
                         .Distinct(StringComparer.Ordinal).ToArray())
            {
                var joined = new HashSet<string>(test, StringComparer.Ordinal);
                foreach (var end in ends) joined.UnionWith(end.TryGetValue(key, out var from) ? from : Of(key));
                env[key] = joined;
            }
            return value;
        }
        HashSet<string> Flow(Node node)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (!setting.Contains(node)) return Uses(node);
            switch (node)
            {
                case Sequence sequence:
                {
                    var value = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var child in sequence.Nodes) value = Flow(child);
                    return value;
                }
                case FunctionBody body:
                    Flow(body.Body);
                    return Of(VariableKey(body.Result));
                case Variable { Value: { } initial } declaration:
                    return Set(declaration.Binding, Flow(initial));
                case Assign assignment:
                    return Set(assignment.Binding, Flow(assignment.Value));
                case Conditional conditional:
                    return Branches(Flow(conditional.Test), [conditional.Yes, conditional.No]);
                case Select select when !select.Cases.Any(c => c.Items.Any(setting.Contains)):
                {
                    var test = Flow(select.Value);
                    foreach (var item in select.Cases.SelectMany(c => c.Items)) test.UnionWith(Uses(item));
                    return Branches(test, select.Cases.Select(c => c.Body).Append(select.Otherwise));
                }
                default:
                {
                    var value = new HashSet<string>(StringComparer.Ordinal);
                    var targets = new List<Binding>();
                    foreach (var inner in Walk(node))
                    {
                        if (SetBinding(inner) is { } target) { targets.Add(target); value.UnionWith(Of(VariableKey(target))); }
                        if (Read(inner) is { } read) value.UnionWith(read);
                    }
                    foreach (var target in targets) env[VariableKey(target)] = new(value, StringComparer.Ordinal);
                    return value;
                }
            }
        }
    }

    // Display formulas may use global declarations as temporaries when every read
    // is preceded by an unconditional initializer in the same cell evaluation.
    internal bool HasSelfContainedVariables()
    {
        if (!UsesPersistentVariables) return true;
        if (UsesSharedVariables || _root is not Sequence sequence) return false;
        var initialized = new HashSet<Binding>();
        foreach (var statement in sequence.Nodes)
        {
            var declaration = statement as Variable;
            foreach (var node in Walk(statement))
            {
                if (node is VarRef reference && reference.Binding.Persistent && !initialized.Contains(reference.Binding)) return false;
                if (node is Assign or ElementAssign or Redim or ForLoop || node is Variable && node != declaration) return false;
            }
            if (declaration is { Value: not null }) initialized.Add(declaration.Binding);
        }
        return true;
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

    public object? Evaluate(CrystalFormulaContext context) => Evaluate(context, recordSelection: false, out _);

    /// <summary>Evaluates the formula; stopped is true when it stopped at a null value (a field, or a formula it refers to,
    /// that is null) and gave null: it may have set some of the variables it assigns and not the rest.</summary>
    internal object? Evaluate(CrystalFormulaContext context, out bool stopped) => Evaluate(context, recordSelection: false, out stopped);

    internal object? EvaluateRecordSelection(CrystalFormulaContext context) => Evaluate(context, recordSelection: true, out _);

    private object? Evaluate(CrystalFormulaContext context, bool recordSelection, out bool stopped)
    {
        stopped = false;
        var frame = new Frame(context, _defaultValue, new Budget());
        try { return recordSelection ? RecordPredicate(frame, _root) : _root.Get(frame); }
        catch (NullFormulaValue) { stopped = true; return null; }
        catch (DivideByZeroException) { throw new InvalidDataException("Division by zero."); }
        catch (OverflowException ex) { throw new InvalidDataException("Crystal formula numeric overflow: " + ex.Message, ex); }
        catch (InsufficientExecutionStackException) { throw new InvalidDataException("Formula nesting is too deep to evaluate."); }
    }

    // Database record-selection predicates retain SQL's unknown Boolean value:
    // NULL OR true selects the record. Ordinary formulas still abort on null.
    private static object? RecordPredicate(Frame frame, Node node)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        frame.Tick();
        if (node is Binary logical && logical.Steps.All(step => step.Op is "and" or "or"))
        {
            var value = RecordBoolean(frame, logical.First);
            foreach (var step in logical.Steps)
            {
                frame.Tick();
                if (step.Op == "and" && value == false || step.Op == "or" && value == true) continue;
                var right = RecordBoolean(frame, step.Right);
                value = step.Op == "and" ? value & right : value | right;
            }
            return value;
        }
        if (node is Unary { Op: "not" } unary) return !RecordBoolean(frame, unary.Value);
        return node.Get(frame);
    }

    private static bool? RecordBoolean(Frame frame, Node node)
    {
        try
        {
            var value = RecordPredicate(frame, node);
            return value is null or DBNull ? null : Boolean(value);
        }
        catch (NullFormulaValue) { return null; }
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
            "number" or "integer" or "decimal" or "long" or "short" or "byte" or "float" or "double" or "int8s" or "int8u" or "int16s" or "int16u" or "int32s" or "int32u" or "int64s" => CrystalBaseType.Number,
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
    public Func<string, string, string, object?>? WeightedAverage { get; init; }
    public CrystalGridFormulaContext? Grid { get; init; }
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
    private DateTime? _now;
    /// <summary>The report's clock; the wall clock unless the host set one. Read lazily: every evaluation builds a context, few need the time.</summary>
    public DateTime Now { get => _now ??= DateTime.Now; init => _now = value; }
    public int RecordIndex { get; init; }
    public int RecordCount { get; init; }
    public object PageNumber { get; init; } = 1;
    public object TotalPageCount { get; init; } = 1;
    public int DrillDownGroupLevel { get; init; }
    public bool InRepeatedGroupHeader { get; init; }
    public bool DefaultValuesForNulls { get; init; }
    public Func<object?> CurrentFieldValue { get; init; } = () => null;
    public Func<object?> DefaultAttribute { get; init; } = () => throw new InvalidDataException("DefaultAttribute requires a formatting condition context.");
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
