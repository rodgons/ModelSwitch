using Microsoft.Extensions.Logging;
using ModelSwitch.AdapterContractTests.Support;
using ModelSwitch.Adapters.OpenAICompatible;

namespace ModelSwitch.AdapterContractTests.OpenAICompatible;

/// <summary>
/// The OpenAI-compatible Adapter against the whole shared contract suite, with OpenAI's wire format (captured through OpenRouter,
/// ADR-0013) as the reference. FR-02 acceptance: it passes unchanged.
/// </summary>
public sealed class OpenAICompatibleAdapterContractTests : AdapterContract<OpenAICompatibleAdapter>
{
    protected override ProviderWireFormat Wire { get; } = new OpenAICompatibleWireFormat();

    protected override OpenAICompatibleAdapter CreateAdapter(AdapterTestEnvironment environment) =>
        new(environment.Http, environment.TimeProvider, environment.LoggerFactory.CreateLogger<OpenAICompatibleAdapter>());
}
