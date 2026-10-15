using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Application;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Infrastructure.Health;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

public sealed class AttemptExecutorTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(_start);
    private readonly Fleet _fleet = new();
    private readonly FixedJitter _jitter = new(0.5);
    private readonly ProviderHealth _health;
    private readonly AttemptExecutor _executor;
    private readonly List<AttemptRecord> _attempts = [];

    public AttemptExecutorTests()
    {
        _health = new ProviderHealth(new CircuitOptions(), _time, NullLogger<ProviderHealth>.Instance);
        _executor = new AttemptExecutor(_fleet, _health, new InputTokenEstimator(), _jitter, _time, NullLogger<AttemptExecutor>.Instance);
    }

    public void Dispose() => _health.Dispose();

    private static NeutralChatRequest Chat() => new([new NeutralMessage(MessageRole.User, "hi")]);

    private static CompletionOutcome.Success Ok() => new CompletionOutcome.Success("ok", FinishReason.Stop, new NeutralUsage(3, 4, null));

    private static CompletionOutcome.Failure Fail(FailureClass failureClass, string code, int? status = null, TimeSpan? retryAfter = null)
        => new CompletionOutcome.Failure(new ProviderFailure(failureClass, code, status, retryAfter, RequestSent: true));

    private static CompletionOutcome.Failure Http503() => Fail(FailureClass.Transient, ProviderFailureCodes.ServerError, 503);

    private ExecutionPlan Plan(IReadOnlyList<string> models, int maxAttempts = 4, int maxRetries = 1, int deadlineMs = 60_000, int attemptTimeoutMs = 30_000)
        => new(
            [.. models.Select(_fleet.Candidate)],
            new ExecutionSettings(deadlineMs, attemptTimeoutMs, maxAttempts, maxRetries, new BackoffSettings(200, 2_000, FullJitter: true)),
            _time.GetUtcNow().AddMilliseconds(deadlineMs));

    // The executor waits on the fake clock, so the clock has to be driven until it finishes.
    private async Task<Result<CompletionOutcome.Success>> RunAsync(ExecutionPlan plan)
    {
        var pending = _executor.ExecuteAsync(plan, Chat(), _attempts, TestContext.Current.CancellationToken);
        while (!pending.IsCompleted)
        {
            _time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Yield();
        }

        return await pending;
    }

    [Fact]
    public async Task The_first_candidate_answering_is_one_primary_attempt()
    {
        _fleet.Script("a", Ok());

        var result = await RunAsync(Plan(["a"]));

        result.IsSuccess.ShouldBeTrue();
        var attempt = _attempts.Single();
        (attempt.AttemptNumber, attempt.Kind, attempt.Status, attempt.ErrorClass).ShouldBe((1, AttemptKind.Primary, AttemptStatus.Succeeded, null));
        (attempt.InputTokens, attempt.OutputTokens, attempt.UsageSource).ShouldBe((3, 4, UsageSource.Reported));
    }

    [Fact]
    public async Task Two_503s_then_success_is_three_attempts_on_the_same_model_when_retries_allow()
    {
        _fleet.Script("a", Http503(), Http503(), Ok());

        var result = await RunAsync(Plan(["a"], maxRetries: 2));

        result.IsSuccess.ShouldBeTrue();
        _attempts.Select(a => (a.AttemptNumber, a.Kind, a.Status)).ShouldBe(
        [
            (1, AttemptKind.Primary, AttemptStatus.Failed),
            (2, AttemptKind.Retry, AttemptStatus.Failed),
            (3, AttemptKind.Retry, AttemptStatus.Succeeded),
        ]);
        _attempts[0].ErrorClass.ShouldBe(AttemptErrorClass.Transient);
        _attempts[0].HttpStatus.ShouldBe(503);
        _attempts[0].ProviderErrorType.ShouldBe("server_error");
        _attempts[0].ErrorCode.ShouldBe("provider_unavailable");
    }

    [Fact]
    public async Task A_transient_failure_that_exhausts_its_retries_falls_back_to_the_next_candidate()
    {
        _fleet.Script("a", Http503(), Http503());
        _fleet.Script("b", Ok());

        var result = await RunAsync(Plan(["a", "b"]));

        result.IsSuccess.ShouldBeTrue();
        _attempts.Select(a => (a.Kind, a.ProviderModelId == _fleet.IdOf("a"))).ShouldBe(
        [
            (AttemptKind.Primary, true),
            (AttemptKind.Retry, true),
            (AttemptKind.Fallback, false),
        ]);
        _fleet.Calls("b").ShouldBe(1);
    }

    [Fact]
    public async Task An_ambiguous_outcome_is_not_retried_on_the_same_model_but_falls_back()
    {
        _fleet.Script("a", Fail(FailureClass.Ambiguous, ProviderFailureCodes.Timeout), Ok());
        _fleet.Script("b", Ok());

        var result = await RunAsync(Plan(["a", "b"]));

        result.IsSuccess.ShouldBeTrue();
        _fleet.Calls("a").ShouldBe(1);
        _attempts.Select(a => (a.Kind, a.Status, a.ProviderModelId == _fleet.IdOf("a"))).ShouldBe(
        [
            (AttemptKind.Primary, AttemptStatus.Ambiguous, true),
            (AttemptKind.Fallback, AttemptStatus.Succeeded, false),
        ]);
        _attempts[0].ErrorClass.ShouldBe(AttemptErrorClass.Ambiguous);
        _attempts[0].OutputTokens.ShouldBeNull("an ambiguous Attempt may have been billed; its output is unknown");
        _attempts[0].UsageSource.ShouldBe(UsageSource.Unavailable);
    }

    [Fact]
    public async Task An_ambiguous_outcome_with_no_other_candidate_fails_after_one_attempt()
    {
        _fleet.Script("a", Fail(FailureClass.Ambiguous, ProviderFailureCodes.ConnectionReset), Ok());

        var result = await RunAsync(Plan(["a"]));

        result.Error.Code.ShouldBe(ErrorCode.ProviderError);
        _fleet.Calls("a").ShouldBe(1);
    }

    [Fact]
    public async Task A_permanent_request_error_stops_without_retry_or_fallback()
    {
        _fleet.Script("a", Fail(FailureClass.Permanent, ProviderFailureCodes.BadRequest, 400));
        _fleet.Script("b", Ok());

        var result = await RunAsync(Plan(["a", "b"]));

        result.Error.Code.ShouldBe(ErrorCode.ProviderError);
        _fleet.Calls("a").ShouldBe(1);
        _fleet.Calls("b").ShouldBe(0);
        _attempts.Single().ErrorClass.ShouldBe(AttemptErrorClass.Permanent);
    }

    [Theory]
    [InlineData(ProviderFailureCodes.Unauthorized, 401)]
    [InlineData(ProviderFailureCodes.Forbidden, 403)]
    [InlineData(ProviderFailureCodes.QuotaExhausted, 402)]
    [InlineData(ProviderFailureCodes.ContextLengthExceeded, 400)]
    public async Task A_provider_side_permanent_failure_is_not_retried_but_falls_back(string code, int status)
    {
        _fleet.Script("a", Fail(FailureClass.Permanent, code, status));
        _fleet.Script("b", Ok());

        var result = await RunAsync(Plan(["a", "b"]));

        result.IsSuccess.ShouldBeTrue();
        _fleet.Calls("a").ShouldBe(1);
        _attempts.Select(a => a.Kind).ShouldBe([AttemptKind.Primary, AttemptKind.Fallback]);
    }

    [Fact]
    public async Task Context_length_exceeded_on_every_candidate_is_reported_as_such()
    {
        _fleet.Script("a", Fail(FailureClass.Permanent, ProviderFailureCodes.ContextLengthExceeded, 400));
        _fleet.Script("b", Fail(FailureClass.Permanent, ProviderFailureCodes.ContextLengthExceeded, 400));

        var result = await RunAsync(Plan(["a", "b"]));

        result.Error.Code.ShouldBe(ErrorCode.ContextLengthExceeded);
    }

    [Fact]
    public async Task Fallbacks_are_tried_in_candidate_order()
    {
        _fleet.Script("a", Http503());
        _fleet.Script("b", Http503());
        _fleet.Script("c", Ok());
        var order = new List<string>();
        _fleet.OnCall = model => order.Add(model);

        var result = await RunAsync(Plan(["a", "b", "c"], maxRetries: 0));

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(["a", "b", "c"]);
        _attempts.Select(a => a.Kind).ShouldBe([AttemptKind.Primary, AttemptKind.Fallback, AttemptKind.Fallback]);
    }

    [Fact]
    public async Task Total_attempts_never_exceed_max_attempts_across_candidates()
    {
        foreach (var model in new[] { "a", "b", "c", "d", "e" })
        {
            _fleet.Script(model, Http503());
        }

        var result = await RunAsync(Plan(["a", "b", "c", "d", "e"], maxAttempts: 3, maxRetries: 1));

        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        _fleet.TotalCalls.ShouldBe(3);
        _attempts.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Exhausting_all_candidates_returns_the_last_failure_normalized()
    {
        _fleet.Script("a", Http503());
        _fleet.Script("b", Fail(FailureClass.Permanent, ProviderFailureCodes.Unauthorized, 401));

        var result = await RunAsync(Plan(["a", "b"], maxRetries: 0));

        result.Error.Code.ShouldBe(ErrorCode.ProviderError);
        _attempts.Count.ShouldBe(2);
    }

    // ---- Retry-After and backoff ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_retry_after_that_fits_the_deadline_is_waited_for_then_the_model_is_retried()
    {
        _fleet.Script("a", Fail(FailureClass.Transient, ProviderFailureCodes.RateLimited, 429, TimeSpan.FromSeconds(1)), Ok());
        var callTimes = new List<DateTimeOffset>();
        _fleet.OnCall = _ => callTimes.Add(_time.GetUtcNow());

        var result = await RunAsync(Plan(["a"]));

        result.IsSuccess.ShouldBeTrue();
        (callTimes[1] - callTimes[0]).ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.1));
        _attempts.Select(a => a.Kind).ShouldBe([AttemptKind.Primary, AttemptKind.Retry]);
    }

    [Fact]
    public async Task A_retry_after_longer_than_the_cap_is_not_waited_for_and_falls_back_at_once()
    {
        _fleet.Script("a", Fail(FailureClass.Transient, ProviderFailureCodes.RateLimited, 429, TimeSpan.FromSeconds(120)), Ok());
        _fleet.Script("b", Ok());

        var result = await RunAsync(Plan(["a", "b"]));

        result.IsSuccess.ShouldBeTrue();
        _fleet.Calls("a").ShouldBe(1);
        _fleet.Calls("b").ShouldBe(1);
        (_time.GetUtcNow() - _start).ShouldBeLessThan(TimeSpan.FromSeconds(1), "no waiting");
    }

    [Fact]
    public async Task A_retry_after_that_does_not_fit_the_remaining_deadline_falls_back()
    {
        // Deadline 10 s, attempt timeout 8 s: 3 s of Retry-After leaves 7 s, less than one Attempt's 8 s.
        _fleet.Script("a", Fail(FailureClass.Transient, ProviderFailureCodes.RateLimited, 429, TimeSpan.FromSeconds(3)), Ok());
        _fleet.Script("b", Ok());

        var result = await RunAsync(Plan(["a", "b"], deadlineMs: 10_000, attemptTimeoutMs: 8_000));

        result.IsSuccess.ShouldBeTrue();
        _fleet.Calls("a").ShouldBe(1);
        _attempts.Select(a => a.Kind).ShouldBe([AttemptKind.Primary, AttemptKind.Fallback]);
    }

    [Fact]
    public async Task Backoff_waits_the_jittered_exponential_before_a_retry()
    {
        _jitter.Value = 0.5;
        _fleet.Script("a", Http503(), Http503(), Ok());
        var callTimes = new List<DateTimeOffset>();
        _fleet.OnCall = _ => callTimes.Add(_time.GetUtcNow());

        await RunAsync(Plan(["a"], maxRetries: 2));

        // base 200 ms: first retry waits 200 * 0.5 = 100 ms, second 400 * 0.5 = 200 ms.
        (callTimes[1] - callTimes[0]).ShouldBeInRange(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));
        (callTimes[2] - callTimes[1]).ShouldBeInRange(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(250));
    }

    // ---- Deadline and timeouts -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_attempt_timeout_is_the_smallest_of_policy_provider_and_remaining_deadline()
    {
        _fleet.Script("a", Ok());
        TimeSpan? seen = null;
        _fleet.OnContext = context => seen = context.AttemptTimeout;
        _fleet.RequestTimeout = TimeSpan.FromSeconds(10);

        await RunAsync(Plan(["a"], deadlineMs: 60_000, attemptTimeoutMs: 30_000));
        seen.ShouldBe(TimeSpan.FromSeconds(10), "the Provider's own request timeout is smaller");

        _attempts.Clear();
        _fleet.RequestTimeout = TimeSpan.FromMinutes(5);
        await RunAsync(Plan(["a"], deadlineMs: 60_000, attemptTimeoutMs: 30_000));
        seen.ShouldBe(TimeSpan.FromSeconds(30), "the policy's Attempt timeout is smaller");

        _attempts.Clear();
        await RunAsync(Plan(["a"], deadlineMs: 5_000, attemptTimeoutMs: 30_000));
        seen.ShouldBe(TimeSpan.FromSeconds(5), "the remaining Deadline is smaller");
    }

    [Fact]
    public async Task Reaching_the_deadline_stops_the_loop_with_deadline_exceeded()
    {
        _fleet.Script("a", Http503(), Http503(), Http503());
        _fleet.OnCall = _ => _time.Advance(TimeSpan.FromSeconds(6)); // each Attempt takes 6 s of a 10 s Deadline

        var result = await RunAsync(Plan(["a", "b"], deadlineMs: 10_000, attemptTimeoutMs: 6_000, maxRetries: 3));

        result.Error.Code.ShouldBe(ErrorCode.DeadlineExceeded);
        _fleet.TotalCalls.ShouldBeLessThanOrEqualTo(2);
        _fleet.Calls("b").ShouldBe(0, "no Attempt starts at or after the Deadline");
    }

    [Fact]
    public async Task A_deadline_already_passed_makes_no_attempt()
    {
        _fleet.Script("a", Ok());
        var plan = Plan(["a"]) with { Deadline = _time.GetUtcNow() };

        var result = await RunAsync(plan);

        result.Error.Code.ShouldBe(ErrorCode.DeadlineExceeded);
        _fleet.TotalCalls.ShouldBe(0);
    }

    // ---- Cancellation ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cancelled_token_stops_before_the_first_attempt()
    {
        _fleet.Script("a", Ok());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _executor.ExecuteAsync(Plan(["a"]), Chat(), _attempts, cancelled.Token));

        _fleet.TotalCalls.ShouldBe(0);
        _attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_during_a_backoff_wait_stops_the_loop_without_another_attempt()
    {
        _fleet.Script("a", Http503(), Ok());
        using var source = new CancellationTokenSource();

        var pending = _executor.ExecuteAsync(Plan(["a"]), Chat(), _attempts, source.Token);
        while (_attempts.Count == 0)
        {
            await Task.Yield();
        }

        await source.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        _fleet.Calls("a").ShouldBe(1);
        _attempts.Single().Status.ShouldBe(AttemptStatus.Failed);
    }

    [Fact]
    public async Task Cancellation_while_an_attempt_is_in_flight_records_it_ambiguous_and_rethrows()
    {
        _fleet.Script("a", Ok());
        using var source = new CancellationTokenSource();
        _fleet.OnCall = _ => source.Cancel();
        _fleet.ThrowCancelledOnCall = true;

        await Should.ThrowAsync<OperationCanceledException>(() => _executor.ExecuteAsync(Plan(["a"]), Chat(), _attempts, source.Token));

        var attempt = _attempts.Single();
        attempt.Status.ShouldBe(AttemptStatus.Ambiguous);
        attempt.ProviderErrorType.ShouldBe("client_cancelled");
        _health.StateOf(_fleet.IdOf("a")).ShouldBe(CircuitState.Closed, "a cancellation says nothing about the Provider");
        _health.Snapshot().ShouldAllBe(h => h.WindowCalls == 0);
    }

    // ---- Circuit breakers -------------------------------------------------------------------------------------------

    private async Task OpenCircuitOfAsync(string model)
    {
        for (var i = 0; i < 10; i++)
        {
            await _health.ExecuteAsync(
                _fleet.IdOf(model),
                _ => Task.FromResult(Http503()),
                static outcome => outcome is CompletionOutcome.Failure f ? f.Error : null,
                CancellationToken.None);
        }

        _health.StateOf(_fleet.IdOf(model)).ShouldBe(CircuitState.Open);
    }

    [Fact]
    public async Task A_candidate_whose_circuit_is_open_is_skipped_without_calling_its_provider()
    {
        _fleet.Script("a", Ok());
        _fleet.Script("b", Ok());
        await OpenCircuitOfAsync("a");
        _fleet.ResetCalls();

        var result = await RunAsync(Plan(["a", "b"]));

        result.IsSuccess.ShouldBeTrue();
        _fleet.Calls("a").ShouldBe(0);
        _attempts.Select(a => (a.AttemptNumber, a.Kind, a.Status)).ShouldBe(
        [
            (1, AttemptKind.Primary, AttemptStatus.SkippedCircuitOpen),
            (2, AttemptKind.Fallback, AttemptStatus.Succeeded),
        ]);
    }

    [Fact]
    public async Task A_skipped_attempt_does_not_count_toward_max_attempts()
    {
        _fleet.Script("a", Ok());
        _fleet.Script("b", Http503(), Ok());
        await OpenCircuitOfAsync("a");

        var result = await RunAsync(Plan(["a", "b"], maxAttempts: 2));

        result.IsSuccess.ShouldBeTrue();
        _fleet.Calls("b").ShouldBe(2);
    }

    [Fact]
    public async Task When_every_circuit_is_open_the_request_fails_provider_unavailable_without_any_call()
    {
        _fleet.Script("a", Ok());
        _fleet.Script("b", Ok());
        await OpenCircuitOfAsync("a");
        await OpenCircuitOfAsync("b");
        _fleet.ResetCalls();

        var result = await RunAsync(Plan(["a", "b"]));

        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        _fleet.TotalCalls.ShouldBe(0);
        _attempts.ShouldAllBe(a => a.Status == AttemptStatus.SkippedCircuitOpen);
        _attempts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Failures_are_fed_to_the_breaker_so_a_failing_model_trips_after_enough_requests()
    {
        _fleet.Script("a", Http503());
        _fleet.Script("b", Ok());
        for (var request = 0; request < 5; request++)
        {
            _attempts.Clear();
            (await RunAsync(Plan(["a", "b"], maxRetries: 1))).IsSuccess.ShouldBeTrue();
        }

        // Five requests made two failing Attempts each: ten failures reach the minimum throughput and open the circuit.
        _health.StateOf(_fleet.IdOf("a")).ShouldBe(CircuitState.Open);
        _attempts.Clear();
        _fleet.ResetCalls();
        (await RunAsync(Plan(["a", "b"]))).IsSuccess.ShouldBeTrue();
        _fleet.Calls("a").ShouldBe(0);
        _attempts[0].Status.ShouldBe(AttemptStatus.SkippedCircuitOpen);
    }

    [Fact]
    public async Task A_candidate_that_cannot_be_bound_is_passed_over()
    {
        _fleet.Script("a", Ok());
        _fleet.Script("b", Ok());
        _fleet.Unbindable.Add("a");

        var result = await RunAsync(Plan(["a", "b"]));

        result.IsSuccess.ShouldBeTrue();
        _fleet.Calls("a").ShouldBe(0);
        _attempts.Single().Kind.ShouldBe(AttemptKind.Fallback, "the second Candidate served the request");
    }

    [Fact]
    public async Task When_no_candidate_can_be_bound_the_binding_error_is_returned()
    {
        _fleet.Script("a", Ok());
        _fleet.Unbindable.Add("a");

        var result = await RunAsync(Plan(["a"]));

        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        _attempts.ShouldBeEmpty();
    }

    // ---- fakes ------------------------------------------------------------------------------------------------------

    private sealed class FixedJitter(double value) : IJitterSource
    {
        public double Value { get; set; } = value;

        public double Next() => Value;
    }

    /// <summary>One scripted Provider Model per name, serving as adapter factory and adapter.</summary>
    private sealed class Fleet : IAdapterFactory
    {
        private readonly Dictionary<string, Guid> _ids = [];
        private readonly Dictionary<string, Queue<CompletionOutcome>> _scripts = [];
        private readonly Dictionary<string, int> _calls = [];

        public HashSet<string> Unbindable { get; } = [];

        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public Action<string>? OnCall { get; set; }

        public Action<AdapterContext>? OnContext { get; set; }

        public bool ThrowCancelledOnCall { get; set; }

        public int TotalCalls => _calls.Values.Sum();

        public Guid IdOf(string model) => _ids.TryGetValue(model, out var id) ? id : _ids[model] = Guid.NewGuid();

        public int Calls(string model) => _calls.GetValueOrDefault(model);

        public void ResetCalls() => _calls.Clear();

        public void Script(string model, params CompletionOutcome[] outcomes)
        {
            _scripts[model] = new Queue<CompletionOutcome>(outcomes);
            _ = IdOf(model);
        }

        public RankedCandidate Candidate(string model) => new(
            IdOf(model),
            IdOf(model),
            "p",
            model,
            0m,
            new ProviderModelTarget(IdOf(model), IdOf(model), "p", "fake", new Uri("http://x/"), null, RequestTimeout, model));

        public ValueTask<Result<AdapterBinding>> BindAsync(ProviderModelTarget target, CancellationToken cancellationToken)
            => ValueTask.FromResult<Result<AdapterBinding>>(
                Unbindable.Contains(target.ProviderModelIdentifier)
                    ? new Error(ErrorCode.ProviderUnavailable, "no credential")
                    : new AdapterBinding(new ScriptedAdapter(this, target.ProviderModelIdentifier), new AdapterContext(target.Endpoint, null, target.ProviderModelIdentifier, target.RequestTimeout)));

        private sealed class ScriptedAdapter(Fleet fleet, string model) : IProviderAdapter
        {
            public string AdapterType => "fake";

            public Task<CompletionOutcome> CompleteAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct)
            {
                fleet._calls[model] = fleet._calls.GetValueOrDefault(model) + 1;
                fleet.OnContext?.Invoke(ctx);
                fleet.OnCall?.Invoke(model);
                if (fleet.ThrowCancelledOnCall)
                {
                    throw new OperationCanceledException(ct);
                }

                var script = fleet._scripts[model];
                return Task.FromResult(script.Count > 1 ? script.Dequeue() : script.Peek());
            }

            public IAsyncEnumerable<StreamEvent> StreamAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct) => throw new NotSupportedException();

            public Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();

            public Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();
        }
    }
}
