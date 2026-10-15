using System.Threading.Channels;

namespace ModelSwitch.Configuration.Application;

/// <summary>Carries "configuration may have changed" from the LISTEN connection to the refresh loop. Signals coalesce.</summary>
internal sealed class ConfigChangeSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    public void Raise() => _channel.Writer.TryWrite(true);

    /// <summary>Completes when a signal is pending, consuming it.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken) => await _channel.Reader.ReadAsync(cancellationToken);
}
