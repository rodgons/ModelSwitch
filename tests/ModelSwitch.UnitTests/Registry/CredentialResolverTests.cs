using ModelSwitch.Registry.Infrastructure.Credentials;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Registry;

public sealed class CredentialResolverTests
{
    private sealed class FakeEnvironment(Dictionary<string, string> values) : IEnvironmentReader
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
    }

    private static CredentialResolver Create(Dictionary<string, string>? env = null)
        => new([new EnvCredentialSource(new FakeEnvironment(env ?? []))]);

    [Fact]
    public async Task Env_reference_resolves_to_the_variable_value()
    {
        var result = await Create(new() { ["OPENAI_API_KEY"] = "sk-secret" }).ResolveAsync("env:OPENAI_API_KEY", TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Reveal().ShouldBe("sk-secret");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task No_reference_means_no_credential(string? reference)
    {
        var result = await Create().ResolveAsync(reference, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Unset_variable_fails_with_a_typed_error_naming_the_variable()
    {
        var result = await Create().ResolveAsync("env:MISSING_KEY", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        result.Error.Message.ShouldContain("MISSING_KEY");
    }

    [Fact]
    public async Task Empty_variable_counts_as_unset()
    {
        var result = await Create(new() { ["EMPTY"] = "" }).ResolveAsync("env:EMPTY", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("db:3f2c9a10-0000-0000-0000-000000000000")] // db: resolver arrives in Phase 3
    [InlineData("vault:thing")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData(":x")]
    public async Task Unknown_scheme_fails(string reference)
    {
        var result = await Create().ResolveAsync(reference, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
    }

    [Theory]
    [InlineData("env:1BAD")]
    [InlineData("env:BAD NAME")]
    [InlineData("env:")]
    [InlineData("env:MODELSWITCH__Security__ApiKeyPepper")]
    [InlineData("env:modelswitch__ConnectionStrings__Postgres")]
    public async Task Malformed_or_reserved_variable_names_are_refused_without_reading_them(string reference)
    {
        var env = new Dictionary<string, string>
        {
            ["MODELSWITCH__Security__ApiKeyPepper"] = "pepper",
            ["modelswitch__ConnectionStrings__Postgres"] = "conn",
        };

        var result = await Create(env).ResolveAsync(reference, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Message.ShouldNotContain("pepper");
    }

    [Fact]
    public async Task Error_messages_and_text_form_never_contain_the_secret()
    {
        var resolved = await Create(new() { ["K"] = "sk-top-secret" }).ResolveAsync("env:K", TestContext.Current.CancellationToken);

        resolved.Value!.ToString().ShouldNotContain("sk-top-secret");
    }
}
