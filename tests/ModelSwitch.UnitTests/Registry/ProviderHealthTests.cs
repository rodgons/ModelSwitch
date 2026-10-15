using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Infrastructure.Health;
using Shouldly;

namespace ModelSwitch.UnitTests.Registry;

public sealed class ProviderHealthTests : IDisposable
{
    private static readonly Guid _model = Guid.CreateVersion7();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly ProviderHealth _health;

    public ProviderHealthTests() => _health = new ProviderHealth(new CircuitOptions(), _time, NullLogger<ProviderHealth>.Instance);

    public void Dispose() => _health.Dispose();

    private static ProviderFailure Failure(FailureClass failureClass, string code) => new(failureClass, code, null, null, RequestSent: true);

    private static ProviderFailure Transient => Failure(FailureClass.Transient, ProviderFailureCodes.ServerError);

    private Task<GuardedCall<ProviderFailure?>> CallAsync(ProviderFailure? outcome, Guid? model = null, Func<Task>? during = null)
        => _health.ExecuteAsync<ProviderFailure?>(
            model ?? _model,
            async _ =>
            {
                if (during is not null)
                {
                    await during();
                }

                return outcome;
            },
            static f => f,
            TestContext.Current.CancellationToken);

    private async Task CallManyAsync(int count, ProviderFailure? outcome)
    {
        for (var i = 0; i < count; i++)
        {
            await CallAsync(outcome);
        }
    }

    private async Task OpenAsync()
    {
        await CallManyAsync(10, Transient);
        _health.StateOf(_model).ShouldBe(CircuitState.Open);
    }

    [Fact]
    public void A_model_nobody_has_called_is_closed()
    {
        _health.StateOf(Guid.CreateVersion7()).ShouldBe(CircuitState.Closed);
        _health.OpenCircuits().ShouldBeEmpty();
        _health.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_circuit_opens_at_ten_calls_with_half_of_them_failing()
    {
        await CallManyAsync(5, null);
        await CallManyAsync(4, Transient);
        _health.StateOf(_model).ShouldBe(CircuitState.Closed, "nine calls are below the minimum throughput");

        await CallAsync(Transient);

        _health.StateOf(_model).ShouldBe(CircuitState.Open);
    }

    [Fact]
    public async Task Fewer_than_half_failing_keeps_the_circuit_closed()
    {
        await CallManyAsync(6, null);
        await CallManyAsync(4, Transient);

        _health.StateOf(_model).ShouldBe(CircuitState.Closed);
    }

    [Theory]
    [InlineData(FailureClass.Transient, ProviderFailureCodes.ServerError, true)]
    [InlineData(FailureClass.Ambiguous, ProviderFailureCodes.Timeout, true)]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.Unauthorized, true)]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.Forbidden, true)]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted, true)]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.BadRequest, false)]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.NotFound, false)]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.PayloadTooLarge, false)]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.Unprocessable, false)]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.ContextLengthExceeded, false)]
    public async Task Only_provider_health_failures_count_toward_opening(FailureClass failureClass, string code, bool opens)
    {
        await CallManyAsync(10, Failure(failureClass, code));

        _health.StateOf(_model).ShouldBe(opens ? CircuitState.Open : CircuitState.Closed);
    }

    [Fact]
    public async Task An_open_circuit_rejects_without_calling_and_reports_the_remaining_break()
    {
        await OpenAsync();
        var called = false;

        var result = await CallAsync(null, during: () =>
        {
            called = true;
            return Task.CompletedTask;
        });

        result.Rejected.ShouldBeTrue();
        called.ShouldBeFalse();
        result.RetryAfter.ShouldNotBeNull().ShouldBeInRange(TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30));
        _health.OpenCircuits().ShouldContainKey(_model);
        _time.Advance(TimeSpan.FromSeconds(10));
        _health.OpenCircuits()[_model].ShouldBeInRange(TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task After_the_break_the_circuit_is_half_open_and_a_successful_probe_closes_it()
    {
        await OpenAsync();

        _time.Advance(TimeSpan.FromSeconds(30));
        _health.StateOf(_model).ShouldBe(CircuitState.HalfOpen);
        _health.OpenCircuits().ShouldBeEmpty("a half-open circuit is eligible: its probe is what tests the Provider");

        var probe = await CallAsync(null);

        probe.Rejected.ShouldBeFalse();
        _health.StateOf(_model).ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public async Task A_half_open_circuit_admits_one_probe_at_a_time()
    {
        await OpenAsync();
        _time.Advance(TimeSpan.FromSeconds(30));
        var release = new TaskCompletionSource();

        var probe = CallAsync(null, during: () => release.Task);
        var second = await CallAsync(null);

        second.Rejected.ShouldBeTrue("other requests treat the breaker as open while the probe is in flight");
        release.SetResult();
        (await probe).Rejected.ShouldBeFalse();
        _health.StateOf(_model).ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public async Task A_failed_probe_reopens_the_circuit_and_the_break_doubles_up_to_five_minutes()
    {
        await OpenAsync();
        var expected = new[] { 60, 120, 240, 300, 300 };

        foreach (var seconds in expected)
        {
            _time.Advance(TimeSpan.FromSeconds(1_000)); // past any break
            await CallAsync(Transient); // the probe fails
            _health.StateOf(_model).ShouldBe(CircuitState.Open);
            _health.OpenCircuits()[_model].TotalSeconds.ShouldBeInRange(seconds - 1, seconds);
        }
    }

    [Fact]
    public async Task Closing_resets_the_break_to_thirty_seconds()
    {
        await OpenAsync();
        _time.Advance(TimeSpan.FromSeconds(31));
        await CallAsync(Transient); // break is now 60 s
        _time.Advance(TimeSpan.FromSeconds(61));
        await CallAsync(null); // probe succeeds: closed
        _health.StateOf(_model).ShouldBe(CircuitState.Closed);

        await OpenAsync();

        _health.OpenCircuits()[_model].TotalSeconds.ShouldBeInRange(29, 30);
    }

    [Fact]
    public async Task Failures_older_than_the_sampling_window_no_longer_count()
    {
        await CallManyAsync(9, Transient);
        _time.Advance(TimeSpan.FromSeconds(31));

        await CallManyAsync(1, Transient);

        _health.StateOf(_model).ShouldBe(CircuitState.Closed, "only one failure is inside the 30 s window");
    }

    [Fact]
    public async Task Cancellation_propagates_and_is_not_counted()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _health.ExecuteAsync<ProviderFailure?>(
            _model,
            token => Task.FromCanceled<ProviderFailure?>(token),
            static f => f,
            source.Token));

        _health.Snapshot().Single().WindowCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Circuits_are_independent_per_provider_model()
    {
        var other = Guid.CreateVersion7();
        await OpenAsync();

        var result = await CallAsync(null, other);

        result.Rejected.ShouldBeFalse();
        _health.StateOf(other).ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public async Task The_snapshot_reports_state_window_failure_rate_and_calls()
    {
        await CallManyAsync(3, null);
        await CallManyAsync(1, Transient);

        var health = _health.Snapshot().Single();

        health.ProviderModelId.ShouldBe(_model);
        health.State.ShouldBe(CircuitState.Closed);
        health.WindowCalls.ShouldBe(4);
        health.WindowFailureRate.ShouldBe(0.25);
        health.RemainingBreak.ShouldBeNull();
    }

    [Fact]
    public void Options_come_from_circuit_settings_and_fall_back_to_the_documented_defaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Circuit:SamplingWindowSeconds"] = "60",
            ["Circuit:MinimumCalls"] = "1",
            ["Circuit:FailureRatio"] = "0.75",
            ["Circuit:BreakSeconds"] = "abc",
        }).Build();

        var options = CircuitOptions.From(configuration);

        options.SamplingWindow.ShouldBe(TimeSpan.FromSeconds(60));
        options.MinimumCalls.ShouldBe(10, "Polly needs at least 2; invalid values use the default");
        options.FailureRatio.ShouldBe(0.75);
        options.BreakDuration.ShouldBe(TimeSpan.FromSeconds(30));
        options.MaxBreakDuration.ShouldBe(TimeSpan.FromMinutes(5));
        options.SnapshotInterval.ShouldBe(TimeSpan.FromSeconds(15));
    }
}
