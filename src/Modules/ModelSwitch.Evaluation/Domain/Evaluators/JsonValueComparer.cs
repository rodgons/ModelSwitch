using System.Globalization;
using System.Text.Json;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>Structural comparison of JSON values for the <c>json_fields</c> comparators.</summary>
internal static class JsonValueComparer
{
    /// <summary>
    /// Deep equality: same kind, strings equal ordinally (no trimming), numbers equal by value (<c>1.0</c> equals <c>1</c>),
    /// objects with the same property names and equal values, arrays equal element by element in order.
    /// </summary>
    public static bool DeepEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return TryNumber(left, out var a) && TryNumber(right, out var b) ? a == b : left.GetDouble().Equals(right.GetDouble());
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().ToArray();
                if (leftProperties.Length != right.EnumerateObject().Count())
                {
                    return false;
                }

                return leftProperties.All(p => right.TryGetProperty(p.Name, out var other) && DeepEquals(p.Value, other));
            case JsonValueKind.Array:
                return left.GetArrayLength() == right.GetArrayLength()
                    && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => DeepEquals(pair.First, pair.Second));
            default:
                return true; // true, false, null: same kind is same value.
        }
    }

    /// <summary>Strings equal ignoring case and surrounding whitespace; any other value falls back to <see cref="DeepEquals"/>.</summary>
    public static bool CaseFoldEquals(JsonElement expected, JsonElement predicted)
        => expected.ValueKind == JsonValueKind.String && predicted.ValueKind == JsonValueKind.String
            ? string.Equals(expected.GetString()!.Trim(), predicted.GetString()!.Trim(), StringComparison.OrdinalIgnoreCase)
            : DeepEquals(expected, predicted);

    /// <summary>
    /// Both values are numbers, or strings holding an invariant-culture number, and differ by at most <paramref name="tolerance"/>.
    /// </summary>
    public static bool NumberEquals(JsonElement expected, JsonElement predicted, decimal tolerance)
        => TryNumber(expected, out var a) && TryNumber(predicted, out var b) && Math.Abs(a - b) <= tolerance;

    /// <summary>Both values are arrays holding the same distinct elements (by <see cref="DeepEquals"/>), in any order.</summary>
    public static bool SetEquals(JsonElement expected, JsonElement predicted)
    {
        if (expected.ValueKind != JsonValueKind.Array || predicted.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var left = expected.EnumerateArray().ToArray();
        var right = predicted.EnumerateArray().ToArray();
        return left.All(x => right.Any(y => DeepEquals(x, y))) && right.All(y => left.Any(x => DeepEquals(x, y)));
    }

    private static bool TryNumber(JsonElement value, out decimal number)
    {
        number = 0m;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out number),
            JsonValueKind.String => decimal.TryParse(value.GetString()!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number),
            _ => false,
        };
    }
}
