using System.Security.Cryptography;
using System.Text;
using ModelSwitch.Identity.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Identity;

public sealed class ApiKeyHasherTests
{
    private static readonly byte[] _pepper = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Hash_is_HMAC_SHA256_of_the_full_key_under_the_pepper()
    {
        var hasher = new ApiKeyHasher(_pepper);

        hasher.Hash("msk_x").ShouldBe(HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes("msk_x")));
    }

    [Fact]
    public void Hash_is_32_bytes_and_deterministic()
    {
        var hasher = new ApiKeyHasher(_pepper);

        hasher.Hash("a").Length.ShouldBe(32);
        hasher.Hash("a").ShouldBe(hasher.Hash("a"));
        hasher.Hash("a").ShouldNotBe(hasher.Hash("b"));
    }

    [Fact]
    public void A_different_pepper_gives_a_different_hash()
    {
        var other = _pepper.Select(b => (byte)(b ^ 0xFF)).ToArray();

        new ApiKeyHasher(_pepper).Hash("a").ShouldNotBe(new ApiKeyHasher(other).Hash("a"));
    }

    [Fact]
    public void A_pepper_shorter_than_32_bytes_is_rejected()
    {
        Should.Throw<ArgumentException>(() => new ApiKeyHasher(new byte[31]));
    }

    [Fact]
    public void FromBase64_accepts_32_bytes_or_more()
    {
        ApiKeyHasher.FromBase64(Convert.ToBase64String(new byte[32])).ShouldNotBeNull();
        ApiKeyHasher.FromBase64(Convert.ToBase64String(new byte[64])).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("AAAA")]
    public void FromBase64_rejects_invalid_or_short_peppers(string value)
    {
        ApiKeyHasher.FromBase64(value).ShouldBeNull();
        ApiKeyHasher.FromBase64(Convert.ToBase64String(new byte[31])).ShouldBeNull();
    }

    [Fact]
    public void Matches_compares_hashes_by_value()
    {
        var hasher = new ApiKeyHasher(_pepper);

        ApiKeyHasher.Matches(hasher.Hash("a"), hasher.Hash("a")).ShouldBeTrue();
        ApiKeyHasher.Matches(hasher.Hash("a"), hasher.Hash("b")).ShouldBeFalse();
        ApiKeyHasher.Matches(hasher.Hash("a"), new byte[16]).ShouldBeFalse();
    }
}
