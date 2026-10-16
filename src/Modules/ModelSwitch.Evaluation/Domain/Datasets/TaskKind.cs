namespace ModelSwitch.Evaluation.Domain.Datasets;

/// <summary>What a Dataset's Cases expect: an object of fields, a class label, or a free-text reference answer (evaluation.md section 1).</summary>
internal enum TaskKind
{
    Extraction,
    Classification,
    FreeText,
}

internal static class TaskKinds
{
    public const string Extraction = "extraction";
    public const string Classification = "classification";
    public const string FreeText = "free_text";

    /// <summary>The wire and database values, in the order the <c>task_kind</c> check constraint lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Extraction, Classification, FreeText];

    public static string ToWire(TaskKind kind) => kind switch
    {
        TaskKind.Extraction => Extraction,
        TaskKind.Classification => Classification,
        TaskKind.FreeText => FreeText,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryParse(string? value, out TaskKind kind)
    {
        switch (value)
        {
            case Extraction:
                kind = TaskKind.Extraction;
                return true;
            case Classification:
                kind = TaskKind.Classification;
                return true;
            case FreeText:
                kind = TaskKind.FreeText;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
