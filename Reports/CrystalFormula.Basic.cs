namespace Fx.ControlKit.Reports;

public sealed partial class CrystalFormula
{
    private static readonly HashSet<string> BasicKeywords = new(("if then else elseif end select case for to step next while wend do loop until exit function dim global shared local redim preserve as " +
        "optional byval byref let call and or xor eqv imp not mod in like startswith is upto upto_ upfrom upfrom_ _to _to_ to_").Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> BasicTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["number"] = "numbervar", ["double"] = "numbervar", ["currency"] = "currencyvar", ["boolean"] = "booleanvar", ["date"] = "datevar", ["time"] = "timevar", ["datetime"] = "datetimevar", ["string"] = "stringvar"
    };

    /// <summary>Crystal Basic syntax under each spelling report XML uses for it.</summary>
    public static bool IsBasicSyntax(string? syntax) =>
        syntax is not null && (syntax.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            || syntax.Equals("BasicSyntax", StringComparison.OrdinalIgnoreCase)
            || syntax.Equals("crFormulaSyntaxBasic", StringComparison.OrdinalIgnoreCase));

    private sealed class ExitFunctionException : Exception;
    private sealed record ExitFunction : Node { protected override object? Eval(Frame f) => throw new ExitFunctionException(); }
    private sealed record FunctionBody(Node Body, Binding Result) : Node
    {
        public override IEnumerable<Node> Children => [Body];
        protected override object? Eval(Frame f)
        {
            try { Body.Get(f); } catch (ExitFunctionException) { }
            return f.Read(Result);
        }
    }

    private sealed class BasicParser(string text, IReadOnlyDictionary<string, CrystalCustomFunction>? functions, bool customFunction) : ParserBase(text, functions, customFunction, true)
    {
        private int _forDepth, _doDepth;
        private static readonly string[] Terminators = ["end", "else", "elseif", "case", "loop", "next", "wend"];
        protected override bool IsKeyword(string word) => BasicKeywords.Contains(word);
        private bool AtLineEnd => Current.Kind == TokenKind.End || IsSymbol("\n");
        private void SkipSeparators() { while (Accept("\n") || Accept(":")) { } }

        public Node ParseFormula()
        {
            var result = Declare("formula", "local", "variant", false, false);
            SkipSeparators();
            var option = ParseOptionLoop();
            var body = option is null ? ParseBlock() : new Sequence([option, ParseBlock()]);
            SkipSeparators();
            var root = new FunctionBody(body, result);
            Finish(root);
            RequireResult(root, "formula");
            InferVariants(root);
            return root;
        }
        public (FunctionSignature Signature, Node Body) ParseFunction(string name)
        {
            SkipSeparators(); ExpectWord("function");
            var header = ExpectName();
            if (!header.Equals(name, StringComparison.OrdinalIgnoreCase)) throw Fail($"the function header names '{header}', not '{name}'");
            var parameters = new List<Parameter>(); var optional = false;
            if (Accept("("))
            {
                if (!Accept(")"))
                {
                    do
                    {
                        if (AcceptWord("optional")) optional = true;
                        else if (optional) throw Fail("optional arguments must follow the required ones");
                        if (AcceptWord("byref")) throw new NotSupportedException($"Custom function '{name}': ByRef arguments are not implemented.");
                        AcceptWord("byval");
                        var parameter = ExpectName();
                        var isArray = Accept("(") && Consume(")");
                        var (type, isRange) = AcceptWord("as") ? ParseType() : throw Fail("'As' expected");
                        var binding = Declare(parameter, "local", type, isArray, isRange);
                        Node? fallback = null;
                        if (optional) { Expect("="); fallback = ParseExpression(); }
                        parameters.Add(new(binding, fallback));
                    }
                    while (Accept(","));
                    Expect(")");
                }
            }
            var (resultType, resultRange, resultArray) = ("variant", false, false);
            if (AcceptWord("as")) { (resultType, resultRange) = ParseType(); resultArray = Accept("(") && Consume(")"); }
            var result = Declare(header, "local", resultType, resultArray, resultRange);
            var body = ParseBlock();
            ExpectWord("end"); ExpectWord("function");
            SkipSeparators();
            var root = new FunctionBody(body, result);
            Finish(root);
            if (resultType == "variant") RequireResult(root, header);
            InferVariants(root);
            return (new(parameters), root);
        }
        private bool Consume(string symbol) { Expect(symbol); return true; }
        /// <summary>Crystal rejects a Basic formula (or an untyped function) whose result variable is never assigned: its type would be unknown.</summary>
        private static void RequireResult(FunctionBody root, string name)
        {
            if (!Walk(root.Body).Any(n => n is Assign a && ReferenceEquals(a.Binding, root.Result) || n is ElementAssign e && ReferenceEquals(e.Binding, root.Result) || n is ForLoop l && ReferenceEquals(l.Binding, root.Result)))
                throw new InvalidDataException($"Invalid Crystal formula: a Basic syntax formula must assign a value to '{name}'.");
        }
        private static void InferVariants(Node root)
        {
            var writes = Walk(root).Select(n => n switch
            {
                Assign a when a.Binding.Type == "variant" => (a.Binding, a.Value),
                ElementAssign e when e.Binding.Type == "variant" => (e.Binding, e.Value),
                ForLoop l when l.Binding.Type == "variant" => (l.Binding, l.Start),
                _ => default
            }).Where(w => w.Binding is not null).ToArray();
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var (binding, value) in writes)
                    if (binding.VariantDefault is null && Infer(value) is { } zero && zero is not (object?[] or CrystalRange))
                    { binding.VariantDefault = zero; changed = true; }
            }
        }
        private (string Type, bool IsRange) ParseType()
        {
            if (Current.Kind != TokenKind.Word || !BasicTypes.TryGetValue(Current.Value, out var type)) throw Fail("a type such as Number or String is expected");
            _index++;
            return (type, AcceptWord("range"));
        }

        private Node ParseBlock(bool singleLine = false)
        {
            var nodes = new List<Node>();
            while (true)
            {
                if (singleLine) { while (Accept(":")) { } } else SkipSeparators();
                if (Current.Kind == TokenKind.End || Terminators.Any(IsWord) || singleLine && AtLineEnd) break;
                nodes.Add(ParseStatement());
                if (IsSymbol(":")) continue;
                if (singleLine) break;
                if (!AtLineEnd && !Terminators.Any(IsWord)) throw Fail("end of statement expected");
            }
            return nodes.Count == 1 ? nodes[0] : new Sequence(nodes.ToArray());
        }

        private Node ParseStatement() => Nest(() =>
        {
            if (AcceptWord("if")) return ParseIf();
            if (AcceptWord("select")) { ExpectWord("case"); return ParseSelect(); }
            if (AcceptWord("for")) return ParseFor();
            if (AcceptWord("while"))
            {
                var test = ParseExpression();
                var body = InLoop(() => ParseBlock());
                ExpectWord("wend");
                return new Loop(test, body, true);
            }
            if (AcceptWord("do")) return ParseDo();
            if (AcceptWord("exit"))
            {
                if (AcceptWord("for")) return _forDepth > 0 ? new ExitLoop(true) : throw Fail("Exit For is only allowed inside a For loop");
                if (AcceptWord("do")) return _doDepth > 0 ? new ExitLoop(false) : throw Fail("Exit Do is only allowed inside a Do or While loop");
                if (AcceptWord("function")) return _customFunction ? new ExitFunction() : throw Fail("Exit Function is only allowed inside a custom function");
                throw Fail("'For', 'Do' or 'Function' expected after 'Exit'");
            }
            if (IsWord("dim") || IsWord("global") || IsWord("shared") || IsWord("local")) return ParseDim();
            if (AcceptWord("redim")) return ParseRedim();
            AcceptWord("let");
            if (AcceptWord("call")) return ParseExpression();
            if (Current.Kind == TokenKind.Word && !IsKeyword(Current.Value) && _declared.TryGetValue(Current.Value, out var binding))
            {
                var mark = _index; _index++;
                if (Accept("(") && !IsSymbol(")"))
                {
                    var index = ParseExpression(); Expect(")");
                    if (Accept("=")) return new ElementAssign(binding, index, ParseExpression());
                }
                else if (Accept("=")) return new Assign(binding, ParseExpression());
                _index = mark;
            }
            return ParseExpression();
        });
        private Node InLoop(Func<Node> body)
        {
            _doDepth++;
            try { return body(); } finally { _doDepth--; }
        }
        private Node ParseIf()
        {
            var test = ParseExpression(); ExpectWord("then");
            if (!AtLineEnd)
            {
                var yes = ParseBlock(true);
                return new Conditional(test, yes, AcceptWord("else") ? ParseBlock(true) : new Literal(true));
            }
            var branches = new List<(Node Test, Node Body)> { (test, ParseBlock()) };
            while (AcceptWord("elseif")) { var condition = ParseExpression(); ExpectWord("then"); branches.Add((condition, ParseBlock())); }
            Node otherwise = AcceptWord("else") ? ParseBlock() : new Literal(true);
            ExpectWord("end"); ExpectWord("if");
            for (var i = branches.Count - 1; i >= 0; i--) otherwise = new Conditional(branches[i].Test, branches[i].Body, otherwise);
            return otherwise;
        }
        private Node ParseSelect()
        {
            var value = ParseExpression();
            var cases = new List<Case>(); Node otherwise = new Literal(true);
            SkipSeparators();
            while (AcceptWord("case"))
            {
                if (AcceptWord("else")) { otherwise = ParseBlock(); break; }
                var items = new List<Node> { ParseExpression() };
                while (Accept(",")) items.Add(ParseExpression());
                cases.Add(new(items.ToArray(), ParseBlock()));
            }
            ExpectWord("end"); ExpectWord("select");
            return new Select(value, cases.ToArray(), otherwise);
        }
        private Node ParseFor()
        {
            var name = ExpectName();
            if (!_declared.TryGetValue(name, out var binding) || binding.Type is not ("numbervar" or "currencyvar" or "variant") || binding.IsArray || binding.IsRange)
                throw Fail($"the For loop variable '{name}' must be a declared Number variable");
            Expect("="); var start = ParseBinary(AdditivePrecedence);
            ExpectWord("to"); var end = ParseBinary(AdditivePrecedence);
            var step = AcceptWord("step") ? ParseBinary(AdditivePrecedence) : null;
            Node body;
            _forDepth++;
            try { body = ParseBlock(); } finally { _forDepth--; }
            ExpectWord("next");
            if (Current.Kind == TokenKind.Word && !IsKeyword(Current.Value))
            {
                if (!Current.Value.Equals(name, StringComparison.OrdinalIgnoreCase)) throw Fail($"'Next {name}' expected");
                _index++;
            }
            return new ForLoop(binding, start, end, step, body);
        }
        private Node ParseDo()
        {
            if (IsWord("while") || IsWord("until"))
            {
                var until = AcceptWord("until"); if (!until) ExpectWord("while");
                var test = ParseExpression();
                var body = InLoop(() => ParseBlock());
                ExpectWord("loop");
                return new Loop(until ? new Unary("not", test) : test, body, true);
            }
            var statements = InLoop(() => ParseBlock());
            ExpectWord("loop");
            var untilAfter = AcceptWord("until"); if (!untilAfter) ExpectWord("while");
            var condition = ParseExpression();
            return new Loop(untilAfter ? new Unary("not", condition) : condition, statements, false);
        }
        private Node ParseDim()
        {
            var scope = _tokens[_index++].Value.ToLowerInvariant() switch { "global" => "global", "shared" => "shared", _ => "local" };
            if (_customFunction && scope != "local") throw Fail("custom functions can only declare local variables");
            var nodes = new List<Node>();
            do
            {
                var name = ExpectName();
                Node? size = null; var isArray = false;
                if (Accept("(")) { isArray = true; if (!Accept(")")) { size = ParseExpression(); Expect(")"); } }
                var (type, isRange) = AcceptWord("as") ? ParseType() : ("variant", false);
                var binding = Declare(name, scope, type, isArray, isRange);
                nodes.Add(new Variable(binding, null));
                if (size is not null) nodes.Add(new Redim(binding, size, true));
            }
            while (Accept(","));
            return nodes.Count == 1 ? nodes[0] : new Sequence(nodes.ToArray());
        }
        private Node ParseRedim()
        {
            var preserve = AcceptWord("preserve");
            var nodes = new List<Node>();
            do
            {
                var name = ExpectName();
                if (!_declared.TryGetValue(name, out var binding) || !binding.IsArray) throw Fail($"ReDim requires a declared array variable; '{name}' is not one");
                Expect("("); var size = ParseExpression(); Expect(")");
                nodes.Add(new Redim(binding, size, preserve));
            }
            while (Accept(","));
            return nodes.Count == 1 ? nodes[0] : new Sequence(nodes.ToArray());
        }

        protected override Node ParsePostfix()
        {
            var node = ParsePrimary(); List<Node>? indexes = null;
            while (IsSymbol("(")) { _index++; (indexes ??= []).Add(ParseExpression()); Expect(")"); }
            return Index(node, indexes);
        }
        private Node ParsePrimary()
        {
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.Number or TokenKind.Text or TokenKind.Date: _index++; return new Literal(token.Literal);
                case TokenKind.Field:
                    if (_customFunction) throw Fail("custom functions cannot refer to report fields");
                    _index++; return new Reference(token.Value);
                case TokenKind.Symbol when token.Value == "(":
                {
                    _index++; var value = ParseExpression(); Expect(")");
                    return value;
                }
                case TokenKind.Word when !IsKeyword(token.Value): break;
                case TokenKind.End: throw Fail("an expression is expected");
                default: throw Fail("an expression is expected");
            }
            if (_declared.TryGetValue(token.Value, out var binding)) { _index++; return new VarRef(binding); }
            _index++;
            return IsSymbol("(") ? ParseCall(token.Value, ")") : Unresolved(token.Value);
        }
    }
}
