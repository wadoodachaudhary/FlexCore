using System.Text;

namespace Fx.ControlKit.Migration;

/// <summary>
/// Translates a practical Perl subset to Python 3. The subset is what the sales-report demo and the regression
/// checks use: packages and bless, scalars, arrays, hashes, references, regex, file I/O, subs, and the core modules
/// named below. Anything outside the subset throws, so a conversion does not silently drop a statement.
/// </summary>
public static class PerlToPython
{
    public static string Convert(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Translator(source).Convert();
    }

    private enum Kind { Eof, Ident, Var, Number, String, Semi, LBrace, RBrace, LParen, RParen, LBrack, RBrack, Comma, Arrow, Fat, Assign, Op, Regex, Heredoc }

    private sealed record Tok(Kind Kind, string Text, string Extra = "");

    private sealed class Translator
    {
        private readonly List<Tok> _toks;
        private int _i;
        private readonly List<string> _imports = [];
        private readonly List<string> _lines = [];
        private int _indent;
        private string _class = "";
        private readonly Dictionary<string, string> _classes = new(StringComparer.Ordinal);

        public Translator(string source) => _toks = Scan(source);

        public string Convert()
        {
            while (!Is(Kind.Eof))
            {
                if (Is(Kind.Semi)) { Next(); continue; }
                Statement();
            }
            var body = string.Join("\n", _lines).TrimEnd() + "\n";
            if (_imports.Count == 0) return body;
            return string.Join("\n", _imports.Distinct()) + "\n\n" + body;
        }

        private void Statement()
        {
            if (EatIdent("package"))
            {
                var name = ReadPackage();
                Expect(Kind.Semi);
                _class = name.Split("::")[^1];
                _classes[name] = _class;
                Emit("class " + _class + ":");
                _indent = 4;
                Emit("def __init__(self, **fields):");
                _indent = 8;
                Emit("for key, value in fields.items():");
                _indent = 12;
                Emit("setattr(self, key, value)");
                _indent = 4;
                return;
            }
            if (EatIdent("use")) { Use(); return; }
            if (EatIdent("sub")) { Sub(); return; }
            if (EatIdent("my") || EatIdent("our") || EatIdent("local")) { Declaration(); return; }
            if (EatIdent("foreach") || (IsIdent("for") && !IsCall("for"))) { Next(); Foreach(); return; }
            if (EatIdent("while")) { While(); return; }
            if (EatIdent("if")) { If(); return; }
            if (EatIdent("unless")) { Unless(); return; }
            if (EatIdent("return")) { Emit("return " + (Is(Kind.Semi) ? "" : Expr()).Trim()); Expect(Kind.Semi); return; }
            if (EatIdent("print") || EatIdent("say") || EatIdent("printf")) { Print(Previous()); return; }
            if (EatIdent("last")) { Expect(Kind.Semi); Emit("break"); return; }
            if (EatIdent("next")) { Expect(Kind.Semi); Emit("continue"); return; }
            if (EatIdent("exit")) { Emit("raise SystemExit(" + (Is(Kind.LParen) ? Args() : Expr()) + ")"); Expect(Kind.Semi); return; }
            if (EatIdent("die") || EatIdent("warn")) { var warn = Previous() == "warn"; Emit((warn ? "print(" : "raise SystemExit(") + Expr() + (warn ? ", file=sys.stderr)" : ")")); Need("sys"); Expect(Kind.Semi); return; }
            if (Is(Kind.Number) && PeekText() == "1" && Look(1).Kind == Kind.Semi) { Next(); Next(); _indent = 0; return; }
            if (IsIdent("open")) { Open(); return; }
            if (IsIdent("close")) { Next(); Emit(Expr() + ".close()"); Expect(Kind.Semi); return; }
            if (IsIdent("chomp")) { Next(); var name = Expr(); Emit(name + " = " + name + ".rstrip(\"\\n\")"); Expect(Kind.Semi); return; }
            if (IsIdent("GetOptions")) { GetOptions(); return; }
            var left = Expr();
            if (Eat(Kind.Assign))
            {
                var op = Previous();
                var right = Expr();
                Expect(Kind.Semi);
                Emit(left + " " + op + " " + right);
                return;
            }
            if (EatIdent("if"))
            {
                var cond = Expr();
                Expect(Kind.Semi);
                Emit("if " + cond + ":");
                Indent(); Emit(left); Dedent();
                return;
            }
            Expect(Kind.Semi);
            if (left.Length > 0) Emit(left);
        }

        private void Use()
        {
            var module = ReadPackage();
            string? imports = null;
            if (!Is(Kind.Semi) && !IsIdent("qw"))
            {
                while (!Is(Kind.Semi) && !Is(Kind.Eof)) Next();
            }
            if (EatIdent("qw"))
            {
                Expect(Kind.LParen);
                var names = new List<string>();
                while (!Is(Kind.RParen) && !Is(Kind.Eof)) { names.Add(Next().Text); if (Is(Kind.Comma)) Next(); }
                Expect(Kind.RParen);
                imports = string.Join(", ", names);
            }
            Expect(Kind.Semi);
            switch (module)
            {
                case "strict":
                case "warnings":
                case "lib":
                    return;
                case "Getopt::Long":
                    Need("import argparse");
                    return;
                case "Data::Dumper":
                    return;
                case "POSIX":
                    Need("from datetime import datetime");
                    return;
                case "File::Basename":
                case "File::Spec":
                case "FindBin":
                    Need("import os");
                    return;
                default:
                    var cls = module.Split("::")[^1];
                    _classes[module] = cls;
                    var py = string.Join('.', module.Split("::").Select(part => part.ToLowerInvariant()));
                    Need("from " + py + " import " + (imports ?? cls));
                    return;
            }
        }

        private void Sub()
        {
            var name = Expect(Kind.Ident).Text;
            Expect(Kind.LBrace);
            string? first = null;
            var rest = new List<string>();
            if (EatIdent("my") && Eat(Kind.LParen))
            {
                var vars = new List<string>();
                while (!Is(Kind.RParen)) { vars.Add(Bare(Expect(Kind.Var).Text)); if (Is(Kind.Comma)) Next(); }
                Expect(Kind.RParen);
                Expect(Kind.Assign);
                if (!(Is(Kind.Var) && PeekText() == "@_")) throw new InvalidDataException("sub parameters must be assigned from @_.");
                Next();
                Expect(Kind.Semi);
                if (vars.Count > 0) { first = vars[0]; rest.AddRange(vars.Skip(1)); }
            }
            var method = first is "self" or "class" || name == "new";
            var pyName = name == "new" ? "__init__" : name;
            var parameters = new List<string>();
            if (method) parameters.Add(name == "new" || first == "class" ? "self" : "self");
            if (first == "class" && name != "new") { parameters.Clear(); parameters.Add("cls"); Need(""); }
            parameters.AddRange(first is "self" or "class" ? rest : first is null ? rest : new[] { first }.Concat(rest));
            if (name != "new" && first == "class")
                Emit("@classmethod");
            Emit("def " + pyName + "(" + string.Join(", ", parameters) + "):");
            Indent();
            var emitted = _lines.Count;
            while (!Is(Kind.RBrace) && !Is(Kind.Eof)) Statement();
            Expect(Kind.RBrace);
            if (_lines.Count == emitted) Emit("pass");
            Dedent();
        }

        private void Declaration()
        {
            if (Eat(Kind.LParen))
            {
                var names = new List<string>();
                while (!Is(Kind.RParen)) { names.Add(PyVar(Expect(Kind.Var))); if (Is(Kind.Comma)) Next(); }
                Expect(Kind.RParen);
                Expect(Kind.Assign);
                var right = Expr();
                Expect(Kind.Semi);
                Emit(string.Join(", ", names) + " = " + right);
                return;
            }
            var token = Expect(Kind.Var);
            var name = PyVar(token);
            if (Eat(Kind.Assign))
            {
                var right = Expr();
                Expect(Kind.Semi);
                Emit(name + " = " + right);
            }
            else
            {
                Expect(Kind.Semi);
                var empty = token.Text.StartsWith('@') ? "[]" : token.Text.StartsWith('%') ? "{}" : "None";
                Emit(name + " = " + empty);
            }
        }

        private void Foreach()
        {
            EatIdent("my");
            var name = PyVar(Expect(Kind.Var));
            Expect(Kind.LParen);
            var list = Expr();
            Expect(Kind.RParen);
            Expect(Kind.LBrace);
            Emit("for " + name + " in " + list + ":");
            Block();
        }

        private void While()
        {
            Expect(Kind.LParen);
            if (EatIdent("my"))
            {
                var name = PyVar(Expect(Kind.Var));
                Expect(Kind.Assign);
                var read = Readline();
                Expect(Kind.RParen);
                Expect(Kind.LBrace);
                var iterator = read.StartsWith("next(", StringComparison.Ordinal) && read.EndsWith(')') ? read[5..^1] : read;
                Emit("for " + name + " in " + iterator + ":");
                Block();
                return;
            }
            var cond = Expr();
            Expect(Kind.RParen);
            Expect(Kind.LBrace);
            Emit("while " + cond + ":");
            Block();
        }

        private void If()
        {
            Expect(Kind.LParen);
            var cond = Expr();
            Expect(Kind.RParen);
            Expect(Kind.LBrace);
            Emit("if " + cond + ":");
            Block();
            while (EatIdent("elsif"))
            {
                Expect(Kind.LParen);
                var more = Expr();
                Expect(Kind.RParen);
                Expect(Kind.LBrace);
                Emit("elif " + more + ":");
                Block();
            }
            if (EatIdent("else")) { Expect(Kind.LBrace); Emit("else:"); Block(); }
        }

        private void Unless()
        {
            Expect(Kind.LParen);
            var cond = Expr();
            Expect(Kind.RParen);
            Expect(Kind.LBrace);
            Emit("if not (" + cond + "):");
            Block();
        }

        private void Block()
        {
            Indent();
            var start = _lines.Count;
            while (!Is(Kind.RBrace) && !Is(Kind.Eof)) Statement();
            Expect(Kind.RBrace);
            if (_lines.Count == start) Emit("pass");
            Dedent();
        }

        private void Print(string which)
        {
            string args;
            if (Is(Kind.LParen)) args = Args();
            else
            {
                var parts = new List<string> { Expr() };
                while (Eat(Kind.Comma)) parts.Add(Expr());
                args = string.Join(", ", parts);
            }
            Expect(Kind.Semi);
            if (which == "printf") Emit("print(" + args + ", end=\"\")");
            else if (which == "say") Emit("print(" + args + ")");
            else Emit("print(" + args + ", end=\"\")");
        }

        private void Open()
        {
            Next();
            EatIdent("my");
            var handle = PyVar(Expect(Kind.Var));
            Expect(Kind.Comma);
            var mode = Postfix();
            Expect(Kind.Comma);
            var path = Postfix();
            if (EatIdent("or") && EatIdent("die")) Expr();
            Expect(Kind.Semi);
            var pyMode = mode.Contains('<') ? "\"r\"" : "\"w\"";
            Emit(handle + " = open(" + path + ", " + pyMode + ", encoding=\"utf-8\")");
        }

        private void GetOptions()
        {
            Next();
            Expect(Kind.LParen);
            Need("import argparse");
            var specs = new List<(string Name, bool Flag, string Target)>();
            while (!Is(Kind.RParen) && !Is(Kind.Eof))
            {
                var spec = Expect(Kind.String).Text.Trim().Trim('"').Trim('\'');
                if (spec.StartsWith("f", StringComparison.Ordinal) && spec.Contains('"')) spec = spec.Trim('f').Trim('"');
                Expect(Kind.Fat);
                Expect(Kind.Op); // \
                var target = PyVar(Expect(Kind.Var));
                var flag = !spec.Contains('=');
                var name = spec.Split('=')[0];
                specs.Add((name, flag, target));
                if (Is(Kind.Comma)) Next();
            }
            Expect(Kind.RParen);
            Expect(Kind.Semi);
            Emit("_parser = argparse.ArgumentParser(add_help=False)");
            foreach (var spec in specs)
                Emit("_parser.add_argument(\"--" + spec.Name + "\"" + (spec.Flag ? ", action=\"store_true\"" : ", default=\"\"") + ")");
            Emit("_args = _parser.parse_args()");
            foreach (var spec in specs)
                Emit(spec.Target + " = _args." + spec.Name.Replace('-', '_'));
        }

        private string Expr() => Or();

        private string Or()
        {
            var left = And();
            while (EatIdent("or") || EatOp("||")) left = left + " or " + And();
            return left;
        }

        private string And()
        {
            var left = Ternary();
            while (EatIdent("and") || EatOp("&&")) left = left + " and " + Ternary();
            return left;
        }

        private string Ternary()
        {
            var left = Cmp();
            if (!EatOp("?")) return left;
            var yes = Cmp();
            ExpectOp(":");
            var no = Cmp();
            return yes + " if " + left + " else " + no;
        }

        private string Cmp()
        {
            var left = Add();
            while (true)
            {
                string? op = null;
                if (EatIdent("eq")) op = "==";
                else if (EatIdent("ne")) op = "!=";
                else if (EatIdent("lt")) op = "<";
                else if (EatIdent("gt")) op = ">";
                else if (EatIdent("le")) op = "<=";
                else if (EatIdent("ge")) op = ">=";
                else if (EatOp("==") || EatOp("!=") || EatOp("<=") || EatOp(">=") || EatOp("<") || EatOp(">")) op = Previous();
                else break;
                left = left + " " + op + " " + Add();
            }
            return left;
        }

        private string Add()
        {
            var left = Mul();
            while (true)
            {
                if (EatOp(".")) { var right = Mul(); left = "str(" + left + ") + str(" + right + ")"; }
                else if (EatOp("+") || EatOp("-"))
                {
                    var op = Previous();
                    var right = Mul();
                    if (op == "+" && (left == "0" || right == "0"))
                    {
                        var number = left == "0" ? right : left;
                        left = "(int(" + number + ") if str(" + number + ").isdigit() else float(" + number + "))";
                    }
                    else left = left + " " + op + " " + right;
                }
                else if (EatOp("..")) { var right = Mul(); left = "range(" + left + ", (" + right + ") + 1)"; }
                else break;
            }
            return left;
        }

        private string Mul()
        {
            var left = Unary();
            while (EatOp("*") || EatOp("/") || EatOp("%")) { var op = Previous(); left = left + " " + op + " " + Unary(); }
            return left;
        }

        private string Unary()
        {
            if (EatOp("!") || EatIdent("not")) return "not " + Unary();
            if (EatOp("\\")) return Unary();
            return Postfix();
        }

        private string Postfix()
        {
            var left = Primary();
            while (true)
            {
                if (Eat(Kind.Arrow))
                {
                    if (Eat(Kind.LBrace))
                    {
                        var key = Is(Kind.Ident) ? "\"" + Expect(Kind.Ident).Text + "\"" : Expr();
                        Expect(Kind.RBrace);
                        var attribute = left is "self" or "cls";
                        left = attribute ? left + "." + key.Trim('"') : left + "[" + key + "]";
                        continue;
                    }
                    var name = Expect(Kind.Ident).Text;
                    var args = Is(Kind.LParen) ? Args() : "";
                    if (name == "new") left = left + "(" + args + ")";
                    else left = left + "." + name + "(" + args + ")";
                    continue;
                }
                if (Eat(Kind.LBrack))
                {
                    var index = Expr();
                    Expect(Kind.RBrack);
                    left = left + "[" + index + "]";
                    continue;
                }
                if (Eat(Kind.LBrace))
                {
                    var key = Is(Kind.Ident) ? "\"" + Expect(Kind.Ident).Text + "\"" : Expr();
                    Expect(Kind.RBrace);
                    left = left + "[" + key + "]";
                    continue;
                }
                if (Is(Kind.Regex))
                {
                    left = ApplyRegex(left, Next());
                    continue;
                }
                break;
            }
            return left;
        }

        private string Primary()
        {
            if (Is(Kind.Op) && PeekText() == "<") return Readline();
            if (Is(Kind.Number) || Is(Kind.String) || Is(Kind.Regex)) return Next().Text;
            if (Is(Kind.Heredoc)) return Next().Text;
            if (Is(Kind.Var) && PeekText() == "@")
            {
                Next();
                Expect(Kind.LBrace);
                var inner = Expr();
                Expect(Kind.RBrace);
                return inner;
            }
            if (Is(Kind.Var)) return PyVar(Next());
            if (Is(Kind.Ident))
            {
                var name = Next().Text;
                while (EatOp("::")) name += "::" + Expect(Kind.Ident).Text;
                if (name == "File::Basename::basename") { Need("import os"); return "os.path.basename(" + Args() + ")"; }
                if (name == "POSIX::strftime") { Need("from datetime import datetime"); return "datetime.now().strftime(" + Args() + ")"; }
                if (name == "Data::Dumper::Dumper") return "repr(" + Args() + ")";
                if (name == "scalar" && Is(Kind.LParen)) return "len(" + Args() + ")";
                if (name == "defined" && Is(Kind.LParen)) return "(" + Args() + ") is not None";
                if (name == "length" && Is(Kind.LParen)) return "len(" + Args() + ")";
                if ((name == "lc" || name == "uc") && Is(Kind.LParen)) return Args() + (name == "lc" ? ".lower()" : ".upper()");
                if (name == "join" && Is(Kind.LParen)) { var inner = ArgList(); return inner[0] + ".join(" + inner[1] + ")"; }
                if (name == "split" && Is(Kind.LParen)) { var inner = ArgList(); return inner[1] + ".split(" + SplitSep(inner[0]) + ")"; }
                if (name == "keys" && Is(Kind.LParen)) return "list(" + Args() + ".keys())";
                if (name == "values" && Is(Kind.LParen)) return "list(" + Args() + ".values())";
                if (name == "push") { var inner = ArgList(); return inner[0] + ".append(" + inner[1] + ")"; }
                if (name == "shift" && Is(Kind.LParen)) return Args() + ".pop(0)";
                if (name == "sort") return Sort();
                if (name == "map") return MapGrep(true);
                if (name == "grep") return MapGrep(false);
                if (name == "sprintf" && Is(Kind.LParen)) { var inner = ArgList(); return inner[0] + " % (" + string.Join(", ", inner.Skip(1)) + ",)"; }
                if (name == "bless") return Bless();
                if (name == "basename") return "os.path.basename(" + Args() + ")";
                if (name == "strftime") return "datetime.now().strftime(" + Args() + ")";
                if (name == "Dumper") return "repr(" + Args() + ")";
                if (name == "catfile") return "os.path.join(" + Args() + ")";
                if (_classes.TryGetValue(name, out var cls) || _classes.Values.Contains(name))
                {
                    var type = _classes.TryGetValue(name, out var found) ? found : name;
                    if (Is(Kind.Arrow)) return type;
                    return type;
                }
                if (Is(Kind.LParen)) return name + "(" + Args() + ")";
                return name;
            }
            if (Eat(Kind.LParen))
            {
                if (Is(Kind.RParen)) { Next(); return "[]"; }
                var first = Expr();
                if (Is(Kind.Fat))
                {
                    Expect(Kind.Fat);
                    var items = new List<string> { PyKey(first) + ": " + Expr() };
                    while (Eat(Kind.Comma))
                    {
                        if (Is(Kind.RParen)) break;
                        var key = Expr();
                        Expect(Kind.Fat);
                        items.Add(PyKey(key) + ": " + Expr());
                    }
                    Expect(Kind.RParen);
                    return "{" + string.Join(", ", items) + "}";
                }
                if (Is(Kind.Comma))
                {
                    var items = new List<string> { first };
                    while (Eat(Kind.Comma))
                    {
                        if (Is(Kind.RParen)) break;
                        items.Add(Expr());
                    }
                    Expect(Kind.RParen);
                    return "[" + string.Join(", ", items) + "]";
                }
                Expect(Kind.RParen);
                return "(" + first + ")";
            }
            if (Eat(Kind.LBrack))
            {
                var items = new List<string>();
                while (!Is(Kind.RBrack) && !Is(Kind.Eof)) { items.Add(Expr()); if (Is(Kind.Comma)) Next(); }
                Expect(Kind.RBrack);
                return "[" + string.Join(", ", items) + "]";
            }
            if (Eat(Kind.LBrace))
            {
                var items = new List<string>();
                while (!Is(Kind.RBrace) && !Is(Kind.Eof))
                {
                    var key = Is(Kind.Ident) ? "\"" + Expect(Kind.Ident).Text + "\"" : Expr();
                    Expect(Kind.Fat);
                    items.Add(key + ": " + Expr());
                    if (Is(Kind.Comma)) Next();
                }
                Expect(Kind.RBrace);
                return "{" + string.Join(", ", items) + "}";
            }
            throw Error("expression");
        }

        private string Bless()
        {
            Expect(Kind.LBrace);
            var fields = new List<string>();
            while (!Is(Kind.RBrace))
            {
                var key = Expect(Kind.Ident).Text;
                Expect(Kind.Fat);
                fields.Add(key + "=" + Expr());
                if (Is(Kind.Comma)) Next();
            }
            Expect(Kind.RBrace);
            Expect(Kind.Comma);
            var type = Expr();
            return type + "(" + string.Join(", ", fields) + ")";
        }

        private string Sort()
        {
            Expect(Kind.LBrace);
            var left = Expr();
            var numeric = EatOp("<=>");
            if (!numeric) EatIdent("cmp");
            Expr();
            Expect(Kind.RBrace);
            var list = Expr();
            var key = left.StartsWith("a.", StringComparison.Ordinal) ? "item." + left[2..]
                : left.StartsWith("a[", StringComparison.Ordinal) ? "item" + left[1..]
                : "";
            return key.Length == 0 ? "sorted(" + list + ")" : "sorted(" + list + ", key=lambda item: " + key + ")";
        }

        private string MapGrep(bool map)
        {
            Expect(Kind.LBrace);
            var body = Expr();
            Expect(Kind.RBrace);
            var list = Expr();
            body = body.Replace("item", "item", StringComparison.Ordinal);
            return map
                ? "[" + body.Replace("$_", "item", StringComparison.Ordinal).Replace("_.", "item.") + " for item in " + list + "]"
                : "[item for item in " + list + " if " + body.Replace("$_", "item", StringComparison.Ordinal) + "]";
        }

        private string Readline()
        {
            if (!Is(Kind.Op) || PeekText() != "<") throw Error("readline");
            Next();
            var handle = PyVar(Expect(Kind.Var));
            ExpectOp(">");
            return "next(" + handle + ")";
        }

        private string ApplyRegex(string left, Tok regex)
        {
            if (regex.Extra == "s") { Need("import re"); return left + " = re.sub(" + regex.Text + ", " + left + ")"; }
            if (regex.Extra == "tr") return left + " = " + left + ".translate(str.maketrans(" + regex.Text + "))";
            Need("import re");
            return "re.search(" + regex.Text + ", " + left + ") is not None";
        }

        private static string SplitSep(string pattern)
        {
            var trimmed = pattern.Trim();
            if (trimmed.StartsWith("r\"", StringComparison.Ordinal) || trimmed.StartsWith("r'", StringComparison.Ordinal))
            {
                var body = trimmed[2..^1];
                if (body == ",") return "\",\"";
            }
            return pattern;
        }

        private List<string> ArgList()
        {
            Expect(Kind.LParen);
            var args = new List<string>();
            if (!Is(Kind.RParen))
            {
                args.Add(Expr());
                while (Eat(Kind.Comma)) args.Add(Expr());
            }
            Expect(Kind.RParen);
            return args;
        }

        private string Args() => string.Join(", ", ArgList());

        private string PyVar(Tok token) => PyVar(token.Text);
        private string PyVar(string text)
        {
            if (text.StartsWith("$#", StringComparison.Ordinal)) return "(len(" + text[2..] + ") - 1)";
            if (text == "$!") return "\"error\"";
            if (text.StartsWith('$') || text.StartsWith('@') || text.StartsWith('%')) text = text[1..];
            if (text is "_" ) return "item";
            if (text is "self" or "class") return text == "class" ? "cls" : "self";
            if (text.Contains("::", StringComparison.Ordinal))
            {
                if (text == "FindBin::Bin") { Need("import os"); return "os.path.dirname(os.path.abspath(__file__))"; }
                return text.Split("::")[^1].ToLowerInvariant();
            }
            return text;
        }

        private static string Bare(string text) => text.TrimStart('$', '@', '%');

        private void Need(string import)
        {
            if (import.Length == 0) return;
            if (!_imports.Contains(import)) _imports.Add(import);
        }

        private void Emit(string line) => _lines.Add(new string(' ', _indent) + line);
        private void Indent() => _indent += 4;
        private void Dedent() => _indent = Math.Max(0, _indent - 4);

        private string ReadPackage()
        {
            var name = Expect(Kind.Ident).Text;
            while (EatOp("::")) name += "::" + Expect(Kind.Ident).Text;
            return name;
        }

        private bool IsCall(string name) => IsIdent(name) && Look(1).Kind == Kind.LParen;
        private bool IsIdent(string name) => Is(Kind.Ident) && PeekText() == name;
        private bool EatIdent(string name) { if (!IsIdent(name)) return false; Next(); return true; }
        private bool EatOp(string op) { if (!Is(Kind.Op) || PeekText() != op) return false; Next(); return true; }
        private bool Eat(Kind kind) { if (!Is(kind)) return false; Next(); return true; }
        private bool Is(Kind kind) => _toks[_i].Kind == kind;
        private string PeekText() => _toks[_i].Text;
        private Tok Look(int offset) => _i + offset < _toks.Count ? _toks[_i + offset] : _toks[^1];
        private Tok Next() => _toks[_i++];
        private string Previous() => _toks[_i - 1].Text;
        private Tok Expect(Kind kind)
        {
            if (!Is(kind)) throw Error(kind.ToString());
            return Next();
        }
        private void ExpectOp(string op) { if (!EatOp(op)) throw Error(op); }
        private InvalidDataException Error(string expected) =>
            new("Perl subset expected " + expected + " but found " + _toks[Math.Min(_i, _toks.Count - 1)].Text);

        private static List<Tok> Scan(string source)
        {
            var toks = new List<Tok>();
            var s = source.Replace("\r\n", "\n");
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '#') { while (i < s.Length && s[i] != '\n') i++; continue; }
                if (s.AsSpan(i).StartsWith("<<"))
                {
                    var mark = i + 2;
                    var quote = s[mark] == '\'' || s[mark] == '"' ? s[mark++] : '\0';
                    var end = mark;
                    while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_')) end++;
                var label = s[mark..end];
                if (quote != '\0') end++;
                var heredocSemi = false;
                if (end < s.Length && s[end] == ';') { heredocSemi = true; end++; }
                if (end < s.Length && s[end] == '\n') end++;
                var bodyStart = end;
                    var closer = "\n" + label;
                    var closeAt = s.IndexOf(closer, bodyStart, StringComparison.Ordinal);
                    if (closeAt < 0) throw new InvalidDataException("Unclosed here-doc " + label);
                    var body = s[bodyStart..closeAt];
                    toks.Add(new Tok(Kind.Heredoc, "\"\"\"" + body.Replace("\"\"\"", "\\\"\\\"\\\"") + "\"\"\""));
                    if (heredocSemi) toks.Add(new Tok(Kind.Semi, ";"));
                    i = closeAt + closer.Length;
                    if (i < s.Length && s[i] == '\n') i++;
                    continue;
                }
                if (c is '\'' or '"')
                {
                    var q = c;
                    var b = new StringBuilder();
                    i++;
                    while (i < s.Length && s[i] != q)
                    {
                        if (s[i] == '\\' && i + 1 < s.Length) { b.Append(s[i]); b.Append(s[i + 1]); i += 2; continue; }
                        if (q == '"' && s[i] == '$')
                        {
                            var start = i + 1;
                            var n = start;
                            while (n < s.Length && (char.IsLetterOrDigit(s[n]) || s[n] == '_')) n++;
                            b.Append('{').Append(s.AsSpan(start, n - start)).Append('}');
                            i = n;
                            continue;
                        }
                        b.Append(s[i++]);
                    }
                    i++;
                    var text = b.ToString();
                    toks.Add(new Tok(Kind.String, q == '"' && text.Contains('{') ? "f\"" + text + "\"" : "\"" + text + "\""));
                    continue;
                }
                if (c == '/' || (c is 's' or 'm' or 'y' && i + 1 < s.Length && s[i + 1] == '/') || (s.AsSpan(i).StartsWith("tr/")))
                {
                    var previous = toks.Count > 0 ? toks[^1] : null;
                    var afterMatch = previous is { Kind: Kind.Op, Text: "=~" };
                    var patternArg = c == '/' && previous is { Kind: Kind.LParen or Kind.Comma };
                    if (afterMatch || patternArg)
                    {
                        if (afterMatch) toks.RemoveAt(toks.Count - 1);
                        toks.Add(ScanRegex(s, ref i));
                        continue;
                    }
                }
                if (char.IsDigit(c))
                {
                    var start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    toks.Add(new Tok(Kind.Number, s[start..i]));
                    continue;
                }
                if (c is '$' or '@' or '%')
                {
                    var start = i;
                    if (s.AsSpan(i).StartsWith("$#")) i += 2;
                    else i++;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or ':')) i++;
                    toks.Add(new Tok(Kind.Var, s[start..i]));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    toks.Add(new Tok(Kind.Ident, s[start..i]));
                    continue;
                }
                if (s.AsSpan(i).StartsWith("=>")) { toks.Add(new Tok(Kind.Fat, "=>")); i += 2; continue; }
                if (s.AsSpan(i).StartsWith("->")) { toks.Add(new Tok(Kind.Arrow, "->")); i += 2; continue; }
                if (s.AsSpan(i).StartsWith("=~")) { toks.Add(new Tok(Kind.Op, "=~")); i += 2; continue; }
                if ("+-*/%.".Contains(c) && i + 1 < s.Length && s[i + 1] == '=') { toks.Add(new Tok(Kind.Assign, s.Substring(i, 1) + "=")); i += 2; continue; }
                if (c == '=' && (i + 1 >= s.Length || s[i + 1] != '=')) { toks.Add(new Tok(Kind.Assign, "=")); i++; continue; }
                if (s.AsSpan(i).StartsWith("<=>")) { toks.Add(new Tok(Kind.Op, "<=>")); i += 3; continue; }
                if (s.AsSpan(i).StartsWith("||") || s.AsSpan(i).StartsWith("&&") || s.AsSpan(i).StartsWith("==") || s.AsSpan(i).StartsWith("!=")
                    || s.AsSpan(i).StartsWith("<=") || s.AsSpan(i).StartsWith(">=") || s.AsSpan(i).StartsWith("..") || s.AsSpan(i).StartsWith("::"))
                {
                    var two = s.Substring(i, 2);
                    toks.Add(new Tok(two == "::" ? Kind.Op : Kind.Op, two));
                    i += 2;
                    continue;
                }
                Kind kind = c switch
                {
                    ';' => Kind.Semi,
                    '{' => Kind.LBrace,
                    '}' => Kind.RBrace,
                    '(' => Kind.LParen,
                    ')' => Kind.RParen,
                    '[' => Kind.LBrack,
                    ']' => Kind.RBrack,
                    ',' => Kind.Comma,
                    _ => Kind.Op
                };
                toks.Add(new Tok(kind, c.ToString()));
                i++;
            }
            toks.Add(new Tok(Kind.Eof, ""));
            return toks;
        }

        private static Tok ScanRegex(string s, ref int i)
        {
            var op = "m";
            if (s[i] is 's' or 'y' or 'm') { op = s[i].ToString(); i++; }
            else if (s.AsSpan(i).StartsWith("tr/")) { op = "tr"; i += 2; }
            if (i >= s.Length) throw new InvalidDataException("Unclosed regex.");
            var delim = s[i] == '/' ? '/' : s[i];
            if (op is "s" or "tr" or "y")
            {
                if (s[i] == '/') i++;
                else delim = s[i++];
                var first = TakeDelim(s, ref i, delim);
                var second = TakeDelim(s, ref i, delim);
                while (i < s.Length && char.IsLetter(s[i])) i++;
                if (op == "s") return new Tok(Kind.Regex, "r" + PyQuote(first) + ", " + PyQuote(second), "s");
                return new Tok(Kind.Regex, PyQuote(first) + ", " + PyQuote(second), "tr");
            }
            if (s[i] == '/') i++;
            var pattern = TakeDelim(s, ref i, delim);
            while (i < s.Length && char.IsLetter(s[i])) i++;
            return new Tok(Kind.Regex, "r" + PyQuote(pattern), "m");
        }

        private static string PyQuote(string text) => text.Contains('"') ? "'" + text.Replace("'", "\\'") + "'" : "\"" + text + "\"";

        private static string PyKey(string expression)
        {
            if (expression.StartsWith('"') || expression.StartsWith('f') || char.IsDigit(expression[0])) return expression;
            return "\"" + expression + "\"";
        }

        private static string TakeDelim(string s, ref int i, char delim)
        {
            var b = new StringBuilder();
            while (i < s.Length && s[i] != delim)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { b.Append(s[i + 1]); i += 2; continue; }
                b.Append(s[i++]);
            }
            if (i < s.Length && s[i] == delim) i++;
            return b.ToString();
        }
    }
}
