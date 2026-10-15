using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ModelSwitch.Identity.Application;

/// <summary>One API Key seen in use at <see cref="At"/>.</summary>
internal readonly record struct ApiKeyUse(Guid ApiKeyId, DateTimeOffset At);

/// <summary>
/// Notes that an API Key was used, for <c>api_key.last_used_at</c> (data-model.md). Throttled per key to one note a
/// minute and handed to <see cref="ApiKeyUsageWriter"/> through a bounded channel, so authentication never waits on
/// the database. When the channel is full the note is dropped; the next request a minute later tries again.
/// </summary>
internal sealed class ApiKeyUsageRecorder(TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private const int _capacity = 1024;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastNoted = new();
    private readonly Channel<ApiKeyUse> _channel = Channel.CreateBounded<ApiKeyUse>(
        new BoundedChannelOptions(_capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public ChannelReader<ApiKeyUse> Reader => _channel.Reader;

    public void Record(Guid apiKeyId)
    {
        var now = time.GetUtcNow();
        while (true)
        {
            if (!_lastNoted.TryGetValue(apiKeyId, out var last))
            {
                if (_lastNoted.TryAdd(apiKeyId, now))
                {
                    break;
                }
            }
            else if (now - last < Interval)
            {
                return;
            }
            else if (_lastNoted.TryUpdate(apiKeyId, now, last))
            {
                break;
            }
        }

        if (!_channel.Writer.TryWrite(new ApiKeyUse(apiKeyId, now)))
        {
            _lastNoted.TryRemove(new KeyValuePair<Guid, DateTimeOffset>(apiKeyId, now));
        }
    }
}
