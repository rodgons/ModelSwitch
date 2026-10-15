using System.Text.Json;
using ModelSwitch.Routing.Application;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Routing;

public sealed class PolicyDefinitionValidatorTests
{
    private static readonly Guid _mini = new("0192f0c8-0000-7000-8000-000000000001");
    private static readonly Guid _full = new("0192f0c8-0000-7000-8000-000000000002");
    private static readonly Guid _llama = new("0192f0c8-0000-7000-8000-000000000003");

    private static readonly PolicyReferenceData _references = new(
        new Dictionary<string, Guid>
        {
            ["openai-main/gpt-4.1-mini"] = _mini,
            ["openai-main/gpt-4.1"] = _full,
            ["ollama-local/llama3.1:8b"] = _llama,
        },
        new HashSet<string> { "openai-main", "ollama-local" });

    private readonly PolicyDefinitionValidator _validator = new();

    private const string _documentExample = """
        {
          "strategy": "rules",
          "candidates": ["openai-main/gpt-4.1-mini", "ollama-local/llama3.1:8b"],
          "rules": [
            { "when": { "taskType": ["extraction", "classification"] },
              "candidates": ["openai-main/gpt-4.1-mini", "openai-main/gpt-4.1"] },
            { "when": { "requires": ["jsonSchema"] },
              "candidates": ["openai-main/gpt-4.1"] }
          ],
          "default": ["ollama-local/llama3.1:8b", "openai-main/gpt-4.1-mini"],
          "allowedProviders": null,
          "maxRequestCost": "0.05",
          "defaultMaxOutputTokens": 1024,
          "maxCandidates": 3,
          "execution": {
            "deadlineMs": 60000,
            "attemptTimeoutMs": 30000,
            "maxAttempts": 4,
            "maxRetriesPerCandidate": 1,
            "backoff": { "baseMs": 200, "maxMs": 2000, "jitter": "full" }
          }
        }
        """;

    private PolicyValidationResult Validate(string json) => _validator.Validate(json, _references);

    private static string Static(string extra = "") => $$"""{ "strategy": "static", "candidates": ["openai-main/gpt-4.1-mini"] {{extra}} }""";

    [Fact]
    public void The_documented_example_is_valid_and_resolves_candidates_to_ids()
    {
        var result = Validate(_documentExample);

        result.Errors.ShouldBeEmpty();
        var definition = result.Definition!;
        definition.Strategy.ShouldBe(PolicyStrategy.Rules);
        definition.Candidates.ShouldBe([_mini, _llama]);
        definition.Rules[0].TaskTypes.ShouldBe(["extraction", "classification"]);
        definition.Rules[0].Candidates.ShouldBe([_mini, _full]);
        definition.Rules[1].TaskTypes.ShouldBeNull();
        definition.Rules[1].Requires.ShouldBe(["jsonSchema"]);
        definition.Default.ShouldBe([_llama, _mini]);
        definition.AllowedProviders.ShouldBeNull();
        definition.MaxRequestCost.ShouldBe(0.05m);
        definition.DefaultMaxOutputTokens.ShouldBe(1024);
        definition.MaxCandidates.ShouldBe(3);
        definition.Execution.ShouldBe(ExecutionSettings.Default);
        result.ResolvedCandidates.Keys.ShouldBe(
            ["openai-main/gpt-4.1-mini", "ollama-local/llama3.1:8b", "openai-main/gpt-4.1"], ignoreOrder: true);
        result.ToError().ShouldBeNull();
    }

    [Fact]
    public void Defaults_apply_for_omitted_optional_fields()
    {
        var definition = Validate(Static()).Definition!;

        definition.MaxRequestCost.ShouldBeNull();
        definition.MaxCandidates.ShouldBeNull();
        definition.AllowedProviders.ShouldBeNull();
        definition.DefaultMaxOutputTokens.ShouldBe(1024);
        definition.Execution.ShouldBe(ExecutionSettings.Default);
    }

    [Fact]
    public void Execution_overrides_are_read()
    {
        var definition = Validate(Static("""
            , "execution": { "deadlineMs": 5000, "attemptTimeoutMs": 2000, "maxAttempts": 2, "maxRetriesPerCandidate": 0,
                             "backoff": { "baseMs": 10, "maxMs": 20, "jitter": "none" } }
            """)).Definition!;

        definition.Execution.ShouldBe(new ExecutionSettings(5000, 2000, 2, 0, new BackoffSettings(10, 20, FullJitter: false)));
    }

    [Theory]
    [InlineData("not json", "")]
    [InlineData("[]", "")]
    [InlineData("""{ "candidates": ["openai-main/gpt-4.1"] }""", "strategy")]
    [InlineData("""{ "strategy": "fastest", "candidates": ["openai-main/gpt-4.1"] }""", "strategy")]
    [InlineData("""{ "strategy": 3, "candidates": ["openai-main/gpt-4.1"] }""", "strategy")]
    [InlineData("""{ "strategy": "static" }""", "candidates")]
    [InlineData("""{ "strategy": "static", "candidates": [] }""", "candidates")]
    [InlineData("""{ "strategy": "cost" }""", "candidates")]
    [InlineData("""{ "strategy": "cost", "candidates": [] }""", "candidates")]
    [InlineData("""{ "strategy": "static", "candidates": "openai-main/gpt-4.1" }""", "candidates")]
    [InlineData("""{ "strategy": "static", "candidates": ["openai-main/gpt-4.1"], "bogus": 1 }""", "bogus")]
    [InlineData("""{ "strategy": "static", "candidates": ["openai-main/gpt-4.1"], "default": ["openai-main/gpt-4.1"] }""", "default")]
    [InlineData("""{ "strategy": "static", "candidates": ["openai-main/gpt-4.1"], "rules": [] }""", "rules")]
    public void Structural_errors_are_reported_at_their_path(string json, string path)
    {
        var errors = Validate(json).Errors;

        errors.ShouldContain(e => e.Path == path, $"{json} -> {string.Join("; ", errors.Select(e => e.Path + ": " + e.Message))}");
    }

    [Theory]
    [InlineData("""["openai-main/gpt-4.1", "openai-main/gpt-4.1"]""", "candidates[1]", "Duplicate")]
    [InlineData("""["openai-main/missing"]""", "candidates[0]", "Unknown model")]
    [InlineData("""["gpt-4.1"]""", "candidates[0]", "providerName")]
    [InlineData("""["openai-main/"]""", "candidates[0]", "providerName")]
    [InlineData("""["/gpt"]""", "candidates[0]", "providerName")]
    [InlineData("""["openai-main/gpt 4"]""", "candidates[0]", "providerName")]
    [InlineData("""[7]""", "candidates[0]", "providerName")]
    public void Candidate_references_must_be_unique_well_formed_and_known(string candidates, string path, string messagePart)
    {
        var errors = Validate($$"""{ "strategy": "static", "candidates": {{candidates}} }""").Errors;

        errors.ShouldContain(e => e.Path == path && e.Message.Contains(messagePart, StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_strategy_requires_rules_and_a_default()
    {
        var errors = Validate("""{ "strategy": "rules" }""").Errors.Select(e => e.Path).ToList();

        errors.ShouldContain("rules");
        errors.ShouldContain("default");
    }

    [Fact]
    public void Rules_strategy_rejects_an_empty_rules_array_and_an_empty_default()
    {
        var errors = Validate("""{ "strategy": "rules", "rules": [], "default": [] }""").Errors.Select(e => e.Path).ToList();

        errors.ShouldContain("rules");
        errors.ShouldContain("default");
    }

    [Fact]
    public void Rules_strategy_does_not_need_the_top_level_candidates()
    {
        Validate("""
            { "strategy": "rules", "rules": [ { "when": { "taskType": ["chat"] }, "candidates": ["openai-main/gpt-4.1"] } ],
              "default": ["openai-main/gpt-4.1-mini"] }
            """).Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{ "candidates": ["openai-main/gpt-4.1"] }""", "rules[0].when")]
    [InlineData("""{ "when": {}, "candidates": ["openai-main/gpt-4.1"] }""", "rules[0].when")]
    [InlineData("""{ "when": { "taskType": ["chat"] } }""", "rules[0].candidates")]
    [InlineData("""{ "when": { "taskType": ["chat"] }, "candidates": [] }""", "rules[0].candidates")]
    [InlineData("""{ "when": { "taskType": [] }, "candidates": ["openai-main/gpt-4.1"] }""", "rules[0].when.taskType")]
    [InlineData("""{ "when": { "taskType": ["Not Valid"] }, "candidates": ["openai-main/gpt-4.1"] }""", "rules[0].when.taskType[0]")]
    [InlineData("""{ "when": { "taskType": ["a", "a"] }, "candidates": ["openai-main/gpt-4.1"] }""", "rules[0].when.taskType[1]")]
    [InlineData("""{ "when": { "requires": [""] }, "candidates": ["openai-main/gpt-4.1"] }""", "rules[0].when.requires[0]")]
    [InlineData("""{ "when": { "colour": ["red"] }, "candidates": ["openai-main/gpt-4.1"] }""", "rules[0].when.colour")]
    [InlineData("""{ "when": { "taskType": ["a"] }, "candidates": ["openai-main/gpt-4.1"], "extra": 1 }""", "rules[0].extra")]
    [InlineData("""{ "when": { "taskType": ["a"] }, "candidates": ["openai-main/nope"] }""", "rules[0].candidates[0]")]
    public void Rule_entries_are_validated(string rule, string path)
    {
        var errors = Validate($$"""{ "strategy": "rules", "rules": [ {{rule}} ], "default": ["openai-main/gpt-4.1-mini"] }""").Errors;

        errors.ShouldContain(e => e.Path == path, string.Join("; ", errors.Select(e => e.Path + ": " + e.Message)));
    }

    public static TheoryData<string, string, int> UnreachableCases() => new()
    {
        // identical conditions
        { """{ "taskType": ["a"] }""", """{ "taskType": ["a"] }""", 1 },
        // a later rule narrower than an earlier one on task type
        { """{ "taskType": ["a", "b"] }""", """{ "taskType": ["b"] }""", 1 },
        // an earlier requires-only rule shadows a later rule that requires more
        { """{ "requires": ["jsonSchema"] }""", """{ "requires": ["jsonSchema", "seed"] }""", 1 },
        // an earlier task-only rule shadows a later rule with the same task and an extra requirement
        { """{ "taskType": ["a"] }""", """{ "taskType": ["a"], "requires": ["jsonObject"] }""", 1 },
        // an earlier requires-only rule shadows any later task-typed rule that requires the same
        { """{ "requires": ["seed"] }""", """{ "taskType": ["a"], "requires": ["seed"] }""", 1 },
    };

    [Theory]
    [MemberData(nameof(UnreachableCases))]
    public void A_rule_that_an_earlier_rule_always_pre_empts_is_unreachable(string earlier, string later, int unreachableIndex)
    {
        var errors = Validate($$"""
            { "strategy": "rules",
              "rules": [ { "when": {{earlier}}, "candidates": ["openai-main/gpt-4.1"] },
                         { "when": {{later}}, "candidates": ["openai-main/gpt-4.1-mini"] } ],
              "default": ["ollama-local/llama3.1:8b"] }
            """).Errors;

        errors.ShouldContain(e => e.Path == $"rules[{unreachableIndex}].when" && e.Message.Contains("Unreachable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "taskType": ["a"] }""", """{ "taskType": ["b"] }""")]
    [InlineData("""{ "taskType": ["a"], "requires": ["jsonSchema"] }""", """{ "taskType": ["a"] }""")] // narrower first is fine
    [InlineData("""{ "taskType": ["a"] }""", """{ "requires": ["jsonSchema"] }""")]
    [InlineData("""{ "requires": ["seed"] }""", """{ "taskType": ["a"] }""")]
    public void Rules_that_can_each_match_something_the_earlier_ones_do_not_are_reachable(string earlier, string later)
    {
        Validate($$"""
            { "strategy": "rules",
              "rules": [ { "when": {{earlier}}, "candidates": ["openai-main/gpt-4.1"] },
                         { "when": {{later}}, "candidates": ["openai-main/gpt-4.1-mini"] } ],
              "default": ["ollama-local/llama3.1:8b"] }
            """).Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""null""", true)]
    [InlineData("""["openai-main"]""", true)]
    [InlineData("""["nobody"]""", false)]
    [InlineData("""["openai-main", "openai-main"]""", false)]
    [InlineData("""[""]""", false)]
    [InlineData("""[7]""", false)]
    [InlineData("""{}""", false)]
    public void Allowed_providers_must_be_known_unique_names(string value, bool valid)
    {
        var result = Validate(Static($$""", "allowedProviders": {{value}}"""));

        result.IsValid.ShouldBe(valid, string.Join("; ", result.Errors.Select(e => e.Path + ": " + e.Message)));
    }

    [Fact]
    public void Allowed_providers_that_exclude_every_candidate_is_an_error()
    {
        var errors = Validate(Static(""", "allowedProviders": ["ollama-local"]""")).Errors;

        errors.ShouldContain(e => e.Path == "allowedProviders" && e.Message.Contains("No candidate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"0.05\"", true)]
    [InlineData("\"5\"", true)]
    [InlineData("\"0\"", true)]
    [InlineData("null", true)]
    [InlineData("0.05", false)]
    [InlineData("\"-1\"", false)]
    [InlineData("\"abc\"", false)]
    [InlineData("\"1e3\"", false)]
    [InlineData("\"0.00000000001\"", false)]
    public void Max_request_cost_is_an_exact_decimal_string(string value, bool valid)
    {
        Validate(Static($$""", "maxRequestCost": {{value}}""")).IsValid.ShouldBe(valid);
    }

    [Theory]
    [InlineData("defaultMaxOutputTokens", "0", "defaultMaxOutputTokens")]
    [InlineData("defaultMaxOutputTokens", "1.5", "defaultMaxOutputTokens")]
    [InlineData("defaultMaxOutputTokens", "\"x\"", "defaultMaxOutputTokens")]
    [InlineData("maxCandidates", "0", "maxCandidates")]
    [InlineData("maxCandidates", "\"3\"", "maxCandidates")]
    [InlineData("execution", "{ \"maxAttempts\": 0 }", "execution.maxAttempts")]
    [InlineData("execution", "{ \"maxAttempts\": \"4\" }", "execution.maxAttempts")]
    [InlineData("execution", "{ \"deadlineMs\": 0 }", "execution.deadlineMs")]
    [InlineData("execution", "{ \"attemptTimeoutMs\": 0 }", "execution.attemptTimeoutMs")]
    [InlineData("execution", "{ \"maxRetriesPerCandidate\": -1 }", "execution.maxRetriesPerCandidate")]
    [InlineData("execution", "{ \"deadlineMs\": 1000, \"attemptTimeoutMs\": 2000 }", "execution.attemptTimeoutMs")]
    [InlineData("execution", "{ \"backoff\": { \"baseMs\": 500, \"maxMs\": 100 } }", "execution.backoff.baseMs")]
    [InlineData("execution", "{ \"backoff\": { \"jitter\": \"equal\" } }", "execution.backoff.jitter")]
    [InlineData("execution", "{ \"backoff\": { \"baseMs\": -1 } }", "execution.backoff.baseMs")]
    [InlineData("execution", "{ \"backoff\": 5 }", "execution.backoff")]
    [InlineData("execution", "{ \"timeout\": 5 }", "execution.timeout")]
    [InlineData("execution", "{ \"backoff\": { \"x\": 5 } }", "execution.backoff.x")]
    [InlineData("execution", "[]", "execution")]
    public void Numeric_and_execution_settings_are_range_checked(string field, string value, string path)
    {
        var errors = Validate(Static($$""", "{{field}}": {{value}}""")).Errors;

        errors.ShouldContain(e => e.Path == path, string.Join("; ", errors.Select(e => e.Path + ": " + e.Message)));
    }

    [Fact]
    public void Max_attempts_of_one_is_valid()
    {
        Validate(Static(""", "execution": { "maxAttempts": 1 }""")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void All_problems_are_reported_together_not_just_the_first()
    {
        var result = Validate("""
            { "strategy": "static", "candidates": ["openai-main/nope"], "maxCandidates": 0,
              "execution": { "maxAttempts": 0 } }
            """);

        result.Errors.Select(e => e.Path).ShouldBe(["candidates[0]", "maxCandidates", "execution.maxAttempts"], ignoreOrder: true);
    }

    [Fact]
    public void To_error_is_validation_failed_with_one_entry_per_path()
    {
        var error = Validate("""{ "strategy": "static", "candidates": ["x/y", "x/y"], "execution": { "maxAttempts": 0 } }""").ToError()!;

        error.Code.ShouldBe(ErrorCode.ValidationFailed);
        error.FieldErrors!.Keys.ShouldBe(["candidates[0]", "candidates[1]", "execution.maxAttempts"], ignoreOrder: true);
    }

    [Fact]
    public void An_invalid_definition_has_no_parsed_definition()
    {
        Validate("""{ "strategy": "static", "candidates": ["openai-main/nope"] }""").Definition.ShouldBeNull();
    }

    // ---- the committed JSON Schema must describe the same shape the validator enforces ----

    [Fact]
    public void Embedded_schema_is_valid_json_and_lists_the_same_properties_as_the_validator()
    {
        using var stream = typeof(PolicyDefinitionValidator).Assembly.GetManifestResourceStream("ModelSwitch.Routing.PolicyDefinition.schema.json");
        stream.ShouldNotBeNull();
        using var schema = JsonDocument.Parse(stream);
        var root = schema.RootElement;

        Keys(root.GetProperty("properties")).ShouldBe(PolicyDefinitionValidator.RootKeys, ignoreOrder: true);
        var rule = root.GetProperty("properties").GetProperty("rules").GetProperty("items");
        Keys(rule.GetProperty("properties")).ShouldBe(PolicyDefinitionValidator.RuleKeys, ignoreOrder: true);
        Keys(rule.GetProperty("properties").GetProperty("when").GetProperty("properties")).ShouldBe(PolicyDefinitionValidator.WhenKeys, ignoreOrder: true);
        var execution = root.GetProperty("properties").GetProperty("execution");
        Keys(execution.GetProperty("properties")).ShouldBe(PolicyDefinitionValidator.ExecutionKeys, ignoreOrder: true);
        Keys(execution.GetProperty("properties").GetProperty("backoff").GetProperty("properties")).ShouldBe(PolicyDefinitionValidator.BackoffKeys, ignoreOrder: true);
        root.GetProperty("properties").GetProperty("strategy").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ShouldBe(["static", "rules", "cost"]);
    }

    private static string[] Keys(JsonElement properties) => properties.EnumerateObject().Select(p => p.Name).ToArray();
}
