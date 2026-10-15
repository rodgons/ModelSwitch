using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Configuration.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Contracts;
using Shouldly;

namespace ModelSwitch.UnitTests.Configuration;

public sealed class SnapshotBuilderTests
{
    private static readonly Guid _modelId = Guid.CreateVersion7();
    private static readonly Guid _policyId = Guid.CreateVersion7();

    [Fact]
    public async Task Builds_candidates_with_parsed_capabilities_current_price_and_the_alias_map()
    {
        var capabilities = """{"streaming":true,"jsonObject":true,"contextWindow":128000,"maxOutputTokens":16384,"parameters":["temperature","seed"],"tokenizer":"o200k_base"}""";
        var entry = new CatalogEntry(_modelId, Guid.CreateVersion7(), "openai", "gpt-4o-mini", ProviderEnabled: true, ModelEnabled: true, capabilities, new CatalogPrice(0.15m, null, 0.6m));
        var policy = new PolicySnapshot(_policyId, "default", 3, new PolicyDefinition(PolicyStrategy.Static, [_modelId], [], [], null, null, 1024, null, ExecutionSettings.Default));
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var builder = new SnapshotBuilder(
            new StubCatalog(new CatalogSnapshot([entry])),
            new StubPolicies(new PolicyCatalog(new Dictionary<string, Guid> { ["balanced"] = _policyId }, new Dictionary<Guid, PolicySnapshot> { [_policyId] = policy })),
            new StubLimits(),
            time);

        var snapshot = await builder.BuildAsync(9, CancellationToken.None);

        snapshot.Version.ShouldBe(9);
        snapshot.LoadedAt.ShouldBe(time.GetUtcNow());
        snapshot.Routing.AliasToPolicy["balanced"].ShouldBe(_policyId);
        snapshot.Routing.Policies[_policyId].Version.ShouldBe(3);
        var model = snapshot.Routing.Models[_modelId];
        model.ProviderName.ShouldBe("openai");
        model.Price.ShouldBe(new PriceInForce(0.15m, 0.6m));
        model.Capabilities.Streaming.ShouldBeTrue();
        model.Capabilities.JsonSchema.ShouldBeFalse();
        model.Capabilities.ContextWindow.ShouldBe(128000);
        model.Capabilities.Supports("seed").ShouldBeTrue();
        model.Capabilities.Tokenizer.ShouldBe("o200k_base");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("[]")]
    public void Undeclared_capabilities_parse_as_unsupported(string json)
    {
        var capabilities = CapabilitiesParser.Parse(json);

        capabilities.Streaming.ShouldBeFalse();
        capabilities.ContextWindow.ShouldBe(0);
        capabilities.Parameters.ShouldBeEmpty();
        capabilities.Tokenizer.ShouldBeNull();
    }

    private sealed class StubCatalog(CatalogSnapshot snapshot) : ICatalogSnapshotSource
    {
        public Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class StubPolicies(PolicyCatalog catalog) : IPolicyCatalogSource
    {
        public Task<PolicyCatalog> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(catalog);
    }

    private sealed class StubLimits : ITenantLimitsSource
    {
        public Task<IReadOnlyDictionary<Guid, TenantLimits>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, TenantLimits>>(new Dictionary<Guid, TenantLimits>());
    }
}
