// purpose: card 20 criterion 7 attack tests: interleavings, scale, store identity, edits between opens, persistence
//          and the accepted partial-load limitation of the two-consecutive-opens prune guard
// owns:    LabelPruneGuardAttackTests
// deps:    LabelPruneGuard, LabelStore, FakeLabelFileSystem

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Labels;

public class LabelPruneGuardAttackTests
{
    private static LabelStore NewStore(params string[] keys) => NewStore(new FakeLabelFileSystem(), "w1", keys);

    private static LabelStore NewStore(FakeLabelFileSystem fs, string? world, params string[] keys)
    {
        var store = new LabelStore(fs, world);
        foreach (string key in keys)
        {
            store.Set(key, "L-" + key, null);
        }

        return store;
    }

    private static string[] Keys(LabelStore store) => store.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Prune_MissSeenMissMiss_PrunesOnlyOnTheSecondConsecutiveMiss()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();
        string[] without = { "a" };
        string[] with = { "a", "gone" };

        Assert.Empty(guard.Prune(store, without, "s").Pruned);
        Assert.Empty(guard.Prune(store, with, "s").Pruned);
        Assert.Empty(guard.Prune(store, without, "s").Pruned);
        LabelPruneResult fourth = guard.Prune(store, without, "s");

        Assert.Equal("gone", Assert.Single(fourth.Pruned).Key);
        Assert.DoesNotContain("gone", Keys(store));
    }

    [Fact]
    public void Prune_AlternatingMissAndSeen_NeverPrunes()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();

        for (int i = 0; i < 20; i++)
        {
            string[] listing = i % 2 == 0 ? new[] { "a" } : new[] { "a", "gone" };
            Assert.Empty(guard.Prune(store, listing, "s").Pruned);
        }

        Assert.Equal(new[] { "a", "gone" }, Keys(store));
    }

    [Fact]
    public void Prune_ThreeScansMissing_PrunesOnceThenNothingMore()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");
        Assert.Single(guard.Prune(store, new[] { "a" }, "s").Pruned);

        LabelPruneResult third = guard.Prune(store, new[] { "a" }, "s");

        Assert.Empty(third.Pruned);
        Assert.Equal(0, third.Pending);
        Assert.Equal(LabelPruneOutcome.Checked, third.Outcome);
    }

    [Fact]
    public void Prune_ManyKeys_PrunesOnlyTheConfirmedMissingOnesInOrdinalOrder()
    {
        string[] all = Enumerable.Range(0, 300).Select(i => "k" + i.ToString("D3")).ToArray();
        LabelStore store = NewStore(all);
        var guard = new LabelPruneGuard();
        string[] firstListing = all.Where((_, i) => i % 3 != 0).ToArray();     // every third missing
        string[] secondListing = all.Where((_, i) => i % 6 != 0 && i % 3 != 1).ToArray();    // sixths missing again; thirds-plus-one newly missing

        LabelPruneResult first = guard.Prune(store, firstListing, "s");
        LabelPruneResult second = guard.Prune(store, secondListing, "s");

        Assert.Equal(100, first.Pending);
        string[] expected = all.Where((_, i) => i % 3 == 0 && i % 6 == 0).ToArray();
        Assert.Equal(expected, second.Pruned.Select(p => p.Key).ToArray());
        Assert.Equal(expected.OrderBy(k => k, StringComparer.Ordinal), second.Pruned.Select(p => p.Key));
        Assert.Equal(100, second.Pending);
        Assert.Equal(300 - 50, store.Count);
    }

    [Fact]
    public void Prune_SeveralKeysPrunedTogether_ReturnedOrdinallyWithTheirFullLabels()
    {
        var store = new LabelStore(new FakeLabelFileSystem(), "w1");
        store.Set("zeta", "Z", "G1");
        store.Set("alpha", "A", null);
        store.Set("mid", "M", "G2");
        store.Set("live", "Live", null);
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "live" }, "s");

        LabelPruneResult result = guard.Prune(store, new[] { "live" }, "s");

        Assert.Equal(new[] { "alpha", "mid", "zeta" }, result.Pruned.Select(p => p.Key).ToArray());
        Assert.Equal(new ShelfLabel("A", null), result.Pruned[0].Label);
        Assert.Equal(new ShelfLabel("M", "G2"), result.Pruned[1].Label);
        Assert.Equal(new ShelfLabel("Z", "G1"), result.Pruned[2].Label);
        Assert.Equal(new[] { "live" }, Keys(store));
    }

    [Fact]
    public void Prune_GroupOnlyLabel_ReturnsEmptyLabelWithGroup()
    {
        var store = new LabelStore(new FakeLabelFileSystem(), "w1");
        store.Set("gone", null, "Base");
        store.Set("live", "x", null);
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "live" }, "s");

        LabelPruneResult result = guard.Prune(store, new[] { "live" }, "s");

        PrunedLabel pruned = Assert.Single(result.Pruned);
        Assert.Equal(new ShelfLabel(string.Empty, "Base"), pruned.Label);
    }

    [Fact]
    public void Prune_SourceShelfWithLabel_NeverPendingOrPruned_EvenWhenAbsentFromListing()
    {
        LabelStore store = NewStore("src", "live");
        var guard = new LabelPruneGuard();

        LabelPruneResult first = guard.Prune(store, new[] { "live" }, "src");
        LabelPruneResult second = guard.Prune(store, new[] { "live" }, "src");
        LabelPruneResult third = guard.Prune(store, new[] { "live" }, "src");

        Assert.All(new[] { first, second, third }, r => Assert.Empty(r.Pruned));
        Assert.All(new[] { first, second, third }, r => Assert.Equal(0, r.Pending));
        Assert.NotNull(store.Get("src"));
    }

    [Fact]
    public void Prune_SourceChangesBetweenOpens_PreviousSourceStartsItsRunFromTheNextScan()
    {
        LabelStore store = NewStore("a", "b", "live");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "live" }, "a");                       // a protected, b pending
        LabelPruneResult second = guard.Prune(store, new[] { "live" }, "b");   // b protected, a pending: nobody pruned

        Assert.Empty(second.Pruned);
        Assert.Equal(1, second.Pending);
        LabelPruneResult third = guard.Prune(store, new[] { "live" }, "b");

        Assert.Equal("a", Assert.Single(third.Pruned).Key);
        Assert.NotNull(store.Get("b"));
    }

    [Fact]
    public void Prune_SourceKeyIsInTheListingToo_NoSpecialProblem()
    {
        LabelStore store = NewStore("src", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "src" }, "src");

        LabelPruneResult result = guard.Prune(store, new[] { "src" }, "src");

        Assert.Equal("gone", Assert.Single(result.Pruned).Key);
        Assert.NotNull(store.Get("src"));
    }

    [Fact]
    public void Prune_DuplicateKeysInListing_AreHarmless()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();
        string[] listing = { "a", "a", "a" };
        guard.Prune(store, listing, "s");

        LabelPruneResult result = guard.Prune(store, listing, "s");

        Assert.Equal("gone", Assert.Single(result.Pruned).Key);
    }

    [Fact]
    public void Prune_KeysCompareOrdinally_CaseDiffersMeansMissing()
    {
        LabelStore store = NewStore("Shelf");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "shelf" }, "s");

        LabelPruneResult result = guard.Prune(store, new[] { "shelf" }, "s");

        Assert.Equal("Shelf", Assert.Single(result.Pruned).Key);
    }

    [Fact]
    public void Prune_EmptyListingBetweenTwoMisses_BreaksTheRunAndLeavesLabelsAlone()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");
        LabelPruneResult empty = guard.Prune(store, Array.Empty<string>(), "s");

        LabelPruneResult next = guard.Prune(store, new[] { "a" }, "s");
        LabelPruneResult after = guard.Prune(store, new[] { "a" }, "s");

        Assert.Equal(LabelPruneOutcome.SkippedNoShelves, empty.Outcome);
        Assert.Equal(0, empty.Pending);
        Assert.Empty(empty.Pruned);
        Assert.Empty(next.Pruned);
        Assert.Equal(1, next.Pending);
        Assert.Equal("gone", Assert.Single(after.Pruned).Key);
    }

    [Fact]
    public void Prune_EmptyListingWithASourceKey_StillSkips_NothingIsProvenLive()
    {
        LabelStore store = NewStore("src", "gone");
        var guard = new LabelPruneGuard();

        LabelPruneResult result = guard.Prune(store, Array.Empty<string>(), "src");

        Assert.Equal(LabelPruneOutcome.SkippedNoShelves, result.Outcome);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Prune_EmptyStoreOnARealWorld_ChecksAndFindsNothing()
    {
        var store = new LabelStore(new FakeLabelFileSystem(), "w1");

        LabelPruneResult result = new LabelPruneGuard().Prune(store, new[] { "a" }, "s");

        Assert.Equal(LabelPruneOutcome.Checked, result.Outcome);
        Assert.Empty(result.Pruned);
        Assert.Equal(0, result.Pending);
    }

    [Fact]
    public void Prune_ListingEqualToAllEntries_PendingZeroAndNothingPruned()
    {
        LabelStore store = NewStore("a", "b");

        LabelPruneResult result = new LabelPruneGuard().Prune(store, new[] { "a", "b" }, "s");

        Assert.Equal(0, result.Pending);
        Assert.Empty(result.Pruned);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    public void Prune_AnyWorldThatSanitisesToTheFallback_NeverPrunesAcrossManyScans(string? world)
    {
        LabelStore store = NewStore(new FakeLabelFileSystem(), world, "a", "gone");
        var guard = new LabelPruneGuard();

        for (int i = 0; i < 5; i++)
        {
            LabelPruneResult result = guard.Prune(store, new[] { "a" }, "s");
            Assert.Equal(LabelPruneOutcome.SkippedUnknownWorld, result.Outcome);
            Assert.Empty(result.Pruned);
            Assert.Equal(0, result.Pending);
        }

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Prune_FallbackWorldChecksBeforeEmptyListing_ReportsUnknownWorld()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem(), null, "a");

        LabelPruneResult result = new LabelPruneGuard().Prune(store, Array.Empty<string>(), "s");

        Assert.Equal(LabelPruneOutcome.SkippedUnknownWorld, result.Outcome);
    }

    [Fact]
    public void Prune_RealWorldWhoseIdContainsUnknown_IsStillPruned()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem(), "unknown-2", "a", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");

        LabelPruneResult result = guard.Prune(store, new[] { "a" }, "s");

        Assert.Equal("gone", Assert.Single(result.Pruned).Key);
    }

    [Fact]
    public void Prune_FallbackStoreBetweenTwoRealOpens_ResetsTheRun()
    {
        var guard = new LabelPruneGuard();
        LabelStore world = NewStore("a", "gone");
        guard.Prune(world, new[] { "a" }, "s");
        guard.Prune(NewStore(new FakeLabelFileSystem(), null, "a"), new[] { "a" }, "s");

        LabelPruneResult back = guard.Prune(world, new[] { "a" }, "s");

        Assert.Empty(back.Pruned);
        Assert.Equal(1, back.Pending);
    }

    [Fact]
    public void Prune_NewStoreForTheSameWorld_StartsOver_EvenWithIdenticalContent()
    {
        var guard = new LabelPruneGuard();
        var fs = new FakeLabelFileSystem();
        LabelStore first = NewStore(fs, "w1", "a", "gone");
        guard.Prune(first, new[] { "a" }, "s");
        LabelStore second = NewStore(fs, "w1", "a", "gone");

        LabelPruneResult result = guard.Prune(second, new[] { "a" }, "s");

        Assert.Empty(result.Pruned);
        Assert.Equal(1, result.Pending);
        Assert.NotNull(second.Get("gone"));
    }

    [Fact]
    public void Prune_ReturningToAnEarlierStoreInstance_DoesNotRememberItsOldRun()
    {
        var guard = new LabelPruneGuard();
        LabelStore a = NewStore("x", "gone");
        LabelStore b = NewStore("x", "gone");
        guard.Prune(a, new[] { "x" }, "s");
        guard.Prune(b, new[] { "x" }, "s");

        LabelPruneResult back = guard.Prune(a, new[] { "x" }, "s");

        Assert.Empty(back.Pruned);
        Assert.Equal(1, back.Pending);
    }

    [Fact]
    public void Prune_TwoGuards_KeepIndependentRuns()
    {
        LabelStore store = NewStore("a", "gone");
        var one = new LabelPruneGuard();
        var two = new LabelPruneGuard();
        one.Prune(store, new[] { "a" }, "s");

        LabelPruneResult fromTwo = two.Prune(store, new[] { "a" }, "s");

        Assert.Empty(fromTwo.Pruned);
        Assert.Single(one.Prune(store, new[] { "a" }, "s").Pruned);
    }

    [Fact]
    public void Prune_StoreReloadedInPlaceBetweenOpens_KeepsTheRun_PinnedBehaviour()
    {
        // The run follows the LabelStore instance, not its content: Load() on the same instance does not reset it.
        var fs = new FakeLabelFileSystem();
        LabelStore store = NewStore(fs, "w1", "a", "gone");
        store.Load();
        store.Set("a", "L-a", null);
        store.Set("gone", "L-gone", null);
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");
        store.Load();
        store.Set("a", "L-a", null);
        store.Set("gone", "L-gone", null);

        LabelPruneResult result = guard.Prune(store, new[] { "a" }, "s");

        Assert.Equal("gone", Assert.Single(result.Pruned).Key);
    }

    [Fact]
    public void Prune_LabelEditedBetweenOpens_PrunedResultCarriesTheLatestLabel()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");
        store.Set("gone", "Renamed", "NewGroup");

        LabelPruneResult result = guard.Prune(store, new[] { "a" }, "s");

        PrunedLabel pruned = Assert.Single(result.Pruned);
        Assert.Equal(new ShelfLabel("Renamed", "NewGroup"), pruned.Label);
    }

    [Fact]
    public void Prune_PendingLabelClearedByThePlayer_DropsOutOfPendingAndIsNotReported()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");
        store.Set("gone", null, null);

        LabelPruneResult result = guard.Prune(store, new[] { "a" }, "s");

        Assert.Empty(result.Pruned);
        Assert.Equal(0, result.Pending);
    }

    [Fact]
    public void Prune_PendingKeyRelabelledAfterClearing_NeedsTwoFreshMisses()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");
        store.Set("gone", null, null);
        guard.Prune(store, new[] { "a" }, "s");
        store.Set("gone", "Back", null);

        LabelPruneResult first = guard.Prune(store, new[] { "a" }, "s");
        LabelPruneResult second = guard.Prune(store, new[] { "a" }, "s");

        Assert.Empty(first.Pruned);
        Assert.Equal("gone", Assert.Single(second.Pruned).Key);
    }

    [Fact]
    public void Prune_KeyLabelledBetweenOpens_StartsItsRunFromItsFirstScan()
    {
        LabelStore store = NewStore("a");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");
        store.Set("late", "Late", null);

        LabelPruneResult first = guard.Prune(store, new[] { "a" }, "s");
        LabelPruneResult second = guard.Prune(store, new[] { "a" }, "s");

        Assert.Empty(first.Pruned);
        Assert.Equal(1, first.Pending);
        Assert.Equal("late", Assert.Single(second.Pruned).Key);
    }

    [Fact]
    public void Prune_ShelfComesBackAfterBeingPending_IsNotPrunedAndNotPendingAfter()
    {
        LabelStore store = NewStore("a", "back");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");

        LabelPruneResult seen = guard.Prune(store, new[] { "a", "back" }, "s");
        LabelPruneResult again = guard.Prune(store, new[] { "a", "back" }, "s");

        Assert.Equal(0, seen.Pending);
        Assert.Equal(0, again.Pending);
        Assert.NotNull(store.Get("back"));
    }

    [Fact]
    public void Prune_KeyPrunedThenRelabelled_StartsAFreshRunNotAnInstantPrune()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");
        guard.Prune(store, new[] { "a" }, "s");
        store.Set("gone", "Again", null);

        LabelPruneResult next = guard.Prune(store, new[] { "a" }, "s");

        Assert.Empty(next.Pruned);
        Assert.Equal(1, next.Pending);
        Assert.NotNull(store.Get("gone"));
    }

    [Fact]
    public void Prune_AfterAPrune_StoreContainsExactlyTheLiveAndPendingKeys()
    {
        LabelStore store = NewStore("a", "p1", "gone1", "gone2");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a", "p1" }, "s");
        store.Set("p2", "P2", null);

        LabelPruneResult result = guard.Prune(store, new[] { "a", "p1" }, "s");

        Assert.Equal(new[] { "gone1", "gone2" }, result.Pruned.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "a", "p1", "p2" }, Keys(store));
        Assert.Equal(1, result.Pending);
    }

    [Fact]
    public void Prune_PrunedEntriesAreReturnedAsASnapshotNotAViewOfTheStore()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");
        LabelPruneResult result = guard.Prune(store, new[] { "a" }, "s");

        store.Set("gone", "Recreated", null);

        Assert.Equal(new ShelfLabel("L-gone", null), Assert.Single(result.Pruned).Label);
    }

    [Fact]
    public void Prune_SkippedResultsShareNoMutableState_AcrossCalls()
    {
        LabelStore store = NewStore("a");
        var guard = new LabelPruneGuard();

        IReadOnlyList<PrunedLabel> one = guard.Prune(store, Array.Empty<string>(), "s").Pruned;
        IReadOnlyList<PrunedLabel> two = guard.Prune(store, Array.Empty<string>(), "s").Pruned;

        Assert.Empty(one);
        Assert.Empty(two);
        Assert.False(one is List<PrunedLabel>);
    }

    [Fact]
    public void Prune_ThenSave_PersistsTheRemoval_AndAReloadedStoreNoLongerHasTheKey()
    {
        var fs = new FakeLabelFileSystem();
        var store = new LabelStore(fs, "w1");
        store.Load();
        store.Set("a", "A", null);
        store.Set("gone", "G", null);
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");
        LabelPruneResult result = guard.Prune(store, new[] { "a" }, "s");

        LabelSaveResult saved = store.Save();

        Assert.Single(result.Pruned);
        Assert.Equal(LabelSaveStatus.Saved, saved.Status);
        var reloaded = new LabelStore(fs, "w1");
        reloaded.Load();
        Assert.Equal(new[] { "a" }, Keys(reloaded));
    }

    [Fact]
    public void Prune_FirstOpenOnly_LeavesThePersistedFileUntouchedWithoutASave()
    {
        var fs = new FakeLabelFileSystem();
        var store = new LabelStore(fs, "w1");
        store.Load();
        store.Set("a", "A", null);
        store.Set("gone", "G", null);

        new LabelPruneGuard().Prune(store, new[] { "a" }, "s");

        Assert.Empty(fs.Writes);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Prune_TwoOpensInsideOnePartialLoadWindow_PrunesALabelWhoseShelfIsJustNotLoadedYet_AcceptedLimitation()
    {
        // Pinned, not endorsed: the guard cannot tell "not loaded yet" from "destroyed". Two opens that both see
        // a partial registry (the world still streaming in) delete the label of the not-yet-listed shelf.
        // Card 20 accepts this; the recovery is that the player re-labels. If the guard ever learns about load
        // progress, this test should flip to "kept".
        LabelStore store = NewStore("loaded", "streaming");
        var guard = new LabelPruneGuard();
        string[] partial = { "loaded" };

        guard.Prune(store, partial, "s");
        LabelPruneResult second = guard.Prune(store, partial, "s");

        Assert.Equal("streaming", Assert.Single(second.Pruned).Key);
    }

    [Fact]
    public void Prune_PartialWindowThenFullListing_KeepsEverything()
    {
        LabelStore store = NewStore("loaded", "streaming");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "loaded" }, "s");

        LabelPruneResult full = guard.Prune(store, new[] { "loaded", "streaming" }, "s");
        LabelPruneResult next = guard.Prune(store, new[] { "loaded" }, "s");

        Assert.Empty(full.Pruned);
        Assert.Empty(next.Pruned);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Prune_OnlyTheSourceShelfExists_ListingContainingJustItIsNotEmptyAndPrunesTheRest()
    {
        // Review gap: a world with exactly one shelf. The listing is [source]; the guard treats it as a real scan.
        LabelStore store = NewStore("src", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "src" }, "src");

        LabelPruneResult result = guard.Prune(store, new[] { "src" }, "src");

        Assert.Equal("gone", Assert.Single(result.Pruned).Key);
        Assert.NotNull(store.Get("src"));
    }

    [Fact]
    public void Prune_ListingExcludingTheSourceAsTheOnlyShelf_IsEmptyAndSkips()
    {
        // Review gap: if the caller lists "other shelves" and there are none, the empty listing skips safely.
        LabelStore store = NewStore("src", "gone");
        var guard = new LabelPruneGuard();

        guard.Prune(store, Array.Empty<string>(), "src");
        LabelPruneResult second = guard.Prune(store, Array.Empty<string>(), "src");

        Assert.Equal(LabelPruneOutcome.SkippedNoShelves, second.Outcome);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Prune_DuplicateKeyTwinShelves_ShareOneEntry_LiveWhileEitherExists()
    {
        // Review gap: twin shelves with one key are one dictionary key; listing it twice keeps the one entry live.
        LabelStore store = NewStore("twin", "gone");
        var guard = new LabelPruneGuard();
        string[] listing = { "twin", "twin" };
        guard.Prune(store, listing, "s");

        LabelPruneResult result = guard.Prune(store, listing, "s");

        Assert.Equal("gone", Assert.Single(result.Pruned).Key);
        Assert.NotNull(store.Get("twin"));
    }

    [Fact]
    public void Prune_ThrowingListingEnumeration_LeavesStoreAndRunUntouched()
    {
        LabelStore store = NewStore("a", "gone");
        var guard = new LabelPruneGuard();
        guard.Prune(store, new[] { "a" }, "s");

        Assert.Throws<InvalidOperationException>(() => guard.Prune(store, new ThrowingKeys(), "s"));
        LabelPruneResult after = guard.Prune(store, new[] { "a" }, "s");

        Assert.Equal("gone", Assert.Single(after.Pruned).Key);
        Assert.Equal(new[] { "a" }, Keys(store));
    }

    private sealed class ThrowingKeys : IReadOnlyCollection<string>
    {
        public int Count => 1;

        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("registry torn down");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
