using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Metrics;
using ModelSwitch.SharedKernel;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>
/// <c>classification</c> v1. The output is trimmed and mapped to a label by exact, case-sensitive match against the
/// configured label set; anything else predicts <c>unparseable</c>. Score 1 when the predicted label equals the expected label
/// (a string), else 0; passed means score 1, and an unparseable output is failed. <c>predicted</c> is the label or
/// <c>"unparseable"</c>. Config: <c>{"labels": ["…", …]}</c>: required, non-empty, distinct, already trimmed, and never
/// <c>unparseable</c>. Details: <c>{"label": string|null, "unparseable": bool}</c>.
/// </summary>
internal sealed class ClassificationEvaluator(IReadOnlyList<string> labels) : ILocalEvaluator
{
    public const string KindName = "classification";

    private readonly HashSet<string> _labels = new(labels, StringComparer.Ordinal);

    public string Kind => KindName;

    public string Version => "1";

    public bool RequiresJudge => false;

    public IReadOnlyList<string> Labels { get; } = labels;

    public static Result<IEvaluator> Create(JsonConfigReader config)
    {
        var labels = new List<string>();
        var path = config.PathOf("labels");
        var element = config.Property("labels");
        if (element is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0)
        {
            config.AddError(path, "Is required: a non-empty array of label strings.");
        }
        else
        {
            var i = 0;
            foreach (var item in array.EnumerateArray())
            {
                var itemPath = $"{path}[{i++}]";
                var label = item.ValueKind == JsonValueKind.String ? item.GetString()! : null;
                if (string.IsNullOrWhiteSpace(label) || label != label.Trim())
                {
                    config.AddError(itemPath, "Must be a non-empty string with no surrounding whitespace.");
                }
                else if (label == ClassificationMetrics.Unparseable)
                {
                    config.AddError(itemPath, "'unparseable' is reserved for outputs that match no label.");
                }
                else if (labels.Contains(label, StringComparer.Ordinal))
                {
                    config.AddError(itemPath, "Labels must be distinct.");
                }
                else
                {
                    labels.Add(label);
                }
            }
        }

        return config.Finish<IEvaluator>(() => new ClassificationEvaluator(labels), "The classification config is invalid.");
    }

    public CaseScore Score(CaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Expected.ValueKind != JsonValueKind.String)
        {
            return CaseScore.Errored(new CaseError(CaseErrorCodes.ExpectedResultInvalid, "classification needs a label string as the expected result."));
        }

        var output = context.Output.Trim();
        var label = _labels.Contains(output) ? output : null;
        var correct = label is not null && string.Equals(label, context.Expected.GetString(), StringComparison.Ordinal);
        return CaseScore.Scored(
            correct ? 1m : 0m,
            correct,
            EvaluationJson.String(label ?? ClassificationMetrics.Unparseable),
            new { Label = label, Unparseable = label is null });
    }
}
