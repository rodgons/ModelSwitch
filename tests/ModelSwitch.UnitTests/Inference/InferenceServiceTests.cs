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
using ModelSwitch.UnitTests.Support;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

public sealed class InferenceServiceTests : IDisposable
{
    private static readonly Guid _policyId = new("0192f0c8-0000-7000-8000-0000000000a1");
    private static readonly ProviderModelTarget _target = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "p", "ollama", new Uri("http://x/"), null, TimeSpan.FromSeconds(5), "m1");
    private static readonly ProviderModelTarget _other = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "p", "ollama", new Uri("http://x/"), null, TimeSpan.FromSeconds(5), "m2");

    private static readonly TestMeterFactory _meters = new();

    private readonly FakeSnapshots _snapshots = new();
    private readonly FakeAdapters _adapters = new();
    private readonly FakeLedger _ledger = new();
    private readonly FakePolicyStore _policies = new();
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
        _policies,
        new InputTokenEstimator(),
        health ?? _health,
        new AttemptExecutor(_adapters, health ?? _health, new InputTokenEstimator(), new RandomJitterSource(), _time, NullLogger<AttemptExecutor>.Instance),
        _ledger,
        new CostCalculator(),
        new InferenceMetrics(_meters),
        new LimitsMetrics(_meters),
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
        => new(Guid.CreateVersion7(), Guid.CreateVersion7(), alias, new NeutralChatRequest([new NeutralMessage(MessageRole.User, "hi")]));

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
    [InlineData(FailureClass.Ambiguous, ProviderFailureCodes.Timeout, "provider_unavailable")]
    [InlineData(FailureClass.Transient, ProviderFailureCodes.EdgeTimeout, "provider_unavailable")]
    [InlineData(FailureClass.Transient, ProviderFailureCodes.RateLimited, "provider_unavailable")]
    // A per-Attempt timeout is a Provider failure; deadline_exceeded is only for the request Deadline (routing-and-resilience.md section 4).
    // A Provider answering HTTP 408 is a transient Provider failure, not the request Deadline (routing-and-resilience.md section 3).
    [InlineData(FailureClass.Transient, ProviderFailureCodes.RequestTimeout, "provider_unavailable")]
    [InlineData(FailureClass.Permanent, ProviderFailureCodes.Unauthorized, "provider_error")]
    [InlineData(FailureClass.Ambiguous, ProviderFailureCodes.ConnectionReset, "provider_error")]
    public void Failure_classes_map_to_error_codes(FailureClass failureClass, string code, string expected)
        => ProviderFailureMapper.ToError(new ProviderFailure(failureClass, code, null, null, true)).Code.Value.ShouldBe(expected);

    [Fact]
    public void Model_list_shows_the_tenants_aliases_only_sorted_with_a_stable_created_time()
    {
        var tenant = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
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
    public async Task Success_without_reported_usage_is_estimated_from_what_was_sent_and_received()
    {
        _adapters.Adapter.Outcome = new CompletionOutcome.Success("a reply with several words in it", FinishReason.Stop, null);

        await Service().CompleteAsync(Request(), CancellationToken.None);

        var attempt = _ledger.Settled.Single().Attempts.Single();
        attempt.UsageSource.ShouldBe(UsageSource.Estimated);
        attempt.EstimateMethod.ShouldBe("tokenizer:o200k_base");
        attempt.InputTokens!.Value.ShouldBeGreaterThan(0);
        attempt.OutputTokens!.Value.ShouldBeGreaterThan(0);
        attempt.CachedInputTokens.ShouldBeNull();
    }

    [Fact]
    public async Task Reported_cached_input_tokens_are_passed_on()
    {
        _adapters.Adapter.Outcome = new CompletionOutcome.Success("hello", FinishReason.Stop, new NeutralUsage(10, 4, 6));

        await Service().CompleteAsync(Request(), CancellationToken.None);

        _ledger.Settled.Single().Attempts.Single().CachedInputTokens.ShouldBe(6);
    }

    [Fact]
    public async Task The_reservation_is_the_worst_case_of_the_dearest_candidate_times_two_attempts()
    {
        _snapshots.Current = Snapshot(1, [_target, _other]);

        await Service().CompleteAsync(Request(), CancellationToken.None);

        var hold = _ledger.Opened.Single().Reservation.ShouldNotBeNull();
        // 777 output tokens at 2 USD per million plus the small input estimate at 1 USD per million, held for min(maxAttempts, 2) = 2 Attempts.
        hold.Amount.Amount.ShouldBeGreaterThan(2 * 777 * 2m / 1_000_000m);
        hold.Amount.Amount.ShouldBeLessThan(2 * (777 * 2m + 100) / 1_000_000m);
        hold.ExpiresAt.ShouldBe(_time.GetUtcNow() + TimeSpan.FromMilliseconds(ExecutionSettings.Default.DeadlineMs) + InferenceService.ReservationGrace);
    }

    [Fact]
    public async Task A_refused_reservation_is_a_402_and_nothing_is_called()
    {
        _ledger.RefuseOpen = true;

        var result = await Service().CompleteAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.BudgetExceeded);
        _adapters.Adapter.Calls.ShouldBe(0);
        _ledger.Settled.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_reservation_on_a_stream_is_a_402_before_any_stream()
    {
        _ledger.RefuseOpen = true;

        var result = await Service().StreamAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.BudgetExceeded);
        _adapters.Adapter.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_third_attempt_tops_up_the_hold_by_its_worst_case_and_the_first_two_do_not()
    {
        _adapters.Adapter.Outcome = new CompletionOutcome.Failure(new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 503, null, true));
        _snapshots.Current = Snapshot(1, [_target], execution: ExecutionSettings.Default with { MaxRetriesPerCandidate = 2, MaxAttempts = 3 });

        await CompleteDrivingTimeAsync(Request());

        _adapters.Adapter.Calls.ShouldBe(3);
        var hold = _ledger.Opened.Single().Reservation!.Amount;
        var topUp = _ledger.TopUps.Single();
        topUp.ShouldBe(hold * 0.5m, "the hold covers two Attempts, so only the third adds its worst case");
    }

    [Fact]
    public async Task A_refused_top_up_stops_the_request_with_budget_exceeded_and_keeps_the_attempts_made()
    {
        _adapters.Adapter.Outcome = new CompletionOutcome.Failure(new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 503, null, true));
        _snapshots.Current = Snapshot(1, [_target], execution: ExecutionSettings.Default with { MaxRetriesPerCandidate = 2, MaxAttempts = 3 });
        _ledger.RefuseTopUp = true;

        var result = await CompleteDrivingTimeAsync(Request());

        result.Error.Code.ShouldBe(ErrorCode.BudgetExceeded);
        _adapters.Adapter.Calls.ShouldBe(2);
        var settled = _ledger.Settled.Single();
        settled.ErrorCode.ShouldBe("budget_exceeded");
        settled.Attempts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Every_attempt_is_settled_with_the_price_in_force_when_the_request_began()
    {
        var priceId = Guid.CreateVersion7();
        var snapshot = Snapshot(1);
        var model = snapshot.Routing.Models[_target.ProviderModelId];
        _snapshots.Current = snapshot with
        {
            Routing = snapshot.Routing with
            {
                Models = new Dictionary<Guid, CandidateModel> { [_target.ProviderModelId] = model with { Price = new PriceInForce(1m, 2m, 0.5m, priceId) } },
            },
        };
        _adapters.Adapter.Outcome = new CompletionOutcome.Success("hello", FinishReason.Stop, new NeutralUsage(3, 4, null));

        await Service().CompleteAsync(Request(), CancellationToken.None);

        var pricing = _ledger.Settled.Single().Attempts.Single().Pricing.ShouldNotBeNull();
        pricing.ModelPriceId.ShouldBe(priceId);
        pricing.Rates.ShouldBe(new PriceRates(1m, 0.5m, 2m));
        _ledger.Settled.Single().MaxOutputTokens.ShouldBe(777);
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

    // ---- streaming -------------------------------------------------------------------------------------------------

    private void StreamScript(params StreamEvent[] events)
    {
        _adapters.Adapter.Events.Clear();
        _adapters.Adapter.Events.AddRange(events);
    }

    private static StreamEvent.ContentDelta Delta(string text) => new(text);

    [Fact]
    public async Task A_stream_settles_after_its_last_part_has_been_handed_over_not_before()
    {
        StreamScript(Delta("a"), Delta("b"), new StreamEvent.Finish(FinishReason.Stop), new StreamEvent.Usage(new NeutralUsage(3, 2, null)));
        var request = Request();

        var started = await Service().StreamAsync(request, CancellationToken.None);
        started.IsSuccess.ShouldBeTrue();
        _ledger.Settled.ShouldBeEmpty("nothing settles before the stream is read");
        var parts = new List<StreamPart>();
        await foreach (var part in started.Value.ReadAsync(CancellationToken.None))
        {
            parts.Add(part);
            if (part is StreamPart.Completed)
            {
                _ledger.Settled.ShouldBeEmpty("the client gets the last part first; settlement follows");
            }
        }

        parts.Last().ShouldBeOfType<StreamPart.Completed>();
        var settled = _ledger.Settled.Single();
        settled.Outcome.ShouldBe(RequestOutcome.Succeeded);
        settled.PolicyVersion.ShouldBe(1);
        settled.TtftMs.ShouldNotBeNull();
        settled.Attempts.Single().Status.ShouldBe(AttemptStatus.Succeeded);
        _ledger.Opened.Single().Streaming.ShouldBeTrue();
        _adapters.Adapter.StreamDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_stream_that_breaks_after_the_first_token_settles_failed_with_stream_interrupted()
    {
        StreamScript(Delta("a"), new StreamEvent.Failed(new ProviderFailure(FailureClass.Ambiguous, ProviderFailureCodes.ConnectionReset, null, null, true)));

        var started = await Service().StreamAsync(Request(), CancellationToken.None);
        var parts = new List<StreamPart>();
        await foreach (var part in started.Value.ReadAsync(CancellationToken.None))
        {
            parts.Add(part);
        }

        parts.Last().ShouldBeOfType<StreamPart.Interrupted>();
        var settled = _ledger.Settled.Single();
        (settled.Outcome, settled.ErrorCode).ShouldBe((RequestOutcome.Failed, "stream_interrupted"));
        _adapters.Adapter.Calls.ShouldBe(1, "no replay");
    }

    [Fact]
    public async Task A_stream_disposed_without_being_read_closes_the_provider_and_settles_cancelled()
    {
        StreamScript(Delta("a"), new StreamEvent.Finish(FinishReason.Stop));
        var started = await Service().StreamAsync(Request(), CancellationToken.None);

        await started.Value.DisposeAsync();

        _adapters.Adapter.StreamDisposed.ShouldBeTrue();
        var settled = _ledger.Settled.Single();
        settled.Outcome.ShouldBe(RequestOutcome.Cancelled);
        settled.Attempts.Single().Status.ShouldBe(AttemptStatus.Ambiguous);
    }

    [Fact]
    public async Task A_consumer_that_stops_reading_settles_cancelled_exactly_once()
    {
        StreamScript(Delta("a"), Delta("b"), new StreamEvent.Finish(FinishReason.Stop));
        var started = await Service().StreamAsync(Request(), CancellationToken.None);

        await foreach (var part in started.Value.ReadAsync(CancellationToken.None))
        {
            part.ShouldBeOfType<StreamPart.Content>();
            break;
        }

        await started.Value.DisposeAsync();
        _ledger.Settled.Single().Outcome.ShouldBe(RequestOutcome.Cancelled);
        _adapters.Adapter.StreamDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_client_abort_mid_stream_settles_cancelled_on_a_fresh_token_and_rethrows()
    {
        StreamScript(Delta("a"), Delta("b"), Delta("c"), new StreamEvent.Finish(FinishReason.Stop));
        using var abort = new CancellationTokenSource();
        var started = await Service().StreamAsync(Request(), abort.Token);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var part in started.Value.ReadAsync(abort.Token))
            {
                part.ShouldBeOfType<StreamPart.Content>();
                await abort.CancelAsync();
            }
        });

        var settled = _ledger.Settled.Single();
        settled.Outcome.ShouldBe(RequestOutcome.Cancelled);
        settled.Attempts.Single().ProviderErrorType.ShouldBe("client_cancelled");
        _ledger.SettleTokenWasCancelled.ShouldBeFalse();
        _adapters.Adapter.StreamDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failure_before_the_first_token_is_an_ordinary_error_and_no_stream()
    {
        StreamScript(new StreamEvent.Failed(new ProviderFailure(FailureClass.Permanent, ProviderFailureCodes.BadRequest, 400, null, true)));

        var result = await Service().StreamAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ProviderError);
        var settled = _ledger.Settled.Single();
        (settled.Outcome, settled.ErrorCode).ShouldBe((RequestOutcome.Failed, "provider_error"));
        settled.Attempts.Single().Status.ShouldBe(AttemptStatus.Failed);
    }

    [Fact]
    public async Task Streaming_needs_the_streaming_capability_so_a_model_without_it_is_no_eligible_model()
    {
        var snapshot = Snapshot(1);
        var model = Model(_target) with { Capabilities = Model(_target).Capabilities with { Streaming = false } };
        _snapshots.Current = snapshot with { Routing = snapshot.Routing with { Models = new Dictionary<Guid, CandidateModel> { [_target.ProviderModelId] = model } } };

        var result = await Service().StreamAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.NoEligibleModel);
        _ledger.Settled.Single().RoutingDecisionJson.ShouldNotBeNull().ShouldContain("capability_missing:streaming");
        _adapters.Adapter.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_streaming_request_for_an_unknown_alias_is_model_not_found()
    {
        var result = await Service().StreamAsync(Request("nope"), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ModelNotFound);
        _ledger.Settled.Single().ErrorCode.ShouldBe("model_not_found");
    }

    // ---- Evaluation traffic: Request Origin, pinned targets and what a Case Result records ----

    private static readonly Guid _runId = Guid.CreateVersion7();

    // m1 and m2 are both in the catalog, whichever ones the policy lists.
    private static ConfigurationSnapshot WithBothModels(ConfigurationSnapshot snapshot)
        => snapshot with { Routing = snapshot.Routing with { Models = new Dictionary<Guid, CandidateModel> { [_target.ProviderModelId] = Model(_target), [_other.ProviderModelId] = Model(_other) } } };

    private static InferenceRequest EvaluationRequest(InferenceTarget target)
        => new(Guid.CreateVersion7(), Guid.CreateVersion7(), target, new NeutralChatRequest([new NeutralMessage(MessageRole.User, "hi")]))
        {
            Origin = RequestOrigin.Evaluation,
            EvaluationRunId = _runId,
        };

    public static TheoryData<RequestOrigin, bool, bool, bool> OriginCases() => new()
    {
        // origin, has API Key, has run, valid
        { RequestOrigin.Evaluation, false, true, true },
        { RequestOrigin.Evaluation, false, false, false },
        { RequestOrigin.Evaluation, true, true, false },
        { RequestOrigin.Api, true, false, true },
        { RequestOrigin.Api, true, true, false },
    };

    [Theory]
    [MemberData(nameof(OriginCases))]
    public void Evaluation_traffic_carries_its_run_and_no_api_key_and_only_it_carries_a_run(RequestOrigin origin, bool hasKey, bool hasRun, bool valid)
    {
        var request = Request() with
        {
            Origin = origin,
            ApiKeyId = hasKey ? Guid.CreateVersion7() : null,
            EvaluationRunId = hasRun ? _runId : null,
        };

        (request.OriginViolation() is null).ShouldBe(valid);
    }

    [Fact]
    public async Task A_request_breaking_the_origin_invariants_is_refused_before_anything_is_recorded()
    {
        var request = EvaluationRequest(InferenceTarget.For("balanced")) with { EvaluationRunId = null };

        await Should.ThrowAsync<ArgumentException>(() => Service().CompleteAsync(request, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => Service().StreamAsync(request, CancellationToken.None));
        _ledger.Opened.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_evaluation_request_is_opened_with_its_origin_and_run_and_no_api_key()
    {
        await Service().CompleteAsync(EvaluationRequest(InferenceTarget.For("balanced")), CancellationToken.None);

        var opened = _ledger.Opened.Single();
        (opened.Origin, opened.ApiKeyId, opened.EvaluationRunId, opened.RequestedAlias).ShouldBe((RequestOrigin.Evaluation, (Guid?)null, (Guid?)_runId, "balanced"));
        opened.Reservation.ShouldNotBeNull("the Budget applies to Evaluation traffic");
    }

    [Fact]
    public void A_request_built_from_a_target_takes_its_alias_from_it()
    {
        EvaluationRequest(InferenceTarget.For("balanced", _policyId, 3)).Alias.ShouldBe("balanced");
        EvaluationRequest(InferenceTarget.Pinned(_other.ProviderModelId)).Alias.ShouldBeEmpty();
        Request().Target.ShouldBe(new InferenceTarget.ModelAlias("balanced"));
    }

    [Fact]
    public async Task A_pinned_policy_version_that_is_the_latest_is_taken_from_the_snapshot()
    {
        var result = await Service().CompleteAsync(EvaluationRequest(InferenceTarget.For("balanced", _policyId, 1)), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _policies.Reads.ShouldBe(0);
        _ledger.Settled.Single().PolicyVersion.ShouldBe(1);
    }

    [Fact]
    public async Task An_older_pinned_policy_version_is_read_from_the_store_and_routed_on()
    {
        // The latest version (2) lists m1; the frozen version 1 listed m2.
        _snapshots.Current = WithBothModels(Snapshot(1, [_target], policyVersion: 2));
        _policies.Versions[(_policyId, 1)] = Snapshot(1, [_other]).Routing.Policies[_policyId];
        var bound = new List<Guid>();
        _adapters.OnBind = target => bound.Add(target.ProviderModelId);

        var result = await Service().CompleteAsync(EvaluationRequest(InferenceTarget.For("balanced", _policyId, 1)), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        bound.ShouldBe([_other.ProviderModelId]);
        var settled = _ledger.Settled.Single();
        (settled.PolicyId, settled.PolicyVersion).ShouldBe(((Guid?)_policyId, (int?)1));
    }

    [Fact]
    public async Task An_unknown_pinned_policy_version_is_model_not_found_and_recorded()
    {
        var result = await Service().CompleteAsync(EvaluationRequest(InferenceTarget.For("balanced", _policyId, 9)), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ModelNotFound);
        _adapters.Adapter.Calls.ShouldBe(0);
        _ledger.Settled.Single().ErrorCode.ShouldBe("model_not_found");
    }

    [Fact]
    public async Task A_pinned_provider_model_bypasses_routing_and_is_recorded_under_provider_slash_model()
    {
        // m2 is in the catalog but no policy lists it; the Tenant may use every alias, so it may pin any model.
        _snapshots.Current = WithBothModels(Snapshot(1, [_target]));
        var bound = new List<Guid>();
        _adapters.OnBind = target => bound.Add(target.ProviderModelId);

        var result = await Service().CompleteAsync(EvaluationRequest(InferenceTarget.Pinned(_other.ProviderModelId)), CancellationToken.None);

        result.Value.Alias.ShouldBe("p/m2");
        bound.ShouldBe([_other.ProviderModelId]);
        _ledger.Opened.Single().RequestedAlias.ShouldBe("p/m2");
        var settled = _ledger.Settled.Single();
        (settled.PolicyId, settled.PolicyVersion).ShouldBe(((Guid?)null, (int?)null));
        settled.RoutingDecisionJson.ShouldNotBeNull().ShouldContain("\"strategy\":\"pinned\"");
        settled.MaxOutputTokens.ShouldBe(PinnedRouting.DefaultMaxOutputTokens);
    }

    [Fact]
    public async Task A_pinned_provider_model_the_tenant_cannot_reach_is_model_not_allowed()
    {
        var tenant = Guid.CreateVersion7();
        _snapshots.Current = WithBothModels(Snapshot(1, [_target], limits: new Dictionary<Guid, TenantLimits> { [tenant] = new(tenant, null, null, null, ["balanced"]) }));

        var result = await Service().CompleteAsync(EvaluationRequest(InferenceTarget.Pinned(_other.ProviderModelId)) with { TenantId = tenant }, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ModelNotAllowed);
        _adapters.Adapter.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_success_reports_the_provider_model_tokens_estimated_cost_and_latency()
    {
        // m1 is priced 1 USD per million input tokens and 2 per million output tokens.
        _adapters.Adapter.Outcome = new CompletionOutcome.Success("hello", FinishReason.Stop, new NeutralUsage(300, 400, null));

        var result = (await Service().CompleteAsync(EvaluationRequest(InferenceTarget.For("balanced")), CancellationToken.None)).Value;

        result.ProviderModelId.ShouldBe(_target.ProviderModelId);
        (result.InputTokens, result.OutputTokens).ShouldBe(((int?)300, (int?)400));
        result.EstimatedCost.ShouldBe(Money.FromUsd(0.0011m));
        result.Latency.ShouldBe(TimeSpan.Zero, "the fake clock did not move");
        result.ToString().ShouldNotContain("hello");
    }

    private sealed class FakePolicyStore : IPolicyStore
    {
        public Dictionary<(Guid PolicyId, int Version), PolicySnapshot> Versions { get; } = [];

        public int Reads { get; private set; }

        public Task<PolicySnapshot?> GetActiveForAliasAsync(string modelAlias, CancellationToken cancellationToken)
            => throw new NotSupportedException("Inference reads the latest version from the configuration snapshot.");

        public Task<PolicySnapshot?> GetVersionAsync(Guid policyId, int version, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Versions.GetValueOrDefault((policyId, version)));
        }
    }

    private sealed class FakeLedger : IUsageLedger
    {
        public List<OpenInferenceRequest> Opened { get; } = [];

        public List<SettleInferenceRequest> Settled { get; } = [];

        public int FailSettleTimes { get; set; }

        public bool RefuseOpen { get; set; }

        public bool RefuseTopUp { get; set; }

        public List<Money> TopUps { get; } = [];

        public bool SettleTokenWasCancelled { get; private set; }

        public Task<Result<OpenedRequest>> OpenAsync(OpenInferenceRequest request, CancellationToken cancellationToken)
        {
            if (RefuseOpen)
            {
                return Task.FromResult<Result<OpenedRequest>>(new Error(ErrorCode.BudgetExceeded, "no budget"));
            }

            Opened.Add(request);
            return Task.FromResult<Result<OpenedRequest>>(new OpenedRequest(request.RequestId, new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)));
        }

        public Task<Result> TopUpAsync(Guid requestId, Money additional, CancellationToken cancellationToken)
        {
            if (RefuseTopUp)
            {
                return Task.FromResult<Result>(new Error(ErrorCode.BudgetExceeded, "no budget"));
            }

            TopUps.Add(additional);
            return Task.FromResult(Result.Success());
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

        /// <summary>What a streaming call yields. A stream with no <c>Finish</c> or <c>Failed</c> at its end is a broken Adapter.</summary>
        public List<StreamEvent> Events { get; } = [];

        /// <summary>Set when the stream's enumerator has been disposed: the Provider connection is closed.</summary>
        public bool StreamDisposed { get; private set; }

        public async IAsyncEnumerable<StreamEvent> StreamAsync(AdapterContext ctx, NeutralChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            OnCall?.Invoke();
            try
            {
                foreach (var item in Events)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.CompletedTask;
                    yield return item;
                }
            }
            finally
            {
                StreamDisposed = true;
            }
        }

        public Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();
    }
}
