using Microsoft.Extensions.Configuration;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

public sealed class InferenceServiceTests
{
    private static readonly ProviderModelTarget _target = new(Guid.NewGuid(), Guid.NewGuid(), "p", "ollama", new Uri("http://x/"), null, TimeSpan.FromSeconds(5), "m1");
    private readonly FakeCatalog _catalog = new();
    private readonly FakeAdapters _adapters = new();

    private InferenceService Service() => new(
        new InferenceOptions { StaticAliases = [new StaticAliasBinding("balanced", "p", "m1")] }, _catalog, _adapters);

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

        public string AdapterType => "fake";

        public Task<CompletionOutcome> CompleteAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct) => Task.FromResult(Outcome);

        public IAsyncEnumerable<StreamEvent> StreamAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();
    }
}
