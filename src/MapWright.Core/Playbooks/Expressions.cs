using System.Globalization;
using System.Text.Json;

namespace MapWright.Core.Playbooks;

public sealed class ExpressionException(string message) : Exception(message);

/// <summary>A number, a list of numbers (e.g. every principal's ownership) or a comparison result.</summary>
public readonly record struct ExprValue
{
    public decimal? Number { get; private init; }
    public IReadOnlyList<decimal>? List { get; private init; }
    public bool? Bool { get; private init; }

    public static ExprValue Of(decimal number) => new() { Number = number };
    public static ExprValue Of(IReadOnlyList<decimal> list) => new() { List = list };
    public static ExprValue Of(bool value) => new() { Bool = value };

    /// <summary>JSON number → number, array of numbers → list, true/false → bool.</summary>
    public static ExprValue FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => Of(element.GetDecimal()),
        JsonValueKind.True => Of(true),
        JsonValueKind.False => Of(false),
        JsonValueKind.Array when element.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Number) =>
            Of([.. element.EnumerateArray().Select(e => e.GetDecimal())]),
        _ => throw new ExpressionException($"Expected a number, an array of numbers or true/false, got {element.ValueKind}."),
    };

    public override string ToString() =>
        Number is { } n ? n.ToString(CultureInfo.InvariantCulture)
        : List is { } l ? $"[{string.Join(", ", l.Select(v => v.ToString(CultureInfo.InvariantCulture)))}]"
        : Bool is true ? "true" : "false";
}

public abstract record Expr
{
    public bool IsComparison => this is BinaryExpr { Op: "<" or "<=" or "==" or "!=" or ">=" or ">" };

    public IEnumerable<string> Variables() => this switch
    {
        VarExpr v => [v.Name],
        UnaryExpr u => u.Operand.Variables(),
        BinaryExpr b => b.Left.Variables().Concat(b.Right.Variables()),
        CallExpr c => c.Args.SelectMany(a => a.Variables()),
        _ => [],
    };
}

public sealed record NumberExpr(decimal Value) : Expr;

public sealed record VarExpr(string Name) : Expr;

public sealed record UnaryExpr(Expr Operand) : Expr;

public sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr;

public sealed record CallExpr(string Function, IReadOnlyList<Expr> Args) : Expr;

/// <summary>
/// Parses and evaluates the small arithmetic language used by derivation and validation rules:
/// numbers, input names, + - * / ( ), one optional comparison (&lt; &lt;= == != &gt;= &gt;),
/// and the functions sum, count, min, max, abs, round.
/// </summary>
public static class Expressions
{
    public static IReadOnlyList<string> Functions { get; } = ["sum", "count", "min", "max", "abs", "round"];

    public static Expr Parse(string text) => new Parser(text).ParseAll();

    public static ExprValue Evaluate(Expr expr, IReadOnlyDictionary<string, ExprValue> inputs) => expr switch
    {
        NumberExpr n => ExprValue.Of(n.Value),
        VarExpr v => inputs.TryGetValue(v.Name, out var value) ? value : throw new ExpressionException($"No value for '{v.Name}'."),
        UnaryExpr u => ExprValue.Of(-Num(Evaluate(u.Operand, inputs), "-")),
        BinaryExpr b => Binary(b, Evaluate(b.Left, inputs), Evaluate(b.Right, inputs)),
        CallExpr c => Call(c.Function, [.. c.Args.Select(a => Evaluate(a, inputs))]),
        _ => throw new ExpressionException("Unknown expression."),
    };

    private static decimal Num(ExprValue value, string op) =>
        value.Number ?? throw new ExpressionException($"'{op}' needs a number, got {value}; use sum() or another function for lists.");

    private static ExprValue Binary(BinaryExpr b, ExprValue left, ExprValue right)
    {
        var l = Num(left, b.Op);
        var r = Num(right, b.Op);
        return b.Op switch
        {
            "+" => ExprValue.Of(l + r),
            "-" => ExprValue.Of(l - r),
            "*" => ExprValue.Of(l * r),
            "/" => r == 0 ? throw new ExpressionException("Division by zero.") : ExprValue.Of(l / r),
            "<" => ExprValue.Of(l < r),
            "<=" => ExprValue.Of(l <= r),
            "==" => ExprValue.Of(l == r),
            "!=" => ExprValue.Of(l != r),
            ">=" => ExprValue.Of(l >= r),
            ">" => ExprValue.Of(l > r),
            _ => throw new ExpressionException($"Unknown operator '{b.Op}'."),
        };
    }

    private static ExprValue Call(string function, IReadOnlyList<ExprValue> args)
    {
        IReadOnlyList<decimal> Flatten() => [.. args.SelectMany(a => a.List ?? [Num(a, function)])];

        return function switch
        {
            "sum" => ExprValue.Of(Flatten().Sum()),
            "count" => ExprValue.Of(Flatten().Count),
            "min" => Flatten() is { Count: > 0 } min ? ExprValue.Of(min.Min()) : throw new ExpressionException("min() of nothing."),
            "max" => Flatten() is { Count: > 0 } max ? ExprValue.Of(max.Max()) : throw new ExpressionException("max() of nothing."),
            "abs" when args.Count == 1 => ExprValue.Of(Math.Abs(Num(args[0], function))),
            "round" when args.Count is 1 or 2 => ExprValue.Of(Math.Round(
                Num(args[0], function),
                args.Count == 2 ? (int)Num(args[1], function) : 0,
                MidpointRounding.AwayFromZero)),
            _ => throw new ExpressionException($"Wrong number of arguments for {function}()."),
        };
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public Expr ParseAll()
        {
            var expr = Comparison();
            Skip();
            if (_pos < text.Length)
            {
                throw Error($"Unexpected '{text[_pos]}'");
            }

            return expr;
        }

        private Expr Comparison()
        {
            var left = Additive();
            foreach (var op in (string[])["<=", ">=", "==", "!=", "<", ">"])
            {
                if (Accept(op))
                {
                    return new BinaryExpr(op, left, Additive());
                }
            }

            return left;
        }

        private Expr Additive()
        {
            var left = Term();
            while (true)
            {
                if (Accept("+"))
                {
                    left = new BinaryExpr("+", left, Term());
                }
                else if (Accept("-"))
                {
                    left = new BinaryExpr("-", left, Term());
                }
                else
                {
                    return left;
                }
            }
        }

        private Expr Term()
        {
            var left = Unary();
            while (true)
            {
                if (Accept("*"))
                {
                    left = new BinaryExpr("*", left, Unary());
                }
                else if (Accept("/"))
                {
                    left = new BinaryExpr("/", left, Unary());
                }
                else
                {
                    return left;
                }
            }
        }

        private Expr Unary() => Accept("-") ? new UnaryExpr(Unary()) : Primary();

        private Expr Primary()
        {
            Skip();
            if (_pos >= text.Length)
            {
                throw Error("Unexpected end of expression");
            }

            if (Accept("("))
            {
                var inner = Additive();
                Expect(")");
                return inner;
            }

            var c = text[_pos];
            if (char.IsAsciiDigit(c) || c == '.')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] == '.'))
                {
                    _pos++;
                }

                return decimal.TryParse(text.AsSpan(start, _pos - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                    ? new NumberExpr(number)
                    : throw Error($"Invalid number '{text[start.._pos]}'");
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_'))
                {
                    _pos++;
                }

                var name = text[start.._pos];
                if (!Accept("("))
                {
                    return new VarExpr(name);
                }

                if (!Functions.Contains(name))
                {
                    throw Error($"Unknown function '{name}'");
                }

                var args = new List<Expr>();
                if (!Accept(")"))
                {
                    do
                    {
                        args.Add(Additive());
                    }
                    while (Accept(","));
                    Expect(")");
                }

                return new CallExpr(name, args);
            }

            throw Error($"Unexpected '{c}'");
        }

        private void Skip()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        private bool Accept(string token)
        {
            Skip();
            if (string.CompareOrdinal(text, _pos, token, 0, token.Length) != 0)
            {
                return false;
            }

            _pos += token.Length;
            return true;
        }

        private void Expect(string token)
        {
            if (!Accept(token))
            {
                throw Error($"Expected '{token}'");
            }
        }

        private ExpressionException Error(string message) => new($"{message} at position {_pos + 1} in '{text}'.");
    }
}
