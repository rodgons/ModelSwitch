using System.Text.Json;
using ModelSwitch.SharedKernel;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>How one <c>json_fields</c> field is compared.</summary>
internal sealed record FieldComparator(string Name, decimal Tolerance = 0m)
{
    public const string Exact = "exact";
    public const string CaseFold = "casefold";
    public const string Number = "number";
    public const string Set = "set";

    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal) { Exact, CaseFold, Number, Set };

    public bool Matches(JsonElement expected, JsonElement predicted) => Name switch
    {
        CaseFold => JsonValueComparer.CaseFoldEquals(expected, predicted),
        Number => JsonValueComparer.NumberEquals(expected, predicted, Tolerance),
        Set => JsonValueComparer.SetEquals(expected, predicted),
        _ => JsonValueComparer.DeepEquals(expected, predicted),
    };
}

/// <summary>
/// <c>json_fields</c> v1, for extraction. The output must be one JSON object (a Markdown fence around it is tolerated, see
/// <see cref="EvaluationJson.ParseObject"/>). The score is the fraction of the expected object's fields that the output
/// matched; extra output fields are ignored and an empty expected object scores 1. A field expected as <c>null</c> matches a
/// <c>null</c> or absent output field; any other expected field must be present and match its comparator. Unparseable output
/// is failed with score 0, <c>parseFailure: true</c> and every field unmatched. Passed means score ≥ <c>passThreshold</c>
/// (default 1: every field right).
/// </summary>
/// <remarks>
/// Config: <c>{"defaultComparator": "exact"|"casefold"|"number"|"set" (default exact), "fields": {"&lt;name&gt;":
/// {"comparator": …, "tolerance": number ≥ 0 (number only, default 0)}}, "passThreshold": 0-1}</c>. Comparators:
/// <c>exact</c> deep JSON equality (strings ordinal, numbers by value); <c>casefold</c> strings ignoring case and surrounding
/// whitespace; <c>number</c> numbers or numeric strings within the tolerance; <c>set</c> arrays with the same distinct
/// elements in any order. Details: <c>{"parseFailure": bool, "fields": {"&lt;name&gt;": bool}}</c>.
/// </remarks>
internal sealed class JsonFieldsEvaluator(FieldComparator defaultComparator, IReadOnlyDictionary<string, FieldComparator> fields, decimal passThreshold)
    : ILocalEvaluator
{
    public const string KindName = "json_fields";

    public string Kind => KindName;

    public string Version => "1";

    public bool RequiresJudge => false;

    public decimal PassThreshold { get; } = passThreshold;

    public static Result<IEvaluator> Create(JsonConfigReader config)
    {
        var defaultName = config.String("defaultComparator") ?? FieldComparator.Exact;
        if (!FieldComparator.Names.Contains(defaultName))
        {
            config.AddError(config.PathOf("defaultComparator"), "Must be exact, casefold, number or set.");
        }

        var fields = new Dictionary<string, FieldComparator>(StringComparer.Ordinal);
        var fieldsPath = config.PathOf("fields");
        var fieldsElement = config.Property("fields");
        if (fieldsElement is { ValueKind: not JsonValueKind.Object })
        {
            config.AddError(fieldsPath, "Must be an object keyed by field name.");
        }
        else if (fieldsElement is { } fieldsObject)
        {
            foreach (var field in fieldsObject.EnumerateObject())
            {
                var path = $"{fieldsPath}.{field.Name}";
                if (field.Value.ValueKind != JsonValueKind.Object)
                {
                    config.AddError(path, "Must be an object with a comparator.");
                    continue;
                }

                var reader = config.Nested(field.Value, path);
                var name = reader.String("comparator", required: true);
                if (name is not null && !FieldComparator.Names.Contains(name))
                {
                    reader.AddError(reader.PathOf("comparator"), "Must be exact, casefold, number or set.");
                }

                var tolerance = reader.Decimal("tolerance", 0m, decimal.MaxValue);
                if (tolerance is not null && name != FieldComparator.Number)
                {
                    reader.AddError(reader.PathOf("tolerance"), "Only the number comparator takes a tolerance.");
                }

                reader.RejectUnknown();
                if (name is not null)
                {
                    fields[field.Name] = new FieldComparator(name, tolerance ?? 0m);
                }
            }
        }

        var passThreshold = config.PassThreshold(1m);
        return config.Finish<IEvaluator>(
            () => new JsonFieldsEvaluator(new FieldComparator(defaultName), fields, passThreshold),
            "The json_fields config is invalid.");
    }

    public CaseScore Score(CaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Expected.ValueKind != JsonValueKind.Object)
        {
            return CaseScore.Errored(new CaseError(CaseErrorCodes.ExpectedResultInvalid, "json_fields needs an object as the expected result."));
        }

        var expectedFields = context.Expected.EnumerateObject().ToArray();
        var predicted = EvaluationJson.ParseObject(context.Output);
        var matched = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var field in expectedFields)
        {
            matched[field.Name] = predicted is { } output && Matches(field, output);
        }

        var score = expectedFields.Length == 0 ? (predicted is null ? 0m : 1m) : (decimal)matched.Count(m => m.Value) / matched.Count;
        return CaseScore.Scored(score, predicted is not null && score >= PassThreshold, predicted, new { ParseFailure = predicted is null, Fields = matched });
    }

    private bool Matches(JsonProperty expected, JsonElement output)
    {
        var present = output.TryGetProperty(expected.Name, out var value) && value.ValueKind != JsonValueKind.Null;
        if (expected.Value.ValueKind == JsonValueKind.Null)
        {
            return !present;
        }

        return present && fields.GetValueOrDefault(expected.Name, defaultComparator).Matches(expected.Value, value);
    }
}
