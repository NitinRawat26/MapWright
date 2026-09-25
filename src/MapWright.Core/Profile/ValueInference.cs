using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MapWright.Core.Profile.Samples;

namespace MapWright.Core.Profile;

internal readonly record struct InferredValue(FieldDataType Type, string? Format = null, int SlashFirst = 0, int SlashSecond = 0)
{
    public const string SlashDateFormat = "slash-date";
}

internal static partial class ValueInference
{
    public const string IsoDate = "yyyy-MM-dd";
    public const string IsoDateTime = "ISO 8601";
    public const int MaxShapeLength = 32;

    public static InferredValue Infer(string text, ScalarKind kind, bool textIsUntyped)
    {
        switch (kind)
        {
            case ScalarKind.Boolean:
                return new(FieldDataType.Boolean);
            case ScalarKind.Number:
                return new(IntegerNumber().IsMatch(text) ? FieldDataType.Integer : FieldDataType.Decimal);
        }

        if (IsoDateTimeValue().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
        {
            return new(FieldDataType.DateTime, IsoDateTime);
        }

        if (DateOnly.TryParseExact(text, IsoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return new(FieldDataType.Date, IsoDate);
        }

        if (SlashDate().Match(text) is { Success: true } slash)
        {
            var first = int.Parse(slash.Groups[1].Value, CultureInfo.InvariantCulture);
            var second = int.Parse(slash.Groups[2].Value, CultureInfo.InvariantCulture);
            if (first is >= 1 and <= 31 && second is >= 1 and <= 31 && (first <= 12 || second <= 12))
            {
                return new(FieldDataType.Date, InferredValue.SlashDateFormat, first, second);
            }
        }

        // JSON strings keep their declared type; untyped XML text is inferred from its content.
        if (textIsUntyped)
        {
            if (bool.TryParse(text, out _))
            {
                return new(FieldDataType.Boolean);
            }

            if (UntypedInteger().IsMatch(text))
            {
                return new(FieldDataType.Integer);
            }

            if (UntypedDecimal().IsMatch(text))
            {
                return new(FieldDataType.Decimal);
            }
        }

        return new(FieldDataType.String);
    }

    public static FieldDataType Widen(FieldDataType current, FieldDataType next) => (current, next) switch
    {
        (FieldDataType.Unknown, _) => next,
        (_, FieldDataType.Unknown) => current,
        _ when current == next => current,
        (FieldDataType.Integer, FieldDataType.Decimal) or (FieldDataType.Decimal, FieldDataType.Integer) => FieldDataType.Decimal,
        (FieldDataType.Date, FieldDataType.DateTime) or (FieldDataType.DateTime, FieldDataType.Date) => FieldDataType.DateTime,
        _ => FieldDataType.String,
    };

    /// <summary>Character-class shape of a value, e.g. "999-99-9999" or "AA9 9AA"; null for long free text.</summary>
    public static string? Shape(string value)
    {
        if (value.Length > MaxShapeLength)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                _ when char.IsAsciiDigit(ch) => '9',
                _ when char.IsUpper(ch) => 'A',
                _ when char.IsLetter(ch) => 'a',
                _ when char.IsWhiteSpace(ch) => ' ',
                _ => ch,
            });
        }

        return builder.ToString();
    }

    public static int Scale(string number)
    {
        var text = number.Split('e', 'E')[0];
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntegerNumber();

    [GeneratedRegex(@"^-?(0|[1-9]\d*)$")]
    private static partial Regex UntypedInteger();

    [GeneratedRegex(@"^-?(0|[1-9]\d*)\.\d+$")]
    private static partial Regex UntypedDecimal();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex IsoDateTimeValue();

    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})/(\d{4})$")]
    private static partial Regex SlashDate();
}
