using ModelSwitch.Inference.Application;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Inference.Infrastructure;

/// <summary>
/// Valkey reachability for <c>GET /ready</c>. Applies only when <c>Limits:FailureMode</c> is <c>Closed</c> (api.md section 4): with
/// <c>Open</c> the gateway serves without Valkey, so it must stay in rotation. Fails fast within the limits budget.
/// </summary>
internal sealed class ValkeyReadinessCheck(ValkeyLimitStore store, LimitsOptions options, TimeProvider time) : IReadinessCheck
{
    private static readonly TimeSpan _minimum = TimeSpan.FromSeconds(2);

    public string Name => "valkey";

    public bool Applies => options.FailureMode == LimitsFailureMode.Closed;

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await store.PingAsync().WaitAsync(options.ValkeyTimeout > _minimum ? options.ValkeyTimeout : _minimum, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
