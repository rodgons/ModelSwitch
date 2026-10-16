namespace ModelSwitch.Evaluation.Domain;

/// <summary>The name rule shared by Datasets and Prompts: trimmed, 1 to 128 characters, no control characters. Unique per Tenant.</summary>
internal static class ResourceNames
{
    public const int MaxLength = 128;

    /// <summary>The trimmed name, or null with a message in <paramref name="error"/>.</summary>
    public static string? Validate(string? name, out string? error)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "The name is required.";
            return null;
        }

        if (trimmed.Length > MaxLength)
        {
            error = $"The name must be at most {MaxLength} characters.";
            return null;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = "The name must not contain control characters.";
            return null;
        }

        error = null;
        return trimmed;
    }
}
