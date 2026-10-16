using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel.Jobs;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// Evaluation Runs end to end (evaluation.md sections 3 to 7, api.md section 3): creation and its validation, the <c>evaluation.run</c>
/// job against the Mock Provider (never a real Provider), resume, cancellation, the cost cap, <c>llm_judge</c>, export and comparison.
/// The job is run by calling the registered handler, as the worker's claim loop does.
/// </summary>
public sealed class EvaluationRunTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    private const string _classification = """
        {"id":"c-1","input":{"text":"spam"},"expected":"spam"}
        {"id":"c-2","input":{"text":"ham"},"expected":"spam"}
        {"id":"c-3","input":{"other":"x"},"expected":"ham"}
        """;

    private static readonly string[] _spamOnly = ["spam"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(Guid DatasetId, Guid PromptId)> SetupAsync(AdminBrowser browser, string taskKind, string jsonl, string content = "{{text}}")
    {
        var dataset = await browser.SendJsonAsync(HttpMethod.Post, "/admin/datasets", new { tenantId = gateway.TenantId, name = $"ds-{Guid.CreateVersion7()}", taskKind });
        dataset.StatusCode.ShouldBe(HttpStatusCode.Created);
        var datasetId = (await AdminBrowser.JsonAsync(dataset)).GetProperty("id").GetGuid();
        (await browser.SendJsonAsync(HttpMethod.Post, $"/admin/datasets/{datasetId}/versions", new StringContent(jsonl, Encoding.UTF8, "application/x-ndjson")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var prompt = await browser.SendJsonAsync(HttpMethod.Post, "/admin/prompts", new { tenantId = gateway.TenantId, name = $"pr-{Guid.CreateVersion7()}" });
        var promptId = (await AdminBrowser.JsonAsync(prompt)).GetProperty("id").GetGuid();
        (await browser.SendJsonAsync(HttpMethod.Post, $"/admin/prompts/{promptId}/versions", new { messages = new[] { new { role = "user", content } } }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        return (datasetId, promptId);
    }

    private object Body(Guid datasetId, Guid promptId, object? evaluator = null, string alias = GatewayFixture.ChatAlias, int concurrency = 2, decimal? maxEstimatedCost = null, object? target = null)
        => new
        {
            tenantId = gateway.TenantId,
            datasetId,
            datasetVersion = 1,
            name = "classification run",
            configurations = new[]
            {
                new
                {
                    target = target ?? new { alias },
                    prompt = new { id = promptId, version = 1 },
                    @params = new { temperature = 0, seed = 7, maxOutputTokens = 64 },
                    evaluator = evaluator ?? new { kind = "classification", version = "1", config = new { labels = new[] { "spam", "ham" } } },
                },
            },
            concurrency,
            maxEstimatedCost,
        };

    private static async Task<(Guid ComparisonId, Guid[] RunIds)> CreateAsync(AdminBrowser browser, object body)
    {
        var response = await browser.SendJsonAsync(HttpMethod.Post, "/admin/evaluations", body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        var json = await AdminBrowser.JsonAsync(response);
        return (json.GetProperty("comparisonId").GetGuid(), [.. json.GetProperty("runIds").EnumerateArray().Select(r => r.GetGuid())]);
    }

    /// <summary>Runs the run's enqueued job through the registered handler, as a worker would after claiming it.</summary>
    private async Task RunJobAsync(Guid runId, CancellationToken cancellationToken)
    {
        var payload = await gateway.ScalarAsync($"select payload::text from jobs.job where kind = 'evaluation.run' and dedupe_key = 'evaluation.run:{runId}'");
        var handler = gateway.Services.GetServices<IJobHandler>().Single(h => h.Kind == "evaluation.run");
        await handler.HandleAsync(new JobContext(Guid.CreateVersion7(), "evaluation.run", payload, 1, 5), cancellationToken);
    }

    private async Task<int> CallsAsync(string model)
        => (await gateway.MockStatsAsync()).Started.GetValueOrDefault(model);

    private Task<string> RunColumnAsync(Guid runId, string column) => gateway.ScalarAsync($"select {column} from evaluation.evaluation_run where id = '{runId}'");

    private static async Task AssertValidationAsync(HttpResponseMessage response, params string[] keys)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("errorCode").GetString().ShouldBe("validation_failed");
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    [HappyPath("POST /admin/evaluations")]
    [HappyPath("GET /admin/evaluations/{runId:guid}")]
    [HappyPath("GET /admin/evaluations/{runId:guid}/results")]
    public async Task A_run_is_created_frozen_and_executed_into_case_results_and_aggregates()
    {
        using var op = await gateway.AdministratorBrowserAsync("operator", gateway.TenantId);
        var (datasetId, promptId) = await SetupAsync(op, "classification", _classification);
        var policyVersion = await gateway.ScalarAsync($"select p.latest_version from routing.routing_policy p join routing.model_alias a on a.policy_id = p.id where a.alias = '{GatewayFixture.ChatAlias}'");

        var (comparisonId, runIds) = await CreateAsync(op, Body(datasetId, promptId));
        var runId = runIds.Single();

        (await RunColumnAsync(runId, "status")).ShouldBe("queued");
        (await RunColumnAsync(runId, "cases_total")).ShouldBe("3");
        (await RunColumnAsync(runId, "comparison_id")).ShouldBe(comparisonId.ToString());
        (await RunColumnAsync(runId, "run_configuration->'target'->>'policyVersion'")).ShouldBe(policyVersion);
        (await RunColumnAsync(runId, "run_configuration->'dataset'->>'contentSha256'")).ShouldMatch("^[0-9a-f]{64}$");
        (await RunColumnAsync(runId, "config_sha256")).ShouldMatch("^[0-9a-f]{64}$");
        (await gateway.ScalarAsync($"select count(*) from jobs.job where dedupe_key = 'evaluation.run:{runId}' and status = 'pending'")).ShouldBe("1");
        (await gateway.ScalarAsync($"select count(*) from identity.audit_record where action = 'evaluation.created' and target_id = '{comparisonId}'")).ShouldBe("1");

        await RunJobAsync(runId, Token);

        (await RunColumnAsync(runId, "status")).ShouldBe("succeeded");
        (await RunColumnAsync(runId, "concat_ws('|', cases_done, cases_failed)")).ShouldBe("3|2");
        (await RunColumnAsync(runId, "started_at is not null and completed_at is not null")).ShouldBe("True");
        var detail = await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/evaluations/{runId}"));
        detail.GetProperty("status").GetString().ShouldBe("succeeded");
        detail.GetProperty("progress").GetProperty("done").GetInt32().ShouldBe(3);
        detail.GetProperty("aggregatesLive").GetBoolean().ShouldBeFalse();
        detail.GetProperty("aggregates").GetProperty("quality").GetProperty("value").GetDecimal().ShouldBe(0.5m, "c-1 passes, c-2 fails, c-3 is errored and outside quality");
        detail.GetProperty("aggregates").GetProperty("erroredRate").GetDecimal().ShouldBe(0.333333m);
        detail.GetProperty("failures").GetProperty("byErrorCode").GetProperty("template_variable_missing").GetInt32().ShouldBe(1);
        detail.GetProperty("reproducibility").GetProperty("gatewayVersion").GetString().ShouldNotBeNullOrEmpty();
        detail.GetProperty("reproducibility").GetProperty("target").GetProperty("label").GetString().ShouldBe($"{GatewayFixture.ChatAlias}@v{policyVersion}");

        var results = await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/evaluations/{runId}/results?limit=2"));
        var page = results.GetProperty("items").EnumerateArray().ToArray();
        page.Select(r => r.GetProperty("externalId").GetString()).ShouldBe(["c-1", "c-2"]);
        page[0].GetProperty("status").GetString().ShouldBe("passed");
        page[0].GetProperty("outputText").GetString().ShouldBe("spam");
        page[0].GetProperty("predicted").GetString().ShouldBe("spam");
        page[0].GetProperty("inputTokens").GetInt32().ShouldBeGreaterThan(0);
        page[0].GetProperty("estimatedCost").GetDecimal().ShouldBeGreaterThan(0m);
        var requestId = page[0].GetProperty("inferenceRequestId").GetGuid();
        (await gateway.ScalarAsync($"select concat_ws('|', origin, evaluation_run_id, task_type, policy_version, coalesce(api_key_id::text, 'null')) from usage.inference_request where id = '{requestId}'"))
            .ShouldBe($"evaluation|{runId}|classification|{policyVersion}|null");
        var rest = await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/evaluations/{runId}/results?cursor={results.GetProperty("nextCursor").GetString()}"));
        var errored = rest.GetProperty("items").EnumerateArray().Single();
        errored.GetProperty("status").GetString().ShouldBe("errored");
        errored.GetProperty("errorCode").GetString().ShouldBe("template_variable_missing");
        errored.GetProperty("inferenceRequestId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/evaluations/{runId}/results?status=failed"))).GetProperty("items").GetArrayLength().ShouldBe(1);

        // The Case drill-down asks for one Case's result by its external id: one request per run, no paging.
        var byCase = (await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/evaluations/{runId}/results?externalId=c-2"))).GetProperty("items").EnumerateArray().Single();
        byCase.GetProperty("externalId").GetString().ShouldBe("c-2");
        byCase.GetProperty("status").GetString().ShouldBe("failed");
        (await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/evaluations/{runId}/results?externalId=C-2"))).GetProperty("items").GetArrayLength().ShouldBe(0, "exact match");

        // The request detail page follows the candidate request back to its Case.
        var request = await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/requests/{requestId}"));
        request.GetProperty("evaluationCase").GetProperty("externalId").GetString().ShouldBe("c-1");
        request.GetProperty("evaluationCase").GetProperty("role").GetString().ShouldBe("candidate");

        // A second claim of the job is a no-op for a finished run.
        var calls = await CallsAsync(GatewayFixture.PrimaryModel);
        await RunJobAsync(runId, Token);
        (await CallsAsync(GatewayFixture.PrimaryModel)).ShouldBe(calls);
    }

    [Fact]
    public async Task An_invalid_evaluation_is_a_400_with_every_problem_keyed_and_writes_nothing()
    {
        using var op = await gateway.AdministratorBrowserAsync("operator", gateway.TenantId);
        var (datasetId, promptId) = await SetupAsync(op, "classification", _classification);
        var before = await gateway.ScalarAsync("select count(*) from evaluation.comparison");

        await AssertValidationAsync(
            await op.SendJsonAsync(HttpMethod.Post, "/admin/evaluations", new { tenantId = gateway.TenantId, configurations = Array.Empty<object>(), concurrency = 17 }),
            "datasetId", "datasetVersion", "name", "configurations", "concurrency");

        // References are resolved after the shape: unknown alias, an evaluator that cannot score the dataset, labels that miss one.
        await AssertValidationAsync(
            await op.SendJsonAsync(HttpMethod.Post, "/admin/evaluations", new
            {
                tenantId = gateway.TenantId,
                datasetId,
                datasetVersion = 1,
                name = "bad",
                configurations = new object[]
                {
                    new { target = new { alias = "no-such-alias" }, prompt = new { id = promptId, version = 1 }, evaluator = new { kind = "classification", version = "1", config = new { labels = _spamOnly } } },
                    new { target = new { providerModelId = Guid.CreateVersion7() }, prompt = new { id = promptId, version = 9 }, evaluator = new { kind = "json_fields", version = "1" } },
                },
            }),
            "configurations[0].target.alias",
            "configurations[0].evaluator.config.labels",
            "configurations[1].target.providerModelId",
            "configurations[1].prompt.version",
            "configurations[1].evaluator.kind");
        await AssertValidationAsync(await op.SendJsonAsync(HttpMethod.Post, "/admin/evaluations", Body(datasetId, promptId, evaluator: new { kind = "classification", version = "2" })), "configurations[0].evaluator.version");
        (await gateway.ScalarAsync("select count(*) from evaluation.comparison")).ShouldBe(before);

        using var viewer = await gateway.AdministratorBrowserAsync("viewer", gateway.TenantId);
        (await viewer.SendJsonAsync(HttpMethod.Post, "/admin/evaluations", Body(datasetId, promptId))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [HappyPath("POST /admin/evaluations/estimate")]
    public async Task The_estimate_is_cases_times_the_per_case_worst_case_and_writes_nothing()
    {
        using var op = await gateway.AdministratorBrowserAsync("operator", gateway.TenantId);
        var (datasetId, promptId) = await SetupAsync(op, "classification", _classification);
        var before = await gateway.ScalarAsync("select count(*) from evaluation.evaluation_run");

        var response = await op.SendJsonAsync(HttpMethod.Post, "/admin/evaluations/estimate", Body(datasetId, promptId, maxEstimatedCost: 0.000001m));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var estimate = (await AdminBrowser.JsonAsync(response)).GetProperty("configurations").EnumerateArray().Single();
        estimate.GetProperty("cases").GetInt32().ShouldBe(3);
        estimate.GetProperty("estimatedCases").GetInt32().ShouldBe(2);
        estimate.GetProperty("failures").GetProperty("template_variable_missing").GetInt32().ShouldBe(1);
        var worst = estimate.GetProperty("worstCase").GetDecimal();
        worst.ShouldBeGreaterThan(0m);
        worst.ShouldBeLessThanOrEqualTo(2 * estimate.GetProperty("maxPerCaseWorstCase").GetDecimal());
        estimate.GetProperty("exceedsMaxEstimatedCost").GetBoolean().ShouldBeTrue();
        (await gateway.ScalarAsync("select count(*) from evaluation.evaluation_run")).ShouldBe(before);
    }

    [Fact]
    public async Task A_resumed_run_skips_the_cases_it_already_has_and_never_calls_the_provider_for_them()
    {
        using var owner = await gateway.OwnerBrowserAsync();
        var (datasetId, promptId) = await SetupAsync(owner, "classification", """
            {"id":"r-1","input":{"text":"spam"},"expected":"spam"}
            {"id":"r-2","input":{"text":"spam"},"expected":"spam"}
            {"id":"r-3","input":{"text":"ham"},"expected":"ham"}
            """);
        var runId = (await CreateAsync(owner, Body(datasetId, promptId))).RunIds.Single();

        // The previous worker died after recording r-1: the run is running and has one Case Result.
        var first = await gateway.ScalarAsync($"select id from evaluation.eval_case where dataset_id = '{datasetId}' and external_id = 'r-1'");
        await gateway.ExecuteAsync(
            $"update evaluation.evaluation_run set status = 'running', started_at = now(), cases_done = 1 where id = '{runId}'; " +
            "insert into evaluation.case_result (id, run_id, case_id, status, score, score_details, created_at) " +
            $"values ('{Guid.CreateVersion7()}', '{runId}', '{first}', 'passed', 1, '{{}}', now())");
        var calls = await CallsAsync(GatewayFixture.PrimaryModel);

        await RunJobAsync(runId, Token);

        (await CallsAsync(GatewayFixture.PrimaryModel) - calls).ShouldBe(2);
        (await gateway.ScalarAsync($"select count(*) from evaluation.case_result where run_id = '{runId}'")).ShouldBe("3");
        (await RunColumnAsync(runId, "concat_ws('|', status, cases_done)")).ShouldBe("succeeded|3");
        (await gateway.ScalarAsync($"select count(*) from usage.inference_request where evaluation_run_id = '{runId}'")).ShouldBe("2");
    }

    [Fact]
    public async Task The_orphan_sweep_fails_unfinished_runs_whose_job_is_dead_or_gone_and_leaves_live_ones_alone()
    {
        using var owner = await gateway.OwnerBrowserAsync();
        var (datasetId, promptId) = await SetupAsync(owner, "classification", _classification);
        var dead = (await CreateAsync(owner, Body(datasetId, promptId))).RunIds.Single();
        var gone = (await CreateAsync(owner, Body(datasetId, promptId))).RunIds.Single();
        var live = (await CreateAsync(owner, Body(datasetId, promptId))).RunIds.Single();

        // A worker died during the last attempt after recording c-1: the claim loop dead-lettered the job, the run stayed running.
        var first = await gateway.ScalarAsync($"select id from evaluation.eval_case where dataset_id = '{datasetId}' and external_id = 'c-1'");
        await gateway.ExecuteAsync(
            $"update evaluation.evaluation_run set status = 'running', started_at = now(), cases_done = 1 where id = '{dead}'; " +
            "insert into evaluation.case_result (id, run_id, case_id, status, score, score_details, created_at) " +
            $"values ('{Guid.CreateVersion7()}', '{dead}', '{first}', 'passed', 1, '{{}}', now()); " +
            $"update jobs.job set status = 'dead', attempts = max_attempts where dedupe_key = 'evaluation.run:{dead}'; " +
            $"delete from jobs.job where dedupe_key = 'evaluation.run:{gone}'");
        var sweep = gateway.Services.GetServices<IJobHandler>().Single(h => h.Kind == "evaluation.sweep_orphaned_runs");

        await sweep.HandleAsync(new JobContext(Guid.CreateVersion7(), sweep.Kind, "{}", 1, 5), Token);

        (await RunColumnAsync(dead, "concat_ws('|', status, error, completed_at is not null, aggregates->>'casesTotal')")).ShouldBe("failed|internal_error|t|1");
        (await RunColumnAsync(gone, "concat_ws('|', status, error, completed_at is not null, aggregates->>'casesTotal')")).ShouldBe("failed|internal_error|t|0");
        (await RunColumnAsync(live, "concat_ws('|', status, error is null, completed_at is null)")).ShouldBe("queued|t|t");
    }

    [Fact]
    [HappyPath("POST /admin/evaluations/{runId:guid}/cancel")]
    public async Task Cancelling_a_queued_run_means_it_never_starts_and_a_finished_run_cannot_be_cancelled()
    {
        using var op = await gateway.AdministratorBrowserAsync("operator", gateway.TenantId);
        var (datasetId, promptId) = await SetupAsync(op, "classification", _classification);
        var runId = (await CreateAsync(op, Body(datasetId, promptId))).RunIds.Single();
        var calls = await CallsAsync(GatewayFixture.PrimaryModel);

        var cancelled = await op.SendJsonAsync(HttpMethod.Post, $"/admin/evaluations/{runId}/cancel");
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AdminBrowser.JsonAsync(cancelled)).GetProperty("status").GetString().ShouldBe("cancelled");
        await RunJobAsync(runId, Token);

        (await CallsAsync(GatewayFixture.PrimaryModel)).ShouldBe(calls);
        (await RunColumnAsync(runId, "concat_ws('|', status, cases_done, completed_at is not null, aggregates->>'casesTotal')")).ShouldBe("cancelled|0|t|0");
        (await gateway.ScalarAsync($"select count(*) from identity.audit_record where action = 'evaluation.cancelled' and target_id = '{runId}'")).ShouldBe("1");
        var again = await op.SendJsonAsync(HttpMethod.Post, $"/admin/evaluations/{runId}/cancel");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AdminBrowser.ErrorCodeAsync(again)).ShouldBe("conflict");
    }

    [Fact]
    public async Task Cancelling_a_running_run_lets_in_flight_cases_finish_and_starts_nothing_new()
    {
        using var owner = await gateway.OwnerBrowserAsync();
        var jsonl = string.Join('\n', Enumerable.Range(0, 8).Select(i => $"{{\"id\":\"k-{i}\",\"input\":{{\"text\":\"spam\"}},\"expected\":\"spam\"}}"));
        var (datasetId, promptId) = await SetupAsync(owner, "classification", jsonl);
        var runId = (await CreateAsync(owner, Body(datasetId, promptId, concurrency: 1))).RunIds.Single();
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "latency=300");
        try
        {
            var job = RunJobAsync(runId, Token);
            var deadline = Environment.TickCount64 + 10_000;
            while (await RunColumnAsync(runId, "cases_done") == "0" && Environment.TickCount64 < deadline)
            {
                await Task.Delay(20, Token);
            }

            (await owner.SendJsonAsync(HttpMethod.Post, $"/admin/evaluations/{runId}/cancel")).StatusCode.ShouldBe(HttpStatusCode.OK);
            await job;
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }

        var done = int.Parse(await RunColumnAsync(runId, "cases_done"), CultureInfo.InvariantCulture);
        done.ShouldBeInRange(1, 7);
        (await gateway.ScalarAsync($"select count(*) from evaluation.case_result where run_id = '{runId}'")).ShouldBe(done.ToString(CultureInfo.InvariantCulture), "Cases that never started get no Case Result");
        (await RunColumnAsync(runId, "concat_ws('|', status, completed_at is not null, aggregates->>'casesTotal')")).ShouldBe($"cancelled|t|{done}");
    }

    [Fact]
    public async Task The_cost_cap_stops_new_cases_skips_the_rest_and_fails_the_run_with_run_cost_cap_reached()
    {
        using var owner = await gateway.OwnerBrowserAsync();
        var jsonl = string.Join('\n', Enumerable.Range(0, 4).Select(i => $"{{\"id\":\"m-{i}\",\"input\":{{\"text\":\"spam\"}},\"expected\":\"spam\"}}"));
        var (datasetId, promptId) = await SetupAsync(owner, "classification", jsonl);
        var estimate = await AdminBrowser.JsonAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/evaluations/estimate", Body(datasetId, promptId)));
        var perCase = estimate.GetProperty("configurations")[0].GetProperty("maxPerCaseWorstCase").GetDecimal();

        // Exactly one Case's worst case: the first starts, and once it has cost anything the next one would exceed the cap.
        var runId = (await CreateAsync(owner, Body(datasetId, promptId, concurrency: 1, maxEstimatedCost: perCase))).RunIds.Single();
        var calls = await CallsAsync(GatewayFixture.PrimaryModel);
        await RunJobAsync(runId, Token);

        (await CallsAsync(GatewayFixture.PrimaryModel) - calls).ShouldBe(1);
        (await RunColumnAsync(runId, "concat_ws('|', status, error, cases_done, cases_failed)")).ShouldBe("failed|run_cost_cap_reached|4|0");
        (await gateway.ScalarAsync($"select string_agg(status, ',' order by status) from evaluation.case_result where run_id = '{runId}'")).ShouldBe("passed,skipped,skipped,skipped");
        (await RunColumnAsync(runId, "aggregates->>'skipped'")).ShouldBe("3");
    }

    [Fact]
    public async Task A_run_resumed_after_the_cost_cap_skipped_cases_still_ends_failed_with_run_cost_cap_reached()
    {
        using var owner = await gateway.OwnerBrowserAsync();
        var (datasetId, promptId) = await SetupAsync(owner, "classification", """
            {"id":"s-1","input":{"text":"spam"},"expected":"spam"}
            {"id":"s-2","input":{"text":"spam"},"expected":"spam"}
            {"id":"s-3","input":{"text":"spam"},"expected":"spam"}
            """);
        var runId = (await CreateAsync(owner, Body(datasetId, promptId, maxEstimatedCost: 1000m))).RunIds.Single();

        // The previous worker hit the cap after s-1, wrote s-2 skipped and died before skipping s-3 and finishing the run.
        var first = await gateway.ScalarAsync($"select id from evaluation.eval_case where dataset_id = '{datasetId}' and external_id = 's-1'");
        var second = await gateway.ScalarAsync($"select id from evaluation.eval_case where dataset_id = '{datasetId}' and external_id = 's-2'");
        await gateway.ExecuteAsync(
            $"update evaluation.evaluation_run set status = 'running', started_at = now(), cases_done = 2 where id = '{runId}'; " +
            "insert into evaluation.case_result (id, run_id, case_id, status, score, score_details, created_at) values " +
            $"('{Guid.CreateVersion7()}', '{runId}', '{first}', 'passed', 1, '{{}}', now()), " +
            $"('{Guid.CreateVersion7()}', '{runId}', '{second}', 'skipped', null, '{{}}', now())");
        var calls = await CallsAsync(GatewayFixture.PrimaryModel);

        await RunJobAsync(runId, Token);

        (await CallsAsync(GatewayFixture.PrimaryModel)).ShouldBe(calls, "no new Case starts once the cap was reached");
        (await gateway.ScalarAsync($"select string_agg(status, ',' order by status) from evaluation.case_result where run_id = '{runId}'")).ShouldBe("passed,skipped,skipped");
        (await RunColumnAsync(runId, "concat_ws('|', status, error, cases_done, completed_at is not null, aggregates->>'skipped')")).ShouldBe("failed|run_cost_cap_reached|3|t|2");
    }

    [Fact]
    public async Task The_orphan_sweep_finalizes_a_cancelled_run_whose_worker_died_before_storing_its_aggregates()
    {
        using var owner = await gateway.OwnerBrowserAsync();
        var (datasetId, promptId) = await SetupAsync(owner, "classification", _classification);
        var dead = (await CreateAsync(owner, Body(datasetId, promptId))).RunIds.Single();
        var live = (await CreateAsync(owner, Body(datasetId, promptId))).RunIds.Single();

        // Both were cancelled while running after recording c-1. The worker of `dead` then died (its job was re-claimed, saw the
        // cancelled run and succeeded doing nothing); the worker of `live` is still finishing its in-flight Cases.
        var first = await gateway.ScalarAsync($"select id from evaluation.eval_case where dataset_id = '{datasetId}' and external_id = 'c-1'");
        await gateway.ExecuteAsync(
            $"update evaluation.evaluation_run set status = 'cancelled', started_at = now(), cases_done = 1 where id in ('{dead}', '{live}'); " +
            "insert into evaluation.case_result (id, run_id, case_id, status, score, score_details, created_at) values " +
            $"('{Guid.CreateVersion7()}', '{dead}', '{first}', 'passed', 1, '{{}}', now()), " +
            $"('{Guid.CreateVersion7()}', '{live}', '{first}', 'passed', 1, '{{}}', now()); " +
            $"update jobs.job set status = 'succeeded' where dedupe_key = 'evaluation.run:{dead}'; " +
            $"update jobs.job set status = 'running' where dedupe_key = 'evaluation.run:{live}'");
        var sweep = gateway.Services.GetServices<IJobHandler>().Single(h => h.Kind == "evaluation.sweep_orphaned_runs");

        await sweep.HandleAsync(new JobContext(Guid.CreateVersion7(), sweep.Kind, "{}", 1, 5), Token);

        (await RunColumnAsync(dead, "concat_ws('|', status, error is null, completed_at is not null, aggregates->>'casesTotal')")).ShouldBe("cancelled|t|t|1");
        (await RunColumnAsync(live, "concat_ws('|', status, completed_at is null, aggregates is null)")).ShouldBe("cancelled|t|t");
    }

    [Fact]
    public async Task An_llm_judge_run_makes_a_second_evaluation_request_and_stores_judge_usage_apart()
    {
        await AddJudgeAliasAsync();
        using var owner = await gateway.OwnerBrowserAsync();
        var (datasetId, promptId) = await SetupAsync(owner, "free_text", """{"id":"j-1","input":{"text":"a short answer"},"expected":"a short answer"}""");
        var runId = (await CreateAsync(owner, Body(datasetId, promptId, evaluator: new { kind = "llm_judge", version = "1", config = new { judgeAlias = "judge", rubric = "Is it right?" } }))).RunIds.Single();
        (await RunColumnAsync(runId, "run_configuration->'evaluator'->'judge'->>'alias'")).ShouldBe("judge");

        await RunJobAsync(runId, Token);

        var result = (await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/evaluations/{runId}/results"))).GetProperty("items").EnumerateArray().Single();
        result.GetProperty("status").GetString().ShouldBe("passed");
        result.GetProperty("score").GetDecimal().ShouldBe(0.75m);
        result.GetProperty("scoreDetails").GetProperty("modelJudged").GetBoolean().ShouldBeTrue();
        result.GetProperty("judgeInputTokens").GetInt32().ShouldBeGreaterThan(0);
        result.GetProperty("judgeEstimatedCost").GetDecimal().ShouldBeGreaterThan(0m);
        var judgeId = result.GetProperty("judgeInferenceRequestId").GetGuid();
        judgeId.ShouldNotBe(result.GetProperty("inferenceRequestId").GetGuid());
        (await gateway.ScalarAsync($"select string_agg(concat_ws('|', origin, requested_alias), ',' order by requested_alias) from usage.inference_request where evaluation_run_id = '{runId}'"))
            .ShouldBe($"evaluation|{GatewayFixture.ChatAlias},evaluation|judge");
        var aggregates = (await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/evaluations/{runId}"))).GetProperty("aggregates");
        aggregates.GetProperty("cost").GetProperty("judge").GetDecimal().ShouldBe(result.GetProperty("judgeEstimatedCost").GetDecimal());
        aggregates.GetProperty("llmJudge").GetProperty("modelJudged").GetBoolean().ShouldBeTrue();
        var request = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/requests/{judgeId}"));
        request.GetProperty("evaluationCase").GetProperty("role").GetString().ShouldBe("judge");
        request.GetProperty("evaluationCase").GetProperty("runId").GetGuid().ShouldBe(runId);
    }

    [Fact]
    public async Task A_run_keeps_the_policy_version_frozen_at_creation_after_the_policy_is_edited()
    {
        var echo = Guid.Parse(await gateway.ScalarAsync($"select m.id from registry.provider_model m join registry.provider p on p.id = m.provider_id where p.name = 'mock-openai' and m.provider_model_id = '{GatewayFixture.PrimaryModel}'"));
        var fallback = Guid.Parse(await gateway.ScalarAsync($"select m.id from registry.provider_model m join registry.provider p on p.id = m.provider_id where p.name = 'mock-openai' and m.provider_model_id = '{GatewayFixture.FallbackModel}'"));
        await gateway.WriteConfigurationAsync(GatewayFixture.PolicyAndAliasSql("frozen-eval", [("mock-openai", GatewayFixture.PrimaryModel, echo)]));
        using var owner = await gateway.OwnerBrowserAsync();
        var (datasetId, promptId) = await SetupAsync(owner, "classification", """{"id":"f-1","input":{"text":"spam"},"expected":"spam"}""");
        var runId = (await CreateAsync(owner, Body(datasetId, promptId, alias: "frozen-eval"))).RunIds.Single();

        var policyId = await gateway.ScalarAsync("select policy_id from routing.model_alias where alias = 'frozen-eval'");
        await gateway.WriteConfigurationAsync(
            $$"""
            insert into routing.routing_policy_version (policy_id, version, definition, resolved_candidates, created_at)
              values ('{{policyId}}', 2, '{"strategy":"static","candidates":["mock-openai/{{GatewayFixture.FallbackModel}}"]}', '{"mock-openai/{{GatewayFixture.FallbackModel}}":"{{fallback}}"}', now());
            update routing.routing_policy set latest_version = 2 where id = '{{policyId}}';
            """);
        await RunJobAsync(runId, Token);

        (await gateway.ScalarAsync($"select concat_ws('|', policy_version, selected_provider_model_id) from usage.inference_request where evaluation_run_id = '{runId}'"))
            .ShouldBe($"1|{echo}");
        (await RunColumnAsync(runId, "status")).ShouldBe("succeeded");
    }

    [Fact]
    [HappyPath("GET /admin/evaluations/{runId:guid}/export")]
    public async Task Exports_stream_the_run_header_and_one_row_per_case_result_as_jsonl_and_csv()
    {
        using var owner = await gateway.OwnerBrowserAsync();
        var (datasetId, promptId) = await SetupAsync(owner, "classification", _classification);
        var runId = (await CreateAsync(owner, Body(datasetId, promptId))).RunIds.Single();
        await RunJobAsync(runId, Token);

        var jsonl = await owner.GetAsync($"/admin/evaluations/{runId}/export?format=jsonl");
        jsonl.StatusCode.ShouldBe(HttpStatusCode.OK);
        jsonl.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-ndjson");
        var lines = (await jsonl.Content.ReadAsStringAsync(Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(4);
        using (var header = JsonDocument.Parse(lines[0]))
        {
            header.RootElement.GetProperty("type").GetString().ShouldBe("run");
            header.RootElement.GetProperty("runId").GetGuid().ShouldBe(runId);
            header.RootElement.GetProperty("configuration").GetProperty("evaluator").GetProperty("kind").GetString().ShouldBe("classification");
            header.RootElement.GetProperty("aggregates").GetProperty("quality").GetProperty("value").GetDecimal().ShouldBe(0.5m);
        }

        lines.Skip(1).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("externalId").GetString()).ShouldBe(["c-1", "c-2", "c-3"]);
        using (var first = JsonDocument.Parse(lines[1]))
        {
            first.RootElement.GetProperty("type").GetString().ShouldBe("case");
            first.RootElement.GetProperty("outputText").GetString().ShouldBe("spam");
            first.RootElement.GetProperty("expected").GetString().ShouldBe("spam");
        }

        var csv = await owner.GetAsync($"/admin/evaluations/{runId}/export?format=csv");
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var rows = (await csv.Content.ReadAsStringAsync(Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        rows[0].ShouldBe("# modelswitch_evaluation_export: 1");
        rows.ShouldContain($"# run_id: {runId}");
        rows.Single(r => r.StartsWith("# aggregates: ", StringComparison.Ordinal)).ShouldContain("\"quality\"");
        var columns = rows.First(r => !r.StartsWith('#'));
        columns.ShouldStartWith("case_id,external_id,ordinal,status,score");
        var data = rows.SkipWhile(r => r != columns).Skip(1).ToArray();
        data.Length.ShouldBe(3);
        data[0].Split(',')[1..4].ShouldBe(["c-1", "0", "passed"]);
        await AssertValidationAsync(await owner.GetAsync($"/admin/evaluations/{runId}/export?format=xml"), "format");
    }

    [Fact]
    [HappyPath("GET /admin/evaluations")]
    [HappyPath("POST /admin/evaluations/{runId:guid}/rerun")]
    [HappyPath("GET /admin/comparisons/{comparisonId:guid}")]
    [HappyPath("POST /admin/evaluations/{runId:guid}/aggregates/recompute")]
    public async Task Run_again_copies_the_frozen_configuration_and_the_comparison_shows_variance_across_runs()
    {
        using var op = await gateway.AdministratorBrowserAsync("operator", gateway.TenantId);
        var (datasetId, promptId) = await SetupAsync(op, "classification", _classification);
        var (comparisonId, runIds) = await CreateAsync(op, Body(datasetId, promptId));
        var runId = runIds.Single();

        var queuedRecompute = await op.SendJsonAsync(HttpMethod.Post, $"/admin/evaluations/{runId}/aggregates/recompute");
        queuedRecompute.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await RunJobAsync(runId, Token);

        var rerun = await op.SendJsonAsync(HttpMethod.Post, $"/admin/evaluations/{runId}/rerun");
        rerun.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var created = await AdminBrowser.JsonAsync(rerun);
        var againId = created.GetProperty("runIds")[0].GetGuid();
        created.GetProperty("comparisonId").GetGuid().ShouldNotBe(comparisonId);
        (await RunColumnAsync(againId, "config_sha256")).ShouldBe(await RunColumnAsync(runId, "config_sha256"));
        (await RunColumnAsync(againId, "run_configuration->'target'")).ShouldBe(await RunColumnAsync(runId, "run_configuration->'target'"));
        await RunJobAsync(againId, Token);

        var list = await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/evaluations?datasetId={datasetId}&limit=1"));
        var newest = list.GetProperty("items").EnumerateArray().Single();
        newest.GetProperty("id").GetGuid().ShouldBe(created.GetProperty("comparisonId").GetGuid());
        newest.GetProperty("name").GetString().ShouldBe("classification run (run again)");
        newest.GetProperty("runs")[0].GetProperty("quality").GetProperty("metric").GetString().ShouldBe("accuracy");
        var older = await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/evaluations?datasetId={datasetId}&cursor={list.GetProperty("nextCursor").GetString()}"));
        older.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldBe([comparisonId]);
        (await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/evaluations?datasetId={datasetId}&status=queued"))).GetProperty("items").GetArrayLength().ShouldBe(0);
        await AssertValidationAsync(await op.GetAsync("/admin/evaluations?status=done&datasetId=x"), "status", "datasetId");

        var comparison = await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/comparisons/{comparisonId}"));
        var run = comparison.GetProperty("runs").EnumerateArray().Single();
        run.GetProperty("quality").GetProperty("value").GetDecimal().ShouldBe(0.5m);
        run.GetProperty("candidateCostPerCase").GetDecimal().ShouldBeGreaterThan(0m);
        run.GetProperty("judgeCost").GetDecimal().ShouldBe(0m);
        run.GetProperty("latencyP50Ms").ValueKind.ShouldBe(JsonValueKind.Number);
        run.GetProperty("variance").GetProperty("runs").GetInt32().ShouldBe(2);
        run.GetProperty("variance").GetProperty("stdDev").GetDecimal().ShouldBe(0m);

        var recomputed = await op.SendJsonAsync(HttpMethod.Post, $"/admin/evaluations/{runId}/aggregates/recompute");
        recomputed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AdminBrowser.JsonAsync(recomputed)).GetProperty("quality").GetProperty("value").GetDecimal().ShouldBe(0.5m);
    }

    /// <summary>A <c>judge</c> alias over a <c>mock-judge</c> Provider Model, which replies <c>{"score":0.75,…}</c>.</summary>
    private async Task AddJudgeAliasAsync()
    {
        if (await gateway.ScalarAsync("select count(*) from routing.model_alias where alias = 'judge'") != "0")
        {
            return;
        }

        var modelId = Guid.CreateVersion7();
        var providerId = await gateway.ScalarAsync("select id from registry.provider where name = 'mock-openai'");
        await gateway.WriteConfigurationAsync(
            $$"""
            insert into registry.provider_model (id, provider_id, provider_model_id, display_name, capabilities, enabled, created_at, updated_at)
              select '{{modelId}}', '{{providerId}}', 'mock-judge', 'mock-judge', capabilities, true, now(), now() from registry.provider_model where provider_model_id = 'mock-echo' and provider_id = '{{providerId}}';
            insert into registry.model_price values (gen_random_uuid(), '{{modelId}}', 1, null, 2, 'USD', '2026-01-01', now(), null);
            """
            + GatewayFixture.PolicyAndAliasSql("judge", [("mock-openai", "mock-judge", modelId)]));
    }
}
