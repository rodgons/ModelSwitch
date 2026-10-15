using Microsoft.Extensions.Logging;
using ModelSwitch.AdapterContractTests.Support;

namespace ModelSwitch.AdapterContractTests.SuiteSelfTest;

/// <summary>
/// Runs the whole contract suite against a minimal reference Adapter and a synthetic wire format. This proves the suite itself is sound
/// (every scenario can pass, and every stub works) before the real Adapters plug into it. A real Adapter's test class looks the same:
/// derive from <see cref="AdapterContract{TAdapter}"/>, supply the factory and its <see cref="ProviderWireFormat"/>.
/// </summary>
public sealed class ReferenceAdapterContractTests : AdapterContract<ReferenceAdapter>
{
    protected override ProviderWireFormat Wire { get; } = new OpenAiStyleWireFormat();

    protected override ReferenceAdapter CreateAdapter(AdapterTestEnvironment environment) =>
        new(environment.Http, environment.LoggerFactory.CreateLogger<ReferenceAdapter>(), environment.TimeProvider);
}
