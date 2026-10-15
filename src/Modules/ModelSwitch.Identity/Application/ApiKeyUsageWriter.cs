using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ModelSwitch.Identity.Application;

/// <summary>Writes <c>api_key.last_used_at</c> off the request path. A failed write is logged and never affects authentication.</summary>
internal sealed partial class ApiKeyUsageWriter(ApiKeyUsageRecorder recorder, IApiKeyStore store, ILogger<ApiKeyUsageWriter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var use in recorder.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await store.MarkUsedAsync(use.ApiKeyId, use.At, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogWriteFailed(ex, use.ApiKeyId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: a missed last_used_at is harmless.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not update last_used_at for API key {ApiKeyId}.")]
    private partial void LogWriteFailed(Exception exception, Guid apiKeyId);
}
