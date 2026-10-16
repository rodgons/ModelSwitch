namespace ModelSwitch.Identity.Contracts;

/// <summary>The three MVP Roles (docs/api.md section 3). Ordered: each Role can do everything the previous one can.</summary>
public enum AdministratorRole
{
    Viewer = 0,
    Operator = 1,
    Owner = 2,
}

public static class AdministratorRoles
{
    /// <summary>The lower-case wire and database value: <c>viewer</c>, <c>operator</c> or <c>owner</c>.</summary>
    public static string ToWire(AdministratorRole role) => role switch
    {
        AdministratorRole.Viewer => "viewer",
        AdministratorRole.Operator => "operator",
        AdministratorRole.Owner => "owner",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static bool TryParse(string? value, out AdministratorRole role)
    {
        switch (value)
        {
            case "viewer":
                role = AdministratorRole.Viewer;
                return true;
            case "operator":
                role = AdministratorRole.Operator;
                return true;
            case "owner":
                role = AdministratorRole.Owner;
                return true;
            default:
                role = default;
                return false;
        }
    }

    /// <summary>True when <paramref name="actual"/> may do what <paramref name="required"/> may.</summary>
    public static bool Satisfies(AdministratorRole actual, AdministratorRole required) => actual >= required;

    /// <summary>The wire values of every Role that satisfies <paramref name="required"/>; the allowed claim values of a policy.</summary>
    public static IReadOnlyList<string> AtLeast(AdministratorRole required)
        => [.. Enum.GetValues<AdministratorRole>().Where(role => Satisfies(role, required)).Select(ToWire)];
}
