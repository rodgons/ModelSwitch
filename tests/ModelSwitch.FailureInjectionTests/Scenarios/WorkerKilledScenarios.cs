using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelSwitch.FailureInjectionTests.Support;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Worker;
using Shouldly;

namespace ModelSwitch.FailureInjectionTests.Scenarios;

/// <summary>
/// testing.md section 3: "Worker killed during evaluation" (NFR-02, evaluation.md section 5). The real worker executable runs an
/// Evaluation Run and is killed mid-run, so it neither hands its job back nor finishes the Cases in flight. Once the job's lease has
/// expired a new worker host claims it again and resumes from the stored Case Results: every Case ends with exactly one Case Result,
/// the run succeeds, and the Inference Requests the dead worker left open are reconciled by the sweeper.
/// </summary>
[Slow]
public sealed class WorkerKilledDuringEvaluationScenario(GatewayFixture gateway) : ScenarioTests(gateway), IClassFixture<GatewayFixture>
{
    private const int _cases = 12;
    private const int _concurrency = 2;
    private const int _killAfter = 3;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_whose_worker_is_killed_is_resumed_by_another_worker_without_duplicate_case_results()
    {
        await ResetAsync();

        // Slow enough that the run is still going when the worker dies, fast enough to finish in a few seconds afterwards.
        await Gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "latency=400");
        var (runId, datasetId) = await CreateRunAsync();
        var job = $"dedupe_key = 'evaluation.run:{runId}'";

        using (var worker = StartWorkerProcess())
        {
            try
            {
                await WaitUntilAsync(async () => await CountAsync($"select count(*) from evaluation.case_result where run_id = '{runId}'") >= _killAfter, "the first worker records some Case Results");
            }
            finally
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(Token);
            }
        }

        var doneAtKill = await CountAsync($"select count(*) from evaluation.case_result where run_id = '{runId}'");
        doneAtKill.ShouldBeLessThan(_cases, "the worker died mid-run");
        (await ScalarAsync($"select status from evaluation.evaluation_run where id = '{runId}'")).ShouldBe("running");
        (await ScalarAsync($"select concat_ws('|', status, attempts, locked_by is not null) from jobs.job where {job}")).ShouldBe("running|1|t", "a killed worker never hands its job back");

        // The job is claimable again once its lease (60 s) has run out; move the lease into the past instead of waiting for it.
        await Gateway.ExecuteAsync($"update jobs.job set locked_until = now() - interval '1 second' where {job}");
        await RunWorkerHostUntilFinishedAsync(runId);

        (await ScalarAsync($"select concat_ws('|', status, cases_done, cases_total) from evaluation.evaluation_run where id = '{runId}'"))
            .ShouldBe($"succeeded|{_cases}|{_cases}");
        (await ScalarAsync($"select concat_ws('|', count(*), count(distinct case_id)) from evaluation.case_result where run_id = '{runId}'"))
            .ShouldBe($"{_cases}|{_cases}", "one Case Result per Case: nothing recorded before the kill is recorded again");
        (await CountAsync($"select count(*) from evaluation.eval_case c where c.dataset_id = '{datasetId}' and not exists (select 1 from evaluation.case_result r where r.run_id = '{runId}' and r.case_id = c.id)"))
            .ShouldBe(0, "and every Case has one");
        (await ScalarAsync($"select concat_ws('|', status, attempts) from jobs.job where {job}")).ShouldBe("succeeded|2", "the second claim resumed the run");

        // The Cases in flight at the kill opened Inference Requests that were never settled. The sweeper reconciles them once their
        // Reservations expire (moved into the past here), so the Budget again equals the Usage Records.
        var orphaned = await CountAsync($"select count(*) from usage.inference_request where evaluation_run_id = '{runId}' and status = 'running'");
        orphaned.ShouldBeLessThanOrEqualTo(_concurrency, "only the Cases in flight at the kill were left open");
        await Gateway.ExecuteAsync(
            "update usage.reservation set expires_at = now() - interval '1 second' where status = 'held' and inference_request_id in " +
            $"(select id from usage.inference_request where evaluation_run_id = '{runId}')");
        (await Gateway.Services.GetRequiredService<IReservationSweeper>().SweepAbandonedAsync(100, Token)).ShouldBe(orphaned);
        (await CountAsync($"select count(*) from usage.inference_request where evaluation_run_id = '{runId}' and status = 'running'")).ShouldBe(0);
        await AssertConsistentBudgetAsync(Gateway.TenantId);

        // Each Case calls the Provider once; only the Cases the dead worker had in flight are called again.
        var calls = (await ProviderCallsAsync())[GatewayFixture.PrimaryModel];
        calls.ShouldBeGreaterThanOrEqualTo(_cases);
        AmplificationReport.Measure(
            "worker killed during evaluation (per Case)",
            _cases,
            calls,
            (double)(_cases + _concurrency) / _cases,
            $"each Case once, plus the {_concurrency} in flight at the kill");
    }

    private async Task<(Guid RunId, Guid DatasetId)> CreateRunAsync()
    {
        using var owner = await Gateway.OwnerBrowserAsync();
        var dataset = await AdminBrowser.JsonAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/datasets", new { tenantId = Gateway.TenantId, name = "killed-worker", taskKind = "classification" }));
        var datasetId = dataset.GetProperty("id").GetGuid();
        var jsonl = string.Join('\n', Enumerable.Range(1, _cases).Select(i => $"{{\"id\":\"k-{i}\",\"input\":{{\"text\":\"case {i}\"}},\"expected\":\"case {i}\"}}"));
        (await owner.SendJsonAsync(HttpMethod.Post, $"/admin/datasets/{datasetId}/versions", new StringContent(jsonl, Encoding.UTF8, "application/x-ndjson")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var promptId = (await AdminBrowser.JsonAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/prompts", new { tenantId = Gateway.TenantId, name = "killed-worker" }))).GetProperty("id").GetGuid();
        (await owner.SendJsonAsync(HttpMethod.Post, $"/admin/prompts/{promptId}/versions", new { messages = new[] { new { role = "user", content = "{{text}}" } } }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await owner.SendJsonAsync(HttpMethod.Post, "/admin/evaluations", new
        {
            tenantId = Gateway.TenantId,
            datasetId,
            datasetVersion = 1,
            name = "killed worker",
            configurations = new[] { new { target = new { alias = GatewayFixture.ChatAlias }, prompt = new { id = promptId, version = 1 }, evaluator = new { kind = "exact_match", version = "1" } } },
            concurrency = _concurrency,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return ((await AdminBrowser.JsonAsync(created)).GetProperty("runIds")[0].GetGuid(), datasetId);
    }

    /// <summary>The worker executable (<c>ModelSwitch.Worker.dll</c>), configured through its environment as in a deployment.</summary>
    private Process StartWorkerProcess()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ModelSwitch.Worker.dll"));
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("MODELSWITCH__", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(key);
        }

        start.Environment["MODELSWITCH__ConnectionStrings__Postgres"] = Gateway.ConnectionString;
        start.Environment["MODELSWITCH__Security__ApiKeyPepper"] = ModuleServices.Pepper;
        start.Environment["MODELSWITCH__Outbound__AllowedPrivateHosts"] = "127.0.0.1";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        var process = Process.Start(start)!;

        // Drain the output so a chatty worker never blocks on a full pipe.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>A second worker, composed in-process from <see cref="WorkerServices"/>, run until the Evaluation Run and its job have finished.</summary>
    private async Task RunWorkerHostUntilFinishedAsync(Guid runId)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = Gateway.ConnectionString,
            ["Security:ApiKeyPepper"] = ModuleServices.Pepper,
            ["Outbound:AllowedPrivateHosts"] = "127.0.0.1",
        });
        builder.Services.AddWorkerServices();
        using var host = builder.Build();
        await host.StartAsync(Token);
        try
        {
            // The run turns terminal a moment before its job handler returns; stopping the host in between would hand the job back
            // (released, not failed), so wait for the job too.
            await WaitUntilAsync(
                async () => await ScalarAsync($"select status from evaluation.evaluation_run where id = '{runId}'") is not ("queued" or "running")
                    && await ScalarAsync($"select status from jobs.job where dedupe_key = 'evaluation.run:{runId}'") == "succeeded",
                "the resumed run and its job finish");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private async Task<int> CountAsync(string sql) => int.Parse((await ScalarAsync(sql))!, CultureInfo.InvariantCulture);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!await condition())
        {
            Environment.TickCount64.ShouldBeLessThan(deadline, $"timed out waiting until {what}");
            await Task.Delay(100, Token);
        }
    }
}
