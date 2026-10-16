using System.Data.Common;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.SharedKernel.Outbox;
using ModelSwitch.Usage.Domain;
using Npgsql;
using NpgsqlTypes;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// Folds each <c>usage.request_completed</c> event into <c>usage.usage_hourly</c> (usage-and-budgets.md section 6). The relay runs it on
/// the transaction that records the event in <c>processed_event</c>, so a redelivered event changes nothing (NFR-06). One upsert:
/// counters and histograms are added to the existing row, bucket by bucket.
/// </summary>
internal sealed class UsageRollupConsumer : IOutboxConsumer
{
    public const string ConsumerName = "usage.rollup";

    private const string _upsert =
        """
        INSERT INTO usage.usage_hourly AS h (tenant_id, provider_model_id, requested_alias, hour, requests, failed_requests, fallback_requests,
            attempts, input_tokens, cached_input_tokens, output_tokens, estimated_cost, duration_ms_sum, latency_histogram, ttft_histogram)
        VALUES (@tenant, @model, @alias, @hour, @requests, @failed, @fallback, @attempts, @input, @cached, @output, @cost, @duration, @latency, @ttft)
        ON CONFLICT (tenant_id, provider_model_id, requested_alias, hour) DO UPDATE SET
            requests = h.requests + excluded.requests,
            failed_requests = h.failed_requests + excluded.failed_requests,
            fallback_requests = h.fallback_requests + excluded.fallback_requests,
            attempts = h.attempts + excluded.attempts,
            input_tokens = h.input_tokens + excluded.input_tokens,
            cached_input_tokens = h.cached_input_tokens + excluded.cached_input_tokens,
            output_tokens = h.output_tokens + excluded.output_tokens,
            estimated_cost = h.estimated_cost + excluded.estimated_cost,
            duration_ms_sum = h.duration_ms_sum + excluded.duration_ms_sum,
            latency_histogram = (SELECT array_agg(coalesce(a, 0) + coalesce(b, 0) ORDER BY i)
                                 FROM unnest(h.latency_histogram, excluded.latency_histogram) WITH ORDINALITY AS t(a, b, i)),
            ttft_histogram = (SELECT array_agg(coalesce(a, 0) + coalesce(b, 0) ORDER BY i)
                              FROM unnest(h.ttft_histogram, excluded.ttft_histogram) WITH ORDINALITY AS t(a, b, i))
        """;

    public string Name => ConsumerName;

    public string EventType => OutboxWriter.UsageRequestCompleted;

    public async Task HandleAsync(OutboxEvent outboxEvent, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        ArgumentNullException.ThrowIfNull(connection);
        var row = HourlyContribution.From(RequestCompletedEvent.FromJson(outboxEvent.PayloadJson));

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _upsert;
        Add(command, "tenant", row.TenantId);
        Add(command, "model", row.ProviderModelId);
        Add(command, "alias", row.RequestedAlias);
        Add(command, "hour", row.Hour);
        Add(command, "requests", row.Requests);
        Add(command, "failed", row.FailedRequests);
        Add(command, "fallback", row.FallbackRequests);
        Add(command, "attempts", row.Attempts);
        Add(command, "input", row.InputTokens);
        Add(command, "cached", row.CachedInputTokens);
        Add(command, "output", row.OutputTokens);
        Add(command, "cost", row.EstimatedCost);
        Add(command, "duration", row.DurationMsSum);
        command.Parameters.Add(new NpgsqlParameter("latency", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = row.LatencyHistogram });
        command.Parameters.Add(new NpgsqlParameter("ttft", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = row.TtftHistogram });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add<T>(DbCommand command, string name, T value) => command.Parameters.Add(new NpgsqlParameter<T>(name, value));
}
