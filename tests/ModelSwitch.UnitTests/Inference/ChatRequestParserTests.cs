using System.Text.Json;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Application;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

/// <summary>One test per validation rule of POST /v1/chat/completions (docs/api.md section 1).</summary>
public sealed class ChatRequestParserTests
{
    private static readonly InferenceOptions _options = new() { MaxMessages = 3 };

    private static Result<ParsedChatRequest> Parse(string json, InferenceOptions? options = null)
    {
        using var document = JsonDocument.Parse(json);
        return ChatRequestParser.Parse(document.RootElement.Clone(), options ?? _options);
    }

    private static string Body(string extra = "") =>
        "{\"model\":\"balanced\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]" + (extra.Length == 0 ? string.Empty : "," + extra) + "}";

    private static Error Fails(string json, ErrorCode code, string field)
    {
        var result = Parse(json);
        result.IsFailure.ShouldBeTrue($"expected a failure for {json}");
        result.Error.Code.ShouldBe(code);
        result.Error.FieldErrors!.ShouldContainKey(field);
        return result.Error;
    }

    [Fact]
    public void Minimal_valid_request_maps_alias_and_messages()
    {
        var result = Parse(Body());

        result.IsSuccess.ShouldBeTrue();
        result.Value.Alias.ShouldBe("balanced");
        result.Value.Chat.Messages.Single().ShouldBe(new NeutralMessage(MessageRole.User, "hi"));
        result.Value.Chat.MaxOutputTokens.ShouldBeNull();
        result.Value.Chat.ResponseFormat.ShouldBeOfType<ResponseFormat.Text>();
    }

    [Fact]
    public void All_supported_parameters_map_to_the_neutral_request()
    {
        var result = Parse(Body(
            "\"max_completion_tokens\":50,\"temperature\":0.5,\"top_p\":0.9,\"presence_penalty\":1,\"frequency_penalty\":-1," +
            "\"seed\":42,\"stop\":[\"a\",\"b\"],\"user\":\"u-1\",\"n\":1,\"stream\":false,\"logprobs\":false," +
            "\"response_format\":{\"type\":\"json_object\"}"));

        result.IsSuccess.ShouldBeTrue();
        var chat = result.Value.Chat;
        chat.MaxOutputTokens.ShouldBe(50);
        chat.Temperature.ShouldBe(0.5);
        chat.TopP.ShouldBe(0.9);
        chat.PresencePenalty.ShouldBe(1);
        chat.FrequencyPenalty.ShouldBe(-1);
        chat.Seed.ShouldBe(42);
        chat.Stop.ShouldBe(["a", "b"]);
        chat.ResponseFormat.ShouldBeOfType<ResponseFormat.JsonObject>();
    }

    [Fact]
    public void Body_that_is_not_an_object_is_invalid_request()
        => Fails("[1]", ErrorCode.InvalidRequest, "body");

    [Fact]
    public void Model_is_required()
        => Fails("{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", ErrorCode.InvalidRequest, "model");

    [Theory]
    [InlineData("1")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Model_must_be_a_non_empty_string(string model)
        => Fails("{\"model\":" + model + ",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", ErrorCode.InvalidRequest, "model");

    [Fact]
    public void Messages_are_required()
        => Fails("{\"model\":\"m\"}", ErrorCode.InvalidRequest, "messages");

    [Fact]
    public void Messages_must_not_be_empty()
        => Fails("{\"model\":\"m\",\"messages\":[]}", ErrorCode.InvalidRequest, "messages");

    [Fact]
    public void Messages_must_be_an_array()
        => Fails("{\"model\":\"m\",\"messages\":\"hi\"}", ErrorCode.InvalidRequest, "messages");

    [Fact]
    public void Messages_over_the_limit_are_rejected()
        => Fails("{\"model\":\"m\",\"messages\":" + $"[{string.Join(',', Enumerable.Repeat("{\"role\":\"user\",\"content\":\"x\"}", 4))}]" + "}", ErrorCode.InvalidRequest, "messages");

    [Fact]
    public void Messages_at_the_limit_are_accepted()
        => Parse("{\"model\":\"m\",\"messages\":" + $"[{string.Join(',', Enumerable.Repeat("{\"role\":\"user\",\"content\":\"x\"}", 3))}]" + "}").IsSuccess.ShouldBeTrue();

    [Fact]
    public void Message_must_be_an_object()
        => Fails("{\"model\":\"m\",\"messages\":[\"hi\"]}", ErrorCode.InvalidRequest, "messages[0]");

    [Fact]
    public void Message_role_is_required()
        => Fails("{\"model\":\"m\",\"messages\":[{\"content\":\"x\"}]}", ErrorCode.InvalidRequest, "messages[0].role");

    [Fact]
    public void Message_content_is_required()
        => Fails("{\"model\":\"m\",\"messages\":[{\"role\":\"user\"}]}", ErrorCode.InvalidRequest, "messages[0].content");

    [Fact]
    public void Message_content_null_is_invalid()
        => Fails("{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":null}]}", ErrorCode.InvalidRequest, "messages[0].content");

    [Fact]
    public void Unknown_role_is_invalid_request()
        => Fails("{\"model\":\"m\",\"messages\":[{\"role\":\"wizard\",\"content\":\"x\"}]}", ErrorCode.InvalidRequest, "messages[0].role");

    [Theory]
    [InlineData("tool")]
    [InlineData("function")]
    public void Tool_roles_are_unsupported(string role)
        => Fails("{\"model\":\"m\",\"messages\":[{\"role\":\"" + role + "\",\"content\":\"x\"}]}", ErrorCode.UnsupportedParameter, "messages[0].role");

    [Fact]
    public void Developer_role_is_treated_as_system()
    {
        var result = Parse("{\"model\":\"m\",\"messages\":[{\"role\":\"developer\",\"content\":\"x\"}]}");

        result.Value.Chat.Messages.Single().Role.ShouldBe(MessageRole.System);
    }

    [Fact]
    public void Message_tool_calls_are_unsupported()
        => Fails("{\"model\":\"m\",\"messages\":[{\"role\":\"assistant\",\"content\":\"x\",\"tool_calls\":[]}]}", ErrorCode.UnsupportedParameter, "messages[0].tool_calls");

    [Fact]
    public void Text_parts_are_concatenated_in_order()
    {
        var result = Parse("{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}]}");

        result.Value.Chat.Messages.Single().Text.ShouldBe("ab");
    }

    [Theory]
    [InlineData("image_url")]
    [InlineData("input_audio")]
    public void Non_text_content_parts_are_unsupported(string type)
        => Fails("{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"" + type + "\"}]}]}", ErrorCode.UnsupportedParameter, "messages[0].content[0].type");

    [Fact]
    public void Text_part_needs_text()
        => Fails("{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\"}]}]}", ErrorCode.InvalidRequest, "messages[0].content[0].text");

    [Fact]
    public void Content_of_the_wrong_type_is_invalid()
        => Fails("{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":5}]}", ErrorCode.InvalidRequest, "messages[0].content");

    [Theory]
    [InlineData("tools", "[]")]
    [InlineData("tool_choice", "\"auto\"")]
    [InlineData("functions", "[]")]
    [InlineData("function_call", "\"auto\"")]
    [InlineData("n", "2")]
    [InlineData("logprobs", "true")]
    [InlineData("top_logprobs", "3")]
    [InlineData("audio", "{}")]
    [InlineData("modalities", "[\"audio\"]")]
    [InlineData("prediction", "{}")]
    [InlineData("stream", "true")]
    public void Unsupported_parameters_are_rejected(string name, string value)
        => Fails(Body($"\"{name}\":{value}"), ErrorCode.UnsupportedParameter, name);

    [Fact]
    public void Unknown_top_level_fields_are_rejected()
        => Fails(Body("\"frobnicate\":1"), ErrorCode.UnsupportedParameter, "frobnicate");

    [Fact]
    public void Null_for_an_unsupported_parameter_means_not_set()
        => Parse(Body("\"tools\":null,\"n\":null")).IsSuccess.ShouldBeTrue();

    [Fact]
    public void User_is_accepted_and_dropped()
        => Parse(Body("\"user\":\"someone\"")).IsSuccess.ShouldBeTrue();

    [Fact]
    public void User_must_be_a_string()
        => Fails(Body("\"user\":5"), ErrorCode.InvalidRequest, "user");

    [Theory]
    [InlineData("max_tokens", "0")]
    [InlineData("max_tokens", "-1")]
    [InlineData("max_tokens", "1.5")]
    [InlineData("max_tokens", "\"10\"")]
    [InlineData("max_completion_tokens", "0")]
    [InlineData("temperature", "2.1")]
    [InlineData("temperature", "-0.1")]
    [InlineData("temperature", "\"hot\"")]
    [InlineData("top_p", "1.5")]
    [InlineData("presence_penalty", "2.5")]
    [InlineData("frequency_penalty", "-2.5")]
    [InlineData("seed", "1.5")]
    [InlineData("seed", "\"x\"")]
    [InlineData("stop", "[]")]
    [InlineData("stop", "[\"a\",\"b\",\"c\",\"d\",\"e\"]")]
    [InlineData("stop", "[\"\"]")]
    [InlineData("stop", "\"\"")]
    [InlineData("stop", "5")]
    [InlineData("stream", "\"yes\"")]
    [InlineData("stream_options", "5")]
    [InlineData("response_format", "5")]
    public void Out_of_range_or_mistyped_parameters_are_invalid(string name, string value)
        => Fails(Body($"\"{name}\":{value}"), ErrorCode.InvalidRequest, name);

    [Fact]
    public void Unknown_response_format_type_is_invalid()
        => Fails(Body("\"response_format\":{\"type\":\"xml\"}"), ErrorCode.InvalidRequest, "response_format.type");

    [Fact]
    public void Max_tokens_and_max_completion_tokens_must_agree()
        => Fails(Body("\"max_tokens\":5,\"max_completion_tokens\":6"), ErrorCode.InvalidRequest, "max_tokens");

    [Fact]
    public void Max_tokens_alone_sets_the_output_limit()
        => Parse(Body("\"max_tokens\":7")).Value.Chat.MaxOutputTokens.ShouldBe(7);

    [Fact]
    public void Stop_accepts_a_single_string()
        => Parse(Body("\"stop\":\"END\"")).Value.Chat.Stop.ShouldBe(["END"]);

    [Fact]
    public void Stream_options_accepts_include_usage_only()
    {
        Parse(Body("\"stream_options\":{\"include_usage\":true}")).IsSuccess.ShouldBeTrue();
        Fails(Body("\"stream_options\":{\"other\":1}"), ErrorCode.UnsupportedParameter, "stream_options.other");
        Fails(Body("\"stream_options\":{\"include_usage\":1}"), ErrorCode.InvalidRequest, "stream_options.include_usage");
    }

    [Fact]
    public void Json_schema_response_format_is_mapped()
    {
        var result = Parse(Body("\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"r\",\"strict\":true,\"schema\":{\"type\":\"object\"}}}"));

        var schema = result.Value.Chat.ResponseFormat.ShouldBeOfType<ResponseFormat.JsonSchema>();
        schema.Name.ShouldBe("r");
        schema.Strict.ShouldBeTrue();
        schema.Schema.GetProperty("type").GetString().ShouldBe("object");
    }

    [Theory]
    [InlineData("{\"type\":\"json_schema\"}", "response_format.json_schema")]
    [InlineData("{\"type\":\"json_schema\",\"json_schema\":{\"schema\":{}}}", "response_format.json_schema.name")]
    [InlineData("{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"r\"}}", "response_format.json_schema.schema")]
    public void Json_schema_response_format_needs_name_and_schema(string format, string field)
        => Fails(Body($"\"response_format\":{format}"), ErrorCode.InvalidRequest, field);

    [Fact]
    public void Every_problem_is_reported_together_and_unsupported_wins_the_code()
    {
        var error = Parse("{\"model\":5,\"messages\":[],\"tools\":[]}").Error;

        error.Code.ShouldBe(ErrorCode.UnsupportedParameter);
        error.FieldErrors!.Keys.ShouldBe(["model", "messages", "tools"], ignoreOrder: true);
    }

    [Fact]
    public void Error_messages_never_echo_content()
    {
        var error = Parse("{\"model\":\"m\",\"messages\":[{\"role\":\"secret-role-text\",\"content\":\"top-secret-content\"}],\"temperature\":\"top-secret-value\"}").Error;

        var text = string.Join(' ', error.FieldErrors!.Values.SelectMany(v => v)) + error.Message;
        text.ShouldNotContain("secret");
    }
}
