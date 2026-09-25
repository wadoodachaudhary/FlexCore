using System.Runtime.CompilerServices;

namespace Fx.ControlKit.Reports;

public sealed partial class CrystalFormula
{
    private sealed record Binding(string Name, string Scope, string Type, bool IsArray, bool IsRange)
    {
        public bool Persistent => Scope != "local";
        /// <summary>For a Basic <c>Dim x</c> without a type: the zero value of the type its assignments give it.</summary>
        public object? VariantDefault { get; set; }
    }
    private sealed record Parameter(Binding Binding, Node? Default);
    private sealed record FunctionSignature(IReadOnlyList<Parameter> Parameters);

    private sealed class Budget { public int Steps; public int Loops; public int Depth; public int MaxLoops = MaxLoopIterations; }
    private sealed class Frame(CrystalFormulaContext context, object? defaultValue, Budget budget)
    {
        public CrystalFormulaContext Context { get; } = context;
        public object? Default { get; } = defaultValue;
        public Budget Budget { get; } = budget;
        public Dictionary<string, object?> Locals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool DefaultNulls { get; set; } = context.DefaultValuesForNulls;
        public object? NonNull(object? value) => value is null or DBNull && !DefaultNulls ? throw new NullFormulaValue() : value;
        public void Tick(int work = 1) { if ((Budget.Steps += work) > MaxSteps) throw new InvalidDataException("Formula evaluation step limit exceeded."); }
        public void Loop() { if (++Budget.Loops > Budget.MaxLoops) throw new InvalidDataException($"A loop was evaluated more than the maximum number of times allowed ({Budget.MaxLoops:N0})."); }
        public IDictionary<string, object?> Variables(string scope) => scope switch { "shared" => Context.SharedVariables, "global" => Context.GlobalVariables, _ => Locals };
        public object? Read(Binding binding) => Variables(binding.Scope).TryGetValue(binding.Name, out var value) ? value : TypeDefault(binding);
        public object? Write(Binding binding, object? value)
        {
            if (binding.IsArray && value is not (null or DBNull)) value = binding.IsRange ? ToArray(value).Select(v => Retype(ToRange(v), DateFlag(BindingKind(binding)))).ToArray() : ToArray(value);
            else if (binding.IsRange) value = Retype(ToRange(value), DateFlag(BindingKind(binding)));
            if (value is object?[] items) Stored(items);
            return Variables(binding.Scope)[binding.Name] = value;
        }
    }
    private static object? ToRange(object? value) => value is null or DBNull or CrystalRange ? value : new CrystalRange(value, value);
    private sealed class NullFormulaValue : Exception;
    private sealed class ExitLoopException(bool forLoop) : Exception { public bool ForLoop { get; } = forLoop; }

    private static object? TypeDefault(Binding binding) =>
        binding.IsArray ? Array.Empty<object?>() : binding.IsRange ? new CrystalRange(ElementDefault(binding), ElementDefault(binding)) : ElementDefault(binding);
    private static object? ElementDefault(Binding binding) => binding.Type switch
    {
        "stringvar" => "", "booleanvar" => false, "datevar" or "datetimevar" => DateTime.MinValue, "timevar" => TimeSpan.Zero,
        "numbervar" or "currencyvar" => 0m, "variant" => binding.VariantDefault,
        _ => throw new InvalidDataException($"Unknown Crystal symbol '{binding.Name}'.")
    };

    private abstract record Node
    {
        public object? Get(Frame f)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            return Account(f, Eval(f));
        }
        protected abstract object? Eval(Frame f);
        public virtual IEnumerable<Node> Children => [];
    }
    private static object? Account(Frame f, object? value)
    {
        f.Tick();
        if (value is string { Length: > 256 } text)
        {
            if (text.Length > MaxTextLength) throw TextTooLong();
            f.Tick(text.Length >> 8);
        }
        else if (value is object?[] { Length: > 16 } array) f.Tick(array.Length >> 4);
        return value;
    }
    private static InvalidDataException TextTooLong() => new("Formula text result exceeds 1 MB.");
    private sealed record Literal(object? Value) : Node { protected override object? Eval(Frame f) => Value; }
    private sealed record DefaultValue(object? Value) : Node { protected override object? Eval(Frame f) => Value ?? f.Default; }
    private sealed record Reference(string Name) : Node { protected override object? Eval(Frame f) => Declared(f, Name, HostValue(f.Context.Resolve(Name))); }
    private sealed record Symbol(string Name) : Node { protected override object? Eval(Frame f) => SymbolValue(f, Name); }
    private sealed record Variable(Binding Binding, Node? Value) : Node
    {
        public override IEnumerable<Node> Children => Value is null ? [] : [Value];
        protected override object? Eval(Frame f)
        {
            if (Value is not null) return f.Write(Binding, Value.Get(f));
            var variables = f.Variables(Binding.Scope);
            if (!variables.TryGetValue(Binding.Name, out var current)) variables[Binding.Name] = current = TypeDefault(Binding);
            return current;
        }
    }
    private sealed record VarRef(Binding Binding) : Node { protected override object? Eval(Frame f) => f.Read(Binding); }
    private sealed record Assign(Binding Binding, Node Value) : Node
    {
        public override IEnumerable<Node> Children => [Value];
        protected override object? Eval(Frame f) => f.Write(Binding, Value.Get(f));
    }
    private sealed record ElementAssign(Binding Binding, Node Index, Node Value) : Node
    {
        public override IEnumerable<Node> Children => [Index, Value];
        protected override object? Eval(Frame f)
        {
            var array = AsArray(f.Read(Binding)) ?? throw new InvalidDataException($"'{Binding.Name}' is not an array.");
            var index = ArrayIndex(f.NonNull(Index.Get(f)), array.Length);
            var value = Value.Get(f);
            var copy = (object?[])array.Clone();
            f.Tick(copy.Length / 16);
            copy[index] = Binding.IsRange ? Retype(ToRange(value), DateFlag(BindingKind(Binding))) : value;
            f.Variables(Binding.Scope)[Binding.Name] = Stored(copy);
            return value;
        }
    }
    private sealed record Redim(Binding Binding, Node Size, bool Preserve) : Node
    {
        public override IEnumerable<Node> Children => [Size];
        protected override object? Eval(Frame f)
        {
            var size = Number(f.NonNull(Size.Get(f)));
            if (size < 1) throw new InvalidDataException("Redim requires an array size of at least 1.");
            if (size > MaxArrayLength) throw new InvalidDataException($"Crystal arrays are limited to {MaxArrayLength:N0} elements.");
            var array = Enumerable.Repeat(ElementDefault(Binding), (int)size).ToArray();
            f.Tick(array.Length / 16);
            if (Preserve && AsArray(f.Read(Binding)) is { } old) Array.Copy(old, array, Math.Min(old.Length, array.Length));
            return f.Variables(Binding.Scope)[Binding.Name] = Stored(array);
        }
    }
    private sealed record Sequence(Node[] Nodes) : Node
    {
        public override IEnumerable<Node> Children => Nodes;
        protected override object? Eval(Frame f) { object? value = null; foreach (var node in Nodes) value = node.Get(f); return value; }
    }
    private sealed record Conditional(Node Test, Node Yes, Node No) : Node
    {
        public override IEnumerable<Node> Children => [Test, Yes, No];
        protected override object? Eval(Frame f)
        {
            for (var node = this; ; f.Tick())
            {
                if (Boolean(f.NonNull(node.Test.Get(f)))) return node.Yes.Get(f);
                if (node.No is not Conditional next) return node.No.Get(f);
                node = next;
            }
        }
    }
    private sealed record Case(Node[] Items, Node Body);
    private sealed record Select(Node Value, Case[] Cases, Node Otherwise) : Node
    {
        public override IEnumerable<Node> Children => Cases.SelectMany(c => c.Items.Append(c.Body)).Prepend(Value).Append(Otherwise);
        protected override object? Eval(Frame f)
        {
            var value = f.NonNull(Value.Get(f));
            foreach (var entry in Cases)
                foreach (var item in entry.Items)
                    if (Member(value, f.NonNull(item.Get(f)), new Sides(f, Value, item))) return entry.Body.Get(f);
            return Otherwise.Get(f);
        }
    }
    private sealed record Loop(Node Test, Node Body, bool TestFirst) : Node
    {
        public override IEnumerable<Node> Children => [Test, Body];
        protected override object? Eval(Frame f)
        {
            while (!TestFirst || Boolean(f.NonNull(Test.Get(f))))
            {
                f.Loop();
                try { Body.Get(f); }
                catch (ExitLoopException exit) when (!exit.ForLoop) { break; }
                if (!TestFirst && !Boolean(f.NonNull(Test.Get(f)))) break;
            }
            return true;
        }
    }
    private sealed record ForLoop(Binding Binding, Node Start, Node End, Node? Step, Node Body) : Node
    {
        public override IEnumerable<Node> Children => Step is null ? [Start, End, Body] : [Start, End, Step, Body];
        protected override object? Eval(Frame f)
        {
            var current = Number(f.NonNull(Start.Get(f)));
            var end = Number(f.NonNull(End.Get(f)));
            var step = Step is null ? 1m : Number(f.NonNull(Step.Get(f)));
            if (step == 0) return true;
            while (true)
            {
                f.Write(Binding, current);
                if (step > 0 ? current > end : current < end) break;
                f.Loop();
                try { Body.Get(f); }
                catch (ExitLoopException exit) when (exit.ForLoop) { break; }
                current = Number(f.NonNull(f.Read(Binding))) + step;
            }
            return true;
        }
    }
    private sealed record OptionLoop(int Limit) : Node { protected override object? Eval(Frame f) { f.Budget.MaxLoops = Limit; return true; } }
    private sealed record ExitLoop(bool ForLoop) : Node { protected override object? Eval(Frame f) => throw new ExitLoopException(ForLoop); }
    private sealed record Unary(string Op, Node Value) : Node
    {
        public override IEnumerable<Node> Children => [Value];
        protected override object? Eval(Frame f) => Op switch { "not" => !Boolean(f.NonNull(Value.Get(f))), "-" => -Number(f.NonNull(Value.Get(f))), _ => Number(f.NonNull(Value.Get(f))) };
    }
    private sealed record Values(Node[] Nodes) : Node
    {
        public override IEnumerable<Node> Children => Nodes;
        protected override object? Eval(Frame f) => Nodes.Length > MaxArrayLength ? throw new InvalidDataException($"Crystal arrays are limited to {MaxArrayLength:N0} elements.") : Nodes.Select(n => n.Get(f)).ToArray();
    }
    private sealed record RangeNode(Node? Start, Node? End, bool IncludeStart, bool IncludeEnd) : Node
    {
        public override IEnumerable<Node> Children => new[] { Start, End }.OfType<Node>();
        protected override object? Eval(Frame f)
        {
            var range = new CrystalRange(Start is null ? null : f.NonNull(Start.Get(f)), End is null ? null : f.NonNull(End.Get(f)), IncludeStart, IncludeEnd);
            return range.Start is DateTime || range.End is DateTime ? DateBounds(f, range, Start, End) : range;
        }
    }
    private sealed record Subscript(Node Target, Node[] Indexes) : Node
    {
        public override IEnumerable<Node> Children => Indexes.Prepend(Target);
        protected override object? Eval(Frame f)
        {
            var value = Target.Get(f);
            for (var i = 0; i < Indexes.Length; i++)
            {
                if (i > 0) Account(f, value);
                value = Element(f.NonNull(value), f.NonNull(Indexes[i].Get(f)));
            }
            return value;
        }
        private static object? Element(object? target, object? index)
        {
            if (AsArray(target) is { } array)
            {
                if (index is not CrystalRange range) return array[ArrayIndex(index, array.Length)];
                var (start, end) = SliceBounds(range, array.Length, array.Length) ?? throw new InvalidDataException("A subscript must be between 1 and the size of the array.");
                return array[start..end];
            }
            if (target is not string text) throw new InvalidDataException("A string or an array is required before a subscript.");
            if (index is CrystalRange part)
            {
                var (start, end) = SliceBounds(part, text.Length, int.MaxValue) ?? throw new InvalidDataException("A string subscript must be at least 1.");
                return start < text.Length ? text[start..end] : "";
            }
            var position = RoundInt(Number(index));
            if (position < 0) position += text.Length + 1;
            if (position <= 0) throw new InvalidDataException("A string subscript must be at least 1.");
            return position <= text.Length ? text[position - 1].ToString() : "";
        }
    }
    private sealed record Step(string Op, Node Right);
    /// <summary>A left-associative operator chain <c>((First op1 r1) op2 r2) ...</c>, evaluated iteratively.</summary>
    private sealed record Binary(Node First, Step[] Steps) : Node
    {
        public override IEnumerable<Node> Children => Steps.Select(s => s.Right).Prepend(First);
        protected override object? Eval(Frame f)
        {
            var value = First.Get(f);
            for (var i = 0; i < Steps.Length; i++)
            {
                if (i > 0) Account(f, value);
                value = Apply(f, Steps[i].Op, f.NonNull(value), i == 0 ? First : null, Steps[i].Right);
            }
            return value;
        }
    }
    /// <summary>One operator step; <paramref name="left"/> is the left operand's expression when it is a single node (always, for comparisons).</summary>
    private static object? Apply(Frame f, string op, object? a, Node? left, Node right)
    {
        if (op == "and") return Boolean(a) && Boolean(f.NonNull(right.Get(f)));
        if (op == "or") return Boolean(a) || Boolean(f.NonNull(right.Get(f)));
        var b = f.NonNull(right.Get(f));
        if (AsArray(b) is { } items) f.Tick(items.Length);
        if (AsArray(a) is { } operands) f.Tick(operands.Length);
        var sides = new Sides(f, left, right);
        return op switch
        {
            "=" => Member(a, b, sides), "<>" or "!=" => !Member(a, b, sides),
            "<" or ">" or "<=" or ">=" when a is CrystalRange || b is CrystalRange => RangeOrder(op, a, b, sides),
            "<" or ">" or "<=" or ">=" when SameDay(a, b) => RangeOrder(op, a, Day(f, b, right), sides),
            "<" or ">" or "<=" or ">=" when SameDay(b, a) => RangeOrder(op, Day(f, a, left), b, sides),
            "<" => Compare(a, b) < 0, ">" => Compare(a, b) > 0, "<=" => Compare(a, b) <= 0, ">=" => Compare(a, b) >= 0,
            "xor" => Boolean(a) ^ Boolean(b), "eqv" => Boolean(a) == Boolean(b), "imp" => !Boolean(a) || Boolean(b),
            "&" => Concat(ToText(a, [], a is DateTime ? KindOf(f, left) : DateKind.Unknown), ToText(b, [], b is DateTime ? KindOf(f, right) : DateKind.Unknown)),
            "+" when AsArray(a) is not null || AsArray(b) is not null || a is CrystalRange || b is CrystalRange => Append(a, b),
            "+" when a is string || b is string => Concat(Text(a), Text(b)),
            "+" when a is DateTime date && b is TimeSpan time => date.Add(time),
            "+" when a is TimeSpan time && b is DateTime date => date.Add(time),
            "+" when a is DateTime date => date.AddDays((double)Number(b)),
            "+" when b is DateTime date => date.AddDays((double)Number(a)),
            "+" when a is TimeSpan time => TimeOfDay(time.TotalSeconds + (double)Number(b)),
            "-" when a is DateTime date && b is DateTime other => (decimal)(date - other).TotalDays,
            "-" when a is TimeSpan time && b is TimeSpan other => (decimal)(time - other).TotalSeconds,
            "-" when a is DateTime date && b is TimeSpan time => date - time,
            "-" when a is DateTime date => date.AddDays(-(double)Number(b)),
            "-" when a is TimeSpan time => TimeOfDay(time.TotalSeconds - (double)Number(b)),
            "+" => Number(a) + Number(b), "-" => Number(a) - Number(b), "*" => Number(a) * Number(b),
            "/" => Number(b) == 0 ? throw new InvalidDataException("Division by zero.") : Number(a) / Number(b),
            "%" => Number(b) == 0 ? throw new InvalidDataException("Division by zero.") : Number(a) * 100 / Number(b),
            "\\" => RoundInt(Number(b)) == 0 ? throw new InvalidDataException("Division by zero.") : decimal.Truncate((decimal)RoundInt(Number(a)) / RoundInt(Number(b))),
            "mod" => Number(b) == 0 ? throw new InvalidDataException("Division by zero.") : Number(a) % Number(b),
            "^" => (decimal)Math.Pow((double)Number(a), (double)Number(b)),
            "in" => b is CrystalRange || AsArray(b) is not null ? Member(a, b, sides)
                : a is string or null or DBNull && b is string or null or DBNull ? Find(f, Text(b), Text(a), 0, true) >= 0
                : a is DateTime moment && b is DateTime day ? InDay(moment, day, sides)
                : throw new InvalidDataException("Crystal 'in' requires a range or an array on its right, or a string on both sides."),
            "like" => AnyOf(b, pattern => Like(f, Text(a), Text(pattern))),
            "startswith" => AnyOf(b, prefix => Text(a).StartsWith(Text(prefix), StringComparison.OrdinalIgnoreCase)),
            _ => throw new NotSupportedException($"Crystal operator '{op}' is not implemented.")
        };
    }
    private static string Concat(string left, string right) => (long)left.Length + right.Length > MaxTextLength ? throw TextTooLong() : left + right;
    /// <summary>Crystal <c>+</c> with an array or range operand: array concatenation, a scalar becoming a one-element array (or range array).</summary>
    private static object?[] Append(object? left, object? right)
    {
        var (a, b) = (AsArray(left) ?? [left], AsArray(right) ?? [right]);
        if (a.Length + b.Length > MaxArrayLength) throw new InvalidDataException($"Crystal arrays are limited to {MaxArrayLength:N0} elements.");
        if (ElementKind(a) is { } first && ElementKind(b) is { } second && first != second)
            throw new InvalidDataException($"Crystal '+' cannot combine {first} and {second} values into one array.");
        var ranges = a.Concat(b).Any(v => v is CrystalRange);
        return ranges ? a.Concat(b).Select(ToRange).ToArray() : [.. a, .. b];
    }
    private static string? ElementKind(object?[] items)
    {
        foreach (var item in items) if (Kind(item) is { } kind) return kind;
        return null;
    }
    private static string? Kind(object? value) => value switch
    {
        null or DBNull => null, string => "string", bool => "boolean", DateTime => "date", TimeSpan => "time",
        CrystalRange range => Kind(range.Start) ?? Kind(range.End), object?[] items => ElementKind(items),
        decimal or double or float or int or long or short or byte or sbyte or uint or ulong or ushort => "number",
        _ => value.GetType().Name
    };
    private sealed record Call(string Name, Node[] Args) : Node
    {
        public override IEnumerable<Node> Children => Args;
        protected override object? Eval(Frame f) => Invoke(f, this);
    }
    private sealed record CustomCall(string Name, Node[] Args, IReadOnlyDictionary<string, CrystalCustomFunction> Functions) : Node
    {
        public override IEnumerable<Node> Children => Args;
        protected override object? Eval(Frame f)
        {
            var function = Functions.TryGetValue(Name, out var found) ? found : throw new NotSupportedException($"Crystal function '{Name}' is not implemented.");
            return function.Body.Invoke(f, function.Name, Args.Select(a => f.NonNull(a.Get(f))).ToArray());
        }
    }

    private static IEnumerable<Node> Walk(Node node)
    {
        var pending = new Stack<Node>(); pending.Push(node);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            foreach (var child in current.Children.Reverse()) pending.Push(child);
        }
    }

    private static object? Infer(Node node)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        while (node is Conditional c)
        {
            if (Infer(c.Yes) is { } yes) return yes;
            node = c.No;
        }
        return node switch
        {
            Literal { Value: string } => "", Literal { Value: bool } => false, Literal { Value: decimal } => 0m,
            Binary b => b.Steps.Aggregate(Infer(b.First), (left, step) => step.Op switch
            {
                "&" => "",
                "+" => (left, Infer(step.Right)) switch { (string, _) or (_, string) => "", (decimal, decimal or null) or (null, decimal) => 0m, _ => null },
                "-" => left is decimal ? 0m : null,
                "*" or "/" or "%" or "\\" or "mod" or "^" => 0m,
                _ => false
            }),
            Unary u => u.Op == "not" ? false : 0m,
            Sequence s => s.Nodes.Length == 0 ? null : Infer(s.Nodes[^1]),
            Select s => s.Cases.Select(c => Infer(c.Body)).FirstOrDefault(v => v is not null) ?? Infer(s.Otherwise),
            VarRef v => BindingDefault(v.Binding), Variable v => BindingDefault(v.Binding), Assign a => BindingDefault(a.Binding) ?? Infer(a.Value),
            Subscript s => Infer(s.Target) is string ? "" : null,
            Loop or ForLoop => false,
            Call c => FunctionDefault(c.Name),
            _ => null
        };
    }
    private static object? BindingDefault(Binding binding) => binding.Type == "var" ? null : TypeDefault(binding);
}
