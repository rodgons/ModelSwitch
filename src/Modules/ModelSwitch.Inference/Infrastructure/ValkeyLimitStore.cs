using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Contracts;
using StackExchange.Redis;

namespace ModelSwitch.Inference.Infrastructure;

/// <summary>
/// The Valkey side of the limits (routing-and-resilience.md section 7, ADR-0003). One Lua script does the Rate Limit check (GCRA)
/// and the Concurrency lease acquire as a single atomic step, so two api instances can never both take the last slot, and a
/// refusal by one limit never consumes the other. Time comes from the Valkey server (<c>TIME</c>), not from the callers, so
/// instances with skewed clocks agree.
/// <list type="bullet">
/// <item><c>rl:{tenant}</c>: the GCRA theoretical arrival time in ms. Emission interval is <c>60000 / rpm</c>, burst is
/// <c>max(1, rpm / 10)</c>.</item>
/// <item><c>cc:{tenant}</c>: sorted set of lease ids scored by expiry in ms. Expired leases are removed on every acquire, so the
/// lease of a crashed instance frees itself.</item>
/// </list>
/// </summary>
internal sealed class ValkeyLimitStore : ILimitStore, IAsyncDisposable
{
    /// <summary>
    /// ARGV: 1 requests per minute (0 = none), 2 max concurrent (0 = none), 3 lease id, 4 lease ttl ms.
    /// Returns {status, retry_after_ms, limit, remaining, reset_ms}; status 0 allowed, 1 rate limited, 2 concurrency limited.
    /// </summary>
    internal const string Script = """
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        local rpm = tonumber(ARGV[1])
        local maxc = tonumber(ARGV[2])
        local ttl = tonumber(ARGV[4])
        local status = 0
        local retry = 0
        local remaining = -1
        local reset = 0
        local newTat = nil
        local interval = 0

        if rpm > 0 then
          interval = 60000 / rpm
          local tau = interval * math.max(1, math.floor(rpm / 10))
          local tat = tonumber(redis.call('GET', KEYS[1]) or '0')
          if tat < now then tat = now end
          newTat = tat + interval
          local allowAt = newTat - tau
          if now < allowAt then
            status = 1
            retry = math.ceil(allowAt - now)
            remaining = 0
            reset = math.ceil(tat - now)
          else
            remaining = math.max(0, math.floor((tau - (newTat - now)) / interval))
            reset = math.ceil(newTat - now)
          end
        end

        if status == 0 and maxc > 0 then
          redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
          if redis.call('ZCARD', KEYS[2]) >= maxc then
            status = 2
            retry = 1000
          end
        end

        if status == 0 then
          if newTat ~= nil then
            redis.call('SET', KEYS[1], string.format('%d', math.ceil(newTat)), 'PX', math.ceil(newTat - now) + 1000)
          end
          if maxc > 0 then
            redis.call('ZADD', KEYS[2], now + ttl, ARGV[3])
            redis.call('PEXPIRE', KEYS[2], ttl + 1000)
          end
        end

        return {status, retry, rpm, remaining, reset}
        """;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "EVALSHA addresses a script by its SHA-1 digest; this is the Redis protocol, not a security use.")]
    private static readonly byte[] _scriptSha = SHA1.HashData(Encoding.UTF8.GetBytes(Script));

    private readonly ConfigurationOptions _configuration;
    private readonly Lock _gate = new();
    private Task<ConnectionMultiplexer>? _connection;

    public ValkeyLimitStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = ConfigurationOptions.Parse(configuration["ConnectionStrings:Valkey"] ?? throw new InvalidOperationException("ConnectionStrings:Valkey is not set."));

        // The first call must not block startup or throw when Valkey is down: the multiplexer reconnects in the background, and
        // each call is bounded by the caller's budget.
        _configuration.AbortOnConnectFail = false;
        _configuration.ClientName = "modelswitch-limits";
    }

    public async Task<LimitDecision> TryAcquireAsync(Guid tenantId, Guid leaseId, int? requestsPerMinute, int? maxConcurrent, TimeSpan leaseTtl, CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync();
        RedisKey[] keys = [RateKey(tenantId), LeaseKey(tenantId)];
        RedisValue[] arguments = [requestsPerMinute ?? 0, maxConcurrent ?? 0, leaseId.ToString("D"), (long)leaseTtl.TotalMilliseconds];

        RedisResult result;
        try
        {
            result = await database.ScriptEvaluateAsync(_scriptSha, keys, arguments);
        }
        catch (RedisServerException exception) when (exception.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            result = await database.ScriptEvaluateAsync(Script, keys, arguments);
        }

        var values = (long[])result!;
        var rate = requestsPerMinute is { } limit
            ? new RateLimitState(limit, (int)values[3], TimeSpan.FromMilliseconds(values[4]))
            : null;
        var outcome = (LimitOutcome)values[0];
        return new LimitDecision(outcome, outcome == LimitOutcome.Allowed ? TimeSpan.Zero : TimeSpan.FromMilliseconds(values[1]), rate);
    }

    public async Task ReleaseAsync(Guid tenantId, Guid leaseId, CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync();
        await database.SortedSetRemoveAsync(LeaseKey(tenantId), leaseId.ToString("D"));
    }

    /// <summary>True when a round trip to Valkey works. Used by <c>GET /ready</c> when the failure mode is Closed.</summary>
    public async Task<bool> PingAsync()
    {
        try
        {
            var database = await DatabaseAsync();
            await database.PingAsync();
            return true;
        }
#pragma warning disable CA1031 // Any fault means "not reachable".
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<ConnectionMultiplexer>? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is { IsCompletedSuccessfully: true })
        {
            await connection.Result.DisposeAsync();
        }
    }

    // The {tenant} hash tag keeps both keys in one slot should Valkey ever run clustered.
    private static RedisKey RateKey(Guid tenantId) => $"rl:{{{tenantId:D}}}";

    private static RedisKey LeaseKey(Guid tenantId) => $"cc:{{{tenantId:D}}}";

    private async Task<IDatabase> DatabaseAsync()
    {
        Task<ConnectionMultiplexer> connecting;
        lock (_gate)
        {
            if (_connection is null || _connection.IsFaulted)
            {
                _connection = ConnectionMultiplexer.ConnectAsync(_configuration);
            }

            connecting = _connection;
        }

        return (await connecting).GetDatabase();
    }
}
