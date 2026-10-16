using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.SharedKernel.Pagination;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;
using Npgsql;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// <see cref="IUsageQueries"/> over the <c>usage</c> schema, in plain SQL: the dashboard's reads are aggregates and keyset pages that EF
/// would only obscure. Every query is bounded by the caller's Tenant Grant. Reading rule for ranges: <see cref="UsageRange"/>.
/// </summary>
internal sealed class UsageQueries(IDbContextFactory<UsageDbContext> contexts) : IUsageQueries
{
    private const int _recentFailures = 10;

    private const string _requestColumns =
        "r.id, r.tenant_id, r.origin, r.requested_alias, r.task_type, r.status, r.error_code, r.streaming, r.attempt_count, r.had_fallback, " +
        "r.selected_provider_model_id, r.started_at, r.completed_at, r.duration_ms, r.ttft_ms, r.total_input_tokens, r.total_output_tokens, " +
        "r.total_estimated_cost, r.usage_status";

    // The Provider Model a raw request is attributed to, as the rollup attributes it (HourlyContribution).
    private const string _attributedModel =
        "coalesce(r.selected_provider_model_id, (SELECT a.provider_model_id FROM usage.provider_attempt a WHERE a.inference_request_id = r.id " +
        "AND a.status <> 'skipped_circuit_open' ORDER BY a.attempt_number DESC LIMIT 1), '00000000-0000-0000-0000-000000000000'::uuid)";

    private const string _failed = "r.status IN ('failed', 'abandoned')";

    public async Task<PagedResult<RequestListItem>> ListRequestsAsync(RequestListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sql = new Sql();
        var where = Where(query.Filter, sql);
        var descending = query.Sort is RequestSort.StartedAtDescending or RequestSort.DurationDescending;
        var byDuration = query.Sort is RequestSort.DurationDescending or RequestSort.DurationAscending;
        var key = byDuration ? "coalesce(r.duration_ms, -1)" : "r.started_at";
        if (query.Page.After is { } after && ParseCursor(after, byDuration) is { } cursor)
        {
            where.Add($"({key}, r.id) {(descending ? "<" : ">")} ({sql.Add(cursor.Key)}, {sql.Add(cursor.Id)})");
        }

        var order = descending ? "DESC" : "ASC";
        sql.Text.Append(CultureInfo.InvariantCulture, $"SELECT {_requestColumns} FROM usage.inference_request r {WhereClause(where)} ORDER BY {key} {order}, r.id {order} LIMIT {query.Page.Limit + 1}");

        var rows = await ReadAsync(sql, ReadRequest, cancellationToken);
        return Pagination.Page(rows, query.Page.Limit, item => byDuration
            ? $"{(item.DurationMs ?? -1).ToString(CultureInfo.InvariantCulture)}|{item.Id}"
            : $"{item.StartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{item.Id}");
    }

    public async Task<RequestDetail?> GetRequestAsync(Guid requestId, IReadOnlyCollection<Guid>? tenantIds, CancellationToken cancellationToken)
    {
        var sql = new Sql();
        var where = new List<string> { $"r.id = {sql.Add(requestId)}" };
        if (tenantIds is not null)
        {
            where.Add($"r.tenant_id = ANY({sql.Add(tenantIds.ToArray())})");
        }

        sql.Text.Append(
            $"SELECT {_requestColumns}, r.client_request_id, r.api_key_id, r.evaluation_run_id, r.policy_id, r.policy_version, r.input_bytes, r.trace_id, r.routing_decision::text " +
            $"FROM usage.inference_request r {WhereClause(where)} LIMIT 1");

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var connection = await OpenAsync(db, cancellationToken);
        RequestDetail? detail = null;
        await using (var command = sql.Command(connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            const int extra = 19;
            detail = new RequestDetail(
                ReadRequest(reader),
                NullableString(reader, extra),
                Nullable<Guid>(reader, extra + 1),
                Nullable<Guid>(reader, extra + 2),
                Nullable<Guid>(reader, extra + 3),
                Nullable<int>(reader, extra + 4),
                reader.GetInt32(extra + 5),
                reader.GetString(extra + 6),
                NullableString(reader, extra + 7) is { } json ? JsonNode.Parse(json) : null,
                [],
                []);
        }

        var attempts = (await ReadAttemptsAsync(connection, [requestId], cancellationToken)).GetValueOrDefault(requestId) ?? [];

        var records = new List<UsageRecordView>();
        await using (var command = new NpgsqlCommand(
            "SELECT id, provider_attempt_id, provider_model_id, model_price_id, input_tokens, cached_input_tokens, output_tokens, usage_source, estimate_method, " +
            "input_cost, output_cost, total_cost, currency, recorded_at FROM usage.usage_record WHERE inference_request_id = @id ORDER BY recorded_at, id",
            connection))
        {
            command.Parameters.AddWithValue("id", requestId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(new UsageRecordView(
                    reader.GetGuid(0),
                    Nullable<Guid>(reader, 1),
                    Nullable<Guid>(reader, 2),
                    Nullable<Guid>(reader, 3),
                    Nullable<int>(reader, 4),
                    Nullable<int>(reader, 5),
                    Nullable<int>(reader, 6),
                    reader.GetString(7),
                    NullableString(reader, 8),
                    reader.GetDecimal(9),
                    reader.GetDecimal(10),
                    reader.GetDecimal(11),
                    reader.GetString(12).Trim(),
                    reader.GetFieldValue<DateTimeOffset>(13)));
            }
        }

        return detail with { Attempts = attempts, UsageRecords = records };
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AttemptView>>> AttemptsForAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestIds);
        if (requestIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<AttemptView>>();
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await ReadAttemptsAsync(await OpenAsync(db, cancellationToken), requestIds, cancellationToken);
    }

    public async Task<IReadOnlyList<UsageRow>> UsageAsync(UsageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = query.Filter;

        // The rollup has no origin, status, error code, fallback or Provider column: such filters are answered from the raw rows only.
        var rawOnly = filter.Origin is not null || filter.Status is not null || filter.ErrorCode is not null || filter.HadFallback is not null || filter.ProviderId is not null;
        var plan = rawOnly ? new UsageRangePlan(true, null, [(query.From, query.To)]) : UsageRange.Plan(query.From, query.To);
        var unit = query.Interval switch
        {
            UsageInterval.Hour => "hour",
            UsageInterval.Day => "day",
            _ => "month",
        };
        var groups = query.GroupBy;
        var totals = new Dictionary<(DateTimeOffset, Guid?, Guid?, string?), UsageRow>();

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var connection = await OpenAsync(db, cancellationToken);

        foreach (var (from, to) in plan.Raw)
        {
            var sql = new Sql();
            var where = Where(filter with { From = from, To = to }, sql);
            where.Add("r.status <> 'running'");
            sql.Text.Append(
                $"SELECT date_trunc('{unit}', r.started_at, 'UTC'), {Group(groups, UsageGrouping.Tenant, "r.tenant_id")}, {Group(groups, UsageGrouping.ProviderModel, _attributedModel)}, " +
                $"{Group(groups, UsageGrouping.Alias, "r.requested_alias", "text")}, count(*), count(*) FILTER (WHERE {_failed}), coalesce(sum(r.total_input_tokens), 0), " +
                "coalesce(sum(c.cached), 0), coalesce(sum(r.total_output_tokens), 0), coalesce(sum(r.total_estimated_cost), 0) " +
                "FROM usage.inference_request r LEFT JOIN LATERAL (SELECT sum(u.cached_input_tokens) AS cached FROM usage.usage_record u WHERE u.inference_request_id = r.id) c ON true " +
                $"{WhereClause(where)} GROUP BY 1, 2, 3, 4");
            await AccumulateAsync(connection, sql, totals, cancellationToken);
        }

        if (plan.Hourly is { } hours)
        {
            var sql = new Sql();
            var where = HourlyWhere(filter, hours.From, hours.To, sql);
            sql.Text.Append(
                $"SELECT date_trunc('{unit}', h.hour, 'UTC'), {Group(groups, UsageGrouping.Tenant, "h.tenant_id")}, {Group(groups, UsageGrouping.ProviderModel, "h.provider_model_id")}, " +
                $"{Group(groups, UsageGrouping.Alias, "h.requested_alias", "text")}, sum(h.requests), sum(h.failed_requests), sum(h.input_tokens), sum(h.cached_input_tokens), " +
                $"sum(h.output_tokens), sum(h.estimated_cost) FROM usage.usage_hourly h {WhereClause(where)} GROUP BY 1, 2, 3, 4");
            await AccumulateAsync(connection, sql, totals, cancellationToken);
        }

        return [.. totals.Values.OrderBy(r => r.Period).ThenBy(r => r.TenantId).ThenBy(r => r.ProviderModelId).ThenBy(r => r.Alias, StringComparer.Ordinal)];
    }

    public async Task<OverviewReport> OverviewAsync(OverviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var plan = UsageRange.Plan(query.From, query.To);
        var step = UsageRange.StepFor(query.To - query.From);
        var filter = new RequestFilter(query.TenantIds, TenantId: query.TenantId);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var connection = await OpenAsync(db, cancellationToken);

        OverviewFigures totals;
        List<OverviewPoint> series;
        if (plan.Exact)
        {
            totals = (await ExactFiguresAsync(connection, filter with { From = query.From, To = query.To }, step: null, cancellationToken)).Single().Figures;
            series = await ExactFiguresAsync(connection, filter with { From = query.From, To = query.To }, step, cancellationToken);
        }
        else
        {
            var buckets = await BucketedFiguresAsync(connection, filter, plan, step, cancellationToken);
            series = [.. buckets.OrderBy(b => b.Key).Select(b => new OverviewPoint(b.Key, b.Value.ToFigures()))];
            totals = buckets.Values.Aggregate(new BucketAccumulator(), (sum, b) => sum.Add(b)).ToFigures();
        }

        var failures = await ListRequestsAsync(
            new RequestListQuery(filter with { From = query.From, To = query.To, Status = "failed" }, RequestSort.StartedAtDescending, new PageRequest(_recentFailures, null)),
            cancellationToken);
        return new OverviewReport(query.From, query.To, step, totals, series, failures.Items);
    }

    public async Task<IReadOnlyList<AttemptStats>> AttemptStatsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var sql = new Sql();
        sql.Text.Append(
            "SELECT provider_model_id, count(*), count(*) FILTER (WHERE status IN ('failed', 'ambiguous')), " +
            "percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_ms), percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms) " +
            $"FROM usage.provider_attempt WHERE started_at >= {sql.Add(since)} AND status <> 'skipped_circuit_open' GROUP BY provider_model_id");
        return await ReadAsync(
            sql,
            reader => new AttemptStats(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), Percentile(reader, 3), Percentile(reader, 4)),
            cancellationToken);
    }

    // Exact figures from the raw rows, either for the whole range (step null) or per step-aligned bucket.
    private static async Task<List<OverviewPoint>> ExactFiguresAsync(NpgsqlConnection connection, RequestFilter filter, TimeSpan? step, CancellationToken cancellationToken)
    {
        var sql = new Sql();
        var where = Where(filter, sql);
        where.Add("r.status <> 'running'");
        var bucket = step is { } s ? $"date_bin({sql.Add(s)}, r.started_at, '1970-01-01T00:00:00Z'::timestamptz)" : sql.Add(filter.From!.Value);
        sql.Text.Append(
            $"SELECT {bucket} AS bucket, count(*), count(*) FILTER (WHERE {_failed}), count(*) FILTER (WHERE r.had_fallback), " +
            "coalesce(sum(r.total_input_tokens), 0), coalesce(sum(r.total_output_tokens), 0), coalesce(sum(r.total_estimated_cost), 0), " +
            "percentile_cont(0.5) WITHIN GROUP (ORDER BY r.duration_ms), percentile_cont(0.95) WITHIN GROUP (ORDER BY r.duration_ms), " +
            "percentile_cont(0.5) WITHIN GROUP (ORDER BY r.ttft_ms), percentile_cont(0.95) WITHIN GROUP (ORDER BY r.ttft_ms) " +
            $"FROM usage.inference_request r {WhereClause(where)} GROUP BY 1 ORDER BY 1");
        var points = await ReadAsync(
            connection,
            sql,
            reader => new OverviewPoint(
                reader.GetFieldValue<DateTimeOffset>(0),
                new OverviewFigures(
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetDecimal(6),
                    Percentile(reader, 7),
                    Percentile(reader, 8),
                    Percentile(reader, 9),
                    Percentile(reader, 10),
                    Exact: true)),
            cancellationToken);
        if (step is null && points.Count == 0)
        {
            points.Add(new OverviewPoint(filter.From!.Value, new OverviewFigures(0, 0, 0, 0, 0, 0m, null, null, null, null, Exact: true)));
        }

        return points;
    }

    // Ranges over a day: whole hours from the rollup, the edge slices raw, all folded into step buckets with merged histograms.
    private static async Task<Dictionary<DateTimeOffset, BucketAccumulator>> BucketedFiguresAsync(
        NpgsqlConnection connection,
        RequestFilter filter,
        UsageRangePlan plan,
        TimeSpan step,
        CancellationToken cancellationToken)
    {
        var buckets = new Dictionary<DateTimeOffset, BucketAccumulator>();
        BucketAccumulator At(DateTimeOffset start)
        {
            var key = UsageRange.BucketOf(start, step);
            if (!buckets.TryGetValue(key, out var accumulator))
            {
                buckets[key] = accumulator = new BucketAccumulator();
            }

            return accumulator;
        }

        if (plan.Hourly is { } hours)
        {
            var sql = new Sql();
            var where = HourlyWhere(filter, hours.From, hours.To, sql);
            sql.Text.Append(
                "SELECT h.hour, h.requests, h.failed_requests, h.fallback_requests, h.input_tokens, h.output_tokens, h.estimated_cost, h.latency_histogram, h.ttft_histogram " +
                $"FROM usage.usage_hourly h {WhereClause(where)}");
            await using var command = sql.Command(connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                At(reader.GetFieldValue<DateTimeOffset>(0)).Add(
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetDecimal(6),
                    reader.GetFieldValue<int[]>(7),
                    reader.GetFieldValue<int[]>(8));
            }
        }

        var bounds = string.Join(',', LatencyHistogram.UpperBoundsMs);
        foreach (var (from, to) in plan.Raw)
        {
            var sql = new Sql();
            var where = Where(filter with { From = from, To = to }, sql);
            where.Add("r.status <> 'running'");
            sql.Text.Append(
                $"SELECT r.started_at, {_failed}, r.had_fallback, coalesce(r.total_input_tokens, 0), coalesce(r.total_output_tokens, 0), coalesce(r.total_estimated_cost, 0), " +
                $"CASE WHEN r.duration_ms IS NULL THEN NULL ELSE width_bucket(r.duration_ms - 1, '{{{bounds}}}'::int[]) END, " +
                $"CASE WHEN r.ttft_ms IS NULL THEN NULL ELSE width_bucket(r.ttft_ms - 1, '{{{bounds}}}'::int[]) END " +
                $"FROM usage.inference_request r {WhereClause(where)}");
            await using var command = sql.Command(connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var latency = LatencyHistogram.Empty();
                var ttft = LatencyHistogram.Empty();
                if (!reader.IsDBNull(6))
                {
                    latency[reader.GetInt32(6)]++;
                }

                if (!reader.IsDBNull(7))
                {
                    ttft[reader.GetInt32(7)]++;
                }

                At(reader.GetFieldValue<DateTimeOffset>(0)).Add(
                    1,
                    reader.GetBoolean(1) ? 1 : 0,
                    reader.GetBoolean(2) ? 1 : 0,
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetDecimal(5),
                    latency,
                    ttft);
            }
        }

        return buckets;
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<AttemptView>>> ReadAttemptsAsync(NpgsqlConnection connection, IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken)
    {
        var byRequest = new Dictionary<Guid, List<AttemptView>>();
        await using var command = new NpgsqlCommand(
            "SELECT inference_request_id, id, attempt_number, provider_id, provider_model_id, kind, status, error_class, error_code, http_status, provider_error_type, " +
            "started_at, completed_at, duration_ms, ttft_ms, input_tokens, output_tokens, estimated_cost FROM usage.provider_attempt " +
            "WHERE inference_request_id = ANY(@ids) ORDER BY inference_request_id, attempt_number",
            connection);
        command.Parameters.AddWithValue("ids", requestIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var requestId = reader.GetGuid(0);
            if (!byRequest.TryGetValue(requestId, out var list))
            {
                byRequest[requestId] = list = [];
            }

            list.Add(new AttemptView(
                reader.GetGuid(1),
                reader.GetInt16(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetString(5),
                reader.GetString(6),
                NullableString(reader, 7),
                NullableString(reader, 8),
                reader.IsDBNull(9) ? null : reader.GetInt16(9),
                NullableString(reader, 10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.GetFieldValue<DateTimeOffset>(12),
                reader.GetInt32(13),
                Nullable<int>(reader, 14),
                Nullable<int>(reader, 15),
                Nullable<int>(reader, 16),
                Nullable<decimal>(reader, 17)));
        }

        return byRequest.ToDictionary(p => p.Key, p => (IReadOnlyList<AttemptView>)p.Value);
    }

    private static async Task AccumulateAsync(NpgsqlConnection connection, Sql sql, Dictionary<(DateTimeOffset, Guid?, Guid?, string?), UsageRow> totals, CancellationToken cancellationToken)
    {
        await using var command = sql.Command(connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new UsageRow(
                reader.GetFieldValue<DateTimeOffset>(0),
                Nullable<Guid>(reader, 1),
                Nullable<Guid>(reader, 2),
                NullableString(reader, 3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetDecimal(9));
            var key = (row.Period, row.TenantId, row.ProviderModelId, row.Alias);
            totals[key] = totals.TryGetValue(key, out var existing)
                ? existing with
                {
                    Requests = existing.Requests + row.Requests,
                    FailedRequests = existing.FailedRequests + row.FailedRequests,
                    InputTokens = existing.InputTokens + row.InputTokens,
                    CachedInputTokens = existing.CachedInputTokens + row.CachedInputTokens,
                    OutputTokens = existing.OutputTokens + row.OutputTokens,
                    EstimatedCost = existing.EstimatedCost + row.EstimatedCost,
                }
                : row;
        }
    }

    private static string Group(UsageGrouping groups, UsageGrouping group, string expression, string type = "uuid")
        => groups.HasFlag(group) ? expression : $"NULL::{type}";

    private static List<string> Where(RequestFilter filter, Sql sql)
    {
        var where = new List<string>();
        if (filter.TenantIds is not null)
        {
            where.Add($"r.tenant_id = ANY({sql.Add(filter.TenantIds.ToArray())})");
        }

        if (filter.From is { } from)
        {
            where.Add($"r.started_at >= {sql.Add(from)}");
        }

        if (filter.To is { } to)
        {
            where.Add($"r.started_at < {sql.Add(to)}");
        }

        if (filter.TenantId is { } tenant)
        {
            where.Add($"r.tenant_id = {sql.Add(tenant)}");
        }

        if (filter.Origin is { } origin)
        {
            where.Add($"r.origin = {sql.Add(RequestOrigins.ToWire(origin))}");
        }

        if (filter.Alias is { } alias)
        {
            where.Add($"r.requested_alias = {sql.Add(alias)}");
        }

        if (filter.Status is { } status)
        {
            // "failed" in a filter means what the dashboard calls a failure: failed or abandoned.
            where.Add(status == "failed" ? _failed : $"r.status = {sql.Add(status)}");
        }

        if (filter.ErrorCode is { } errorCode)
        {
            where.Add($"r.error_code = {sql.Add(errorCode)}");
        }

        if (filter.HadFallback is { } hadFallback)
        {
            where.Add($"r.had_fallback = {sql.Add(hadFallback)}");
        }

        if (filter.ProviderId is { } provider)
        {
            where.Add($"EXISTS (SELECT 1 FROM usage.provider_attempt a WHERE a.inference_request_id = r.id AND a.provider_id = {sql.Add(provider)})");
        }

        if (filter.ProviderModelId is { } model)
        {
            where.Add($"EXISTS (SELECT 1 FROM usage.provider_attempt a WHERE a.inference_request_id = r.id AND a.provider_model_id = {sql.Add(model)})");
        }

        return where;
    }

    private static List<string> HourlyWhere(RequestFilter filter, DateTimeOffset from, DateTimeOffset to, Sql sql)
    {
        var where = new List<string> { $"h.hour >= {sql.Add(from)}", $"h.hour < {sql.Add(to)}" };
        if (filter.TenantIds is not null)
        {
            where.Add($"h.tenant_id = ANY({sql.Add(filter.TenantIds.ToArray())})");
        }

        if (filter.TenantId is { } tenant)
        {
            where.Add($"h.tenant_id = {sql.Add(tenant)}");
        }

        if (filter.Alias is { } alias)
        {
            where.Add($"h.requested_alias = {sql.Add(alias)}");
        }

        if (filter.ProviderModelId is { } model)
        {
            where.Add($"h.provider_model_id = {sql.Add(model)}");
        }

        return where;
    }

    private static string WhereClause(List<string> where) => where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);

    private static (object Key, Guid Id)? ParseCursor(string after, bool byDuration)
    {
        var parts = after.Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id) || !long.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key))
        {
            return null;
        }

        return byDuration ? ((int)Math.Clamp(key, int.MinValue, int.MaxValue), id) : (new DateTimeOffset(key, TimeSpan.Zero), id);
    }

    private static RequestListItem ReadRequest(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.GetString(3),
        NullableString(reader, 4),
        reader.GetString(5),
        NullableString(reader, 6),
        reader.GetBoolean(7),
        reader.GetInt16(8),
        reader.GetBoolean(9),
        Nullable<Guid>(reader, 10),
        reader.GetFieldValue<DateTimeOffset>(11),
        Nullable<DateTimeOffset>(reader, 12),
        Nullable<int>(reader, 13),
        Nullable<int>(reader, 14),
        Nullable<int>(reader, 15),
        Nullable<int>(reader, 16),
        Nullable<decimal>(reader, 17),
        reader.GetString(18));

    private static T? Nullable<T>(NpgsqlDataReader reader, int ordinal)
        where T : struct
        => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? Percentile(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : (int)Math.Round(reader.GetDouble(ordinal));

    private static async Task<NpgsqlConnection> OpenAsync(UsageDbContext db, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<List<T>> ReadAsync<T>(Sql sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await ReadAsync(await OpenAsync(db, cancellationToken), sql, read, cancellationToken);
    }

    private static async Task<List<T>> ReadAsync<T>(NpgsqlConnection connection, Sql sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken)
    {
        await using var command = sql.Command(connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <summary>SQL text with positional parameters: every value goes through <see cref="Add"/>, never into the text.</summary>
    private sealed class Sql
    {
        private readonly List<object> _values = [];

        public StringBuilder Text { get; } = new();

        public string Add(object value)
        {
            _values.Add(value);
            return $"${_values.Count}";
        }

        public NpgsqlCommand Command(NpgsqlConnection connection)
        {
            var command = new NpgsqlCommand(Text.ToString(), connection);
            foreach (var value in _values)
            {
                command.Parameters.Add(new NpgsqlParameter { Value = value });
            }

            return command;
        }
    }

    /// <summary>Sums of a bucket read from the rollup and raw edges, with merged latency histograms.</summary>
    private sealed class BucketAccumulator
    {
        private long _requests;
        private long _failed;
        private long _fallback;
        private long _input;
        private long _output;
        private decimal _cost;
        private int[] _latency = LatencyHistogram.Empty();
        private int[] _ttft = LatencyHistogram.Empty();

        public BucketAccumulator Add(long requests, long failed, long fallback, long input, long output, decimal cost, int[] latency, int[] ttft)
        {
            _requests += requests;
            _failed += failed;
            _fallback += fallback;
            _input += input;
            _output += output;
            _cost += cost;
            _latency = LatencyHistogram.Merge(_latency, latency);
            _ttft = LatencyHistogram.Merge(_ttft, ttft);
            return this;
        }

        public BucketAccumulator Add(BucketAccumulator other)
            => Add(other._requests, other._failed, other._fallback, other._input, other._output, other._cost, other._latency, other._ttft);

        public OverviewFigures ToFigures() => new(
            _requests,
            _failed,
            _fallback,
            _input,
            _output,
            _cost,
            LatencyHistogram.Percentile(_latency, 0.5),
            LatencyHistogram.Percentile(_latency, 0.95),
            LatencyHistogram.Percentile(_ttft, 0.5),
            LatencyHistogram.Percentile(_ttft, 0.95),
            Exact: false);
    }
}
