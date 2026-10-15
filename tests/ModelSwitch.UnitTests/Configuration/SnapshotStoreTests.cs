using ModelSwitch.Configuration.Application;
using ModelSwitch.Identity.Contracts;
using Shouldly;

namespace ModelSwitch.UnitTests.Configuration;

public sealed class SnapshotStoreTests
{
    [Fact]
    public void Current_throws_and_IsLoaded_is_false_before_the_first_load()
    {
        var store = new SnapshotStore();

        store.IsLoaded.ShouldBeFalse();
        store.Version.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => store.Current);
    }

    [Fact]
    public void A_newer_snapshot_replaces_the_current_one()
    {
        var store = new SnapshotStore();
        store.TrySwap(FakeSnapshotBuilder.Snapshot(1)).ShouldBeTrue();

        store.TrySwap(FakeSnapshotBuilder.Snapshot(2)).ShouldBeTrue();

        store.Current.Version.ShouldBe(2);
    }

    [Fact]
    public void An_equal_or_older_snapshot_never_moves_the_version_backwards()
    {
        var store = new SnapshotStore();
        store.TrySwap(FakeSnapshotBuilder.Snapshot(5));

        store.TrySwap(FakeSnapshotBuilder.Snapshot(5)).ShouldBeFalse();
        store.TrySwap(FakeSnapshotBuilder.Snapshot(4)).ShouldBeFalse();

        store.Current.Version.ShouldBe(5);
    }

    [Fact]
    public async Task Concurrent_swaps_end_on_the_highest_version()
    {
        var store = new SnapshotStore();
        var swaps = Enumerable.Range(1, 200).Select(v => Task.Run(() => store.TrySwap(FakeSnapshotBuilder.Snapshot(v))));

        await Task.WhenAll(swaps);

        store.Current.Version.ShouldBe(200);
    }

    [Fact]
    public void A_Tenant_without_limits_may_use_every_alias_with_no_cost_limit()
    {
        var snapshot = FakeSnapshotBuilder.Snapshot(1);

        var access = snapshot.AccessFor(Guid.CreateVersion7());

        access.Allows("anything").ShouldBeTrue();
        access.MaxRequestCost.ShouldBeNull();
    }

    [Fact]
    public void A_Tenant_with_limits_gets_its_allowed_aliases_and_cost_limit()
    {
        var tenant = Guid.CreateVersion7();
        var snapshot = FakeSnapshotBuilder.Snapshot(1) with
        {
            TenantLimits = new Dictionary<Guid, TenantLimits> { [tenant] = new(tenant, 60, 4, 0.5m, ["balanced"]) },
        };

        var access = snapshot.AccessFor(tenant);

        access.Allows("balanced").ShouldBeTrue();
        access.Allows("local").ShouldBeFalse();
        access.MaxRequestCost.ShouldBe(0.5m);
    }
}
