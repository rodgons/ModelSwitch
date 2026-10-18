using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Worker;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// The real worker host composition (<see cref="WorkerServices"/>): it resolves <see cref="IInferenceService"/> with its own
/// configuration snapshot and claims the <c>evaluation.run</c> job the api host enqueued, as in production.
/// </summary>
public sealed class EvaluationWorkerTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    [Fact]
    public async Task The_worker_host_claims_an_evaluation_run_and_executes_it_through_the_mock_provider()
    {
        var token = TestContext.Current.CancellationToken;
        using var owner = await gateway.OwnerBrowserAsync();
        var dataset = await AdminBrowser.JsonAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/datasets", new { tenantId = gateway.TenantId, name = "worker-ds", taskKind = "classification" }));
        var datasetId = dataset.GetProperty("id").GetGuid();
        (await owner.SendJsonAsync(
                HttpMethod.Post,
                $"/admin/datasets/{datasetId}/versions",
                new StringContent("{\"id\":\"w-1\",\"input\":{\"text\":\"spam\"},\"expected\":\"spam\"}\n{\"id\":\"w-2\",\"input\":{\"text\":\"ham\"},\"expected\":\"ham\"}", Encoding.UTF8, "application/x-ndjson")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var promptId = (await AdminBrowser.JsonAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/prompts", new { tenantId = gateway.TenantId, name = "worker-prompt" }))).GetProperty("id").GetGuid();
        await owner.SendJsonAsync(HttpMethod.Post, $"/admin/prompts/{promptId}/versions", new { messages = new[] { new { role = "user", content = "{{text}}" } } });
        var created = await owner.SendJsonAsync(HttpMethod.Post, "/admin/evaluations", new
        {
            tenantId = gateway.TenantId,
            datasetId,
            datasetVersion = 1,
            name = "worker run",
            configurations = new[] { new { target = new { alias = GatewayFixture.ChatAlias }, prompt = new { id = promptId, version = 1 }, evaluator = new { kind = "exact_match", version = "1" } } },
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var runId = (await AdminBrowser.JsonAsync(created)).GetProperty("runIds")[0].GetGuid();

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = gateway.ConnectionString,
            ["Security:ApiKeyPepper"] = ModuleServices.Pepper,
            ["Outbound:AllowedPrivateHosts"] = "127.0.0.1",
        });
        builder.Services.AddWorkerServices();
        using var worker = builder.Build();
        worker.Services.GetRequiredService<IInferenceService>().ShouldNotBeNull();
        await worker.StartAsync(token);
        try
        {
            await Eventually.UntilAsync(
                async () => await gateway.ScalarAsync($"select status from evaluation.evaluation_run where id = '{runId}'") is not ("queued" or "running"),
                TimeSpan.FromSeconds(30),
                "the worker host finishes the run");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        (await gateway.ScalarAsync($"select concat_ws('|', status, cases_done, aggregates->'quality'->>'value') from evaluation.evaluation_run where id = '{runId}'")).ShouldBe("succeeded|2|1");
        (await gateway.ScalarAsync($"select count(*) from usage.inference_request where evaluation_run_id = '{runId}' and origin = 'evaluation'")).ShouldBe("2");
    }
}
