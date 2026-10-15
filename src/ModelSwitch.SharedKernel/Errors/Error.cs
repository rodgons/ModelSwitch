namespace ModelSwitch.SharedKernel.Errors;

/// <summary>An expected failure: a code from the closed set, a client-safe message and optional per-field errors.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Error is the documented domain term (architecture.md section 7); no VB consumers.")]
public sealed record Error
{
    public Error(ErrorCode code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
        FieldErrors = fieldErrors;
    }

    public ErrorCode Code { get; }

    /// <summary>Safe to show to clients: never Provider bodies, credentials or prompt content (ADR-0010).</summary>
    public string Message { get; }

    /// <summary>Invalid fields keyed by path, e.g. <c>messages[3].content</c>. Only for validation errors.</summary>
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; }

    public static Error Validation(string message, IReadOnlyDictionary<string, string[]> fieldErrors)
        => new(ErrorCode.ValidationFailed, message, fieldErrors);
}
