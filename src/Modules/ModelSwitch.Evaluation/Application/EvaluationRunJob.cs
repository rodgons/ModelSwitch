using System.Text.Json;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Jobs;

namespace ModelSwitch.Evaluation.Application;

/// <summary>
/// The <c>evaluation.run</c> job (evaluation.md section 5): one per Evaluation Run, enqueued with the run in the creating transaction
/// (dedupe key <c>evaluation.run:&lt;runId&gt;</c>). Payload <c>{"runId": "…"}</c>, metadata only. A claim after a worker crash resumes
/// the run (<see cref="RunExecutor"/>). When the last attempt throws, the run is marked <c>failed</c> with <c>internal_error</c> so it
/// does not stay <c>running</c> once its job is dead.
/// </summary>
internal sealed class EvaluationRunJob(RunExecutor executor) : IJobHandler
{
    public const string JobKind = "evaluation.run";

    public string Kind => JobKind;

    public static JobRequest Request(Guid runId)
        => new(JobKind, JsonSerializer.Serialize(new Payload(runId), JsonSerializerOptions.Web), DedupeKey: $"{JobKind}:{runId:D}");

    public async Task HandleAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(context.PayloadJson, JsonSerializerOptions.Web);
        }
        catch (JsonException exception)
        {
            throw new PermanentJobFailureException("The evaluation.run payload is not valid JSON.", exception);
        }

        if (payload is not { RunId: var runId } || runId == Guid.Empty)
        {
            throw new PermanentJobFailureException("The evaluation.run payload has no runId.");
        }

        try
        {
            await executor.ExecuteAsync(runId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && (context.Attempt >= context.MaxAttempts || exception is PermanentJobFailureException))
        {
            await executor.FailAsync(runId, ErrorCode.InternalError.Value, CancellationToken.None);
            throw;
        }
    }

    private sealed record Payload(Guid RunId);
}
