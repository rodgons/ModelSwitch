using Microsoft.Extensions.Logging.Abstractions;
using ModelSwitch.Configuration.Application;
using Shouldly;

namespace ModelSwitch.UnitTests.Configuration;

public sealed class ConfigurationRefresherTests
{
    private readonly FakeVersionReader _versions = new();
    private readonly FakeSnapshotBuilder _builder = new();
    private readonly SnapshotStore _store = new();
    private readonly ConfigurationRefresher _refresher;

    public ConfigurationRefresherTests() =>
        _refresher = new ConfigurationRefresher(_versions, _builder, _store, NullLogger<ConfigurationRefresher>.Instance);

    [Fact]
    public async Task First_refresh_loads_the_snapshot_at_the_current_version()
    {
        _versions.Version = 7;

        (await _refresher.RefreshAsync(CancellationToken.None)).ShouldBeTrue();

        _store.IsLoaded.ShouldBeTrue();
        _store.Current.Version.ShouldBe(7);
    }

    [Fact]
    public async Task Refresh_at_the_same_version_does_not_rebuild()
    {
        _versions.Version = 3;
        await _refresher.RefreshAsync(CancellationToken.None);

        (await _refresher.RefreshAsync(CancellationToken.None)).ShouldBeFalse();

        _builder.Built.ShouldBe([3]);
    }

    [Fact]
    public async Task A_bumped_version_swaps_in_a_new_snapshot_and_in_flight_holders_keep_the_old_one()
    {
        _versions.Version = 1;
        await _refresher.RefreshAsync(CancellationToken.None);
        var inFlight = _store.Current;

        _versions.Version = 2;
        (await _refresher.RefreshAsync(CancellationToken.None)).ShouldBeTrue();

        inFlight.Version.ShouldBe(1);
        _store.Current.Version.ShouldBe(2);
        _store.Current.ShouldNotBeSameAs(inFlight);
    }

    [Fact]
    public async Task A_write_during_the_build_repeats_the_build_so_the_snapshot_never_mixes_versions()
    {
        _versions.Script(5, 6, 6); // before the first build, after it (moved), after the second build (stable)
        _versions.Version = 6;

        await _refresher.RefreshAsync(CancellationToken.None);

        _builder.Built.ShouldBe([5, 6]);
        _store.Current.Version.ShouldBe(6);
    }

    [Fact]
    public async Task A_version_that_never_settles_swaps_the_last_build_stamped_with_the_version_read_before_it()
    {
        _versions.Script(1, 2, 3, 4, 5, 6);

        await _refresher.RefreshAsync(CancellationToken.None);

        _builder.Built.Count.ShouldBe(3);
        _store.Current.Version.ShouldBe(_builder.Built[^1]);
    }

    [Fact]
    public async Task A_failed_build_keeps_the_previous_snapshot_and_does_not_throw()
    {
        _versions.Version = 1;
        await _refresher.RefreshAsync(CancellationToken.None);

        _versions.Version = 2;
        _builder.Failure = new InvalidOperationException("db down");
        (await _refresher.RefreshAsync(CancellationToken.None)).ShouldBeFalse();

        _store.Current.Version.ShouldBe(1);

        _builder.Failure = null;
        (await _refresher.RefreshAsync(CancellationToken.None)).ShouldBeTrue();
        _store.Current.Version.ShouldBe(2);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _builder.Failure = new OperationCanceledException(cancelled.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => _refresher.RefreshAsync(cancelled.Token));
    }
}
