using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// Evaluation traffic through the real host's <see cref="IInferenceService"/> (evaluation.md sections 4 and 5): a frozen Policy
/// Version, a pinned Provider Model, Request Origin <c>evaluation</c>, no Rate or Concurrency Limits, but the Budget and model access.
/// </summary>
public sealed class EvaluationTrafficTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    private static readonly Guid _runId = Guid.CreateVersion7();

    private IInferenceService Inference => gateway.Services.GetRequiredService<IInferenceService>();

    private static InferenceRequest Request(Guid tenantId, InferenceTarget target)
        => new(Guid.CreateVersion7(), tenantId, target, new NeutralChatRequest([new NeutralMessage(MessageRole.User, "hello")]))
        {
            Origin = RequestOrigin.Evaluation,
            EvaluationRunId = _runId,
        };

    private Task<Guid> ModelIdAsync(string provider, string model)
        => gateway.ScalarAsync(
            $"select m.id from registry.provider_model m join registry.provider p on p.id = m.provider_id where p.name = '{provider}' and m.provider_model_id = '{model}'")
            .ContinueWith(t => Guid.Parse(t.Result), TaskScheduler.Default);

    [Fact]
    public async Task A_pinned_policy_version_keeps_routing_on_the_old_version_after_the_policy_is_edited()
    {
        var echo = await ModelIdAsync("mock-openai", GatewayFixture.PrimaryModel);
        var fallback = await ModelIdAsync("mock-openai", GatewayFixture.FallbackModel);
        await gateway.WriteConfigurationAsync(GatewayFixture.PolicyAndAliasSql("frozen", [("mock-openai", GatewayFixture.PrimaryModel, echo)]));

        // Run creation: freeze the version current now.
        var current = (await gateway.Services.GetRequiredService<IPolicyStore>().GetActiveForAliasAsync("frozen", CancellationToken.None)).ShouldNotBeNull();
        current.Version.ShouldBe(1);

        // The policy is edited: version 2 routes to mock-fallback.
        await gateway.WriteConfigurationAsync(
            $$"""
            insert into routing.routing_policy_version (policy_id, version, definition, resolved_candidates, created_at)
              values ('{{current.PolicyId}}', 2, '{"strategy":"static","candidates":["mock-openai/{{GatewayFixture.FallbackModel}}"]}', '{"mock-openai/{{GatewayFixture.FallbackModel}}":"{{fallback}}"}', now());
            update routing.routing_policy set latest_version = 2 where id = '{{current.PolicyId}}';
            """);

        var pinned = await Inference.CompleteAsync(Request(gateway.TenantId, InferenceTarget.For("frozen", current.PolicyId, current.Version)), CancellationToken.None);
        var latest = await Inference.CompleteAsync(Request(gateway.TenantId, InferenceTarget.For("frozen")), CancellationToken.None);

        pinned.Value.ProviderModelId.ShouldBe(echo);
        latest.Value.ProviderModelId.ShouldBe(fallback);
        (await gateway.ScalarAsync($"select policy_version from usage.inference_request where id = '{pinned.Value.RequestId}'")).ShouldBe("1");
        (await gateway.ScalarAsync($"select policy_version from usage.inference_request where id = '{latest.Value.RequestId}'")).ShouldBe("2");
    }

    [Fact]
    public async Task A_pinned_provider_model_bypasses_routing_and_is_recorded_as_evaluation_traffic()
    {
        var fallback = await ModelIdAsync("mock-openai", GatewayFixture.FallbackModel);

        // Run creation checks the model exists and is enabled.
        (await gateway.Services.GetRequiredService<IProviderCatalog>().FindEnabledByIdAsync(fallback, CancellationToken.None)).ShouldNotBeNull().ProviderModelId.ShouldBe(fallback);
        (await gateway.Services.GetRequiredService<IProviderCatalog>().FindEnabledByIdAsync(Guid.CreateVersion7(), CancellationToken.None)).ShouldBeNull();

        var result = (await Inference.CompleteAsync(Request(gateway.TenantId, InferenceTarget.Pinned(fallback)), CancellationToken.None)).Value;

        result.ProviderModelId.ShouldBe(fallback);
        result.Alias.ShouldBe("mock-openai/mock-fallback");
        result.InputTokens.ShouldNotBeNull().ShouldBeGreaterThan(0);
        result.OutputTokens.ShouldNotBeNull().ShouldBeGreaterThan(0);
        var row = await gateway.ScalarAsync(
            "select concat_ws('|', origin, coalesce(api_key_id::text, 'null'), evaluation_run_id, requested_alias, coalesce(policy_id::text, 'null'), " +
            $"routing_decision->>'strategy', selected_provider_model_id, status, total_estimated_cost) from usage.inference_request where id = '{result.RequestId}'");
        var columns = row.Split('|');
        columns[..8].ShouldBe(["evaluation", "null", _runId.ToString(), "mock-openai/mock-fallback", "null", "pinned", fallback.ToString(), "succeeded"]);
        decimal.Parse(columns[8], CultureInfo.InvariantCulture).ShouldBe(result.EstimatedCost.ShouldNotBeNull().Amount, "the reported Estimated Cost is the one settlement booked");
    }

    [Fact]
    public async Task Evaluation_traffic_is_not_held_to_the_tenants_rate_limit_while_api_traffic_is()
    {
        var (tenantId, key) = await gateway.SeedTenantAsync("eval-rate", requestsPerMinute: 1);
        using var client = gateway.Authenticated(key);
        static StringContent Body() => new(
            new JsonObject { ["model"] = GatewayFixture.ChatAlias, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        (await client.PostAsync("/v1/chat/completions", Body(), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync("/v1/chat/completions", Body(), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        for (var i = 0; i < 3; i++)
        {
            (await Inference.CompleteAsync(Request(tenantId, InferenceTarget.For(GatewayFixture.ChatAlias)), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task The_budget_still_refuses_evaluation_traffic()
    {
        var (tenantId, _) = await gateway.SeedTenantAsync("eval-budget");
        await gateway.Services.GetRequiredService<IBudgets>().SetMonthlyLimitAsync(tenantId, Money.FromUsd(0.0000001m), CancellationToken.None);
        var fallback = await ModelIdAsync("mock-openai", GatewayFixture.FallbackModel);

        var aliased = await Inference.CompleteAsync(Request(tenantId, InferenceTarget.For(GatewayFixture.ChatAlias)), CancellationToken.None);
        var pinned = await Inference.CompleteAsync(Request(tenantId, InferenceTarget.Pinned(fallback)), CancellationToken.None);

        aliased.Error.Code.ShouldBe(ErrorCode.BudgetExceeded);
        pinned.Error.Code.ShouldBe(ErrorCode.BudgetExceeded);
        (await gateway.ScalarAsync($"select string_agg(concat_ws('|', origin, status, error_code), ',') from usage.inference_request where tenant_id = '{tenantId}'"))
            .ShouldBe("evaluation|failed|budget_exceeded,evaluation|failed|budget_exceeded");
    }

    [Fact]
    public async Task A_pinned_provider_model_no_allowed_alias_reaches_is_model_not_allowed()
    {
        var (tenantId, _) = await gateway.SeedTenantAsync("eval-access", requestsPerMinute: 1000);
        await gateway.WriteConfigurationAsync($"update identity.tenant_limits set allowed_aliases = array['{GatewayFixture.OllamaAlias}'] where tenant_id = '{tenantId}'");
        var openAiFallback = await ModelIdAsync("mock-openai", GatewayFixture.FallbackModel);
        var ollamaEcho = await ModelIdAsync("mock-ollama", GatewayFixture.PrimaryModel);

        var refused = await Inference.CompleteAsync(Request(tenantId, InferenceTarget.Pinned(openAiFallback)), CancellationToken.None);
        var allowed = await Inference.CompleteAsync(Request(tenantId, InferenceTarget.Pinned(ollamaEcho)), CancellationToken.None);

        refused.Error.Code.ShouldBe(ErrorCode.ModelNotAllowed);
        allowed.IsSuccess.ShouldBeTrue("the 'local' alias the Tenant may use routes to mock-ollama/mock-echo");
    }
}
