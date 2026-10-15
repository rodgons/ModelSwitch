using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Configuration.Application;
using Shouldly;

namespace ModelSwitch.UnitTests.Configuration;

public sealed class ConfigurationRefreshServiceTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeVersionReader _versions = new() { Version = 1 };
    private readonly FakeSnapshotBuilder _builder = new();
    private readonly SnapshotStore _store = new();
    private readonly ConfigChangeSignal _signal = new();
    private readonly ConfigurationRefreshService _service;

    public ConfigurationRefreshServiceTests() =>
        _service = new ConfigurationRefreshService(
            new ConfigurationRefresher(_versions, _builder, _store, NullLogger<ConfigurationRefresher>.Instance),
            _signal,
            _time);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _service.StopAsync(CancellationToken.None);

    [Fact]
    public async Task The_first_snapshot_is_loaded_before_start_returns()
    {
        await _service.StartAsync(CancellationToken.None);

        _store.IsLoaded.ShouldBeTrue();
        _store.Current.Version.ShouldBe(1);
    }

    [Fact]
    public async Task A_version_change_is_picked_up_by_the_5_second_poll_when_no_notification_arrives()
    {
        await _service.StartAsync(CancellationToken.None);
        _versions.Version = 2;

        _time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _store.Current.Version.ShouldBe(1);

        await WaitForVersionAsync(2, advanceBy: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_notification_refreshes_immediately_without_waiting_for_the_poll()
    {
        await _service.StartAsync(CancellationToken.None);
        _versions.Version = 2;

        _signal.Raise();

        await WaitForVersionAsync(2);
    }

    [Fact]
    public async Task A_failed_first_load_leaves_the_snapshot_unloaded_and_the_poll_recovers()
    {
        _builder.Failure = new InvalidOperationException("db down");
        await _service.StartAsync(CancellationToken.None);
        _store.IsLoaded.ShouldBeFalse();

        _builder.Failure = null;
        await WaitForVersionAsync(1, advanceBy: ConfigurationRefreshService.PollInterval);
    }

    private async Task WaitForVersionAsync(long version, TimeSpan? advanceBy = null)
    {
        // The loop runs on the thread pool and may not have registered its delay yet, so time is advanced until it fires.
        var deadline = Environment.TickCount64 + 5_000;
        while ((_store.Version ?? 0) < version)
        {
            Environment.TickCount64.ShouldBeLessThan(deadline, $"snapshot never reached version {version}");
            if (advanceBy is { } step)
            {
                _time.Advance(step);
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
