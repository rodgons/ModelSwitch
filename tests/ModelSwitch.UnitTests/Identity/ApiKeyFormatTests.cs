using ModelSwitch.Identity.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Identity;

public sealed class ApiKeyFormatTests
{
    private const string _valid = "msk_7f3a9c21_abcdefghijklmnopqrstuvwxyzABCDEFGHI-_012345";

    [Fact]
    public void Generated_key_has_the_documented_shape()
    {
        var key = ApiKeyFormat.Generate();

        key.Plaintext.Length.ShouldBe(56);
        key.Plaintext.ShouldStartWith("msk_");
        key.Plaintext[12].ShouldBe('_');
        key.Prefix.Length.ShouldBe(8);
        key.Plaintext.Substring(4, 8).ShouldBe(key.Prefix);
        key.Prefix.ShouldAllBe(c => ApiKeyFormat.PrefixAlphabet.Contains(c, StringComparison.Ordinal));
    }

    [Fact]
    public void Generated_key_parses_back_to_its_prefix()
    {
        var key = ApiKeyFormat.Generate();

        ApiKeyFormat.TryParse(key.Plaintext, out var parsed).ShouldBeTrue();
        parsed.Prefix.ShouldBe(key.Prefix);
    }

    [Fact]
    public void Generated_keys_are_unique()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => ApiKeyFormat.Generate().Plaintext).ToHashSet();

        keys.Count.ShouldBe(200);
    }

    [Fact]
    public void Well_formed_key_is_accepted()
    {
        ApiKeyFormat.TryParse(_valid, out var parsed).ShouldBeTrue();
        parsed.Prefix.ShouldBe("7f3a9c21");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("msk_")]
    [InlineData("sk_7f3a9c21_abcdefghijklmnopqrstuvwxyzABCDEFGHI-_012345")]
    [InlineData("MSK_7f3a9c21_abcdefghijklmnopqrstuvwxyzABCDEFGHI-_012345")]
    [InlineData("msk_7f3a9c21_abcdefghijklmnopqrstuvwxyzABCDEFGHI-_01234")]
    [InlineData("msk_7f3a9c21_abcdefghijklmnopqrstuvwxyzABCDEFGHI-_0123456")]
    [InlineData("msk_7f3a9c21-abcdefghijklmnopqrstuvwxyzABCDEFGHI-_012345")]
    [InlineData("msk_7F3A9C21_abcdefghijklmnopqrstuvwxyzABCDEFGHI-_012345")]
    [InlineData("msk_7f3a9cil_abcdefghijklmnopqrstuvwxyzABCDEFGHI-_012345")]
    [InlineData("msk_7f3a9c21_abcdefghijklmnopqrstuvwxyzABCDEFGHI+/012345")]
    [InlineData("msk_7f3a9c21_abcdefghijklmnopqrstuvwxyzABCDEFGHI =012345")]
    public void Malformed_key_is_rejected(string? value)
    {
        ApiKeyFormat.TryParse(value, out _).ShouldBeFalse();
    }
}
