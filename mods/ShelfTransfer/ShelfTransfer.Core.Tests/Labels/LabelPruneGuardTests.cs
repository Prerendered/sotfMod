// purpose: card 20 criterion 7 guards: a label is pruned only when absent on two consecutive scans of one world,
//          never in the fallback world, never on an empty registry listing
// owns:    LabelPruneGuardTests
// deps:    LabelPruneGuard, LabelStore, FakeLabelFileSystem

using System;
using System.Linq;

using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Labels;

public class LabelPruneGuardTests
{
    private static LabelStore NewStore(string? world = "w1")
    {
        var store = new LabelStore(new FakeLabelFileSystem(), world);
        store.Set("live", "Logs", null);
        store.Set("gone", "Stones", "Base");
        return store;
    }

    [Fact]
    public void Prune_FirstScanMissingKey_KeepsItAsPending()
    {
        LabelStore store = NewStore();

        LabelPruneResult result = new LabelPruneGuard().Prune(store, new[] { "live" }, "src");

        Assert.Equal(LabelPruneOutcome.Checked, result.Outcome);
        Assert.Empty(result.Pruned);
        Assert.Equal(1, result.Pending);
        Assert.NotNull(store.Get("gone"));
    }

    [Fact]
    public void Prune_SecondConsecutiveScanMissingKey_RemovesItAndReturnsItsLabel()
    {
        LabelStore store = NewStore();
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "live" }, "src");

        LabelPruneResult result = guard.Prune(store, new[] { "live" }, "src");

        PrunedLabel pruned = Assert.Single(result.Pruned);
        Assert.Equal("gone", pruned.Key);
        Assert.Equal(new ShelfLabel("Stones", "Base"), pruned.Label);
        Assert.Equal(0, result.Pending);
        Assert.Null(store.Get("gone"));
        Assert.NotNull(store.Get("live"));
    }

    [Fact]
    public void Prune_KeySeenBetweenTwoMissingScans_IsNotPruned()
    {
        LabelStore store = NewStore();
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "live" }, "src");
        guard.Prune(store, new[] { "live", "gone" }, "src");

        LabelPruneResult result = guard.Prune(store, new[] { "live" }, "src");

        Assert.Empty(result.Pruned);
        Assert.NotNull(store.Get("gone"));
    }

    [Fact]
    public void Prune_SourceKeyOnly_IsLive()
    {
        LabelStore store = NewStore();
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "live" }, "gone");

        LabelPruneResult result = guard.Prune(store, new[] { "live" }, "gone");

        Assert.Empty(result.Pruned);
        Assert.NotNull(store.Get("gone"));
    }

    [Fact]
    public void Prune_FallbackWorld_NeverPrunes()
    {
        LabelStore store = NewStore(world: null);
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "live" }, "src");

        LabelPruneResult result = guard.Prune(store, new[] { "live" }, "src");

        Assert.Equal(LabelPruneOutcome.SkippedUnknownWorld, result.Outcome);
        Assert.Empty(result.Pruned);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Prune_EmptyListing_SkipsAndBreaksTheRun()
    {
        LabelStore store = NewStore();
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "live" }, "src");

        LabelPruneResult skipped = guard.Prune(store, Array.Empty<string>(), "src");
        LabelPruneResult after = guard.Prune(store, new[] { "live" }, "src");

        Assert.Equal(LabelPruneOutcome.SkippedNoShelves, skipped.Outcome);
        Assert.Empty(after.Pruned);
        Assert.NotNull(store.Get("gone"));
    }

    [Fact]
    public void Prune_NewStoreInstance_StartsOver()
    {
        var guard = new LabelPruneGuard();
        guard.Prune(NewStore(), new[] { "live" }, "src");
        LabelStore reentered = NewStore();

        LabelPruneResult result = guard.Prune(reentered, new[] { "live" }, "src");

        Assert.Empty(result.Pruned);
        Assert.Equal(new[] { "gone", "live" }, reentered.Entries.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }
}
