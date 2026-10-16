using System.Diagnostics.Metrics;
using ModelSwitch.SharedKernel.Telemetry;
using ModelSwitch.Usage.Domain;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// Tokens and Estimated Cost as metrics (observability.md section 3), recorded once a settlement has committed. They are for
/// operations only: the Usage Records are authoritative and these may be dropped (usage-and-budgets.md section 1).
/// </summary>
internal sealed class UsageMetrics
{
    private readonly Counter<long> _tokens;
    private readonly Counter<double> _estimatedCost;

    public UsageMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(GatewayTelemetry.Name);
        _tokens = meter.CreateCounter<long>("modelswitch.tokens", "{token}", "Tokens by direction and Usage Source; cached_input is a subset of input.");
        _estimatedCost = meter.CreateCounter<double>("modelswitch.estimated_cost", "USD", "Estimated Cost of settled Attempts. Never a Provider's charge.");
    }

    public void Recorded(string alias, Guid? providerModelId, UsageRecordDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var model = providerModelId?.ToString() ?? string.Empty;
        var source = UsageSourceName(draft.Source);
        Tokens(draft.InputTokens, "input");
        Tokens(draft.CachedInputTokens, "cached_input");
        Tokens(draft.OutputTokens, "output");
        Cost(alias, model, draft.TotalCost.Amount);

        void Tokens(int? count, string direction)
        {
            if (count is > 0)
            {
                _tokens.Add(
                    count.Value,
                    new(GatewayTelemetry.Labels.Alias, alias),
                    new(GatewayTelemetry.Labels.Model, model),
                    new(GatewayTelemetry.Labels.Direction, direction),
                    new(GatewayTelemetry.Labels.Source, source));
            }
        }
    }

    /// <summary>The held amount the sweeper charges an abandoned request: cost with no tokens and no Provider Model.</summary>
    public void Abandoned(string alias, decimal amount) => Cost(alias, string.Empty, amount);

    private void Cost(string alias, string model, decimal amount)
    {
        if (amount > 0)
        {
            _estimatedCost.Add((double)amount, new(GatewayTelemetry.Labels.Alias, alias), new(GatewayTelemetry.Labels.Model, model));
        }
    }

    private static string UsageSourceName(Contracts.UsageSource source) => source switch
    {
        Contracts.UsageSource.Reported => "reported",
        Contracts.UsageSource.Estimated => "estimated",
        _ => "unavailable",
    };
}
