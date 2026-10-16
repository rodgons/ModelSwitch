using System.Text.Json;
using ModelSwitch.Registry.Application;
using Shouldly;

namespace ModelSwitch.UnitTests.Registry;

public sealed class AdminRulesTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Theory]
    [InlineData("openai-main", true)]
    [InlineData("a", true)]
    [InlineData("OpenAI", false)]
    [InlineData("has/slash", false)]
    [InlineData("-leading", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Provider_names_are_lower_case_and_never_contain_a_slash(string? name, bool valid)
    {
        var errors = new Dictionary<string, string[]>();

        var result = AdminRules.ProviderName(name, errors);

        (result is not null).ShouldBe(valid);
        errors.ContainsKey("name").ShouldBe(!valid);
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    [InlineData(600000, true)]
    [InlineData(600001, false)]
    public void Request_timeouts_are_bounded(int timeout, bool valid)
    {
        var errors = new Dictionary<string, string[]>();

        AdminRules.RequestTimeout(timeout, errors);

        errors.ContainsKey("requestTimeoutMs").ShouldBe(!valid);
    }

    [Fact]
    public void A_credential_has_exactly_one_of_secret_or_env_var()
    {
        var both = new Dictionary<string, string[]>();
        var neither = new Dictionary<string, string[]>();

        AdminRules.Credential(new CredentialRequest("s", "V"), both, out _);
        AdminRules.Credential(new CredentialRequest(null, null), neither, out _);

        both.ContainsKey("credential").ShouldBeTrue();
        neither.ContainsKey("credential").ShouldBeTrue();
    }

    [Fact]
    public void An_env_var_credential_becomes_an_env_reference_and_a_secret_one_returns_no_reference()
    {
        var errors = new Dictionary<string, string[]>();

        AdminRules.Credential(new CredentialRequest(null, " OPENAI_KEY "), errors, out var envSecret).ShouldBe("env:OPENAI_KEY");
        AdminRules.Credential(new CredentialRequest("sk-1", null), errors, out var isSecret).ShouldBeNull();

        envSecret.ShouldBeFalse();
        isSecret.ShouldBeTrue();
        errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("MODELSWITCH__Security__MasterKey")]
    [InlineData("modelswitch__x")]
    [InlineData("1BAD")]
    [InlineData("has space")]
    public void Env_vars_that_are_reserved_or_malformed_are_rejected(string name)
    {
        var errors = new Dictionary<string, string[]>();

        AdminRules.Credential(new CredentialRequest(null, name), errors, out _).ShouldBeNull();

        errors.ContainsKey("credential.envVar").ShouldBeTrue();
    }

    [Fact]
    public void A_credential_request_never_prints_its_secret()
        => new CredentialRequest("sk-canary", null).ToString().ShouldNotContain("sk-canary");

    [Fact]
    public void Valid_capabilities_pass_and_come_back_as_json()
    {
        var errors = new Dictionary<string, string[]>();

        var result = AdminRules.Capabilities(Json("""{"streaming":true,"contextWindow":8000,"maxOutputTokens":1000,"tokenizer":"cl100k_base","parameters":["seed"]}"""), errors);

        errors.ShouldBeEmpty();
        result.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("""{"streamin":true}""", "capabilities.streamin")]
    [InlineData("""{"streaming":"yes"}""", "capabilities.streaming")]
    [InlineData("""{"contextWindow":0}""", "capabilities.contextWindow")]
    [InlineData("""{"tokenizer":"gpt2"}""", "capabilities.tokenizer")]
    [InlineData("""{"parameters":[1]}""", "capabilities.parameters")]
    [InlineData("""{"contextWindow":100,"maxOutputTokens":200}""", "capabilities.maxOutputTokens")]
    [InlineData("""[]""", "capabilities")]
    public void Invalid_capabilities_are_reported_by_field(string json, string field)
    {
        var errors = new Dictionary<string, string[]>();

        AdminRules.Capabilities(Json(json), errors).ShouldBeNull();

        errors.ContainsKey(field).ShouldBeTrue();
    }

    [Fact]
    public void Missing_capabilities_are_an_error()
    {
        var errors = new Dictionary<string, string[]>();

        AdminRules.Capabilities(null, errors).ShouldBeNull();

        errors.ContainsKey("capabilities").ShouldBeTrue();
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("0.0000000001", true)]
    [InlineData("0.00000000001", false)]
    [InlineData("-1", false)]
    [InlineData("10000000000", false)]
    public void Prices_are_non_negative_and_fit_numeric_20_10(string text, bool valid)
    {
        var errors = new Dictionary<string, string[]>();

        var result = AdminRules.PricePerMillion(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), "p", required: true, errors);

        (result is not null).ShouldBe(valid);
    }

    [Fact]
    public void A_required_price_must_be_present_and_an_optional_one_may_be_missing()
    {
        var errors = new Dictionary<string, string[]>();

        AdminRules.PricePerMillion(null, "input", required: true, errors);
        AdminRules.PricePerMillion(null, "cached", required: false, errors);

        errors.Keys.ShouldBe(["input"]);
    }
}
