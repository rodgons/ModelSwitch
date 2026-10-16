using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Identity.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Identity;

public sealed class AdministratorDomainTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static Administrator NewAdministrator() => new() { Id = Guid.CreateVersion7(), Email = "a@example.com", PasswordHash = "x" };

    [Fact]
    public void Four_failures_do_not_lock_the_account()
    {
        var administrator = NewAdministrator();

        for (var i = 0; i < 4; i++)
        {
            administrator.RegisterFailedSignIn(_start.AddMinutes(i));
        }

        administrator.IsLockedOut(_start.AddMinutes(4)).ShouldBeFalse();
    }

    [Fact]
    public void The_fifth_failure_inside_fifteen_minutes_locks_the_account_for_fifteen_minutes()
    {
        var administrator = NewAdministrator();

        for (var i = 0; i < 5; i++)
        {
            administrator.RegisterFailedSignIn(_start.AddMinutes(i));
        }

        var lockedAt = _start.AddMinutes(4);
        administrator.IsLockedOut(lockedAt).ShouldBeTrue();
        administrator.IsLockedOut(lockedAt.AddMinutes(14)).ShouldBeTrue();
        administrator.IsLockedOut(lockedAt.AddMinutes(15)).ShouldBeFalse();
    }

    [Fact]
    public void Failures_more_than_fifteen_minutes_apart_never_add_up_to_a_lockout()
    {
        var administrator = NewAdministrator();

        for (var i = 0; i < 10; i++)
        {
            administrator.RegisterFailedSignIn(_start.AddMinutes(i * 16));
        }

        administrator.IsLockedOut(_start.AddMinutes(9 * 16)).ShouldBeFalse();
    }

    [Fact]
    public void A_failure_while_locked_does_not_extend_the_lockout()
    {
        var administrator = NewAdministrator();
        for (var i = 0; i < 5; i++)
        {
            administrator.RegisterFailedSignIn(_start);
        }

        var until = administrator.LockedUntil;
        administrator.RegisterFailedSignIn(_start.AddMinutes(10));

        administrator.LockedUntil.ShouldBe(until);
    }

    [Fact]
    public void A_successful_sign_in_clears_failures_and_lockout_and_stamps_the_time()
    {
        var administrator = NewAdministrator();
        for (var i = 0; i < 3; i++)
        {
            administrator.RegisterFailedSignIn(_start);
        }

        administrator.RegisterSuccessfulSignIn(_start.AddMinutes(1));

        administrator.FailedSignInCount.ShouldBe(0);
        administrator.LockedUntil.ShouldBeNull();
        administrator.LastSignedInAt.ShouldBe(_start.AddMinutes(1));
    }

    [Theory]
    [InlineData("  Admin@Example.COM ", "admin@example.com")]
    [InlineData("a@b.co", "a@b.co")]
    public void Emails_are_trimmed_and_lower_cased(string raw, string expected) => Administrator.NormalizeEmail(raw).ShouldBe(expected);

    [Theory]
    [InlineData(null, "The password is required.")]
    [InlineData("", "The password is required.")]
    [InlineData("short", "The password must be at least 12 characters.")]
    [InlineData("elevenchars", "The password must be at least 12 characters.")]
    public void Passwords_shorter_than_twelve_characters_are_rejected(string? password, string expected) => PasswordPolicy.Violation(password).ShouldBe(expected);

    [Fact]
    public void Twelve_characters_is_enough_and_an_absurdly_long_password_is_not()
    {
        PasswordPolicy.Violation(new string('a', 12)).ShouldBeNull();
        PasswordPolicy.Violation(new string('a', PasswordPolicy.MaximumLength + 1)).ShouldNotBeNull();
    }

    [Fact]
    public void A_session_slides_by_eight_hours_but_never_past_twenty_four()
    {
        var time = new FakeTimeProvider(_start);
        var session = AdministratorSession.Start("id", Guid.CreateVersion7(), time.GetUtcNow());
        session.ExpiresAt.ShouldBe(_start.AddHours(8));
        session.AbsoluteExpiresAt.ShouldBe(_start.AddHours(24));

        session.Renew(_start.AddHours(7));
        session.ExpiresAt.ShouldBe(_start.AddHours(15));

        session.Renew(_start.AddHours(20));
        session.ExpiresAt.ShouldBe(_start.AddHours(24));
    }

    [Fact]
    public void A_session_is_expired_after_the_sliding_period_and_after_the_absolute_limit()
    {
        var session = AdministratorSession.Start("id", Guid.CreateVersion7(), _start);

        session.IsExpired(_start.AddHours(8).AddSeconds(-1)).ShouldBeFalse();
        session.IsExpired(_start.AddHours(8)).ShouldBeTrue();

        session.Renew(_start.AddHours(23));
        session.IsExpired(_start.AddHours(23.9)).ShouldBeFalse();
        session.IsExpired(_start.AddHours(24)).ShouldBeTrue();
    }
}
