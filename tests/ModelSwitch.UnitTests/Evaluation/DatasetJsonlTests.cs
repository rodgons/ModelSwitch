using System.Text;
using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Datasets;
using ModelSwitch.SharedKernel;
using Shouldly;

namespace ModelSwitch.UnitTests.Evaluation;

/// <summary>The Dataset Version upload rules of evaluation.md section 1.</summary>
public sealed class DatasetJsonlTests
{
    private static Result<ParsedDataset> Parse(string text, TaskKind kind = TaskKind.Classification)
        => DatasetJsonl.Parse(Encoding.UTF8.GetBytes(text), kind);

    private static IReadOnlyDictionary<string, string[]> Errors(Result<ParsedDataset> result)
    {
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.Value.ShouldBe("validation_failed");
        return result.Error.FieldErrors!;
    }

    [Fact]
    public void A_valid_classification_upload_yields_its_cases_in_file_order_with_ordinals_from_zero()
    {
        var result = Parse("""
            {"id":"c-1","input":{"text":"buy now"},"expected":"spam","metadata":{"lang":"en"}}
            {"id":"c-2","input":{"text":"hi mum"},"expected":"ham"}
            """);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Cases.Select(c => (c.ExternalId, c.Ordinal)).ShouldBe([("c-1", 0), ("c-2", 1)]);
        result.Value.Cases[0].MetadataJson.ShouldBe("""{"lang":"en"}""");
        result.Value.Cases[1].MetadataJson.ShouldBeNull();
        result.Value.Cases[0].ExpectedJson.ShouldBe("\"spam\"");
    }

    [Fact]
    public void Blank_lines_anywhere_and_crlf_line_endings_are_ignored()
    {
        var result = Parse("\r\n{\"id\":\"a\",\"input\":{},\"expected\":\"x\"}\r\n\r\n   \n{\"id\":\"b\",\"input\":{},\"expected\":\"y\"}\n\n");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Cases.Count.ShouldBe(2);
    }

    [Fact]
    public void A_utf8_byte_order_mark_is_tolerated()
    {
        Parse("\uFEFF{\"id\":\"a\",\"input\":{},\"expected\":\"x\"}").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Bytes_that_are_not_utf8_reject_the_upload()
    {
        var result = DatasetJsonl.Parse([0x7b, 0xff, 0xfe, 0x7d], TaskKind.Classification);

        Errors(result).Keys.ShouldBe([DatasetJsonl.BodyKey]);
    }

    [Fact]
    public void An_upload_without_cases_is_rejected()
    {
        Errors(Parse("\n  \n")).Keys.ShouldBe([DatasetJsonl.BodyKey]);
    }

    [Fact]
    public void More_than_5000_cases_is_rejected_as_a_whole()
    {
        var text = string.Join('\n', Enumerable.Range(0, DatasetJsonl.MaxCases + 1).Select(i => $"{{\"id\":\"{i}\",\"input\":{{}},\"expected\":\"x\"}}"));

        Errors(Parse(text))[DatasetJsonl.BodyKey].Single().ShouldContain("5000");
    }

    [Fact]
    public void Exactly_5000_cases_is_accepted()
    {
        var text = string.Join('\n', Enumerable.Range(0, DatasetJsonl.MaxCases).Select(i => $"{{\"id\":\"{i}\",\"input\":{{}},\"expected\":\"x\"}}"));

        Parse(text).Value.Cases.Count.ShouldBe(DatasetJsonl.MaxCases);
    }

    [Fact]
    public void Invalid_json_and_non_object_lines_are_reported_by_1_based_line_number()
    {
        var errors = Errors(Parse("{\"id\":\"a\",\"input\":{},\"expected\":\"x\"}\n{not json\n[1,2]"));

        errors.Keys.Order(StringComparer.Ordinal).ShouldBe(["lines[2]", "lines[3]"]);
    }

    [Fact]
    public void A_repeated_field_name_in_a_line_is_rejected()
    {
        Errors(Parse("{\"id\":\"a\",\"id\":\"b\",\"input\":{},\"expected\":\"x\"}")).Keys.ShouldBe(["lines[1]"]);
    }

    [Fact]
    public void Unknown_fields_are_rejected()
    {
        Errors(Parse("{\"id\":\"a\",\"input\":{},\"expected\":\"x\",\"extra\":1}")).Keys.ShouldBe(["lines[1].extra"]);
    }

    [Theory]
    [InlineData("{\"input\":{},\"expected\":\"x\"}")]
    [InlineData("{\"id\":7,\"input\":{},\"expected\":\"x\"}")]
    [InlineData("{\"id\":\"  \",\"input\":{},\"expected\":\"x\"}")]
    public void The_id_is_a_required_non_empty_string(string line)
    {
        Errors(Parse(line)).Keys.ShouldBe(["lines[1].id"]);
    }

    [Fact]
    public void An_id_longer_than_256_characters_is_rejected()
    {
        Errors(Parse($"{{\"id\":\"{new string('a', 257)}\",\"input\":{{}},\"expected\":\"x\"}}")).Keys.ShouldBe(["lines[1].id"]);
    }

    [Fact]
    public void A_duplicate_id_names_the_line_that_used_it_first()
    {
        var errors = Errors(Parse("{\"id\":\"a\",\"input\":{},\"expected\":\"x\"}\n{\"id\":\"b\",\"input\":{},\"expected\":\"x\"}\n{\"id\":\"a\",\"input\":{},\"expected\":\"x\"}"));

        errors.Keys.ShouldBe(["lines[3].id"]);
        errors["lines[3].id"].Single().ShouldContain("line 1");
    }

    [Theory]
    [InlineData("{\"id\":\"a\",\"expected\":\"x\"}")]
    [InlineData("{\"id\":\"a\",\"input\":\"text\",\"expected\":\"x\"}")]
    [InlineData("{\"id\":\"a\",\"input\":[],\"expected\":\"x\"}")]
    public void The_input_is_a_required_object(string line)
    {
        Errors(Parse(line)).Keys.ShouldBe(["lines[1].input"]);
    }

    [Fact]
    public void The_expected_result_is_required()
    {
        Errors(Parse("{\"id\":\"a\",\"input\":{}}")).Keys.ShouldBe(["lines[1].expected"]);
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("{}")]
    [InlineData("[1]")]
    public void An_extraction_case_expects_a_non_empty_object(string expected)
    {
        Errors(Parse($"{{\"id\":\"a\",\"input\":{{}},\"expected\":{expected}}}", TaskKind.Extraction)).Keys.ShouldBe(["lines[1].expected"]);
    }

    [Theory]
    [InlineData("{\"label\":\"x\"}")]
    [InlineData("3")]
    [InlineData("\"\"")]
    [InlineData("\" spam\"")]
    [InlineData("\"spam \"")]
    public void A_classification_case_expects_a_trimmed_non_empty_label(string expected)
    {
        Errors(Parse($"{{\"id\":\"a\",\"input\":{{}},\"expected\":{expected}}}")).Keys.ShouldBe(["lines[1].expected"]);
    }

    [Fact]
    public void A_classification_label_longer_than_256_characters_is_rejected()
    {
        Errors(Parse($"{{\"id\":\"a\",\"input\":{{}},\"expected\":\"{new string('x', 257)}\"}}")).Keys.ShouldBe(["lines[1].expected"]);
    }

    [Theory]
    [InlineData("{\"answer\":\"x\"}")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public void A_free_text_case_expects_a_non_empty_reference_string(string expected)
    {
        Errors(Parse($"{{\"id\":\"a\",\"input\":{{}},\"expected\":{expected}}}", TaskKind.FreeText)).Keys.ShouldBe(["lines[1].expected"]);
    }

    [Fact]
    public void A_free_text_reference_may_have_surrounding_whitespace()
    {
        Parse("{\"id\":\"a\",\"input\":{},\"expected\":\" Paris \"}", TaskKind.FreeText).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("\"en\"")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Metadata_when_present_must_be_an_object(string metadata)
    {
        Errors(Parse($"{{\"id\":\"a\",\"input\":{{}},\"expected\":\"x\",\"metadata\":{metadata}}}")).Keys.ShouldBe(["lines[1].metadata"]);
    }

    [Fact]
    public void Every_problem_of_a_line_is_reported_together()
    {
        Errors(Parse("{\"id\":1,\"input\":2}")).Keys.Order(StringComparer.Ordinal).ShouldBe(["lines[1].expected", "lines[1].id", "lines[1].input"]);
    }

    [Fact]
    public void One_invalid_line_rejects_the_whole_upload()
    {
        var result = Parse("{\"id\":\"a\",\"input\":{},\"expected\":\"x\"}\n{\"id\":\"b\",\"input\":{}}");

        Errors(result).Keys.ShouldBe(["lines[2].expected"]);
    }

    [Fact]
    public void At_most_100_invalid_lines_are_listed_with_a_body_note()
    {
        var text = string.Join('\n', Enumerable.Range(0, 150).Select(_ => "[]"));

        var errors = Errors(Parse(text));

        errors.Keys.Count(k => k.StartsWith("lines[", StringComparison.Ordinal)).ShouldBe(DatasetJsonl.MaxReportedLines);
        errors[DatasetJsonl.BodyKey].Single().ShouldContain("150");
    }

    [Fact]
    public void The_content_hash_is_sha256_of_the_canonical_cases_joined_by_newlines()
    {
        var result = Parse("{\"input\":{\"b\":1,\"a\":\"x\"},\"id\":\"c1\",\"expected\":\"spam\"}\n{\"id\":\"c2\",\"expected\":\"ham\",\"input\":{}}");

        var canonical = "{\"expected\":\"spam\",\"id\":\"c1\",\"input\":{\"a\":\"x\",\"b\":1}}\n{\"expected\":\"ham\",\"id\":\"c2\",\"input\":{}}";
        result.Value.ContentSha256.ShouldBe(DatasetJsonl.Sha256Hex(canonical));
        result.Value.ContentSha256.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Key_order_whitespace_escapes_and_blank_lines_do_not_change_the_hash()
    {
        var a = Parse("{\"id\":\"c1\",\"input\":{\"text\":\"A\"},\"expected\":\"spam\"}");
        var b = Parse("\n  { \"expected\" : \"spam\" , \"input\" : { \"text\" : \"\\u0041\" }, \"id\" : \"c1\" }  \r\n\n");

        a.Value.ContentSha256.ShouldBe(b.Value.ContentSha256);
    }

    [Fact]
    public void Case_order_and_number_literals_change_the_hash()
    {
        var original = Parse("{\"id\":\"a\",\"input\":{\"n\":1},\"expected\":\"x\"}\n{\"id\":\"b\",\"input\":{},\"expected\":\"y\"}").Value.ContentSha256;
        var reordered = Parse("{\"id\":\"b\",\"input\":{},\"expected\":\"y\"}\n{\"id\":\"a\",\"input\":{\"n\":1},\"expected\":\"x\"}").Value.ContentSha256;
        var otherLiteral = Parse("{\"id\":\"a\",\"input\":{\"n\":1.0},\"expected\":\"x\"}\n{\"id\":\"b\",\"input\":{},\"expected\":\"y\"}").Value.ContentSha256;

        reordered.ShouldNotBe(original);
        otherLiteral.ShouldNotBe(original);
    }

    [Fact]
    public void The_classification_scoring_schema_is_the_sorted_distinct_label_set()
    {
        var result = Parse("{\"id\":\"1\",\"input\":{},\"expected\":\"spam\"}\n{\"id\":\"2\",\"input\":{},\"expected\":\"ham\"}\n{\"id\":\"3\",\"input\":{},\"expected\":\"spam\"}");

        result.Value.ScoringSchemaJson.ShouldBe("""{"taskKind":"classification","labels":["ham","spam"]}""");
    }

    [Fact]
    public void The_extraction_scoring_schema_is_the_sorted_union_of_expected_field_names()
    {
        var result = Parse(
            "{\"id\":\"1\",\"input\":{},\"expected\":{\"name\":\"Ana\",\"email\":null}}\n{\"id\":\"2\",\"input\":{},\"expected\":{\"phone\":\"1\"}}",
            TaskKind.Extraction);

        result.Value.ScoringSchemaJson.ShouldBe("""{"taskKind":"extraction","fields":["email","name","phone"]}""");
    }

    [Fact]
    public void The_free_text_scoring_schema_names_only_the_task_kind()
    {
        Parse("{\"id\":\"1\",\"input\":{},\"expected\":\"Paris\"}", TaskKind.FreeText).Value.ScoringSchemaJson.ShouldBe("""{"taskKind":"free_text"}""");
    }

    [Fact]
    public void Stored_case_values_are_canonical_json()
    {
        var result = Parse("{\"id\":\"1\",\"input\":{ \"z\" : [1, {\"b\":true,\"a\":null}], \"a\" : \"é\" },\"expected\":{\"k\":\"v\"}}", TaskKind.Extraction);

        result.Value.Cases[0].InputJson.ShouldBe("""{"a":"é","z":[1,{"a":null,"b":true}]}""");
        using var _ = JsonDocument.Parse(result.Value.Cases[0].InputJson);
    }

    [Fact]
    public void Error_messages_never_echo_case_content()
    {
        var errors = Errors(Parse("{\"id\":\"secret-id\",\"input\":\"secret-input\",\"expected\":{\"secret\":1}}\n{broken secret-text"));

        errors.Values.SelectMany(m => m).ShouldAllBe(message => !message.Contains("secret", StringComparison.Ordinal));
    }
}
