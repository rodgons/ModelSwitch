using System.Globalization;
using System.Text.RegularExpressions;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;

namespace ModelSwitch.Identity.Application;

/// <summary>
/// The hand-written validation rules of the Tenant, limits, model access and API Key admin bodies. Each method adds one entry
/// per invalid field to the caller's error dictionary (the 400 <c>validation_failed</c> <c>errors</c> member) and returns the
/// normalized value.
/// </summary>
internal static partial class TenantRules
{
    public const int MaxNameLength = 100;
    public const int MaxLimitValue = 10_000_000;
    public const int MaxAliasCount = 200;
    public const string Wildcard = "*";
    public const string BudgetPeriodMonth = "month";

    // numeric(20,10): ten integer digits and ten decimals.
    private const decimal _maxMoney = 9_999_999_999m;
    private const int _maxMoneyDecimals = 10;

    public static string? ValidateName(string? name, Dictionary<string, string[]> errors, string field = "name")
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            errors[field] = ["The name is required."];
            return null;
        }

        if (trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl))
        {
            errors[field] = [$"The name must be at most {MaxNameLength} characters and contain no control characters."];
            return null;
        }

        return trimmed;
    }

    public static TenantStatus? ValidateStatus(string? status, Dictionary<string, string[]> errors)
    {
        switch (status)
        {
            case "active":
                return TenantStatus.Active;
            case "suspended":
                return TenantStatus.Suspended;
            default:
                errors["status"] = ["The status must be one of: active, suspended."];
                return null;
        }
    }

    public static string ToWire(TenantStatus status) => status == TenantStatus.Active ? "active" : "suspended";

    public static int? ValidateLimit(int? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is < 1 or > MaxLimitValue)
        {
            errors[field] = [$"It must be between 1 and {MaxLimitValue.ToString(CultureInfo.InvariantCulture)}, or null for no limit."];
        }

        return value;
    }

    public static decimal? ValidateMoney(decimal? amount, string field, Dictionary<string, string[]> errors)
    {
        if (amount is not { } value)
        {
            return null;
        }

        if (value < 0 || value > _maxMoney || decimal.Round(value, _maxMoneyDecimals) != value)
        {
            errors[field] = [$"It must be a non-negative amount with at most {_maxMoneyDecimals} decimals, or null for no limit."];
        }

        return value;
    }

    /// <summary>The Budget of a limits body: null removes it. Only a monthly USD period exists in the MVP.</summary>
    public static decimal? ValidateBudget(BudgetRequest? budget, Dictionary<string, string[]> errors)
    {
        if (budget is null)
        {
            return null;
        }

        if (budget.Amount is null)
        {
            errors["budget.amount"] = ["The Budget amount is required; send budget: null to remove the Budget."];
        }

        if (budget.Period is not (null or BudgetPeriodMonth))
        {
            errors["budget.period"] = ["The only supported period is \"month\"."];
        }

        return ValidateMoney(budget.Amount, "budget.amount", errors);
    }

    /// <summary>A de-duplicated, sorted alias list: either <c>["*"]</c> alone or concrete Model Alias names.</summary>
    public static string[]? ValidateAliases(IReadOnlyList<string?>? aliases, Dictionary<string, string[]> errors)
    {
        if (aliases is null || aliases.Count == 0)
        {
            errors["allowedAliases"] = ["Give at least one Model Alias, or [\"*\"] for all of them. To block a Tenant, suspend it."];
            return null;
        }

        if (aliases.Count > MaxAliasCount)
        {
            errors["allowedAliases"] = [$"At most {MaxAliasCount} aliases are allowed."];
            return null;
        }

        if (aliases.Any(a => a is null || (a != Wildcard && !AliasPattern().IsMatch(a))))
        {
            errors["allowedAliases"] = ["Each entry must be \"*\" or a Model Alias name: a lower-case letter or digit, then up to 63 of a-z 0-9 . _ -."];
            return null;
        }

        var distinct = aliases.Select(a => a!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (distinct.Length > 1 && distinct.Contains(Wildcard, StringComparer.Ordinal))
        {
            errors["allowedAliases"] = ["\"*\" cannot be combined with other aliases."];
            return null;
        }

        return distinct;
    }

    /// <summary>The Scopes of a new key; <c>inference</c> when the body names none.</summary>
    public static string[]? ValidateScopes(IReadOnlyList<string?>? scopes, Dictionary<string, string[]> errors)
    {
        if (scopes is null)
        {
            return [ApiKeyScopes.Inference];
        }

        if (scopes.Count == 0 || scopes.Any(s => s is null || !ApiKeyScopes.All.Contains(s)))
        {
            errors["scopes"] = [$"Give at least one scope from: {string.Join(", ", ApiKeyScopes.All)}."];
            return null;
        }

        return [.. scopes.Select(s => s!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex AliasPattern();
}
