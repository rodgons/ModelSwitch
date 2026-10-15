using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel;

namespace ModelSwitch.Registry.Infrastructure.Credentials;

/// <summary>
/// Resolves the part of a Credential Reference after the scheme. The seam for new reference kinds:
/// Phase 3 adds a <c>db:</c> source (AES-GCM secret store) by registering another implementation.
/// </summary>
internal interface ICredentialSource
{
    /// <summary>The reference scheme this source handles, without the colon (e.g. <c>env</c>).</summary>
    string Scheme { get; }

    ValueTask<Result<CredentialSecret>> ResolveAsync(string value, CancellationToken cancellationToken);
}

/// <summary>Reads process environment variables. Injectable for tests.</summary>
internal interface IEnvironmentReader
{
    string? Get(string name);
}

internal sealed class ProcessEnvironmentReader : IEnvironmentReader
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}
