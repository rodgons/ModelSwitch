using System.Net;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Api.Authentication;
using Shouldly;

namespace ModelSwitch.UnitTests.Api;

/// <summary>security.md section 2, "Abuse": repeated invalid API Keys from one client address are throttled per instance.</summary>
public sealed class ApiKeyFailureThrottleTests
{
    private static readonly IPAddress _client = IPAddress.Parse("203.0.113.7");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    private ApiKeyFailureThrottle Throttle() => new(_time);

    private static void Fail(ApiKeyFailureThrottle throttle, IPAddress? client, int times)
    {
        for (var i = 0; i < times; i++)
        {
            throttle.RecordFailure(client);
        }
    }

    [Fact]
    public void A_client_below_the_failure_limit_is_not_throttled()
    {
        var throttle = Throttle();

        Fail(throttle, _client, ApiKeyFailureThrottle.MaxFailuresPerWindow - 1);

        throttle.RetryAfter(_client).ShouldBeNull();
    }

    [Fact]
    public void A_client_that_reaches_the_failure_limit_is_throttled_until_the_window_ends()
    {
        var throttle = Throttle();
        Fail(throttle, _client, ApiKeyFailureThrottle.MaxFailuresPerWindow);

        _time.Advance(TimeSpan.FromSeconds(20));

        throttle.RetryAfter(_client).ShouldBe(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void The_throttle_lifts_when_the_window_ends()
    {
        var throttle = Throttle();
        Fail(throttle, _client, ApiKeyFailureThrottle.MaxFailuresPerWindow);

        _time.Advance(ApiKeyFailureThrottle.Window);

        throttle.RetryAfter(_client).ShouldBeNull();
    }

    [Fact]
    public void Failures_from_an_earlier_window_do_not_add_up()
    {
        var throttle = Throttle();
        Fail(throttle, _client, ApiKeyFailureThrottle.MaxFailuresPerWindow - 1);
        _time.Advance(ApiKeyFailureThrottle.Window);

        Fail(throttle, _client, ApiKeyFailureThrottle.MaxFailuresPerWindow - 1);

        throttle.RetryAfter(_client).ShouldBeNull();
    }

    [Fact]
    public void One_client_reaching_the_limit_does_not_throttle_another()
    {
        var throttle = Throttle();

        Fail(throttle, _client, ApiKeyFailureThrottle.MaxFailuresPerWindow);

        throttle.RetryAfter(IPAddress.Parse("198.51.100.9")).ShouldBeNull();
    }

    [Fact]
    public void Ipv6_clients_in_the_same_64_bit_prefix_share_one_failure_count()
    {
        var throttle = Throttle();

        Fail(throttle, IPAddress.Parse("2001:db8:1:2::a"), ApiKeyFailureThrottle.MaxFailuresPerWindow);

        throttle.RetryAfter(IPAddress.Parse("2001:db8:1:2::b")).ShouldNotBeNull();
        throttle.RetryAfter(IPAddress.Parse("2001:db8:1:3::a")).ShouldBeNull();
    }

    [Fact]
    public void An_ipv4_mapped_address_counts_as_the_ipv4_client()
    {
        var throttle = Throttle();

        Fail(throttle, IPAddress.Parse("::ffff:203.0.113.7"), ApiKeyFailureThrottle.MaxFailuresPerWindow);

        throttle.RetryAfter(_client).ShouldNotBeNull();
    }

    [Fact]
    public void Clients_without_a_known_address_share_one_failure_count()
    {
        var throttle = Throttle();

        Fail(throttle, null, ApiKeyFailureThrottle.MaxFailuresPerWindow);

        throttle.RetryAfter(null).ShouldNotBeNull();
    }
}
