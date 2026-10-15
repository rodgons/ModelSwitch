namespace ModelSwitch.Registry.Contracts;

/// <summary>A resolved Provider secret. Its text form is redacted so it cannot reach a log by accident.</summary>
public sealed class CredentialSecret
{
    private readonly string _value;

    public CredentialSecret(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    /// <summary>The secret itself. Use only to build the outbound Authorization header; never log it.</summary>
    public string Reveal() => _value;

    public override string ToString() => "[redacted]";
}
