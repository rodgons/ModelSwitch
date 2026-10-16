using System.Text.Json;
using ModelSwitch.Evaluation.Domain;
using ModelSwitch.Evaluation.Domain.Datasets;
using ModelSwitch.Evaluation.Domain.Prompts;
using Shouldly;

namespace ModelSwitch.UnitTests.Evaluation;

/// <summary>The Prompt Version template rules of evaluation.md section 2.</summary>
public sealed class PromptTemplateRulesTests
{
    private static readonly PromptMessageInput[] _valid = [new("system", "Extract the name."), new("user", "{{text}}")];

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static IReadOnlyDictionary<string, string[]> Errors(IReadOnlyList<PromptMessageInput?>? messages, JsonElement? format = null)
    {
        var result = PromptTemplateRules.Validate(messages, format);
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.Value.ShouldBe("validation_failed");
        return result.Error.FieldErrors!;
    }

    [Fact]
    public void A_valid_template_is_stored_as_messages_without_a_response_format()
    {
        var result = PromptTemplateRules.Validate(_valid, null);

        result.Value.ShouldBe("""{"messages":[{"role":"system","content":"Extract the name."},{"role":"user","content":"{{text}}"}]}""");
    }

    [Fact]
    public void A_json_null_response_format_means_none()
    {
        PromptTemplateRules.Validate(_valid, Json("null")).Value.ShouldNotContain("responseFormat");
    }

    [Fact]
    public void A_json_schema_response_format_is_stored_in_canonical_form()
    {
        var result = PromptTemplateRules.Validate(_valid, Json("""{"schema":{"type":"object","properties":{}},"type":"json_schema","name":"person","strict":true}"""));

        result.Value.ShouldEndWith(""","responseFormat":{"name":"person","schema":{"properties":{},"type":"object"},"strict":true,"type":"json_schema"}}""");
    }

    [Theory]
    [InlineData("""{"type":"text"}""")]
    [InlineData("""{"type":"json_object"}""")]
    public void Text_and_json_object_response_formats_are_accepted(string format)
    {
        PromptTemplateRules.Validate(_valid, Json(format)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void At_least_one_message_is_required()
    {
        Errors([]).Keys.ShouldBe(["messages"]);
        Errors(null).Keys.ShouldBe(["messages"]);
    }

    [Fact]
    public void At_most_256_messages_are_allowed()
    {
        Errors([.. Enumerable.Repeat(new PromptMessageInput("user", "x"), PromptTemplateRules.MaxMessages + 1)]).Keys.ShouldBe(["messages"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("developer")]
    [InlineData("tool")]
    [InlineData("User")]
    public void The_role_must_be_system_user_or_assistant(string? role)
    {
        Errors([new PromptMessageInput("user", "a"), new PromptMessageInput(role, "b")]).Keys.ShouldBe(["messages[1].role"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public void The_content_must_not_be_empty(string? content)
    {
        Errors([new PromptMessageInput("assistant", content)]).Keys.ShouldBe(["messages[0].content"]);
    }

    [Fact]
    public void A_null_message_is_rejected()
    {
        Errors([null]).Keys.ShouldBe(["messages[0]"]);
    }

    [Theory]
    [InlineData("\"json\"", "responseFormat")]
    [InlineData("""{}""", "responseFormat.type")]
    [InlineData("""{"type":"yaml"}""", "responseFormat.type")]
    [InlineData("""{"type":"json_schema"}""", "responseFormat.schema")]
    [InlineData("""{"type":"json_schema","schema":"{}"}""", "responseFormat.schema")]
    [InlineData("""{"type":"json_schema","schema":{},"name":"has space"}""", "responseFormat.name")]
    [InlineData("""{"type":"json_schema","schema":{},"strict":"yes"}""", "responseFormat.strict")]
    [InlineData("""{"type":"json_schema","schema":{},"extra":1}""", "responseFormat.extra")]
    [InlineData("""{"type":"text","schema":{}}""", "responseFormat.schema")]
    public void The_response_format_is_checked_field_by_field(string format, string key)
    {
        Errors(_valid, Json(format)).Keys.ShouldBe([key]);
    }
}

public sealed class EvaluationNamingTests
{
    [Theory]
    [InlineData("  churn  ", "churn")]
    [InlineData("Customer extraction v2", "Customer extraction v2")]
    public void A_name_is_trimmed(string name, string expected)
    {
        ResourceNames.Validate(name, out var error).ShouldBe(expected);
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("tab\there")]
    public void A_name_is_required_and_has_no_control_characters(string? name)
    {
        ResourceNames.Validate(name, out var error).ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void A_name_has_at_most_128_characters()
    {
        ResourceNames.Validate(new string('n', 128), out _).ShouldNotBeNull();
        ResourceNames.Validate(new string('n', 129), out var error).ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("extraction", nameof(TaskKind.Extraction))]
    [InlineData("classification", nameof(TaskKind.Classification))]
    [InlineData("free_text", nameof(TaskKind.FreeText))]
    public void Task_kinds_round_trip_through_their_wire_values(string wire, string kind)
    {
        TaskKinds.TryParse(wire, out var parsed).ShouldBeTrue();
        parsed.ToString().ShouldBe(kind);
        TaskKinds.ToWire(parsed).ShouldBe(wire);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Extraction")]
    [InlineData("free-text")]
    public void Other_task_kinds_are_rejected(string? wire)
    {
        TaskKinds.TryParse(wire, out _).ShouldBeFalse();
    }

    [Fact]
    public void Canonical_json_sorts_members_in_ordinal_order_and_keeps_number_literals()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{ "b": 1.50, "B": [true, null], "a": { "y": "é", "x": "q\"t" } }""");

        CanonicalJson.Serialize(document.RootElement).ShouldBe("""{"B":[true,null],"a":{"x":"q\"t","y":"é"},"b":1.50}""");
    }
}
