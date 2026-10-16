using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using ModelSwitch.Registry.Infrastructure.Secrets;
using Shouldly;

namespace ModelSwitch.UnitTests.Registry;

public sealed class SecretCipherTests
{
    private static readonly Guid _provider = Guid.Parse("0192f0c8-0000-7000-8000-0000000000a1");
    private static readonly byte[] _keyOne = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] _keyTwo = Enumerable.Range(1, 32).Select(i => (byte)(i * 3)).ToArray();

    private static SecretCipher Cipher(short version = 1, byte[]? key = null, short? previousVersion = null, byte[]? previous = null)
        => new(MasterKeyRing.Create(version, key ?? _keyOne, previousVersion, previous));

    [Fact]
    public void Round_trips_and_does_not_store_plaintext()
    {
        var cipher = Cipher();
        var sealedSecret = cipher.Encrypt("sk-live-canary", _provider);

        sealedSecret.Ciphertext.ShouldNotBe(System.Text.Encoding.UTF8.GetBytes("sk-live-canary"));
        sealedSecret.Nonce.Length.ShouldBe(12);
        sealedSecret.Tag.Length.ShouldBe(16);
        cipher.Decrypt(sealedSecret, _provider).ShouldBe("sk-live-canary");
    }

    [Fact]
    public void Each_encryption_uses_a_fresh_nonce()
    {
        var cipher = Cipher();
        cipher.Encrypt("x", _provider).Nonce.ShouldNotBe(cipher.Encrypt("x", _provider).Nonce);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Tampering_with_ciphertext_nonce_or_tag_is_detected(int part)
    {
        var cipher = Cipher();
        var s = cipher.Encrypt("secret-value", _provider);
        var tampered = part switch
        {
            0 => s with { Ciphertext = Flip(s.Ciphertext) },
            1 => s with { Nonce = Flip(s.Nonce) },
            _ => s with { Tag = Flip(s.Tag) },
        };

        Should.Throw<CryptographicException>(() => cipher.Decrypt(tampered, _provider));
    }

    [Fact]
    public void A_row_moved_to_another_provider_fails_authentication()
    {
        var cipher = Cipher();
        var s = cipher.Encrypt("secret-value", _provider);

        Should.Throw<CryptographicException>(() => cipher.Decrypt(s, Guid.Parse("0192f0c8-0000-7000-8000-0000000000a2")));
    }

    [Fact]
    public void A_relabelled_key_version_fails()
    {
        var cipher = Cipher(2, _keyOne, 1, _keyOne);
        var s = cipher.Encrypt("secret-value", _provider);

        Should.Throw<CryptographicException>(() => cipher.Decrypt(s with { KeyVersion = 1 }, _provider));
    }

    [Fact]
    public void An_unknown_key_version_or_wrong_key_fails()
    {
        var s = Cipher().Encrypt("secret-value", _provider);

        Should.Throw<CryptographicException>(() => Cipher(1, _keyTwo).Decrypt(s, _provider));
        Should.Throw<CryptographicException>(() => Cipher(3, _keyTwo).Decrypt(s, _provider));
    }

    [Fact]
    public void Rows_under_the_previous_key_still_decrypt_after_rotation()
    {
        var old = Cipher(1, _keyOne).Encrypt("secret-value", _provider);
        var rotated = Cipher(2, _keyTwo, 1, _keyOne);

        rotated.Decrypt(old, _provider).ShouldBe("secret-value");
        rotated.Encrypt("secret-value", _provider).KeyVersion.ShouldBe((short)2);
    }

    [Fact]
    public void Key_ring_reports_the_environment_variable_for_a_bad_key()
    {
        var (ring, problems) = MasterKeyRing.From(Config(("Security:MasterKey", Convert.ToBase64String(new byte[16]))));

        ring.HasKey.ShouldBeFalse();
        problems.ShouldHaveSingleItem().ShouldContain("MODELSWITCH__Security__MasterKey");
    }

    [Fact]
    public void Key_ring_without_a_key_is_empty_and_not_a_problem()
    {
        var (ring, problems) = MasterKeyRing.From(Config());

        ring.HasKey.ShouldBeFalse();
        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Key_ring_requires_a_version_for_the_previous_key()
    {
        var (ring, problems) = MasterKeyRing.From(Config(
            ("Security:MasterKey", Convert.ToBase64String(_keyTwo)),
            ("Security:MasterKeyVersion", "2"),
            ("Security:PreviousMasterKey", Convert.ToBase64String(_keyOne))));

        problems.ShouldHaveSingleItem().ShouldContain("MODELSWITCH__Security__PreviousMasterKeyVersion");
        ring.HasKey.ShouldBeFalse();
    }

    [Fact]
    public void Key_ring_reads_current_and_previous_keys()
    {
        var (ring, problems) = MasterKeyRing.From(Config(
            ("Security:MasterKey", Convert.ToBase64String(_keyTwo)),
            ("Security:MasterKeyVersion", "2"),
            ("Security:PreviousMasterKey", Convert.ToBase64String(_keyOne)),
            ("Security:PreviousMasterKeyVersion", "1")));

        problems.ShouldBeEmpty();
        ring.CurrentVersion.ShouldBe((short)2);
        ring.TryGetKey(1, out _).ShouldBeTrue();
    }

    private static byte[] Flip(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        copy[0] ^= 0x01;
        return copy;
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();
}
