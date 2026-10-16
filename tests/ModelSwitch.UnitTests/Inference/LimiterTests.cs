using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Configuration.Contracts;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

/// <summary>The decision logic of step 6 (routing-and-resilience.md section 7) over a fake store; the Lua script is covered by the integration tests.</summary>
public sealed class LimiterTests : IDisposable
{
    private static readonly Guid _tenant = new("0192f0c8-0000-7000-8000-000000000001");
    private static readonly Guid _policyId = new("0192f0c8-0000-7000-8000-0000000000a1");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeStore _store = new();
    private readonly TestMeters _meters = new();
    private readonly Dictionary<string, long> _counts = [];
    private readonly MeterListener _listener = new();
    private TenantLimits? _limits;

    public LimiterTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == LimitsMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var key = tags.Length == 0 ? instrument.Name : $"{instrument.Name}:{tags[0].Value}";
            _counts[key] = _counts.GetValueOrDefault(key) + value;
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _meters.Dispose();
    }

    private Limiter Limiter(LimitsFailureMode mode = LimitsFailureMode.Closed, ExecutionSettings? execution = null)
    {
        var routing = execution is null
            ? new RoutingSnapshot(new Dictionary<string, Guid>(), new Dictionary<Guid, PolicySnapshot>(), new Dictionary<Guid, CandidateModel>())
            : new RoutingSnapshot(
                new Dictionary<string, Guid> { ["balanced"] = _policyId },
                new Dictionary<Guid, PolicySnapshot>
                {
                    [_policyId] = new(_policyId, "p", 1, new PolicyDefinition(PolicyStrategy.Static, [], [], [], null, null, 100, null, execution)),
                },
                new Dictionary<Guid, CandidateModel>());
        var limits = _limits is null ? new Dictionary<Guid, TenantLimits>() : new Dictionary<Guid, TenantLimits> { [_tenant] = _limits };
        var snapshot = new ConfigurationSnapshot(1, _time.GetUtcNow(), routing, limits);
        return new Limiter(
            new FakeSnapshots(snapshot),
            _store,
            new LimitsOptions { FailureMode = mode, ValkeyTimeout = TimeSpan.FromMilliseconds(50) },
            new LimitsMetrics(_meters),
            _time,
            NullLogger<Limiter>.Instance);
    }

    private static LimitRequest Request(TimeSpan? timeout = null) => new(_tenant, Guid.CreateVersion7(), "balanced", timeout);

    private static TenantLimits Limits(int? rpm, int? concurrent) => new(_tenant, rpm, concurrent, null, ["*"]);

    [Fact]
    public async Task A_tenant_without_limits_is_admitted_without_asking_the_store()
    {
        var result = await Limiter().AcquireAsync(Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Rate.ShouldBeNull();
        _store.Acquires.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tenant_whose_limits_are_all_null_is_admitted_without_asking_the_store()
    {
        _limits = Limits(null, null);

        (await Limiter().AcquireAsync(Request(), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        _store.Acquires.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_tenants_limits_from_the_snapshot_go_to_the_store_in_one_call()
    {
        _limits = Limits(rpm: 120, concurrent: 4);
        _store.Next = new LimitDecision(LimitOutcome.Allowed, TimeSpan.Zero, new RateLimitState(120, 11, TimeSpan.FromSeconds(5)));
        var request = Request();

        var result = await Limiter().AcquireAsync(request, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Rate.ShouldBe(new RateLimitState(120, 11, TimeSpan.FromSeconds(5)));
        var call = _store.Acquires.ShouldHaveSingleItem();
        (call.TenantId, call.LeaseId, call.Rpm, call.MaxConcurrent).ShouldBe((_tenant, request.LeaseId, 120, 4));
    }

    [Fact]
    public async Task Disposing_the_lease_releases_it_once()
    {
        _limits = Limits(rpm: null, concurrent: 2);
        var request = Request();
        var lease = (await Limiter().AcquireAsync(request, TestContext.Current.CancellationToken)).Value;

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        _store.Releases.ShouldBe([(_tenant, request.LeaseId)]);
    }

    [Fact]
    public async Task A_rate_limit_only_tenant_holds_no_lease_to_release()
    {
        _limits = Limits(rpm: 60, concurrent: null);
        var lease = (await Limiter().AcquireAsync(Request(), TestContext.Current.CancellationToken)).Value;

        await lease.DisposeAsync();

        _store.Releases.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rate_limited_request_is_a_429_with_the_stores_retry_after_and_is_counted()
    {
        _limits = Limits(rpm: 60, concurrent: null);
        _store.Next = new LimitDecision(LimitOutcome.RateLimited, TimeSpan.FromMilliseconds(1500), null);

        var result = await Limiter().AcquireAsync(Request(), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCode.RateLimitExceeded);
        result.Error.RetryAfter.ShouldBe(TimeSpan.FromMilliseconds(1500));
        _counts["modelswitch.limits.rejections:rate"].ShouldBe(1);
    }

    [Fact]
    public async Task A_request_over_the_concurrency_limit_is_a_429_and_is_counted()
    {
        _limits = Limits(rpm: null, concurrent: 1);
        _store.Next = new LimitDecision(LimitOutcome.ConcurrencyLimited, TimeSpan.FromSeconds(1), null);

        var result = await Limiter().AcquireAsync(Request(), TestContext.Current.CancellationToken);

        result.Error.Code.ShouldBe(ErrorCode.ConcurrencyLimitExceeded);
        result.Error.RetryAfter.ShouldBe(TimeSpan.FromSeconds(1));
        _counts["modelswitch.limits.rejections:concurrency"].ShouldBe(1);
    }

    [Fact]
    public async Task When_the_store_fails_and_the_mode_is_closed_the_request_gets_limits_unavailable()
    {
        _limits = Limits(rpm: 60, concurrent: 2);
        _store.Failure = new InvalidOperationException("connection refused");

        var result = await Limiter(LimitsFailureMode.Closed).AcquireAsync(Request(), TestContext.Current.CancellationToken);

        result.Error.Code.ShouldBe(ErrorCode.LimitsUnavailable);
        result.Error.RetryAfter.ShouldNotBeNull();
        _counts.ShouldNotContainKey("modelswitch.limits.degraded");
    }

    [Fact]
    public async Task When_the_store_fails_and_the_mode_is_open_the_request_is_admitted_and_counted_as_degraded()
    {
        _limits = Limits(rpm: 60, concurrent: 2);
        _store.Failure = new InvalidOperationException("connection refused");

        var result = await Limiter(LimitsFailureMode.Open).AcquireAsync(Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Rate.ShouldBeNull();
        _counts["modelswitch.limits.degraded"].ShouldBe(1);
        await result.Value.DisposeAsync();
        _store.Releases.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_store_that_outlasts_the_budget_is_treated_as_unavailable()
    {
        _limits = Limits(rpm: 60, concurrent: null);
        _store.Hang = true;
        var pending = Limiter(LimitsFailureMode.Closed).AcquireAsync(Request(), TestContext.Current.CancellationToken);

        pending.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(51));
        var result = await pending;

        result.Error.Code.ShouldBe(ErrorCode.LimitsUnavailable);
    }

    [Fact]
    public async Task A_failing_release_does_not_throw_because_the_lease_expires_by_itself()
    {
        _limits = Limits(rpm: null, concurrent: 2);
        var lease = (await Limiter().AcquireAsync(Request(), TestContext.Current.CancellationToken)).Value;
        _store.Failure = new InvalidOperationException("connection reset");

        await Should.NotThrowAsync(async () => await lease.DisposeAsync());
    }

    [Fact]
    public async Task The_lease_lives_for_the_policys_deadline_plus_five_seconds()
    {
        _limits = Limits(rpm: null, concurrent: 2);
        var execution = ExecutionSettings.Default with { DeadlineMs = 20_000 };

        await Limiter(execution: execution).AcquireAsync(Request(), TestContext.Current.CancellationToken);

        _store.Acquires.Single().Ttl.ShouldBe(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task A_shorter_client_timeout_shortens_the_lease()
    {
        _limits = Limits(rpm: null, concurrent: 2);
        var execution = ExecutionSettings.Default with { DeadlineMs = 20_000 };

        await Limiter(execution: execution).AcquireAsync(Request(TimeSpan.FromSeconds(3)), TestContext.Current.CancellationToken);

        _store.Acquires.Single().Ttl.ShouldBe(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task A_longer_client_timeout_cannot_extend_the_lease_past_the_deadline()
    {
        _limits = Limits(rpm: null, concurrent: 2);
        var execution = ExecutionSettings.Default with { DeadlineMs = 20_000 };

        await Limiter(execution: execution).AcquireAsync(Request(TimeSpan.FromMinutes(10)), TestContext.Current.CancellationToken);

        _store.Acquires.Single().Ttl.ShouldBe(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task An_unknown_alias_gets_the_default_deadline()
    {
        _limits = Limits(rpm: null, concurrent: 2);

        await Limiter().AcquireAsync(Request(), TestContext.Current.CancellationToken);

        _store.Acquires.Single().Ttl.ShouldBe(TimeSpan.FromMilliseconds(ExecutionSettings.Default.DeadlineMs) + LimitsOptions.LeaseGrace);
    }

    private sealed class FakeSnapshots(ConfigurationSnapshot snapshot) : IConfigurationSnapshotProvider
    {
        public bool IsLoaded => true;

        public ConfigurationSnapshot Current { get; } = snapshot;
    }

    private sealed record AcquireCall(Guid TenantId, Guid LeaseId, int? Rpm, int? MaxConcurrent, TimeSpan Ttl);

    private sealed class FakeStore : ILimitStore
    {
        public List<AcquireCall> Acquires { get; } = [];

        public List<(Guid TenantId, Guid LeaseId)> Releases { get; } = [];

        public LimitDecision Next { get; set; } = new(LimitOutcome.Allowed, TimeSpan.Zero, null);

        public Exception? Failure { get; set; }

        public bool Hang { get; set; }

        public async Task<LimitDecision> TryAcquireAsync(Guid tenantId, Guid leaseId, int? requestsPerMinute, int? maxConcurrent, TimeSpan leaseTtl, CancellationToken cancellationToken)
        {
            Acquires.Add(new AcquireCall(tenantId, leaseId, requestsPerMinute, maxConcurrent, leaseTtl));
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Failure is null ? Next : throw Failure;
        }

        public Task ReleaseAsync(Guid tenantId, Guid leaseId, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Releases.Add((tenantId, leaseId));
            return Task.CompletedTask;
        }
    }

    private sealed class TestMeters : IMeterFactory
    {
        private readonly List<Meter> _created = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
            _created.Add(meter);
            return meter;
        }

        public void Dispose() => _created.ForEach(m => m.Dispose());
    }
}
