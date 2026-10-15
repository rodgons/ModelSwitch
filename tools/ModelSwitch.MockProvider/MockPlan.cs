namespace ModelSwitch.MockProvider;

internal sealed record MockMessage(string Role, string Text);

/// <summary>Everything one request will do, resolved from the model id, the control headers and the scenario store.</summary>
internal sealed record MockPlan(
    string ModelId,
    ModelSpec Model,
    IReadOnlyList<string> Chunks,
    bool Truncated,
    int InputTokens,
    int OutputTokens,
    int? CachedTokens,
    FaultSpec? Fault,
    bool Stream,
    bool IncludeUsage)
{
    public string Text => string.Concat(Chunks);

    public string FinishReason => Truncated ? "length" : "stop";

    public TimeSpan Latency => Fault?.Latency ?? Model.Latency;
}

internal static class PlanResolver
{
    public const string FaultHeader = "X-Mock-Fault";
    public const string UsageHeader = "X-Mock-Usage";
    public const string ScenarioHeader = "X-Mock-Scenario";

    public static bool TryResolve(
        HttpContext http,
        ScenarioStore store,
        string? modelId,
        IReadOnlyList<MockMessage> messages,
        int? maxTokens,
        bool stream,
        bool usageRequested,
        out MockPlan? plan,
        out string? error)
    {
        plan = null;
        if (!ModelSpec.TryParse(modelId, out var model, out error) || model is null)
        {
            return false;
        }

        if (!UsageOverride.TryParse(http.Request.Headers[UsageHeader].ToString(), out var usage, out error) || usage is null)
        {
            return false;
        }

        var faultText = http.Request.Headers[FaultHeader].ToString();
        if (string.IsNullOrWhiteSpace(faultText))
        {
            faultText = store.GetFault(modelId!) ?? string.Empty;
        }

        FaultSpec? fault = null;
        if (!string.IsNullOrWhiteSpace(faultText))
        {
            if (!FaultSpec.TryParse(faultText, out fault, out error))
            {
                return false;
            }

            var scenarioKey = http.Request.Headers[ScenarioHeader].ToString();
            if (string.IsNullOrEmpty(scenarioKey))
            {
                scenarioKey = modelId!;
            }

            if (!store.ShouldApply(scenarioKey, faultText, fault!))
            {
                fault = null;
            }
        }

        var chunks = BuildChunks(model, messages);
        var truncated = false;
        if (maxTokens is { } limit && limit < chunks.Count)
        {
            chunks = chunks.Take(Math.Max(limit, 0)).ToList();
            truncated = true;
        }

        var inputTokens = usage.Input ?? Math.Max(1, messages.Sum(m => (m.Text.Length + 3) / 4));
        var outputTokens = usage.Output ?? chunks.Count;
        var includeUsage = usageRequested && fault is not { NoUsage: true };

        plan = new MockPlan(modelId!, model, chunks, truncated, inputTokens, outputTokens, usage.Cached, fault, stream, includeUsage);
        return true;
    }

    private static List<string> BuildChunks(ModelSpec model, IReadOnlyList<MockMessage> messages)
    {
        string[] words;
        if (model.Echo)
        {
            var lastUser = messages.LastOrDefault(m => m.Role == "user")?.Text ?? string.Empty;
            words = lastUser.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                words = ["echo"];
            }
        }
        else
        {
            words = Enumerable.Repeat("token", model.Tokens).ToArray();
        }

        return words.Select((word, index) => index == 0 ? word : " " + word).ToList();
    }
}
