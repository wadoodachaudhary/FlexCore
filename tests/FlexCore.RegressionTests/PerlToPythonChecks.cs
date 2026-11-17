using Fx.ControlKit.Migration;

internal static class PerlToPythonChecks
{
    public static void Run(Action<bool, string> check)
    {
        var scalar = PerlToPython.Convert("my $name = \"Ada\";\nprint \"$name\\n\";\n");
        check(scalar.Contains("name = \"Ada\"", StringComparison.Ordinal) && scalar.Contains("print(f\"{name}\\n\", end=\"\")", StringComparison.Ordinal), "a scalar and an interpolated print become Python");

        var list = PerlToPython.Convert("my @nums = (1, 2);\nmy %row = (sku => \"A\");\n");
        check(list.Contains("nums = [1, 2]", StringComparison.Ordinal) && list.Contains("row = {\"sku\": \"A\"}", StringComparison.Ordinal), "arrays and hashes become a list and a dict");

        var sub = PerlToPython.Convert("sub greet { my ($who) = @_; return \"hi $who\"; }\n");
        check(sub.Contains("def greet(who):", StringComparison.Ordinal) && sub.Contains("return f\"hi {who}\"", StringComparison.Ordinal), "a sub with @_ becomes a function");

        var regex = PerlToPython.Convert("my $text = \"cat\";\n$text =~ s/c/b/;\nif ($text =~ /bat/) { print \"ok\"; }\n");
        check(regex.Contains("re.sub(r\"c\", \"b\", text)", StringComparison.Ordinal) && regex.Contains("re.search(r\"bat\", text)", StringComparison.Ordinal), "substitution and match become re calls");

        var file = PerlToPython.Convert("open my $fh, \"<\", $path or die \"no\";\nwhile (my $line = <$fh>) { chomp $line; }\nclose $fh;\n");
        check(file.Contains("open(path, \"r\"", StringComparison.Ordinal) && file.Contains("for line in fh:", StringComparison.Ordinal) && file.Contains("fh.close()", StringComparison.Ordinal), "file reads become a Python file loop");

        var tools = PerlToPython.Convert("use Getopt::Long;\nmy $input = \"\";\nGetOptions(\"input=s\" => \\$input);\nmy @ordered = sort { $a <=> $b } @nums;\nmy $body = <<'END';\nhello\nEND\n");
        check(tools.Contains("argparse", StringComparison.Ordinal) && tools.Contains("sorted(nums)", StringComparison.Ordinal) && tools.Contains("\"\"\"hello\"\"\"", StringComparison.Ordinal), "Getopt, sort, and a here-doc are translated");

        var package = PerlToPython.Convert("package Sales::Line;\nsub load {\n  my ($class, $sku) = @_;\n  return bless { sku => $sku }, $class;\n}\n1;\n");
        check(package.Contains("class Line:", StringComparison.Ordinal) && package.Contains("def load(cls, sku):", StringComparison.Ordinal) && package.Contains("return cls(sku=sku)", StringComparison.Ordinal), "bless becomes a class constructor");
    }
}
