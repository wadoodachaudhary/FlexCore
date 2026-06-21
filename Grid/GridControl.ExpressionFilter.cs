using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    private string? _expressionFilterDraft;
    private string? _expressionFilterText;
    private string? _expressionFilterError;
    private bool _expressionFilterOpen;
    private ExpressionFilterNode? _expressionFilterRoot;

    private bool HasExpressionFilter => _expressionFilterRoot != null;
    private string ExpressionFilterDraft => _expressionFilterDraft ?? string.Empty;
    private string ExpressionFilterToggleTitle =>
        _expressionFilterOpen ? "Close filter expression" : "Filter expression";
    private string ExpressionFilterToggleExpanded =>
        _expressionFilterOpen ? "true" : "false";

    private void ToggleExpressionFilter()
    {
        _expressionFilterOpen = !_expressionFilterOpen;
        if (_expressionFilterOpen)
        {
            _expressionFilterDraft = _expressionFilterText ?? _expressionFilterDraft ?? string.Empty;
            _expressionFilterError = null;
        }
        else
        {
            _expressionFilterError = null;
        }
    }

    private void OnExpressionFilterInput(ChangeEventArgs e)
    {
        _expressionFilterDraft = e.Value?.ToString() ?? string.Empty;
    }

    private async Task HandleExpressionFilterKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await ApplyExpressionFilterAsync();
        }
        else if (e.Key == "Escape")
        {
            _expressionFilterOpen = false;
            _expressionFilterError = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ApplyExpressionFilterAsync()
    {
        var expression = (_expressionFilterDraft ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(expression))
        {
            await ClearExpressionFilterAsync();
            return;
        }

        try
        {
            var parser = new ExpressionFilterParser(expression, BuildExpressionFilterColumnAliases());
            _expressionFilterRoot = parser.Parse();
            _expressionFilterText = expression;
            _expressionFilterDraft = expression;
            _expressionFilterError = null;
            _pageState.CurrentPage = 1;
        }
        catch (Exception ex)
        {
            _expressionFilterError = ex.Message;
            _expressionFilterOpen = true;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task ClearExpressionFilterAsync()
    {
        ClearExpressionFilterState();
        _pageState.CurrentPage = 1;
        await InvokeAsync(StateHasChanged);
    }

    private void ClearExpressionFilterState()
    {
        _expressionFilterRoot = null;
        _expressionFilterText = null;
        _expressionFilterDraft = null;
        _expressionFilterError = null;
    }

    private bool PassesExpressionFilter(TValue item)
    {
        if (_expressionFilterRoot == null)
            return true;

        try
        {
            return _expressionFilterRoot.Evaluate(this, item);
        }
        catch
        {
            return false;
        }
    }

    private Dictionary<string, GridColumn> BuildExpressionFilterColumnAliases()
    {
        var aliases = new Dictionary<string, GridColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in Columns.Where(c => !string.IsNullOrWhiteSpace(c.Field)))
        {
            AddAlias(col.Field, col);
            AddAlias(col.HeaderText, col);
            AddAlias(col.DisplayHeader, col);
        }

        return aliases;

        void AddAlias(string? alias, GridColumn col)
        {
            if (string.IsNullOrWhiteSpace(alias))
                return;

            var trimmed = alias.Trim();
            aliases.TryAdd(trimmed, col);

            var normalized = NormalizeExpressionFilterName(trimmed);
            if (!string.IsNullOrEmpty(normalized))
                aliases.TryAdd(normalized, col);
        }
    }

    private static string NormalizeExpressionFilterName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private abstract class ExpressionFilterNode
    {
        public abstract bool Evaluate(GridControl<TValue> grid, TValue item);
    }

    private sealed class ExpressionFilterBinaryNode : ExpressionFilterNode
    {
        private readonly ExpressionFilterNode _left;
        private readonly ExpressionFilterNode _right;
        private readonly string _logicalOperator;

        public ExpressionFilterBinaryNode(ExpressionFilterNode left, string logicalOperator, ExpressionFilterNode right)
        {
            _left = left;
            _logicalOperator = logicalOperator;
            _right = right;
        }

        public override bool Evaluate(GridControl<TValue> grid, TValue item)
        {
            return _logicalOperator == "and"
                ? _left.Evaluate(grid, item) && _right.Evaluate(grid, item)
                : _left.Evaluate(grid, item) || _right.Evaluate(grid, item);
        }
    }

    private sealed class ExpressionFilterConditionNode : ExpressionFilterNode
    {
        private readonly GridColumn _column;
        private readonly string _operator;
        private readonly string _expected;

        public ExpressionFilterConditionNode(GridColumn column, string comparisonOperator, string expected)
        {
            _column = column;
            _operator = comparisonOperator;
            _expected = expected;
        }

        public override bool Evaluate(GridControl<TValue> grid, TValue item)
        {
            var actual = grid.ResolveCellValue(item, _column);
            return EvaluateExpressionFilterCondition(actual, _operator, _expected);
        }
    }

    private sealed class ExpressionFilterParser
    {
        private readonly List<ExpressionFilterToken> _tokens;
        private readonly Dictionary<string, GridColumn> _columns;
        private int _position;
        private GridColumn? _lastColumn;

        public ExpressionFilterParser(string expression, Dictionary<string, GridColumn> columns)
        {
            _tokens = Tokenize(expression);
            _columns = columns;
        }

        public ExpressionFilterNode Parse()
        {
            if (Current.Kind == ExpressionFilterTokenKind.End)
                throw new InvalidOperationException("Enter a filter expression.");

            var node = ParseOr();
            if (Current.Kind != ExpressionFilterTokenKind.End)
                throw new InvalidOperationException($"Unexpected token '{Current.Text}'.");
            return node;
        }

        private ExpressionFilterNode ParseOr()
        {
            var node = ParseAnd();
            while (MatchWord("or"))
            {
                node = new ExpressionFilterBinaryNode(node, "or", ParseAnd());
            }
            return node;
        }

        private ExpressionFilterNode ParseAnd()
        {
            var node = ParsePrimary();
            while (MatchWord("and"))
            {
                node = new ExpressionFilterBinaryNode(node, "and", ParsePrimary());
            }
            return node;
        }

        private ExpressionFilterNode ParsePrimary()
        {
            if (Match(ExpressionFilterTokenKind.LeftParen))
            {
                var node = ParseOr();
                Expect(ExpressionFilterTokenKind.RightParen, "Missing closing ')'.");
                return node;
            }

            return ParseCondition();
        }

        private ExpressionFilterNode ParseCondition()
        {
            var columnParts = new List<string>();
            while (Current.Kind != ExpressionFilterTokenKind.End)
            {
                if (IsComparisonOperatorStart(Current))
                {
                    var column = columnParts.Count > 0
                        ? ResolveColumn(columnParts)
                        : _lastColumn ?? throw new InvalidOperationException("Missing column before comparison operator.");

                    var op = ReadComparisonOperator();
                    var expected = ReadConditionValue();
                    _lastColumn = column;
                    return new ExpressionFilterConditionNode(column, op, expected);
                }

                if (Current.Kind == ExpressionFilterTokenKind.RightParen || IsLogicalToken(Current))
                    throw new InvalidOperationException("Missing comparison operator.");

                columnParts.Add(Advance().Text);
            }

            throw new InvalidOperationException("Incomplete filter expression.");
        }

        private string ReadComparisonOperator()
        {
            if (Current.Kind == ExpressionFilterTokenKind.Operator)
                return Advance().Text.ToLowerInvariant();

            if (MatchWord("not"))
            {
                if (MatchWord("like"))
                    return "not like";
                throw new InvalidOperationException("Expected 'like' after 'not'.");
            }

            var word = Advance().Text.ToLowerInvariant();
            return word switch
            {
                "like" => "like",
                "contains" => "contains",
                "startswith" => "startswith",
                "endswith" => "endswith",
                _ => throw new InvalidOperationException($"Unknown operator '{word}'.")
            };
        }

        private string ReadConditionValue()
        {
            var parts = new List<string>();
            while (Current.Kind != ExpressionFilterTokenKind.End
                   && Current.Kind != ExpressionFilterTokenKind.RightParen
                   && !IsLogicalToken(Current))
            {
                parts.Add(Advance().Text);
            }

            if (parts.Count == 0)
                throw new InvalidOperationException("Missing comparison value.");

            return string.Join(" ", parts).Trim();
        }

        private GridColumn ResolveColumn(List<string> columnParts)
        {
            var candidate = string.Join(" ", columnParts).Trim();
            if (_columns.TryGetValue(candidate, out var column))
                return column;

            var normalized = NormalizeExpressionFilterName(candidate);
            if (_columns.TryGetValue(normalized, out column))
                return column;

            throw new InvalidOperationException($"Unknown column '{candidate}'.");
        }

        private bool Match(ExpressionFilterTokenKind kind)
        {
            if (Current.Kind != kind)
                return false;
            _position++;
            return true;
        }

        private bool MatchWord(string word)
        {
            if (Current.Kind != ExpressionFilterTokenKind.Word
                || !string.Equals(Current.Text, word, StringComparison.OrdinalIgnoreCase))
                return false;
            _position++;
            return true;
        }

        private void Expect(ExpressionFilterTokenKind kind, string message)
        {
            if (!Match(kind))
                throw new InvalidOperationException(message);
        }

        private ExpressionFilterToken Advance()
        {
            var token = Current;
            if (Current.Kind != ExpressionFilterTokenKind.End)
                _position++;
            return token;
        }

        private ExpressionFilterToken Current =>
            _position < _tokens.Count ? _tokens[_position] : _tokens[^1];

        private static bool IsLogicalToken(ExpressionFilterToken token) =>
            token.Kind == ExpressionFilterTokenKind.Word
            && (string.Equals(token.Text, "and", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token.Text, "or", StringComparison.OrdinalIgnoreCase));

        private static bool IsComparisonOperatorStart(ExpressionFilterToken token)
        {
            if (token.Kind == ExpressionFilterTokenKind.Operator)
                return true;
            if (token.Kind != ExpressionFilterTokenKind.Word)
                return false;

            return token.Text.Equals("like", StringComparison.OrdinalIgnoreCase)
                   || token.Text.Equals("not", StringComparison.OrdinalIgnoreCase)
                   || token.Text.Equals("contains", StringComparison.OrdinalIgnoreCase)
                   || token.Text.Equals("startswith", StringComparison.OrdinalIgnoreCase)
                   || token.Text.Equals("endswith", StringComparison.OrdinalIgnoreCase);
        }

        private static List<ExpressionFilterToken> Tokenize(string expression)
        {
            var tokens = new List<ExpressionFilterToken>();
            var i = 0;
            while (i < expression.Length)
            {
                var ch = expression[i];
                if (char.IsWhiteSpace(ch))
                {
                    i++;
                    continue;
                }

                if (ch == '(')
                {
                    tokens.Add(new(ExpressionFilterTokenKind.LeftParen, "("));
                    i++;
                    continue;
                }

                if (ch == ')')
                {
                    tokens.Add(new(ExpressionFilterTokenKind.RightParen, ")"));
                    i++;
                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    var quote = ch;
                    i++;
                    var sb = new StringBuilder();
                    var closed = false;
                    while (i < expression.Length)
                    {
                        ch = expression[i++];
                        if (ch == quote)
                        {
                            closed = true;
                            break;
                        }
                        if (ch == '\\' && i < expression.Length)
                            ch = expression[i++];
                        sb.Append(ch);
                    }

                    if (!closed)
                        throw new InvalidOperationException("Missing closing quote.");

                    tokens.Add(new(ExpressionFilterTokenKind.Word, sb.ToString()));
                    continue;
                }

                if (IsOperatorChar(ch))
                {
                    if (i + 1 < expression.Length)
                    {
                        var two = expression.Substring(i, 2);
                        if (two is ">=" or "<=" or "<>" or "!=" or "==")
                        {
                            tokens.Add(new(ExpressionFilterTokenKind.Operator, two));
                            i += 2;
                            continue;
                        }
                    }

                    tokens.Add(new(ExpressionFilterTokenKind.Operator, ch.ToString()));
                    i++;
                    continue;
                }

                var start = i;
                while (i < expression.Length
                       && !char.IsWhiteSpace(expression[i])
                       && expression[i] != '('
                       && expression[i] != ')'
                       && !IsOperatorChar(expression[i]))
                {
                    i++;
                }

                tokens.Add(new(ExpressionFilterTokenKind.Word, expression[start..i]));
            }

            tokens.Add(new(ExpressionFilterTokenKind.End, string.Empty));
            return tokens;
        }

        private static bool IsOperatorChar(char ch) => ch is '<' or '>' or '=' or '!';
    }

    private readonly record struct ExpressionFilterToken(ExpressionFilterTokenKind Kind, string Text);

    private enum ExpressionFilterTokenKind
    {
        Word,
        Operator,
        LeftParen,
        RightParen,
        End
    }

    private static bool EvaluateExpressionFilterCondition(object? actual, string comparisonOperator, string expected)
    {
        var actualText = Convert.ToString(actual, CultureInfo.CurrentCulture) ?? string.Empty;
        var expectedText = expected.Trim();

        return comparisonOperator switch
        {
            "=" or "==" => CompareExpressionFilterValues(actual, actualText, expectedText) == 0,
            "!=" or "<>" => CompareExpressionFilterValues(actual, actualText, expectedText) != 0,
            ">" => CompareExpressionFilterValues(actual, actualText, expectedText) > 0,
            ">=" => CompareExpressionFilterValues(actual, actualText, expectedText) >= 0,
            "<" => CompareExpressionFilterValues(actual, actualText, expectedText) < 0,
            "<=" => CompareExpressionFilterValues(actual, actualText, expectedText) <= 0,
            "like" or "contains" => MatchesExpressionFilterLike(actualText, expectedText),
            "not like" => !MatchesExpressionFilterLike(actualText, expectedText),
            "startswith" => actualText.StartsWith(expectedText, StringComparison.OrdinalIgnoreCase),
            "endswith" => actualText.EndsWith(expectedText, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static int CompareExpressionFilterValues(object? actual, string actualText, string expectedText)
    {
        if (TryGetExpressionFilterDecimal(actual, actualText, out var actualDecimal)
            && TryGetExpressionFilterDecimal(expectedText, expectedText, out var expectedDecimal))
            return actualDecimal.CompareTo(expectedDecimal);

        if (TryGetExpressionFilterDate(actual, actualText, out var actualDate)
            && TryGetExpressionFilterDate(expectedText, expectedText, out var expectedDate))
            return actualDate.CompareTo(expectedDate);

        if (TryGetExpressionFilterBool(actual, actualText, out var actualBool)
            && TryGetExpressionFilterBool(expectedText, expectedText, out var expectedBool))
            return actualBool.CompareTo(expectedBool);

        return string.Compare(actualText, expectedText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesExpressionFilterLike(string actualText, string pattern)
    {
        if (pattern.Contains('%') || pattern.Contains('*') || pattern.Contains('_'))
        {
            var regex = "^" + Regex.Escape(pattern)
                .Replace("%", ".*")
                .Replace("\\*", ".*")
                .Replace("_", ".") + "$";
            return Regex.IsMatch(actualText, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return actualText.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetExpressionFilterDecimal(object? value, string text, out decimal result)
    {
        if (value is IConvertible convertible && value is not string && value is not DateTime && value is not bool)
        {
            try
            {
                result = convertible.ToDecimal(CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
            }
        }

        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result)
               || decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out result);
    }

    private static bool TryGetExpressionFilterDate(object? value, string text, out DateTime result)
    {
        if (value is DateTime date)
        {
            result = date;
            return true;
        }

        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out result)
               || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static bool TryGetExpressionFilterBool(object? value, string text, out bool result)
    {
        if (value is bool b)
        {
            result = b;
            return true;
        }

        if (bool.TryParse(text, out result))
            return true;

        if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }
}
