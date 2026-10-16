using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Templates;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class PromptTemplateTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static PromptTemplate Template(string json) => PromptTemplate.Parse(Json(json)).Value;

    [Fact]
    public void A_valid_template_parses_its_messages_and_response_format()
    {
        var template = Template("""
            {"messages":[{"role":"system","content":"Reply with JSON."},{"role":"user","content":"{{text}}"}],
             "responseFormat":{"type":"json_object"}}
            """);

        template.Messages.Count.ShouldBe(2);
        template.Messages[1].ShouldBe(new PromptMessage("user", "{{text}}"));
        template.ResponseFormat!.Value.GetProperty("type").GetString().ShouldBe("json_object");
        template.Variables.ShouldBe(["text"]);
    }

    [Theory]
    [InlineData("""{"messages":[]}""", "messages")]
    [InlineData("""{}""", "messages")]
    [InlineData("""{"messages":[{"role":"robot","content":"x"}]}""", "messages[0].role")]
    [InlineData("""{"messages":[{"role":"user"}]}""", "messages[0].content")]
    [InlineData("""{"messages":[{"role":"user","content":"x"}],"responseFormat":"json"}""", "responseFormat")]
    [InlineData("""{"messages":[{"role":"user","content":"x"}],"extra":1}""", "extra")]
    [InlineData("""[]""", "")]
    public void An_invalid_template_is_a_validation_error_keyed_by_path(string json, string path)
    {
        var result = PromptTemplate.Parse(Json(json));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        result.Error.FieldErrors!.Keys.ShouldContain(path);
    }

    [Fact]
    public void Variables_are_substituted_as_plain_text()
    {
        var template = Template("""{"messages":[{"role":"user","content":"Hi {{name}}, about {{ topic }}: {{name}}."}]}""");

        var rendered = PromptRenderer.Render(template, Json("""{"name":"Ana","topic":"billing"}"""));

        rendered.IsSuccess.ShouldBeTrue();
        rendered.Prompt!.Messages[0].Content.ShouldBe("Hi Ana, about billing: Ana.");
    }

    [Fact]
    public void Non_string_values_render_as_compact_json_and_strings_render_raw()
    {
        var template = Template("""{"messages":[{"role":"user","content":"{{n}}|{{b}}|{{o}}|{{a}}|{{z}}|{{s}}"}]}""");

        var rendered = PromptRenderer.Render(template, Json("""{"n":4.50,"b":true,"o":{ "k" : "v" },"a":[1, 2],"z":null,"s":"say \"hi\""}"""));

        rendered.Prompt!.Messages[0].Content.ShouldBe("4.50|true|{\"k\":\"v\"}|[1,2]|null|say \"hi\"");
    }

    [Fact]
    public void A_missing_variable_is_a_case_error_not_an_empty_string()
    {
        var template = Template("""{"messages":[{"role":"user","content":"{{text}} {{lang}}"}]}""");

        var rendered = PromptRenderer.Render(template, Json("""{"text":"hello"}"""));

        rendered.IsSuccess.ShouldBeFalse();
        rendered.Error!.Code.ShouldBe("template_variable_missing");
        rendered.Error.Message.ShouldContain("lang");
    }

    [Fact]
    public void Substituted_values_are_not_scanned_again_for_variables()
    {
        var template = Template("""{"messages":[{"role":"user","content":"{{text}}"}]}""");

        var rendered = PromptRenderer.Render(template, Json("""{"text":"{{secret}}"}"""));

        rendered.Prompt!.Messages[0].Content.ShouldBe("{{secret}}");
    }

    [Fact]
    public void Braces_that_do_not_hold_a_variable_name_are_left_as_written()
    {
        var template = Template("""{"messages":[{"role":"user","content":"{{ }} {{a-b}} {single} {{x}}"}]}""");

        var rendered = PromptRenderer.Render(template, Json("""{"x":"1"}"""));

        rendered.Prompt!.Messages[0].Content.ShouldBe("{{ }} {{a-b}} {single} 1");
    }

    [Fact]
    public void The_response_format_passes_through_unchanged()
    {
        var template = Template("""{"messages":[{"role":"user","content":"x"}],"responseFormat":{"type":"json_schema","schema":{"type":"object"}}}""");

        var rendered = PromptRenderer.Render(template, Json("{}"));

        rendered.Prompt!.ResponseFormat!.Value.GetRawText().ShouldBe("""{"type":"json_schema","schema":{"type":"object"}}""");
    }
}
