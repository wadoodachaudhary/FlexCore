using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Fx.ControlKit.Reports;

public sealed partial class CrystalFormula
{
    private enum TokenKind { End, Number, Text, Date, Field, Word, Symbol }
    private readonly record struct Token(TokenKind Kind, string Value, object? Literal, int Position);

    private static readonly HashSet<string> Keywords = new(("if then else select case default while do for to step exit redim preserve and or xor eqv imp not mod in like startswith is " +
        "upto upto_ upfrom upfrom_ _to _to_ to_ local global shared numbervar currencyvar stringvar booleanvar datevar timevar datetimevar function optional option").Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly string[] TypeWords = "numbervar currencyvar stringvar booleanvar datevar timevar datetimevar".Split(' ');
    private static readonly Dictionary<string, int> Precedence = new(StringComparer.OrdinalIgnoreCase)
    {
        ["imp"] = 1, ["eqv"] = 2, ["xor"] = 3, ["or"] = 4, ["and"] = 5,
        ["="] = 7, ["<>"] = 7, ["!="] = 7, ["startswith"] = 7, ["like"] = 7, ["in"] = 7,
        ["<"] = 8, [">"] = 8, ["<="] = 8, [">="] = 8, ["&"] = 9,
        ["to"] = 10, ["_to"] = 10, ["to_"] = 10, ["_to_"] = 10,
        ["+"] = 11, ["-"] = 11, ["mod"] = 12, ["\\"] = 13, ["*"] = 14, ["/"] = 14, ["%"] = 14
    };
    private const int EqualityPrecedence = 7, RelationalPrecedence = 8, RangePrecedence = 10, AdditivePrecedence = 11;

    private static InvalidDataException Error(string text, int position, string message)
    {
        int line = 1, column = 1;
        for (var i = 0; i < position && i < text.Length; i++) if (text[i] == '\n') { line++; column = 1; } else column++;
        return new($"Invalid Crystal formula: {message} (Line {line}, Column {column}).");
    }

    private static List<Token> Tokenize(string text, bool basic = false)
    {
        var tokens = new List<Token>(); var i = 0;
        bool LineStart() => tokens.Count == 0 || tokens[^1] is { Kind: TokenKind.Symbol, Value: "\n" or ":" };
        bool Continuation(int at)
        {
            if (at > 0 && !char.IsWhiteSpace(text[at - 1])) return false;
            var next = at + 1; while (next < text.Length && text[next] is ' ' or '\t') next++;
            return next >= text.Length || text[next] is '\r' or '\n';
        }
        while (true)
        {
            while (i < text.Length)
            {
                if (basic && text[i] == '\n') { if (!LineStart()) tokens.Add(new(TokenKind.Symbol, "\n", null, i)); i++; }
                else if (basic && text[i] == '_' && Continuation(i)) { i++; while (i < text.Length && text[i] != '\n') i++; i++; }
                else if (char.IsWhiteSpace(text[i])) i++;
                else if (basic && (text[i] == '\'' || LineStart() && string.Compare(text, i, "rem", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 && (i + 3 >= text.Length || !char.IsLetterOrDigit(text[i + 3]) && text[i + 3] != '_')))
                { while (i < text.Length && text[i] != '\n') i++; }
                else if (basic) break;
                else if (string.CompareOrdinal(text, i, "//", 0, 2) == 0) { while (i < text.Length && text[i] != '\n') i++; }
                else if (string.CompareOrdinal(text, i, "/*", 0, 2) == 0)
                {
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? throw Error(text, i, "unterminated comment") : close + 2;
                }
                else break;
            }
            if (i >= text.Length) { tokens.Add(new(TokenKind.End, "", null, i)); return tokens; }
            if (tokens.Count >= MaxTokens) throw new InvalidDataException("Formula token limit exceeded.");
            var start = i; var c = text[i];
            if (c == '"' || c == '\'' && !basic)
            {
                var builder = new StringBuilder(); i++;
                while (true)
                {
                    if (i >= text.Length) throw Error(text, start, "unterminated string");
                    if (text[i] == c) { if (i + 1 < text.Length && text[i + 1] == c) { builder.Append(c); i += 2; continue; } i++; break; }
                    builder.Append(text[i++]);
                }
                tokens.Add(new(TokenKind.Text, builder.ToString(), builder.ToString(), start));
            }
            else if (c == '{')
            {
                var close = text.IndexOf('}', i + 1);
                if (close < 0) throw Error(text, start, "unterminated field reference");
                if (close == i + 1) throw Error(text, start, "empty field reference");
                tokens.Add(new(TokenKind.Field, text[i..(close + 1)], null, start)); i = close + 1;
            }
            else if (c == '#')
            {
                var close = text.IndexOf('#', i + 1);
                if (close < 0) throw Error(text, start, "unterminated date literal");
                var value = text[(i + 1)..close];
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
                    && !DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date)) throw Error(text, start, $"invalid date literal #{value}#");
                tokens.Add(new(TokenKind.Date, value, date, start)); i = close + 1;
            }
            else if (c is >= '0' and <= '9' || c == '.' && i + 1 < text.Length && text[i + 1] is >= '0' and <= '9')
            {
                while (i < text.Length && text[i] is >= '0' and <= '9') i++;
                if (i < text.Length && text[i] == '.') { i++; while (i < text.Length && text[i] is >= '0' and <= '9') i++; }
                var digits = text[start..i];
                if (!decimal.TryParse(digits.EndsWith('.') ? digits + "0" : digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) throw Error(text, start, $"number {digits} is out of range");
                tokens.Add(new(TokenKind.Number, digits, number, start));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                tokens.Add(new(TokenKind.Word, text[start..i], null, start));
            }
            else
            {
                var pair = i + 1 < text.Length ? text.Substring(i, 2) : "";
                if (pair is "<=" or ">=" or "<>" || !basic && pair is ":=" or "!=") { tokens.Add(new(TokenKind.Symbol, pair, null, start)); i += 2; }
                else if ("+-*/\\%^&()[],;:=<>$".Contains(c)) { tokens.Add(new(TokenKind.Symbol, c.ToString(), null, start)); i++; }
                else throw Error(text, start, $"unexpected character '{c}'");
            }
        }
    }

    private abstract class ParserBase
    {
        protected readonly string _text;
        protected readonly List<Token> _tokens;
        protected readonly IReadOnlyDictionary<string, CrystalCustomFunction>? _functions;
        protected readonly bool _customFunction;
        protected readonly Dictionary<string, Binding> _declared = new(StringComparer.OrdinalIgnoreCase);
        protected int _index;
        private int _depth;
        public List<string> Unknown { get; } = [];
        public IReadOnlyCollection<Binding> Declarations => _declared.Values;
        public bool IsEmpty => _tokens.All(t => t.Kind == TokenKind.End || _basic && t is { Kind: TokenKind.Symbol, Value: "\n" or ":" });
        private readonly bool _basic;

        protected ParserBase(string text, IReadOnlyDictionary<string, CrystalCustomFunction>? functions, bool customFunction, bool basic)
        {
            if (text.Length > 32768) throw new InvalidDataException("Formula exceeds the 32 KB limit.");
            _text = text; _functions = functions; _customFunction = customFunction; _basic = basic; _tokens = Tokenize(text, basic);
        }
        protected abstract bool IsKeyword(string word);
        protected abstract Node ParsePostfix();

        protected void Finish(Node root)
        {
            if (Current.Kind != TokenKind.End) throw Fail("end of formula expected");
            foreach (var call in Walk(root).OfType<Call>())
                if (!Functions.Contains(call.Name)) throw new NotSupportedException($"Crystal function '{call.Name}' is not implemented.");
        }
        protected Token Current => _tokens[_index];
        protected bool IsSymbol(string value) => Current.Kind == TokenKind.Symbol && Current.Value == value;
        protected bool IsWord(string value) => Current.Kind == TokenKind.Word && Current.Value.Equals(value, StringComparison.OrdinalIgnoreCase);
        protected bool Accept(string value) { if (!IsSymbol(value)) return false; _index++; return true; }
        protected bool AcceptWord(string value) { if (!IsWord(value)) return false; _index++; return true; }
        protected void Expect(string value) { if (!Accept(value)) throw Fail($"'{value}' expected"); }
        protected void ExpectWord(string value) { if (!AcceptWord(value)) throw Fail($"'{value}' expected"); }
        protected InvalidDataException Fail(string message) =>
            Error(_text, Current.Position, Current.Kind == TokenKind.End ? message + " at end of formula"
                : $"{message}; found '{(Current.Kind == TokenKind.Text ? "\"" + Current.Value + "\"" : Current.Value == "\n" ? "end of line" : Current.Value)}'");
        protected T Nest<T>(Func<T> parse)
        {
            if (++_depth > MaxNesting || !RuntimeHelpers.TryEnsureSufficientExecutionStack()) throw new InvalidDataException("Formula nesting limit exceeded.");
            try { return parse(); } finally { _depth--; }
        }
        protected string ExpectName()
        {
            if (Current.Kind != TokenKind.Word || IsKeyword(Current.Value)) throw Fail("a variable name is expected");
            return _tokens[_index++].Value;
        }
        protected Binding Declare(string name, string scope, string type, bool isArray, bool isRange)
        {
            var binding = new Binding(name, scope, type, isArray, isRange);
            if (_declared.TryGetValue(name, out var existing) && existing.Type != "var")
            {
                if (existing.Scope != scope || existing.Type != type || existing.IsArray != isArray || existing.IsRange != isRange)
                    throw Fail($"variable '{name}' is redeclared with a different type or scope");
                return existing;
            }
            return _declared[name] = binding;
        }

        protected Node ParseExpression() => ParseBinary(1);
        protected Node ParseBinary(int minimum)
        {
            var left = ParseUnary(); List<Step>? steps = null; var previous = 0;
            while (Current.Kind is TokenKind.Symbol or TokenKind.Word && Precedence.TryGetValue(Current.Value, out var precedence) && precedence >= minimum)
            {
                if (precedence == previous && precedence is EqualityPrecedence or RelationalPrecedence or RangePrecedence) throw Fail("comparison and range operators cannot be chained");
                if (precedence < previous) { left = Fold(left, steps); steps = null; }
                previous = precedence;
                var op = _tokens[_index++].Value.ToLowerInvariant();
                var right = ParseBinary(precedence + 1);
                if (precedence != RangePrecedence) (steps ??= []).Add(new(op, right));
                else { left = new RangeNode(Fold(left, steps), right, op[0] != '_', !op.EndsWith('_')); steps = null; }
            }
            return Fold(left, steps);
        }
        protected static Node Fold(Node first, List<Step>? steps) => steps is null ? first : new Binary(first, steps.ToArray());
        protected static Node Index(Node target, List<Node>? indexes) =>
            indexes is null ? target : target is Subscript inner ? new Subscript(inner.Target, [.. inner.Indexes, .. indexes]) : new Subscript(target, indexes.ToArray());
        protected Node ParseUnary() => Nest(() =>
        {
            if (Accept("-")) return new Unary("-", ParseUnary());
            if (Accept("+") || Accept("$")) return new Unary("+", ParseUnary());
            if (AcceptWord("not")) return new Unary("not", ParseUnary());
            if (AcceptWord("upto")) return new RangeNode(null, ParseUnary(), true, true);
            if (AcceptWord("upto_")) return new RangeNode(null, ParseUnary(), true, false);
            if (AcceptWord("upfrom")) return new RangeNode(ParseUnary(), null, true, true);
            if (AcceptWord("upfrom_")) return new RangeNode(ParseUnary(), null, false, true);
            if (AcceptWord("is"))
            {
                if (Accept("<=")) return new RangeNode(null, ParseUnary(), true, true);
                if (Accept("<")) return new RangeNode(null, ParseUnary(), true, false);
                if (Accept(">=")) return new RangeNode(ParseUnary(), null, true, true);
                if (Accept(">")) return new RangeNode(ParseUnary(), null, false, true);
                throw Fail("'<', '<=', '>' or '>=' expected after 'is'");
            }
            var left = ParsePostfix(); List<Step>? steps = null;
            while (Accept("^")) (steps ??= []).Add(new("^", IsSymbol("-") || IsSymbol("+") ? ParseUnary() : ParsePostfix()));
            return Fold(left, steps);
        });
        protected Node ParseCall(string name, string close)
        {
            _index++;
            var args = new List<Node>();
            if (!Accept(close)) { do args.Add(ParseExpression()); while (Accept(",")); Expect(close); }
            if (args.Count == 0 && !Functions.Contains(name) && KnownSymbol(name)) return new Symbol(name);
            if (!Functions.Contains(name) && _functions is CrystalCustomFunctionLibrary library && library.Errors.TryGetValue(name, out var reason))
                throw new InvalidDataException($"Custom function '{name}' cannot be used: {reason}");
            return !Functions.Contains(name) && _functions?.ContainsKey(name) == true ? new CustomCall(name, args.ToArray(), _functions) : new Call(name, args.ToArray());
        }
        protected Node? ParseOptionLoop()
        {
            if (!AcceptWord("option")) return null;
            ExpectWord("loop");
            if (Current is not { Kind: TokenKind.Number, Literal: decimal limit } || limit < 1 || limit != decimal.Truncate(limit) || limit > int.MaxValue) throw Fail("a positive whole number is expected after 'Option Loop'");
            _index++;
            return new OptionLoop((int)limit);
        }
        protected Node Unresolved(string name)
        {
            if (!KnownSymbol(name) && !Unknown.Contains(name, StringComparer.OrdinalIgnoreCase)) Unknown.Add(name);
            return new Symbol(name);
        }
    }

    private sealed class Parser(string text, IReadOnlyDictionary<string, CrystalCustomFunction>? functions, bool customFunction) : ParserBase(text, functions, customFunction, false)
    {
        private int _forDepth, _whileDepth;
        protected override bool IsKeyword(string word) => Keywords.Contains(word);

        public Node ParseFormula()
        {
            if (ParseOptionLoop() is { } option)
            {
                if (!Accept(";") && Current.Kind != TokenKind.End) throw Fail("';' expected after 'Option Loop'");
                var rest = ParseProgram();
                Finish(rest);
                return new Sequence([option, rest]);
            }
            var root = ParseProgram();
            Finish(root);
            return root;
        }
        public (FunctionSignature Signature, Node Body) ParseFunction()
        {
            ExpectWord("function"); Expect("(");
            var parameters = new List<Parameter>(); var optional = false;
            if (!Accept(")"))
            {
                do
                {
                    if (AcceptWord("optional")) optional = true;
                    else if (optional) throw Fail("optional arguments must follow the required ones");
                    var type = TypeWords.FirstOrDefault(AcceptWord) ?? throw Fail("an argument type such as stringVar is expected");
                    var isRange = AcceptWord("range"); var isArray = AcceptWord("array");
                    var binding = Declare(ExpectName(), "local", type, isArray, isRange);
                    Node? fallback = null;
                    if (optional) { Expect(":="); fallback = ParseExpression(); }
                    parameters.Add(new(binding, fallback));
                }
                while (Accept(","));
                Expect(")");
            }
            var body = ParseProgram();
            Finish(body);
            return (new(parameters), body);
        }

        private Node ParseProgram()
        {
            var nodes = new List<Node>();
            do { if (!IsSymbol(";") && !IsSymbol(")") && Current.Kind != TokenKind.End) nodes.Add(ParseStatement()); }
            while (Accept(";"));
            return nodes.Count == 1 ? nodes[0] : new Sequence(nodes.ToArray());
        }

        private Node ParseStatement()
        {
            if (AcceptWord("exit"))
            {
                if (AcceptWord("while")) return _whileDepth > 0 ? new ExitLoop(false) : throw Fail("Exit While is only allowed inside a While loop");
                if (AcceptWord("for")) return _forDepth > 0 ? new ExitLoop(true) : throw Fail("Exit For is only allowed inside a For loop");
                throw Fail("'while' or 'for' expected after 'exit'");
            }
            return IsWord("while") || IsWord("do") || IsWord("for") ? Nest(ParseLoop) : ParseExpression();
        }

        private Node ParseLoop()
        {
            if (AcceptWord("while"))
            {
                var test = ParseExpression(); ExpectWord("do");
                _whileDepth++;
                try { return new Loop(test, ParseStatement(), true); } finally { _whileDepth--; }
            }
            if (AcceptWord("do"))
            {
                Node body;
                _whileDepth++;
                try { body = ParseStatement(); } finally { _whileDepth--; }
                ExpectWord("while");
                return new Loop(ParseExpression(), body, false);
            }
            if (AcceptWord("for"))
            {
                var name = ExpectName();
                if (!_declared.TryGetValue(name, out var binding) || binding.Type is not ("numbervar" or "currencyvar") || binding.IsArray || binding.IsRange)
                    throw Fail($"the For loop variable '{name}' must be a declared NumberVar");
                Expect(":="); var start = ParseBinary(AdditivePrecedence);
                ExpectWord("to"); var end = ParseBinary(AdditivePrecedence);
                var step = AcceptWord("step") ? ParseBinary(AdditivePrecedence) : null;
                ExpectWord("do");
                _forDepth++;
                try { return new ForLoop(binding, start, end, step, ParseStatement()); } finally { _forDepth--; }
            }
            throw Fail("a loop is expected");
        }

        protected override Node ParsePostfix()
        {
            var node = ParsePrimary(); List<Node>? indexes = null;
            while (Accept("[")) { (indexes ??= []).Add(ParseExpression()); Expect("]"); }
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
                    _index++; var body = ParseProgram(); Expect(")");
                    return body;
                }
                case TokenKind.Symbol when token.Value == "[":
                {
                    _index++; var items = new List<Node> { ParseExpression() };
                    while (Accept(",")) items.Add(ParseExpression());
                    Expect("]");
                    return new Values(items.ToArray());
                }
                case TokenKind.Word: break;
                case TokenKind.End: throw Fail("an expression is expected");
                default: throw Fail("unexpected symbol");
            }
            var word = token.Value.ToLowerInvariant();
            switch (word)
            {
                case "if": _index++; return ParseIf();
                case "select": _index++; return ParseSelect();
                case "redim": _index++; return ParseRedim();
                case "local" or "global" or "shared": return ParseDeclaration();
            }
            if (TypeWords.Contains(word)) return ParseDeclaration();
            if (Keywords.Contains(word)) throw Fail("an expression is expected");
            _index++;
            if (IsSymbol("(")) return ParseCall(token.Value, ")");
            if (_declared.TryGetValue(word, out var binding))
            {
                if (Accept(":=")) return new Assign(binding, ParseExpression());
                if (!Accept("[")) return new VarRef(binding);
                var index = ParseExpression(); Expect("]");
                return Accept(":=") ? new ElementAssign(binding, index, ParseExpression()) : new Subscript(new VarRef(binding), [index]);
            }
            if (Accept(":=")) return new Assign(Declare(token.Value, "local", "var", false, false), ParseExpression());
            return Unresolved(token.Value);
        }
        private Node ParseIf()
        {
            var branches = new List<(Node Test, Node Yes)>(); Node? otherwise = null;
            do
            {
                var test = ParseExpression(); ExpectWord("then");
                branches.Add((test, ParseStatement()));
                if (!AcceptWord("else")) break;
                if (!AcceptWord("if")) { otherwise = ParseStatement(); break; }
            }
            while (true);
            var result = otherwise ?? new DefaultValue(Infer(branches[^1].Yes));
            for (var i = branches.Count - 1; i >= 0; i--) result = new Conditional(branches[i].Test, branches[i].Yes, result);
            return result;
        }
        private Node ParseSelect()
        {
            var value = ParseExpression();
            var cases = new List<Case>();
            while (AcceptWord("case"))
            {
                var items = new List<Node> { ParseExpression() };
                while (Accept(",")) items.Add(ParseExpression());
                Expect(":");
                cases.Add(new(items.ToArray(), ParseStatement()));
            }
            Node? otherwise = null;
            if (AcceptWord("default")) { Expect(":"); otherwise = ParseStatement(); }
            if (cases.Count == 0 && otherwise is null) throw Fail("'case' expected");
            return new Select(value, cases.ToArray(), otherwise ?? new DefaultValue(cases.Select(c => Infer(c.Body)).FirstOrDefault(v => v is not null)));
        }
        private Node ParseRedim()
        {
            var preserve = AcceptWord("preserve");
            var name = ExpectName();
            if (!_declared.TryGetValue(name, out var binding) || !binding.IsArray) throw Fail($"Redim requires a declared array variable; '{name}' is not one");
            Expect("["); var size = ParseExpression(); Expect("]");
            return new Redim(binding, size, preserve);
        }
        private Node ParseDeclaration()
        {
            string? scope = AcceptWord("local") ? "local" : AcceptWord("global") ? "global" : AcceptWord("shared") ? "shared" : null;
            var type = TypeWords.FirstOrDefault(AcceptWord) ?? throw Fail("a variable type such as NumberVar is expected");
            var isRange = AcceptWord("range"); var isArray = AcceptWord("array");
            if (_customFunction)
            {
                if (scope is "global" or "shared") throw Fail("custom functions can only declare local variables");
                scope = "local";
            }
            var binding = Declare(ExpectName(), scope ?? "global", type.ToLowerInvariant(), isArray, isRange);
            return new Variable(binding, Accept(":=") ? ParseExpression() : null);
        }
    }
}
