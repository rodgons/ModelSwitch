using System.Text.RegularExpressions;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Hosting;

namespace ModelSwitch.Registry.Infrastructure.Credentials;

/// <summary><c>env:NAME</c>: the secret is the value of environment variable NAME.</summary>
internal sealed partial class EnvCredentialSource(IEnvironmentReader environment) : ICredentialSource
{
    public string Scheme => "env";

    public ValueTask<Result<CredentialSecret>> ResolveAsync(string value, CancellationToken cancellationToken)
    {
        // Deployment settings (pepper, connection strings, ...) live under the MODELSWITCH__ prefix and must never be a Provider credential.
        if (!VariableName().IsMatch(value) || value.StartsWith(RequiredSetting.EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Credential reference env: names an invalid or reserved environment variable.");
        }

        var secret = environment.Get(value);
        return string.IsNullOrEmpty(secret)
            ? Fail($"Credential environment variable '{value}' is not set.")
            : ValueTask.FromResult<Result<CredentialSecret>>(new CredentialSecret(secret));
    }

    private static ValueTask<Result<CredentialSecret>> Fail(string message)
        => ValueTask.FromResult<Result<CredentialSecret>>(new Error(ErrorCode.ProviderUnavailable, message));

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex VariableName();
}
