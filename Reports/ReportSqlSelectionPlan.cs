namespace Fx.ControlKit.Reports;

/// <summary>Typed bindings for the complete bounded date, integral and string/list AST selector. Not a general SQL translator.</summary>
internal sealed class ReportSqlSelectionPlan
{
    internal const string ReservedPrefix = "__fxPositionedSelection";
    internal List<string> Dates { get; } = [];
    internal List<string> Integers { get; } = [];
    internal List<StringBinding> Strings { get; } = [];
    internal HashSet<string> DeclaredNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal sealed class StringBinding(string name, string guard, string? constantName, string? constantValue, bool allowMultiple)
    {
        internal string Name { get; } = name;
        internal string Guard { get; } = guard;
        internal string? ConstantName { get; } = constantName;
        internal string? ConstantValue { get; } = constantValue;
        internal bool AllowMultiple { get; } = allowMultiple;
        internal string[] SqlNames { get; set; } = [name];
        internal bool Expanded { get; set; }
        internal StringBinding Clone() => new(Name, Guard, ConstantName, ConstantValue, AllowMultiple) { SqlNames = SqlNames.ToArray(), Expanded = Expanded };
    }

    internal ReportSqlSelectionPlan Clone()
    {
        var copy = new ReportSqlSelectionPlan();
        copy.Dates.AddRange(Dates);
        copy.Integers.AddRange(Integers);
        copy.Strings.AddRange(Strings.Select(binding => binding.Clone()));
        copy.DeclaredNames.UnionWith(DeclaredNames);
        return copy;
    }

    internal void ValidateUnexpandedLists(IReadOnlyDictionary<string, object> parameters)
    {
        foreach (var name in Dates.Concat(Integers).Concat(Strings.Select(binding => binding.Name)))
        {
            var matches = parameters.Where(pair => Sanitized(pair.Key).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1 || matches.Length == 1 && !matches[0].Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Ambiguous or sanitized positioned SQL selection '{name}'.");
            if (Dates.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (matches.Length != 1 || !LocalDate(matches[0].Value))
                    throw new InvalidDataException($"Positioned SQL date parameter '{name}' requires one typed local date before binding.");
                continue;
            }
            if (Integers.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (matches.Length == 1 && matches[0].Value is System.Collections.IEnumerable and not string)
                    throw new InvalidDataException($"Positioned SQL integral parameter '{name}' requires a scalar, not a list.");
                continue;
            }
            var binding = Strings.First(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (matches.Length == 0 || matches[0].Value is string || matches[0].Value is not System.Collections.IEnumerable sequence) continue;
            var values = sequence.Cast<object?>().Take(1001).ToArray();
            if (values.Length == 0 || values.Length > 1000 || values.Any(value => value is not string)
                || !binding.AllowMultiple && values.Length != 1)
                throw new InvalidDataException($"Positioned SQL selection '{binding.Name}' requires a nonempty list of strings before binding.");
        }
    }

    internal void ConfigureLists(IReadOnlyDictionary<string, IReadOnlyList<string>> selections,
        IReadOnlyDictionary<string, object>? extraParameters)
    {
        // Inspect the caller's original dictionary before the rewriter folds
        // its keys into a case-insensitive dictionary and could lose ambiguity.
        foreach (var name in Dates.Concat(Integers).Concat(Strings.Select(binding => binding.Name)))
        {
            var matches = extraParameters?.Keys.Where(key => Sanitized(key).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            if (matches.Length > 1 || matches.Length == 1 && !matches[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Ambiguous or sanitized positioned SQL extra binding '{name}'.");
        }
        // Each selection writes its scalar name and (for lists) its generated
        // suffix names. Reject cross-selection collisions before the rewriter
        // can overwrite an earlier value, including undeclared host seeds.
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in selections)
        {
            var sanitized = Sanitized(name);
            if (sanitized.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase)
                || Dates.Concat(Integers).Concat(Strings.Select(binding => binding.Name)).Any(required => required.Equals(sanitized, StringComparison.OrdinalIgnoreCase)
                    && !required.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Reserved or sanitized positioned SQL selection name.");
            if (!owners.Add(sanitized)) throw new InvalidDataException("Ambiguous positioned SQL selection names.");
            if (values?.Count > 2100) throw new InvalidDataException("Positioned SQL selection exceeds the parameter limit.");
            if (values?.Count > 1)
                for (var index = 0; index < values.Count; index++)
                    if (!owners.Add(sanitized + "_" + index))
                        throw new InvalidDataException("Generated positioned SQL selection names collide.");
        }
        foreach (var date in Dates)
            if (selections.Keys.Any(key => key.Equals(date, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Positioned SQL date parameter '{date}' requires one typed date, not a list.");
        foreach (var integer in Integers)
            if (selections.Keys.Any(key => key.Equals(integer, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Positioned SQL integral parameter '{integer}' requires a scalar, not a list.");
        foreach (var binding in Strings)
        {
            var matches = selections.Where(pair => pair.Key.Equals(binding.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1) throw new InvalidDataException($"Ambiguous positioned SQL selection '{binding.Name}'.");
            var (name, values) = matches[0];
            if (values is null || values.Count == 0 || values.Count > 1000 || values.Any(value => value is null))
                throw new InvalidDataException($"Positioned SQL selection '{binding.Name}' requires 1–1000 non-null strings.");
            if (!binding.AllowMultiple && values.Count != 1)
                throw new InvalidDataException($"Positioned SQL parameter '{binding.Name}' does not allow multiple values.");
            var names = values.Count == 1 ? new[] { name } : Enumerable.Range(0, values.Count).Select(index => name + "_" + index).ToArray();
            if (binding.Expanded && !binding.SqlNames.SequenceEqual(names, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Reload the positioned report before changing an expanded SQL selection's shape.");
            if (values.Count > 1 && names.Any(candidate => DeclaredNames.Contains(candidate)
                || !binding.Expanded && extraParameters?.Keys.Any(key => Sanitized(key).Equals(candidate, StringComparison.OrdinalIgnoreCase)) == true))
                throw new InvalidDataException($"Expanded positioned SQL selection '{binding.Name}' collides with another parameter.");
            binding.SqlNames = names;
            binding.Expanded = values.Count > 1;
        }
    }

    internal void ValidateAndBind(IDictionary<string, object>? parameters)
    {
        object Value(string name)
        {
            var matches = parameters?.Where(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray() ?? [];
            if (matches.Length != 1 || matches[0].Value is null or DBNull)
                throw new InvalidDataException($"Positioned SQL parameter '{name}' is missing, null or ambiguous.");
            return matches[0].Value;
        }
        foreach (var name in Dates)
            if (!LocalDate(Value(name)))
                throw new InvalidDataException($"Positioned SQL date parameter '{name}' requires a typed local date (DateTime, unspecified kind, midnight), not text or a timezone-bearing value.");
        foreach (var integer in Integers)
        {
            if (parameters?.Keys.Any(key => Sanitized(key).Equals(integer, StringComparison.OrdinalIgnoreCase)
                && !key.Equals(integer, StringComparison.OrdinalIgnoreCase)) == true
                || Value(integer) is System.Collections.IEnumerable and not string)
                throw new InvalidDataException($"Positioned SQL integral parameter '{integer}' requires an unambiguous scalar binding.");
        }
        var generated = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in Strings)
        {
            var values = binding.SqlNames.Select(name => Value(name) is string value ? value
                : throw new InvalidDataException($"Positioned SQL selection '{binding.Name}' requires non-null string bindings; expand lists before execution.")).ToArray();
            // SQL's default collation is not trusted. Printable ASCII is narrowed
            // using an explicit CI collation; every uncertain list value admits
            // all rows through this guard for the unchanged Crystal AST to decide.
            generated[binding.Guard] = values.Any(value => value.Any(character => character is < ' ' or > '~'));
            if (binding.ConstantName is not null) generated[binding.ConstantName] = binding.ConstantValue!;
        }
        foreach (var pair in generated)
        {
            var aliases = parameters!.Keys.Where(key => key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (aliases.Length > 1) throw new InvalidDataException("Ambiguous reserved positioned SQL binding.");
            if (aliases.Length == 1) parameters[aliases[0]] = pair.Value;
            else parameters.Add(pair.Key, pair.Value);
        }
        if (parameters!.Count > 2100) throw new InvalidDataException("Positioned report exceeds SQL Server's 2,100 parameter limit.");
    }

    private static bool LocalDate(object value) => value is DateTime date && date.Kind == DateTimeKind.Unspecified && date.TimeOfDay == TimeSpan.Zero;
    private static string Sanitized(string name) => new(name.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
}
