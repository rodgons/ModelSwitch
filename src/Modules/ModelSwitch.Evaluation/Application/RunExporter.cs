using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Evaluation.Domain.Runs;
using ModelSwitch.Evaluation.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Evaluation.Application;

/// <summary>An export ready to stream: the response's media type and file name, and the writer that streams it.</summary>
internal sealed record RunExport(string ContentType, string FileName, Func<Stream, CancellationToken, Task> WriteAsync);

/// <summary>
/// Exports one run's Case Results (evaluation.md section 7), one row per Case Result in upload order, streamed from the database row
/// by row and never built in memory.
/// <list type="bullet">
/// <item><c>jsonl</c>: the first line is <c>{"type":"run", …}</c> (ids, status, Run Configuration, <c>configSha256</c>, aggregates),
/// then one <c>{"type":"case", …}</c> line per Case Result.</item>
/// <item><c>csv</c> (RFC 4180, UTF-8, <c>\n</c>): a header block of <c># key: value</c> lines (the run's ids, status,
/// <c>config_sha256</c>, and <c>run_configuration</c> and <c>aggregates</c> as one-line canonical JSON), then the column row, then one row
/// per Case Result. JSON-valued cells (<c>predicted</c>, <c>expected</c>, <c>score_details</c>) hold compact JSON.</item>
/// </list>
/// The aggregates are the stored ones, or for an unfinished run computed from the Case Results stored when the export starts.
/// </summary>
internal sealed class RunExporter(IDbContextFactory<EvaluationDbContext> contexts, EvaluationRunService runs)
{
    public const string Csv = "csv";
    public const string Jsonl = "jsonl";

    /// <summary>The CSV columns, in order.</summary>
    public static IReadOnlyList<string> CsvColumns { get; } =
    [
        "case_id", "external_id", "ordinal", "status", "score", "predicted", "output_text", "expected", "score_details", "error_code",
        "input_tokens", "output_tokens", "estimated_cost", "judge_input_tokens", "judge_output_tokens", "judge_estimated_cost",
        "latency_ms", "inference_request_id", "judge_inference_request_id", "created_at",
    ];

    private static readonly JsonWriterOptions _jsonOptions = new() { Indented = false };

    private static readonly ReadOnlyMemory<byte> _newLine = new[] { (byte)'\n' };

    public async Task<Result<RunExport>> OpenAsync(Guid runId, string? format, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var found = await runs.FindRunAsync(db, runId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        if (format is not (Csv or Jsonl))
        {
            return Error.Validation("The export query is invalid.", new Dictionary<string, string[]> { ["format"] = ["The format must be csv or jsonl."] });
        }

        var run = found.Value;
        var aggregates = run.AggregatesJson
            ?? RunAggregation.ToJson(await RunAggregation.ComputeAsync(db, runId, RunConfiguration.Parse(run.RunConfigurationJson).Evaluator.Kind, cancellationToken));
        var header = new ExportHeader(run, aggregates);
        var name = $"evaluation-run-{runId:D}.{format}";
        return format == Csv
            ? new RunExport("text/csv; charset=utf-8", name, (stream, token) => WriteCsvAsync(header, stream, token))
            : new RunExport("application/x-ndjson; charset=utf-8", name, (stream, token) => WriteJsonlAsync(header, stream, token));
    }

    /// <summary>One RFC 4180 field: quoted when it holds a comma, a quote, CR or LF (quotes doubled); null is empty.</summary>
    public static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }

    private sealed record ExportHeader(EvaluationRun Run, string AggregatesJson);

    private sealed record Row(CaseResult Result, string ExternalId, int Ordinal, string ExpectedJson);

    private async IAsyncEnumerable<Row> RowsAsync(Guid runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = (from result in db.CaseResults.AsNoTracking()
                    join @case in db.Cases.AsNoTracking() on result.CaseId equals @case.Id
                    where result.RunId == runId
                    orderby @case.Ordinal
                    select new Row(result, @case.ExternalId, @case.Ordinal, @case.ExpectedJson))
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken);
        await foreach (var row in rows)
        {
            yield return row;
        }
    }

    private async Task WriteJsonlAsync(ExportHeader header, Stream stream, CancellationToken cancellationToken)
    {
        await using (var writer = new Utf8JsonWriter(stream, _jsonOptions))
        {
            var run = header.Run;
            writer.WriteStartObject();
            writer.WriteString("type", "run");
            writer.WriteString("runId", run.Id);
            writer.WriteString("comparisonId", run.ComparisonId);
            writer.WriteString("tenantId", run.TenantId);
            writer.WriteString("status", run.Status);
            WriteNullableString(writer, "error", run.Error);
            writer.WriteString("configSha256", run.ConfigSha256);
            writer.WritePropertyName("configuration");
            writer.WriteRawValue(run.RunConfigurationJson);
            WriteNullableDecimal(writer, "maxEstimatedCost", run.MaxEstimatedCost);
            writer.WriteNumber("casesTotal", run.CasesTotal);
            writer.WriteString("createdAt", run.CreatedAt);
            WriteNullableDate(writer, "startedAt", run.StartedAt);
            WriteNullableDate(writer, "completedAt", run.CompletedAt);
            writer.WritePropertyName("aggregates");
            writer.WriteRawValue(header.AggregatesJson);
            writer.WriteEndObject();
        }

        await NewLineAsync(stream, cancellationToken);
        await foreach (var row in RowsAsync(header.Run.Id, cancellationToken))
        {
            await using (var writer = new Utf8JsonWriter(stream, _jsonOptions))
            {
                var r = row.Result;
                writer.WriteStartObject();
                writer.WriteString("type", "case");
                writer.WriteString("caseId", r.CaseId);
                writer.WriteString("externalId", row.ExternalId);
                writer.WriteNumber("ordinal", row.Ordinal);
                writer.WriteString("status", r.Status);
                WriteNullableDecimal(writer, "score", r.Score);
                WriteRaw(writer, "predicted", r.PredictedJson);
                WriteNullableString(writer, "outputText", r.OutputText);
                WriteRaw(writer, "expected", row.ExpectedJson);
                WriteRaw(writer, "scoreDetails", r.ScoreDetailsJson);
                WriteNullableString(writer, "errorCode", r.ErrorCode);
                WriteNullableInt(writer, "inputTokens", r.InputTokens);
                WriteNullableInt(writer, "outputTokens", r.OutputTokens);
                WriteNullableDecimal(writer, "estimatedCost", r.EstimatedCost);
                WriteNullableInt(writer, "judgeInputTokens", r.JudgeInputTokens);
                WriteNullableInt(writer, "judgeOutputTokens", r.JudgeOutputTokens);
                WriteNullableDecimal(writer, "judgeEstimatedCost", r.JudgeEstimatedCost);
                WriteNullableInt(writer, "latencyMs", r.LatencyMs);
                WriteNullableGuid(writer, "inferenceRequestId", r.InferenceRequestId);
                WriteNullableGuid(writer, "judgeInferenceRequestId", r.JudgeInferenceRequestId);
                writer.WriteString("createdAt", r.CreatedAt);
                writer.WriteEndObject();
            }

            await NewLineAsync(stream, cancellationToken);
        }
    }

    private async Task WriteCsvAsync(ExportHeader header, Stream stream, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 16 * 1024, leaveOpen: true) { NewLine = "\n" };
        var run = header.Run;
        await writer.WriteLineAsync("# modelswitch_evaluation_export: 1");
        await writer.WriteLineAsync($"# run_id: {run.Id:D}");
        await writer.WriteLineAsync($"# comparison_id: {run.ComparisonId:D}");
        await writer.WriteLineAsync($"# tenant_id: {run.TenantId:D}");
        await writer.WriteLineAsync($"# status: {run.Status}");
        await writer.WriteLineAsync($"# error: {run.Error}");
        await writer.WriteLineAsync($"# config_sha256: {run.ConfigSha256}");
        await writer.WriteLineAsync($"# max_estimated_cost: {run.MaxEstimatedCost?.ToString(CultureInfo.InvariantCulture)}");
        await writer.WriteLineAsync($"# started_at: {Date(run.StartedAt)}");
        await writer.WriteLineAsync($"# completed_at: {Date(run.CompletedAt)}");
        await writer.WriteLineAsync($"# run_configuration: {OneLine(run.RunConfigurationJson)}");
        await writer.WriteLineAsync($"# aggregates: {OneLine(header.AggregatesJson)}");
        await writer.WriteLineAsync(string.Join(',', CsvColumns));
        await foreach (var row in RowsAsync(run.Id, cancellationToken))
        {
            var r = row.Result;
            string?[] fields =
            [
                r.CaseId.ToString("D"), row.ExternalId, row.Ordinal.ToString(CultureInfo.InvariantCulture), r.Status, Number(r.Score),
                Compact(r.PredictedJson), r.OutputText, Compact(row.ExpectedJson), Compact(r.ScoreDetailsJson), r.ErrorCode,
                Number(r.InputTokens), Number(r.OutputTokens), Number(r.EstimatedCost), Number(r.JudgeInputTokens), Number(r.JudgeOutputTokens),
                Number(r.JudgeEstimatedCost), Number(r.LatencyMs), r.InferenceRequestId?.ToString("D"), r.JudgeInferenceRequestId?.ToString("D"),
                Date(r.CreatedAt),
            ];
            await writer.WriteLineAsync(string.Join(',', fields.Select(CsvField)));
        }

        await writer.FlushAsync(cancellationToken);
    }

    private static string OneLine(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Domain.CanonicalJson.Serialize(document.RootElement);
    }

    private static string? Compact(string? json) => json is null ? null : OneLine(json);

    private static string? Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Date(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static async Task NewLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(_newLine, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void WriteRaw(Utf8JsonWriter writer, string name, string? json)
    {
        writer.WritePropertyName(name);
        if (json is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteRawValue(json);
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableDecimal(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is { } v)
        {
            writer.WriteNumber(name, v);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableInt(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } v)
        {
            writer.WriteNumber(name, v);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } v)
        {
            writer.WriteString(name, v);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableDate(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } v)
        {
            writer.WriteString(name, v);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
