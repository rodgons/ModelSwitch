using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel.Jobs;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// Deleting evaluation content (evaluation.md section 8, data-model.md "Retention", ADR-0010): a Comparison with its runs and Case
/// Results, then the Dataset and Prompt it used. Deletes are Operator writes, audited without content, and never leave a run
/// pointing at a missing Dataset or Prompt.
/// </summary>
public sealed class EvaluationDeletionTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    // A distinctive Case input, so the test can prove no Audit Record copies content.
    private const string _secretInput = "Ana Silva ana.silva-canary@example.com";

    private static readonly string[] _labels = ["spam", "ham"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(Guid DatasetId, Guid PromptId)> SetupAsync(AdminBrowser browser)
    {
        var dataset = await browser.SendJsonAsync(HttpMethod.Post, "/admin/datasets", new { tenantId = gateway.TenantId, name = $"ds-{Guid.CreateVersion7()}", taskKind = "classification" });
        dataset.StatusCode.ShouldBe(HttpStatusCode.Created);
        var datasetId = (await AdminBrowser.JsonAsync(dataset)).GetProperty("id").GetGuid();
        var jsonl = $"{{\"id\":\"c-1\",\"input\":{{\"text\":\"{_secretInput}\"}},\"expected\":\"spam\"}}\n{{\"id\":\"c-2\",\"input\":{{\"text\":\"ham\"}},\"expected\":\"ham\"}}\n";
        (await browser.SendJsonAsync(HttpMethod.Post, $"/admin/datasets/{datasetId}/versions", new StringContent(jsonl, Encoding.UTF8, "application/x-ndjson")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var prompt = await browser.SendJsonAsync(HttpMethod.Post, "/admin/prompts", new { tenantId = gateway.TenantId, name = $"pr-{Guid.CreateVersion7()}" });
        var promptId = (await AdminBrowser.JsonAsync(prompt)).GetProperty("id").GetGuid();
        (await browser.SendJsonAsync(HttpMethod.Post, $"/admin/prompts/{promptId}/versions", new { messages = new[] { new { role = "user", content = "{{text}}" } } }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        return (datasetId, promptId);
    }

    private async Task<(Guid ComparisonId, Guid RunId)> CreateRunAsync(AdminBrowser browser, Guid datasetId, Guid promptId)
    {
        var response = await browser.SendJsonAsync(HttpMethod.Post, "/admin/evaluations", new
        {
            tenantId = gateway.TenantId,
            datasetId,
            datasetVersion = 1,
            name = "deletion run",
            configurations = new[]
            {
                new
                {
                    target = new { alias = GatewayFixture.ChatAlias },
                    prompt = new { id = promptId, version = 1 },
                    evaluator = new { kind = "classification", version = "1", config = new { labels = _labels } },
                },
            },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        var json = await AdminBrowser.JsonAsync(response);
        return (json.GetProperty("comparisonId").GetGuid(), json.GetProperty("runIds").EnumerateArray().Single().GetGuid());
    }

    private async Task RunJobAsync(Guid runId)
    {
        var payload = await gateway.ScalarAsync($"select payload::text from jobs.job where kind = 'evaluation.run' and dedupe_key = 'evaluation.run:{runId}'");
        var handler = gateway.Services.GetServices<IJobHandler>().Single(h => h.Kind == "evaluation.run");
        await handler.HandleAsync(new JobContext(Guid.CreateVersion7(), "evaluation.run", payload, 1, 5), Token);
    }

    private Task<string> CountAsync(string table, string where) => gateway.ScalarAsync($"select count(*) from evaluation.{table} where {where}");

    private static async Task AssertConflictAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await AdminBrowser.ErrorCodeAsync(response)).ShouldBe("conflict");
    }

    private async Task AssertAuditedWithoutContentAsync(string action, Guid targetId)
    {
        (await gateway.ScalarAsync($"select count(*) from identity.audit_record where action = '{action}' and target_id = '{targetId}'")).ShouldBe("1");
        var details = await gateway.ScalarAsync($"select details::text from identity.audit_record where action = '{action}' and target_id = '{targetId}'");
        details.ShouldNotContain("canary", Case.Insensitive, $"{action} must never copy evaluation content");
        details.ShouldNotContain("{{text}}", Case.Sensitive, $"{action} must never copy a template");
    }

    [Fact]
    [HappyPath("DELETE /admin/comparisons/{comparisonId:guid}")]
    [HappyPath("DELETE /admin/datasets/{datasetId:guid}")]
    [HappyPath("DELETE /admin/prompts/{promptId:guid}")]
    public async Task An_operator_deletes_a_finished_comparison_then_the_dataset_and_prompt_it_used_and_every_delete_is_audited_without_content()
    {
        using var op = await gateway.AdministratorBrowserAsync("operator", gateway.TenantId);
        var (datasetId, promptId) = await SetupAsync(op);
        var (comparisonId, runId) = await CreateRunAsync(op, datasetId, promptId);
        await RunJobAsync(runId);
        (await CountAsync("case_result", $"run_id = '{runId}'")).ShouldBe("2");

        // The Comparison still uses them, so the Dataset and Prompt stay.
        await AssertConflictAsync(await op.SendJsonAsync(HttpMethod.Delete, $"/admin/datasets/{datasetId}", null));
        await AssertConflictAsync(await op.SendJsonAsync(HttpMethod.Delete, $"/admin/prompts/{promptId}", null));

        var comparison = await op.SendJsonAsync(HttpMethod.Delete, $"/admin/comparisons/{comparisonId}", null);

        comparison.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await CountAsync("comparison", $"id = '{comparisonId}'")).ShouldBe("0");
        (await CountAsync("evaluation_run", $"id = '{runId}'")).ShouldBe("0");
        (await CountAsync("case_result", $"run_id = '{runId}'")).ShouldBe("0");
        (await op.GetAsync($"/admin/comparisons/{comparisonId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await op.GetAsync($"/admin/evaluations/{runId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertAuditedWithoutContentAsync("evaluation.deleted", comparisonId);
        (await gateway.ScalarAsync($"select details->'before'->>'caseResults' from identity.audit_record where action = 'evaluation.deleted' and target_id = '{comparisonId}'")).ShouldBe("2");

        var dataset = await op.SendJsonAsync(HttpMethod.Delete, $"/admin/datasets/{datasetId}", null);
        var prompt = await op.SendJsonAsync(HttpMethod.Delete, $"/admin/prompts/{promptId}", null);

        dataset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        prompt.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await CountAsync("dataset_version", $"dataset_id = '{datasetId}'")).ShouldBe("0");
        (await CountAsync("eval_case", $"dataset_id = '{datasetId}'")).ShouldBe("0");
        (await CountAsync("prompt_version", $"prompt_id = '{promptId}'")).ShouldBe("0");
        (await op.GetAsync($"/admin/datasets/{datasetId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await op.GetAsync($"/admin/prompts/{promptId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertAuditedWithoutContentAsync("dataset.deleted", datasetId);
        await AssertAuditedWithoutContentAsync("prompt.deleted", promptId);
        (await gateway.ScalarAsync($"select details->'before'->>'cases' from identity.audit_record where action = 'dataset.deleted' and target_id = '{datasetId}'")).ShouldBe("2");

        // Deleting again finds nothing.
        (await op.SendJsonAsync(HttpMethod.Delete, $"/admin/datasets/{datasetId}", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await op.SendJsonAsync(HttpMethod.Delete, $"/admin/comparisons/{comparisonId}", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_comparison_with_a_queued_or_still_finishing_run_is_409_until_the_run_has_finished()
    {
        using var op = await gateway.AdministratorBrowserAsync("operator", gateway.TenantId);
        var (datasetId, promptId) = await SetupAsync(op);
        var (comparisonId, runId) = await CreateRunAsync(op, datasetId, promptId);

        await AssertConflictAsync(await op.SendJsonAsync(HttpMethod.Delete, $"/admin/comparisons/{comparisonId}", null));

        // Cancelled while running: the worker still owes the run its in-flight Cases and aggregates.
        await gateway.ScalarAsync($"update evaluation.evaluation_run set status = 'cancelled', started_at = now() where id = '{runId}' returning 1");
        await AssertConflictAsync(await op.SendJsonAsync(HttpMethod.Delete, $"/admin/comparisons/{comparisonId}", null));
        (await CountAsync("evaluation_run", $"id = '{runId}'")).ShouldBe("1");

        await gateway.ScalarAsync($"update evaluation.evaluation_run set completed_at = now() where id = '{runId}' returning 1");
        (await op.SendJsonAsync(HttpMethod.Delete, $"/admin/comparisons/{comparisonId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The run's job, claimed after the delete, finds no run and does nothing.
        await RunJobAsync(runId);
        (await CountAsync("case_result", $"run_id = '{runId}'")).ShouldBe("0");
    }

    [Fact]
    public async Task A_viewer_cannot_delete_evaluation_content()
    {
        using var op = await gateway.AdministratorBrowserAsync("operator", gateway.TenantId);
        var (datasetId, promptId) = await SetupAsync(op);
        var (comparisonId, _) = await CreateRunAsync(op, datasetId, promptId);
        using var viewer = await gateway.AdministratorBrowserAsync("viewer", gateway.TenantId);

        (await viewer.SendJsonAsync(HttpMethod.Delete, $"/admin/comparisons/{comparisonId}", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.SendJsonAsync(HttpMethod.Delete, $"/admin/datasets/{datasetId}", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.SendJsonAsync(HttpMethod.Delete, $"/admin/prompts/{promptId}", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CountAsync("comparison", $"id = '{comparisonId}'")).ShouldBe("1");
    }
}
