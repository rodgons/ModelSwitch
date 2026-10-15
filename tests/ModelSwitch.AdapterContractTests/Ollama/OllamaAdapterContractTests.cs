using Microsoft.Extensions.Logging;
using ModelSwitch.AdapterContractTests.Support;
using ModelSwitch.Adapters.Ollama;

namespace ModelSwitch.AdapterContractTests.Ollama;

/// <summary>The real Ollama Adapter against the shared contract suite (FR-02 acceptance).</summary>
public sealed class OllamaAdapterContractTests : AdapterContract<OllamaAdapter>
{
    protected override ProviderWireFormat Wire { get; } = new OllamaWireFormat();

    protected override OllamaAdapter CreateAdapter(AdapterTestEnvironment environment) =>
        new(environment.Http, environment.LoggerFactory.CreateLogger<OllamaAdapter>(), environment.TimeProvider);
}
