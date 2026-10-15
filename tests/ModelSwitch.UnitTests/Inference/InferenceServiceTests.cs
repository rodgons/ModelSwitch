using Microsoft.Extensions.Configuration;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

public sealed class InferenceServiceTests
{
    private static readonly ProviderModelTarget _target = new(Guid.NewGuid(), Guid.NewGuid(), "p", "ollama", new Uri("http://x/"), null, TimeSpan.FromSeconds(5), "m1");
    private readonly FakeCatalog _catalog = new();
    private readonly FakeAdapters _adapters = new();

    private readonly FakeLedger _ledger = new();
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    private InferenceService Service() => new(
        new InferenceOptions { StaticAliases = [new StaticAliasBinding("balanced", "p", "m1")] },
        _catalog,
        _adapters,
        _ledger,
        new ModelSwitch.Usage.Domain.InputTokenEstimator(),
        _time,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<InferenceService>.Instance);

    private static InferenceRequest Request(string alias = "balanced")
        => new(Guid.NewGuid(), Guid.NewGuid(), alias, new NeutralChatRequest([new NeutralMessage(MessageRole.User, "hi")]));

    [Fact]
    public async Task Unknown_alias_is_model_not_found()
    {
        var result = await Service().CompleteAsync(Request("nope"), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ModelNotFound);
    }

    [Fact]
    public async Task Alias_bound_to_a_missing_or_disabled_provider_model_is_no_eligible_model()
    {
        var result = await Service().CompleteAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.NoEligibleModel);
    }

    [Fact]
    public async Task Binding_failure_is_returned_as_is()
    {
        _catalog.Target = _target;
        _adapters.Result = new Error(ErrorCode.ProviderUnavailable, "no credential");

        var result = await Service().CompleteAsync(Request(), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
    }

    [Fact]
    public async Task Success_echoes_the_alias_and_reports_one_attempt()
    {
        _catalog.Target = _target;
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

    [Fact]
    public async Task Provider_failure_is_mapped_to_a_client_error()
    {
        _catalog.Target = _target;
        _adapters.Adapter.Outcome = new CompletionOutcome.Failure(new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 503, null, true));

        var result = await Service().CompleteAsync(Request(), CancellationToken.None);

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
    public void Model_list_shows_aliases_only_sorted_with_a_stable_created_time()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        var list = new ModelListService(
            new InferenceOptions { StaticAliases = [new StaticAliasBinding("b", "p", "secret-model"), new StaticAliasBinding("a", "p", "x")] }, time);
        time.Advance(TimeSpan.FromHours(1));

        var models = list.ListFor(Guid.NewGuid());

        models.Select(m => m.Id).ShouldBe(["a", "b"]);
        models.ShouldAllBe(m => m.Created == time.GetUtcNow().AddHours(-1).ToUnixTimeSeconds());
    }

    [Fact]
    public void Options_read_static_aliases_and_limits()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Inference:StaticAliases:balanced"] = "ollama/llama3.2:1b",
            ["Inference:StaticAliases:broken"] = "noslash",
            ["Limits:MaxRequestBodyBytes"] = "2048",
            ["Limits:MaxMessages"] = "not-a-number",
        }).Build();

        var options = InferenceOptions.From(configuration);

        options.StaticAliases.ShouldBe([new StaticAliasBinding("balanced", "ollama", "llama3.2:1b")]);
        options.MaxRequestBodyBytes.ShouldBe(2048);
        options.MaxMessages.ShouldBe(InferenceOptions.DefaultMaxMessages);
    }

    [Fact]
    public async Task Success_opens_then_settles_one_succeeded_request_with_one_reported_attempt()
    {
        _catalog.Target = _target;
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
        _catalog.Target = _target;

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
        _catalog.Target = _target;
        _adapters.Adapter.Outcome = new CompletionOutcome.Failure(new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 503, null, true));

        await Service().CompleteAsync(Request(), CancellationToken.None);

        var attempt = _ledger.Settled.Single().Attempts.Single();
        attempt.Status.ShouldBe(AttemptStatus.Failed);
        attempt.ErrorClass.ShouldBe(AttemptErrorClass.Transient);
        attempt.HttpStatus.ShouldBe(503);
        attempt.ProviderErrorType.ShouldBe("server_error");
        (attempt.InputTokens, attempt.OutputTokens).ShouldBe((0, 0));
    }

    [Fact]
    public async Task Ambiguous_failure_is_billable_with_an_estimated_input_and_unknown_output()
    {
        _catalog.Target = _target;
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
        _catalog.Target = _target;
        _adapters.Adapter.Throw = true;
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Service().CompleteAsync(Request(), aborted.Token));

        var settled = _ledger.Settled.Single();
        settled.Outcome.ShouldBe(RequestOutcome.Cancelled);
        settled.Attempts.Single().Status.ShouldBe(AttemptStatus.Ambiguous);
        _ledger.SettleTokenWasCancelled.ShouldBeFalse();
    }

    [Fact]
    public async Task Settlement_failure_is_retried_and_never_fails_the_response()
    {
        _catalog.Target = _target;
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

    private sealed class FakeCatalog : IProviderCatalog
    {
        public ProviderModelTarget? Target { get; set; }

        public Task<ProviderModelTarget?> FindEnabledAsync(string providerName, string providerModelIdentifier, CancellationToken cancellationToken)
            => Task.FromResult(Target);
    }

    private sealed class FakeAdapters : IAdapterFactory
    {
        public FakeAdapter Adapter { get; } = new();

        public Result<AdapterBinding>? Result { get; set; }

        public ValueTask<Result<AdapterBinding>> BindAsync(ProviderModelTarget target, CancellationToken cancellationToken)
            => ValueTask.FromResult(Result ?? new AdapterBinding(Adapter, new AdapterContext(target.Endpoint, null, target.ProviderModelIdentifier, target.RequestTimeout)));
    }

    private sealed class FakeAdapter : IProviderAdapter
    {
        public CompletionOutcome Outcome { get; set; } = new CompletionOutcome.Success("ok", FinishReason.Stop, null);

        public bool Throw { get; set; }

        public string AdapterType => "fake";

        public Task<CompletionOutcome> CompleteAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct)
            => Throw ? throw new OperationCanceledException(ct) : Task.FromResult(Outcome);

        public IAsyncEnumerable<StreamEvent> StreamAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();
    }
}
