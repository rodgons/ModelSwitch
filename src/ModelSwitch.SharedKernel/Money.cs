namespace ModelSwitch.SharedKernel;

/// <summary>
/// A monetary amount in one currency. MVP accepts USD only. Amounts are <c>decimal</c> (never double) and
/// persist as <c>numeric(20,10)</c>; <see cref="Round"/> applies that scale with banker's rounding.
/// </summary>
public readonly record struct Money
{
    public const string Usd = "USD";

    /// <summary>Decimal places stored in the database (numeric(20,10)).</summary>
    public const int Scale = 10;

    private readonly string? _currency;

    public Money(decimal amount, string currency)
    {
        if (!string.Equals(currency, Usd, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported currency '{currency}'. Only {Usd} is accepted.", nameof(currency));
        }

        Amount = amount;
        _currency = currency;
    }

    public decimal Amount { get; }

    /// <summary>ISO-4217 code. A defaulted <c>Money</c> is zero USD.</summary>
    public string Currency => _currency ?? Usd;

    public static Money Zero => default;

    public static Money FromUsd(decimal amount) => new(amount, Usd);

    public Money Round() => new(decimal.Round(Amount, Scale, MidpointRounding.ToEven), Currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator *(Money money, decimal factor) => new(money.Amount * factor, money.Currency);

    public static Money operator *(decimal factor, Money money) => money * factor;

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public bool Equals(Money other) => Amount == other.Amount && Currency == other.Currency;

    public override int GetHashCode() => HashCode.Combine(Amount, Currency);

    public override string ToString() => $"{Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} {Currency}";

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException($"Currency mismatch: {left.Currency} vs {right.Currency}.");
        }
    }
}
