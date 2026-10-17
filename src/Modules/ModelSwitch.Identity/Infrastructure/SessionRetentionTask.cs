using ModelSwitch.SharedKernel.Retention;

namespace ModelSwitch.Identity.Infrastructure;

/// <summary>
/// Retention of Administrator sessions (data-model.md, "Retention"): a session past its sliding or absolute expiry can never be used
/// again, so the daily run deletes it. Sign-in also clears that Administrator's expired sessions, but one who never signs in again
/// would otherwise leave them forever.
/// </summary>
internal sealed class SessionRetentionTask : IRetentionTask
{
    public string Name => "sessions";

    public async Task<long> RunAsync(RetentionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await RetentionSql.DeleteInBatchesAsync(
            context.Source.DataSource,
            "identity.administrator_session",
            "id",
            "expires_at < @cutoff OR absolute_expires_at < @cutoff",
            context.Cutoffs.Sessions,
            context.Options.BatchSize,
            cancellationToken);
    }
}
