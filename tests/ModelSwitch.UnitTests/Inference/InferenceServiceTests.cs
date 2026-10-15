using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Configuration.Contracts;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Infrastructure.Health;
using ModelSwitch.Routing.Application;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

public sealed class InferenceServiceTests : IDisposable
{
    private static readonly Guid _policyId = new("0192f0c8-0000-7000-8000-0000000000a1");
    private static readonly ProviderModelTarget _target = new(Guid.NewGuid(), Guid.NewGuid(), "p", "ollama", new Uri("http://x/"), null, TimeSpan.FromSeconds(5), "m1");
    private static readonly ProviderModelTarget _other = new(Guid.NewGuid(), Guid.NewGuid(), "p", "ollama", new Uri("http://x/"), null, TimeSpan.FromSeconds(5), "m2");

    private readonly FakeSnapshots _snapshots = new();
    private readonly FakeAdapters _adapters = new();
    private readonly FakeLedger _ledger = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly ProviderHealth _health;

    public InferenceServiceTests()
    {
        _health = new ProviderHealth(new CircuitOptions(), _time, NullLogger<ProviderHealth>.Instance);
        _snapshots.Current = Snapshot(1);
    }

    public void Dispose() => _health.Dispose();

    private InferenceService Service(IProviderHealth? health = null) => new(
        _snapshots,
        new Router(),
        new InputTokenEstimator(),
        health ?? _health,
        new AttemptExecutor(_adapters, health ?? _health, new InputTokenEstimator(), new RandomJitterSource(), _time, NullLogger<AttemptExecutor>.Instance),
        _ledger,
        _time,
        NullLogger<InferenceService>.Instance);

    private static CandidateModel Model(ProviderModelTarget target, bool priced = true, int contextWindow = 100_000)
        => new(
            target.ProviderModelId,
            target.ProviderId,
            target.ProviderName,
            target.ProviderModelIdentifier,
            ProviderEnabled: true,
            ModelEnabled: true,
            new ModelCapabilities(true, true, true, contextWindow, 8_000, new HashSet<string>(), "o200k_base"),
            priced ? new PriceInForce(1m, 2m) : null,
            target with { Tokenizer = "o200k_base" });

    /// <summary>Alias <c>balanced</c> to a static policy over <c>m1</c> then <c>m2</c>.</summary>
    private static ConfigurationSnapshot Snapshot(
        long version,
        ProviderModelTarget[]? targets = null,
        bool priced = true,
        int policyVersion = 1,
        ExecutionSettings? execution = null,
        IReadOnlyDictionary<Guid, TenantLimits>? limits = null)
    {
        targets ??= [_target];
        var definition = new PolicyDefinition(
            PolicyStrategy.Static,
            [.. targets.Select(t => t.ProviderModelId)],
            [],
            [],
            AllowedProviders: null,
            MaxRequestCost: null,
            DefaultMaxOutputTokens: 777,
            MaxCandidates: null,
            execution ?? ExecutionSettings.Default);
        return new ConfigurationSnapshot(
            version,
            DateTimeOffset.UnixEpoch,
            new RoutingSnapshot(
                new Dictionary<string, Guid> { ["balanced"] = _policyId },
                new Dictionary<Guid, PolicySnapshot> { [_policyId] = new(_policyId, "balanced-policy", policyVersion, definition) },
                targets.ToDictionary(t => t.ProviderModelId, t => Model(t, priced))),
            limits ?? new Dictionary<Guid, TenantLimits>());
    }

    private static InferenceRequest Request(string alias = "balanced")
        => new(Guid.NewGuid(), Guid.NewGuid(), alias, new NeutralChatRequest([new NeutralMessage(MessageRole.User, "hi")]));

    [Fact]
    public async Task Unknown_alias_is_model_not_found()
    {
        var result = await Service().CompleteAsync(Request("nope"), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ModelNotFound);
    }

    [Fact]
    public async Task Binding_failure_is_returned_as_is()
    {
        _adapters.Result = new Error(ErrorCode.ProviderUnavailable, "no credential");

        var result = await Service().CompleteAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
    }

    [Fact]
    public async Task Success_echoes_the_alias_and_reports_one_attempt()
    {
        _adapters.Adapter.Outcome = new CompletionOutcome.Success("hello", FinishReason.Length, new NeutralUsage(3, 4, null));
        var request = Request();

        var result = await Service().CompleteAsync(request, CancellationToken.None);

        result.Value.Alias.ShouldBe("balanced");
        result.Value.RequestId.ShouldBe(request.RequestId);
        result.Value.Text.ShouldBe("hello");
        result.Value.FinishReason.ShouldBe(FinishReason.Length);
        result.Value.Attempts.ShouldBe(1);
        result.Value.ToString().ShouldNotContain("hello");
    }

    // A transient failure is retried after a backoff wait on the fake clock, so the clock has to be driven.
    private async Task<Result<InferenceResult>> CompleteDrivingTimeAsync(InferenceRequest request)
    {
        var pending = Service().CompleteAsync(request, CancellationToken.None);
        while (!pending.IsCompleted)
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Yield();
        }

        return await pending;
    }

    [Fact]
    public async Task Provider_failure_is_mapped_to_a_client_error()
    {
        _adapters.Adapter.Outcome = new CompletionOutcome.Failure(new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 503, null, true));

        var result = await CompleteDrivingTimeAsync(Request());

        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
    }

    [Theory]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.ContextLengthExceeded, "context_length_exceeded")]
    [InlineData(FailureClass.Ambiguous, ProviderFailureCodes.Timeout, "deadline_exceeded")]
    [InlineData(FailureClass.Transient, ProviderFailureCodes.EdgeTimeout, "deadline_exceeded")]
    [InlineData(FailureClass.Transient, ProviderFailureCodes.RateLimited, "provider_unavailable")]
    // A Provider answering HTTP 408 is a transient Provider failure, not the request Deadline (routing-and-resilience.md section 3).
    [InlineData(FailureClass.Transient, ProviderFailureCodes.RequestTimeout, "provider_unavailable")]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.Unauthorized, "provider_error")]
    [InlineData(FailureClass.Ambiguous, ProviderFailureCodes.ConnectionReset, "provider_error")]
    public void Failure_classes_map_to_error_codes(FailureClass failureClass, string code, string expected)
        => ProviderFailureMapper.ToError(new ProviderFailure(failureClass, code, null, null, true)).Code.Value.ShouldBe(expected);

    [Fact]
    public void Model_list_shows_the_tenants_aliases_only_sorted_with_a_stable_created_time()
    {
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var policies = new Dictionary<string, Guid> { ["b"] = _policyId, ["a"] = _policyId, ["secret"] = _policyId };
        _snapshots.Current = Snapshot(1, limits: new Dictionary<Guid, TenantLimits>
        {
            [tenant] = new(tenant, null, null, null, ["a", "b"]),
        }) with
        {
            Routing = Snapshot(1).Routing with { AliasToPolicy = policies },
        };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        var list = new ModelListService(_snapshots, time);
        time.Advance(TimeSpan.FromHours(1));

        var models = list.ListFor(tenant);

        models.Select(m => m.Id).ShouldBe(["a", "b"], "the Tenant's model access hides 'secret'");
        models.ShouldAllBe(m => m.Created == time.GetUtcNow().AddHours(-1).ToUnixTimeSeconds());
        list.ListFor(other).Select(m => m.Id).ShouldBe(["a", "b", "secret"], "a Tenant without limits may use every alias");
    }

    [Fact]
    public void Options_read_limits_and_fall_back_to_the_defaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Limits:MaxRequestBodyBytes"] = "2048",
            ["Limits:MaxMessages"] = "not-a-number",
        }).Build();

        var options = InferenceOptions.From(configuration);

        options.MaxRequestBodyBytes.ShouldBe(2048);
        options.MaxMessages.ShouldBe(InferenceOptions.DefaultMaxMessages);
    }

    // ---- routing ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_policy_version_and_routing_decision_are_recorded_on_the_request()
    {
        _snapshots.Current = Snapshot(1, policyVersion: 7);

        await Service().CompleteAsync(Request(), CancellationToken.None);

        var settled = _ledger.Settled.Single();
        settled.PolicyId.ShouldBe(_policyId);
        settled.PolicyVersion.ShouldBe(7);
        settled.RoutingDecisionJson.ShouldNotBeNull().ShouldContain(_target.ProviderModelId.ToString());
        settled.RoutingDecisionJson.ShouldContain("\"strategy\":\"static\"");
        _ledger.Opened.Single().TaskType.ShouldBeNull();
    }

    [Fact]
    public async Task The_snapshot_is_held_for_the_whole_request_even_when_the_configuration_changes_meanwhile()
    {
        _snapshots.Current = Snapshot(1, policyVersion: 1);
        _adapters.Adapter.OnCall = () => _snapshots.Current = Snapshot(2, policyVersion: 2);

        var result = await Service().CompleteAsync(Request(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _ledger.Settled.Single().PolicyVersion.ShouldBe(1, "the request never mixes two Policy Versions");
    }

    [Fact]
    public async Task The_policys_default_max_output_tokens_applies_when_the_client_sets_none()
    {
        await Service().CompleteAsync(Request(), CancellationToken.None);

        _adapters.Adapter.LastRequest!.MaxOutputTokens.ShouldBe(777);
    }

    [Fact]
    public async Task A_client_max_output_tokens_is_kept()
    {
        var request = Request() with { Chat = new NeutralChatRequest([new NeutralMessage(MessageRole.User, "hi")]) { MaxOutputTokens = 5 } };

        await Service().CompleteAsync(request, CancellationToken.None);

        _adapters.Adapter.LastRequest!.MaxOutputTokens.ShouldBe(5);
    }

    [Fact]
    public async Task A_tenant_without_access_to_the_alias_gets_model_not_allowed_and_nothing_is_called()
    {
        var request = Request();
        _snapshots.Current = Snapshot(1, limits: new Dictionary<Guid, TenantLimits>
        {
            [request.TenantId] = new(request.TenantId, null, null, null, ["other"]),
        });

        var result = await Service().CompleteAsync(request, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ModelNotAllowed);
        _adapters.Adapter.Calls.ShouldBe(0);
        _ledger.Settled.Single().ErrorCode.ShouldBe("model_not_allowed");
    }

    [Fact]
    public async Task No_eligible_model_is_a_422_that_still_records_the_policy_and_the_exclusion_reasons()
    {
        _snapshots.Current = Snapshot(1, priced: false);

        var result = await Service().CompleteAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.NoEligibleModel);
        var settled = _ledger.Settled.Single();
        settled.PolicyId.ShouldBe(_policyId);
        settled.RoutingDecisionJson.ShouldNotBeNull().ShouldContain("no_price");
        settled.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_exceeding_every_context_window_is_no_eligible_model()
    {
        var request = Request() with { Chat = new NeutralChatRequest([new NeutralMessage(MessageRole.User, string.Join(' ', Enumerable.Repeat("hello", 150_000)))]) };

        var result = await Service().CompleteAsync(request, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.NoEligibleModel);
        result.Error.Message.ShouldContain("context_window");
    }

    [Fact]
    public async Task A_json_schema_request_needs_the_capability_so_a_model_without_it_is_excluded()
    {
        var model = Model(_target) with { Capabilities = Model(_target).Capabilities with { JsonSchema = false } };
        var snapshot = Snapshot(1);
        _snapshots.Current = snapshot with { Routing = snapshot.Routing with { Models = new Dictionary<Guid, CandidateModel> { [_target.ProviderModelId] = model } } };
        var format = new ResponseFormat.JsonSchema("s", System.Text.Json.JsonDocument.Parse("{}").RootElement, Strict: true);
        var request = Request() with { Chat = new NeutralChatRequest([new NeutralMessage(MessageRole.User, "hi")]) { ResponseFormat = format } };

        var result = await Service().CompleteAsync(request, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.NoEligibleModel);
        _ledger.Settled.Single().RoutingDecisionJson.ShouldNotBeNull().ShouldContain("capability_missing:jsonSchema");
    }

    [Fact]
    public async Task Every_circuit_open_at_routing_is_provider_unavailable_with_the_shortest_remaining_break()
    {
        await OpenCircuitAsync(_target);

        var result = await Service().CompleteAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        result.Error.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
        _adapters.Adapter.Calls.ShouldBe(0);
        _ledger.Settled.Single().PolicyId.ShouldBe(_policyId);
    }

    [Fact]
    public async Task The_plan_uses_the_policys_execution_settings()
    {
        _snapshots.Current = Snapshot(1, [_target, _other], execution: new ExecutionSettings(60_000, 30_000, MaxAttempts: 1, MaxRetriesPerCandidate: 3, new BackoffSettings(0, 0, FullJitter: false)));
        _adapters.Adapter.Outcome = new CompletionOutcome.Failure(new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 503, null, true));

        var result = await Service().CompleteAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        _adapters.Adapter.Calls.ShouldBe(1, "maxAttempts = 1 bounds the whole request");
    }

    [Fact]
    public async Task The_clients_timeout_shortens_the_deadline_but_never_extends_it()
    {
        _snapshots.Current = Snapshot(1, execution: new ExecutionSettings(10_000, 10_000, 4, 1, new BackoffSettings(0, 0, FullJitter: false)));
        TimeSpan? seen = null;
        _adapters.Adapter.OnContext = context => seen = context.AttemptTimeout;

        await Service().CompleteAsync(Request() with { Timeout = TimeSpan.FromSeconds(2) }, CancellationToken.None);
        seen.ShouldBe(TimeSpan.FromSeconds(2));

        await Service().CompleteAsync(Request() with { Timeout = TimeSpan.FromMinutes(5) }, CancellationToken.None);
        seen.ShouldBe(TimeSpan.FromSeconds(5), "the Provider's own request timeout (5 s) is smaller than the policy's 10 s, and the client cannot extend either");
    }

    [Fact]
    public async Task The_task_type_is_routed_on_and_recorded()
    {
        var definition = new PolicyDefinition(
            PolicyStrategy.Rules,
            [_target.ProviderModelId],
            [new PolicyRule(["extraction"], [], [_other.ProviderModelId])],
            [_target.ProviderModelId],
            null,
            null,
            1024,
            null,
            ExecutionSettings.Default);
        var snapshot = Snapshot(1, [_target, _other]);
        _snapshots.Current = snapshot with
        {
            Routing = snapshot.Routing with
            {
                Policies = new Dictionary<Guid, PolicySnapshot> { [_policyId] = new(_policyId, "p", 1, definition) },
            },
        };
        ProviderModelTarget? called = null;
        _adapters.OnBind = target => called = target;

        await Service().CompleteAsync(Request() with { TaskType = "extraction" }, CancellationToken.None);

        called.ShouldBe(_other with { Tokenizer = "o200k_base" });
        _ledger.Opened.Single().TaskType.ShouldBe("extraction");
    }

    [Fact]
    public async Task A_circuit_that_opens_after_routing_is_skipped_and_still_gives_a_retry_after()
    {
        await OpenCircuitAsync(_target);
        var racy = new BlindFirstLook(_health);

        var result = await Service(racy).CompleteAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        result.Error.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
        _ledger.Settled.Single().Attempts.Single().Status.ShouldBe(AttemptStatus.SkippedCircuitOpen);
        _adapters.Adapter.Calls.ShouldBe(0);
    }

    private async Task OpenCircuitAsync(ProviderModelTarget target)
    {
        for (var i = 0; i < 10; i++)
        {
            await _health.ExecuteAsync(
                target.ProviderModelId,
                _ => Task.FromResult(new CompletionOutcome.Failure(new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 503, null, true))),
                static outcome => outcome.Error,
                CancellationToken.None);
        }

        _health.StateOf(target.ProviderModelId).ShouldBe(CircuitState.Open);
    }

    /// <summary>Reports no open circuit to the router on the first look, as if the circuit opened right after routing.</summary>
    private sealed class BlindFirstLook(IProviderHealth inner) : IProviderHealth
    {
        private bool _looked;

        public IReadOnlyDictionary<Guid, TimeSpan> OpenCircuits()
        {
            if (_looked)
            {
                return inner.OpenCircuits();
            }

            _looked = true;
            return new Dictionary<Guid, TimeSpan>();
        }

        public CircuitState StateOf(Guid providerModelId) => inner.StateOf(providerModelId);

        public IReadOnlyList<ProviderModelHealth> Snapshot() => inner.Snapshot();

        public Task<GuardedCall<T>> ExecuteAsync<T>(Guid providerModelId, Func<CancellationToken, Task<T>> attempt, Func<T, ProviderFailure?> failureOf, CancellationToken cancellationToken)
            => inner.ExecuteAsync(providerModelId, attempt, failureOf, cancellationToken);
    }

    [Fact]
    public async Task Success_opens_then_settles_one_succeeded_request_with_one_reported_attempt()
    {
        _adapters.Adapter.Outcome = new CompletionOutcome.Success("hello", FinishReason.Stop, new NeutralUsage(3, 4, null));
        var request = Request();

        await Service().CompleteAsync(request, CancellationToken.None);

        _ledger.Opened.Single().RequestId.ShouldBe(request.RequestId);
        var settled = _ledger.Settled.Single();
        settled.Outcome.ShouldBe(RequestOutcome.Succeeded);
        settled.ErrorCode.ShouldBeNull();
        var attempt = settled.Attempts.Single();
        (attempt.Status, attempt.Kind, attempt.UsageSource).ShouldBe((AttemptStatus.Succeeded, AttemptKind.Primary, UsageSource.Reported));
        (attempt.InputTokens, attempt.OutputTokens).ShouldBe((3, 4));
    }

    [Fact]
    public async Task Success_without_reported_usage_is_unavailable()
    {

        await Service().CompleteAsync(Request(), CancellationToken.None);

        _ledger.Settled.Single().Attempts.Single().UsageSource.ShouldBe(UsageSource.Unavailable);
    }

    [Fact]
    public async Task Unknown_alias_settles_a_failed_request_with_no_attempts()
    {
        await Service().CompleteAsync(Request("nope"), CancellationToken.None);

        var settled = _ledger.Settled.Single();
        settled.Outcome.ShouldBe(RequestOutcome.Failed);
        settled.ErrorCode.ShouldBe("model_not_found");
        settled.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Transient_failure_is_a_failed_attempt_with_zero_tokens()
    {
        _adapters.Adapter.Outcome = new CompletionOutcome.Failure(new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 503, null, true));

        await CompleteDrivingTimeAsync(Request());

        var attempts = _ledger.Settled.Single().Attempts;
        attempts.Count.ShouldBe(2, "a transient failure is retried once on the same model");
        var attempt = attempts[0];
        attempt.Status.ShouldBe(AttemptStatus.Failed);
        attempt.ErrorClass.ShouldBe(AttemptErrorClass.Transient);
        attempt.HttpStatus.ShouldBe(503);
        attempt.ProviderErrorType.ShouldBe("server_error");
        (attempt.InputTokens, attempt.OutputTokens).ShouldBe((0, 0));
    }

    [Fact]
    public async Task Ambiguous_failure_is_billable_with_an_estimated_input_and_unknown_output()
    {
        _adapters.Adapter.Outcome = new CompletionOutcome.Failure(new ProviderFailure(FailureClass.Ambiguous, ProviderFailureCodes.Timeout, null, null, true));

        await Service().CompleteAsync(Request(), CancellationToken.None);

        var attempt = _ledger.Settled.Single().Attempts.Single();
        attempt.Status.ShouldBe(AttemptStatus.Ambiguous);
        attempt.UsageSource.ShouldBe(UsageSource.Unavailable);
        attempt.InputTokens!.Value.ShouldBeGreaterThan(0);
        attempt.OutputTokens.ShouldBeNull();
    }

    [Fact]
    public async Task Client_abort_settles_a_cancelled_request_on_a_fresh_token_and_rethrows()
    {
        using var aborted = new CancellationTokenSource();
        _adapters.Adapter.CancelOnCall = aborted;

        await Should.ThrowAsync<OperationCanceledException>(() => Service().CompleteAsync(Request(), aborted.Token));

        var settled = _ledger.Settled.Single();
        settled.Outcome.ShouldBe(RequestOutcome.Cancelled);
        settled.Attempts.Single().Status.ShouldBe(AttemptStatus.Ambiguous);
        _ledger.SettleTokenWasCancelled.ShouldBeFalse();
    }

    [Fact]
    public async Task Settlement_failure_is_retried_and_never_fails_the_response()
    {
        _ledger.FailSettleTimes = 1;
        var service = Service();

        var pending = service.CompleteAsync(Request(), CancellationToken.None);
        while (!pending.IsCompleted)
        {
            _time.Advance(TimeSpan.FromMilliseconds(200));
            await Task.Yield();
        }

        (await pending).IsSuccess.ShouldBeTrue();
        _ledger.Settled.Count.ShouldBe(1);
    }

    private sealed class FakeLedger : IUsageLedger
    {
        public List<OpenInferenceRequest> Opened { get; } = [];

        public List<SettleInferenceRequest> Settled { get; } = [];

        public int FailSettleTimes { get; set; }

        public bool SettleTokenWasCancelled { get; private set; }

        public Task<Result<OpenedRequest>> OpenAsync(OpenInferenceRequest request, CancellationToken cancellationToken)
        {
            Opened.Add(request);
            return Task.FromResult<Result<OpenedRequest>>(new OpenedRequest(request.RequestId, new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)));
        }

        public Task SettleAsync(SettleInferenceRequest settlement, CancellationToken cancellationToken)
        {
            SettleTokenWasCancelled = cancellationToken.IsCancellationRequested;
            if (FailSettleTimes-- > 0)
            {
                throw new InvalidOperationException("db down");
            }

            Settled.Add(settlement);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSnapshots : IConfigurationSnapshotProvider
    {
        public ConfigurationSnapshot Current { get; set; } = null!;

        public bool IsLoaded => Current is not null;
    }

    private sealed class FakeAdapters : IAdapterFactory
    {
        public FakeAdapter Adapter { get; } = new();

        public Result<AdapterBinding>? Result { get; set; }

        public Action<ProviderModelTarget>? OnBind { get; set; }

        public ValueTask<Result<AdapterBinding>> BindAsync(ProviderModelTarget target, CancellationToken cancellationToken)
        {
            OnBind?.Invoke(target);
            return ValueTask.FromResult(Result ?? new AdapterBinding(Adapter, new AdapterContext(target.Endpoint, null, target.ProviderModelIdentifier, target.RequestTimeout)));
        }
    }

    private sealed class FakeAdapter : IProviderAdapter
    {
        public CompletionOutcome Outcome { get; set; } = new CompletionOutcome.Success("ok", FinishReason.Stop, null);

        /// <summary>Simulates the client aborting while the call is in flight.</summary>
        public CancellationTokenSource? CancelOnCall { get; set; }

        public Action? OnCall { get; set; }

        public Action<AdapterContext>? OnContext { get; set; }

        public int Calls { get; private set; }

        public NeutralChatRequest? LastRequest { get; private set; }

        public string AdapterType => "fake";

        public Task<CompletionOutcome> CompleteAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            OnContext?.Invoke(ctx);
            OnCall?.Invoke();
            if (CancelOnCall is { } source)
            {
                source.Cancel();
                throw new OperationCanceledException(ct);
            }

            return Task.FromResult(Outcome);
        }

        public IAsyncEnumerable<StreamEvent> StreamAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();
    }
}
