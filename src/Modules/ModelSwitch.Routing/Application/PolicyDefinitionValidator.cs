using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.Routing.Application;

/// <summary>
/// Validates a Routing Policy definition: the structure in <c>PolicyDefinition.schema.json</c> (checked here directly,
/// so no JSON Schema library is needed), then the semantic rules (routing-and-resilience.md section 2). Every problem
/// is reported with its field path; nothing throws on bad input.
/// </summary>
internal sealed partial class PolicyDefinitionValidator : IPolicyDefinitionValidator
{
    // These sets must match the schema's "properties"; a unit test compares them.
    internal static readonly string[] RootKeys = ["strategy", "candidates", "rules", "default", "allowedProviders", "maxRequestCost", "defaultMaxOutputTokens", "maxCandidates", "execution"];
    private static readonly string[] _ruleOnlyKeys = ["rules", "default"];
    internal static readonly string[] RuleKeys = ["when", "candidates"];
    internal static readonly string[] WhenKeys = ["taskType", "requires"];
    internal static readonly string[] ExecutionKeys = ["deadlineMs", "attemptTimeoutMs", "maxAttempts", "maxRetriesPerCandidate", "backoff"];
    internal static readonly string[] BackoffKeys = ["baseMs", "maxMs", "jitter"];

    public PolicyValidationResult Validate(string definitionJson, PolicyReferenceData references)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);
        ArgumentNullException.ThrowIfNull(references);
        var run = new Run(references);
        PolicyDefinition? definition = null;
        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            definition = run.ReadDocument(document.RootElement);
        }
        catch (JsonException)
        {
            run.Add(string.Empty, "The definition is not valid JSON.");
        }

        return run.Finish(definition);
    }

    private sealed class Run(PolicyReferenceData references)
    {
        private readonly List<PolicyFieldError> _errors = [];
        private readonly Dictionary<string, Guid> _resolved = new(StringComparer.Ordinal);

        public void Add(string path, string message) => _errors.Add(new PolicyFieldError(path, message));

        public PolicyValidationResult Finish(PolicyDefinition? definition)
            => new(_errors, _errors.Count == 0 ? definition : null, _resolved);

        public PolicyDefinition? ReadDocument(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                return ReadRoot(root);
            }

            Add(string.Empty, "The definition must be a JSON object.");
            return null;
        }

        private PolicyDefinition? ReadRoot(JsonElement root)
        {
            RejectUnknown(root, string.Empty, RootKeys);

            var strategy = ReadStrategy(root);
            var candidates = Has(root, "candidates") ? ReadCandidates(root.GetProperty("candidates"), "candidates") : null;
            var defaults = Has(root, "default") ? ReadCandidates(root.GetProperty("default"), "default") : null;
            var rules = Has(root, "rules") ? ReadRules(root.GetProperty("rules")) : null;

            ApplyStrategyRules(strategy, root);

            var allowedProviders = ReadAllowedProviders(root);
            var maxRequestCost = ReadMaxRequestCost(root);
            var defaultMaxOutput = ReadInt(root, "defaultMaxOutputTokens", "defaultMaxOutputTokens", 1, 1024);
            var maxCandidates = Has(root, "maxCandidates") && root.GetProperty("maxCandidates").ValueKind != JsonValueKind.Null
                ? ReadInt(root, "maxCandidates", "maxCandidates", 1, 1)
                : (int?)null;
            var execution = ReadExecution(root);

            if (allowedProviders is { Count: > 0 })
            {
                var all = (candidates ?? []).Concat(defaults ?? []).Concat(rules?.SelectMany(r => r.Candidates) ?? []).ToList();
                var byId = _resolved.ToDictionary(kv => kv.Value, kv => kv.Key);
                if (all.Count > 0 && all.All(id => !allowedProviders.Contains(ProviderOf(byId[id]))))
                {
                    Add("allowedProviders", "No candidate is served by an allowed provider, so every request would be excluded.");
                }
            }

            if (strategy is null)
            {
                return null;
            }

            return new PolicyDefinition(
                strategy.Value,
                candidates ?? [],
                rules ?? [],
                defaults ?? [],
                allowedProviders,
                maxRequestCost,
                defaultMaxOutput,
                maxCandidates,
                execution);
        }

        private static bool HasProviderAndModel(string text)
        {
            var slash = text.IndexOf('/', StringComparison.Ordinal);
            return slash > 0 && slash < text.Length - 1;
        }

        private static bool IsEmptyArray(JsonElement root, string name)
            => root.GetProperty(name) is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() == 0;

        private static bool Has(JsonElement element, string name) => element.TryGetProperty(name, out _);

        private static string ProviderOf(string reference) => reference[..reference.IndexOf('/', StringComparison.Ordinal)];

        private void RejectUnknown(JsonElement element, string path, string[] known)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!known.Contains(property.Name, StringComparer.Ordinal))
                {
                    Add(Join(path, property.Name), "Unknown property.");
                }
            }
        }

        private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

        private PolicyStrategy? ReadStrategy(JsonElement root)
        {
            if (!Has(root, "strategy"))
            {
                Add("strategy", "Required. One of: static, rules, cost.");
                return null;
            }

            var value = root.GetProperty("strategy");
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            switch (text)
            {
                case "static":
                    return PolicyStrategy.Static;
                case "rules":
                    return PolicyStrategy.Rules;
                case "cost":
                    return PolicyStrategy.Cost;
                default:
                    Add("strategy", "Must be one of: static, rules, cost.");
                    return null;
            }
        }

        private void ApplyStrategyRules(PolicyStrategy? strategy, JsonElement root)
        {
            switch (strategy)
            {
                case PolicyStrategy.Static or PolicyStrategy.Cost:
                    var name = strategy == PolicyStrategy.Static ? "static" : "cost";
                    if (!Has(root, "candidates"))
                    {
                        Add("candidates", $"Required for strategy '{name}'.");
                    }
                    else if (IsEmptyArray(root, "candidates"))
                    {
                        Add("candidates", "At least one candidate is required.");
                    }

                    foreach (var unused in _ruleOnlyKeys.Where(k => Has(root, k)))
                    {
                        Add(unused, $"Not used by strategy '{name}'. Remove it or use strategy 'rules'.");
                    }

                    break;
                case PolicyStrategy.Rules:
                    if (!Has(root, "rules"))
                    {
                        Add("rules", "Required for strategy 'rules'.");
                    }

                    if (!Has(root, "default"))
                    {
                        Add("default", "Required for strategy 'rules'.");
                    }
                    else if (IsEmptyArray(root, "default"))
                    {
                        Add("default", "At least one candidate is required.");
                    }

                    break;
                default:
                    break;
            }
        }

        private List<Guid>? ReadCandidates(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                Add(path, "Must be an array of '<providerName>/<providerModelId>' strings.");
                return null;
            }

            var ids = new List<Guid>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var itemPath = $"{path}[{index++}]";
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (text is null || WhitespaceRegex().IsMatch(text) || !HasProviderAndModel(text))
                {
                    Add(itemPath, "Must be '<providerName>/<providerModelId>'.");
                    continue;
                }

                if (!seen.Add(text))
                {
                    Add(itemPath, $"Duplicate candidate '{text}'.");
                    continue;
                }

                if (!references.Models.TryGetValue(text, out var id))
                {
                    Add(itemPath, $"Unknown model '{text}'.");
                    continue;
                }

                _resolved[text] = id;
                ids.Add(id);
            }

            return ids;
        }

        private List<PolicyRule>? ReadRules(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                Add("rules", "Must be an array.");
                return null;
            }

            if (element.GetArrayLength() == 0)
            {
                Add("rules", "At least one rule is required for strategy 'rules'.");
                return [];
            }

            var rules = new List<PolicyRule>();
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var path = $"rules[{index++}]";
                if (item.ValueKind != JsonValueKind.Object)
                {
                    Add(path, "Must be an object.");
                    continue;
                }

                RejectUnknown(item, path, RuleKeys);
                var (taskTypes, requires, whenOk) = Has(item, "when") ? ReadWhen(item.GetProperty("when"), $"{path}.when") : Missing($"{path}.when");
                List<Guid>? candidates = null;
                if (Has(item, "candidates"))
                {
                    candidates = ReadCandidates(item.GetProperty("candidates"), $"{path}.candidates");
                    if (candidates is { Count: 0 } && item.GetProperty("candidates").ValueKind == JsonValueKind.Array)
                    {
                        Add($"{path}.candidates", "At least one candidate is required.");
                    }
                }
                else
                {
                    Add($"{path}.candidates", "Required.");
                }

                if (whenOk && candidates is not null)
                {
                    rules.Add(new PolicyRule(taskTypes, requires, candidates));
                }
            }

            CheckReachability(rules);
            return rules;
        }

        private (List<string>?, List<string>, bool) Missing(string path)
        {
            Add(path, "Required.");
            return (null, [], false);
        }

        private (List<string>? TaskTypes, List<string> Requires, bool Ok) ReadWhen(JsonElement when, string path)
        {
            if (when.ValueKind != JsonValueKind.Object)
            {
                Add(path, "Must be an object.");
                return (null, [], false);
            }

            RejectUnknown(when, path, WhenKeys);
            var ok = true;
            List<string>? taskTypes = null;
            var requires = new List<string>();

            if (Has(when, "taskType"))
            {
                taskTypes = ReadStrings(when.GetProperty("taskType"), $"{path}.taskType", TaskTypeRegex());
                ok &= taskTypes is not null;
            }

            if (Has(when, "requires"))
            {
                var read = ReadStrings(when.GetProperty("requires"), $"{path}.requires", CapabilityRegex());
                ok &= read is not null;
                requires = read ?? [];
            }

            if (!Has(when, "taskType") && !Has(when, "requires"))
            {
                Add(path, "At least one condition (taskType or requires) is required.");
                ok = false;
            }

            return (taskTypes, requires, ok);
        }

        private List<string>? ReadStrings(JsonElement element, string path, Regex pattern)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                Add(path, "Must be an array of strings.");
                return null;
            }

            var values = new List<string>();
            var ok = true;
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var itemPath = $"{path}[{index++}]";
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (text is null || !pattern.IsMatch(text))
                {
                    Add(itemPath, "Invalid value.");
                    ok = false;
                }
                else if (values.Contains(text, StringComparer.Ordinal))
                {
                    Add(itemPath, $"Duplicate value '{text}'.");
                    ok = false;
                }
                else
                {
                    values.Add(text);
                }
            }

            if (ok && values.Count == 0)
            {
                Add(path, "At least one value is required.");
                return null;
            }

            return ok ? values : null;
        }

        // A rule can never match when an earlier rule matches every request it would match: the earlier rule's
        // conditions are all implied by this rule's conditions.
        private void CheckReachability(List<PolicyRule> rules)
        {
            for (var later = 1; later < rules.Count; later++)
            {
                for (var earlier = 0; earlier < later; earlier++)
                {
                    if (Shadows(rules[earlier], rules[later]))
                    {
                        Add($"rules[{later}].when", $"Unreachable: rules[{earlier}] already matches every request this rule would match.");
                        break;
                    }
                }
            }
        }

        private static bool Shadows(PolicyRule earlier, PolicyRule later)
        {
            var taskImplied = earlier.TaskTypes is null
                || (later.TaskTypes is not null && later.TaskTypes.All(t => earlier.TaskTypes.Contains(t, StringComparer.Ordinal)));
            var requiresImplied = earlier.Requires.All(r => later.Requires.Contains(r, StringComparer.Ordinal));
            return taskImplied && requiresImplied;
        }

        private HashSet<string>? ReadAllowedProviders(JsonElement root)
        {
            if (!Has(root, "allowedProviders") || root.GetProperty("allowedProviders").ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var element = root.GetProperty("allowedProviders");
            if (element.ValueKind != JsonValueKind.Array)
            {
                Add("allowedProviders", "Must be an array of provider names or null.");
                return null;
            }

            var set = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var path = $"allowedProviders[{index++}]";
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (string.IsNullOrEmpty(text))
                {
                    Add(path, "Must be a provider name.");
                }
                else if (!references.ProviderNames.Contains(text))
                {
                    Add(path, $"Unknown provider '{text}'.");
                }
                else if (!set.Add(text))
                {
                    Add(path, $"Duplicate provider '{text}'.");
                }
            }

            return set;
        }

        private decimal? ReadMaxRequestCost(JsonElement root)
        {
            if (!Has(root, "maxRequestCost") || root.GetProperty("maxRequestCost").ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var element = root.GetProperty("maxRequestCost");
            var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (text is null || !DecimalRegex().IsMatch(text))
            {
                Add("maxRequestCost", "Must be a decimal string such as \"0.05\", or null.");
                return null;
            }

            return decimal.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }

        private int ReadInt(JsonElement parent, string name, string path, int min, int fallback)
        {
            if (!Has(parent, name))
            {
                return fallback;
            }

            var element = parent.GetProperty(name);
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            {
                Add(path, "Must be an integer.");
                return fallback;
            }

            if (value < min)
            {
                Add(path, $"Must be at least {min}.");
                return fallback;
            }

            return value;
        }

        private ExecutionSettings ReadExecution(JsonElement root)
        {
            var defaults = ExecutionSettings.Default;
            if (!Has(root, "execution"))
            {
                return defaults;
            }

            var element = root.GetProperty("execution");
            if (element.ValueKind != JsonValueKind.Object)
            {
                Add("execution", "Must be an object.");
                return defaults;
            }

            RejectUnknown(element, "execution", ExecutionKeys);
            var deadline = ReadInt(element, "deadlineMs", "execution.deadlineMs", 1, defaults.DeadlineMs);
            var attemptTimeout = ReadInt(element, "attemptTimeoutMs", "execution.attemptTimeoutMs", 1, defaults.AttemptTimeoutMs);
            var maxAttempts = ReadInt(element, "maxAttempts", "execution.maxAttempts", 1, defaults.MaxAttempts);
            var retries = ReadInt(element, "maxRetriesPerCandidate", "execution.maxRetriesPerCandidate", 0, defaults.MaxRetriesPerCandidate);
            var backoff = ReadBackoff(element, defaults.Backoff);

            if (Has(element, "deadlineMs") && Has(element, "attemptTimeoutMs") && attemptTimeout > deadline)
            {
                Add("execution.attemptTimeoutMs", "Must not exceed execution.deadlineMs.");
            }

            return new ExecutionSettings(deadline, attemptTimeout, maxAttempts, retries, backoff);
        }

        private BackoffSettings ReadBackoff(JsonElement execution, BackoffSettings defaults)
        {
            if (!Has(execution, "backoff"))
            {
                return defaults;
            }

            var element = execution.GetProperty("backoff");
            if (element.ValueKind != JsonValueKind.Object)
            {
                Add("execution.backoff", "Must be an object.");
                return defaults;
            }

            RejectUnknown(element, "execution.backoff", BackoffKeys);
            var baseMs = ReadInt(element, "baseMs", "execution.backoff.baseMs", 0, defaults.BaseMs);
            var maxMs = ReadInt(element, "maxMs", "execution.backoff.maxMs", 0, defaults.MaxMs);
            var fullJitter = defaults.FullJitter;
            if (Has(element, "jitter"))
            {
                var jitter = element.GetProperty("jitter");
                var text = jitter.ValueKind == JsonValueKind.String ? jitter.GetString() : null;
                if (text is "full" or "none")
                {
                    fullJitter = text == "full";
                }
                else
                {
                    Add("execution.backoff.jitter", "Must be 'full' or 'none'.");
                }
            }

            if (baseMs > maxMs)
            {
                Add("execution.backoff.baseMs", "Must not exceed execution.backoff.maxMs.");
            }

            return new BackoffSettings(baseMs, maxMs, fullJitter);
        }
    }

    [GeneratedRegex(@"\s")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("^[a-z0-9_-]{1,64}$")]
    private static partial Regex TaskTypeRegex();

    [GeneratedRegex("^.{1,64}$")]
    private static partial Regex CapabilityRegex();

    [GeneratedRegex(@"^[0-9]+(\.[0-9]{1,10})?$")]
    private static partial Regex DecimalRegex();
}
