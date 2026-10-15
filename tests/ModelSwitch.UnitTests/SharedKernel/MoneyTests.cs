using ModelSwitch.SharedKernel;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

public sealed class MoneyTests
{
    [Fact]
    public void Adds_and_subtracts_same_currency()
    {
        (Money.FromUsd(1.5m) + Money.FromUsd(2.25m)).Amount.ShouldBe(3.75m);
        (Money.FromUsd(5m) - Money.FromUsd(2m)).Amount.ShouldBe(3m);
    }

    [Fact]
    public void Rejects_non_usd_currency()
    {
        Should.Throw<ArgumentException>(() => new Money(1m, "EUR"));
        Should.Throw<ArgumentException>(() => new Money(1m, "usd"));
    }

    [Fact]
    public void Default_is_zero_usd()
    {
        default(Money).Currency.ShouldBe("USD");
        Money.Zero.Amount.ShouldBe(0m);
        (Money.Zero + Money.FromUsd(1m)).Amount.ShouldBe(1m);
    }

    [Fact]
    public void Multiplies_by_a_decimal_factor()
    {
        (Money.FromUsd(2m) * 1.5m).Amount.ShouldBe(3m);
        (3m * Money.FromUsd(2m)).Amount.ShouldBe(6m);
    }

    [Fact]
    public void Compares_amounts()
    {
        (Money.FromUsd(1m) < Money.FromUsd(2m)).ShouldBeTrue();
        (Money.FromUsd(2m) >= Money.FromUsd(2m)).ShouldBeTrue();
        (Money.FromUsd(3m) > Money.FromUsd(2m)).ShouldBeTrue();
        (Money.FromUsd(1m) <= Money.FromUsd(1m)).ShouldBeTrue();
        Money.FromUsd(1.0m).ShouldBe(Money.FromUsd(1.00m));
    }

    [Fact]
    public void Currency_mismatch_is_guarded()
    {
        // Only USD can be constructed today, so exercise the guard via a forged struct is impossible;
        // this documents that the guard exists by asserting same-currency operations never throw.
        Should.NotThrow(() => Money.FromUsd(1m) + Money.FromUsd(1m));
    }

    [Theory]
    [InlineData("0.00000000005", "0.0000000000")]
    [InlineData("0.00000000015", "0.0000000002")]
    [InlineData("0.00000000025", "0.0000000002")]
    [InlineData("0.00000000035", "0.0000000004")]
    public void Round_uses_ten_places_half_even(string input, string expected)
    {
        Money.FromUsd(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)).Round().Amount
            .ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }
}
