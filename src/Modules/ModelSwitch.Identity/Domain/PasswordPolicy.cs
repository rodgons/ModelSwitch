namespace ModelSwitch.Identity.Domain;

/// <summary>Password rules for Administrators (security.md section 3): at least 12 characters.</summary>
internal static class PasswordPolicy
{
    public const int MinimumLength = 12;

    /// <summary>Caps the work PBKDF2 does for one request.</summary>
    public const int MaximumLength = 128;

    /// <summary>A client-safe message for the first violated rule, or null when <paramref name="password"/> is acceptable.</summary>
    public static string? Violation(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "The password is required.";
        }

        if (password.Length < MinimumLength)
        {
            return $"The password must be at least {MinimumLength} characters.";
        }

        return password.Length > MaximumLength ? $"The password must be at most {MaximumLength} characters." : null;
    }
}
