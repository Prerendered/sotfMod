// purpose: card 20 dry-run fix: in DryRun a label due for a prune is only reported, once per world session; the
//          store, the label file and <id>.pruned.json are never touched
// owns:    LabelPruneDryRunTests
// deps:    LabelPruneGuard, LabelStore, JsonLabelFileSystem, FakeLabelFileSystem, FakeModLog
// caution: the JsonLabelFileSystem case uses real disk, one temp directory per test

using System;
using System.IO;
using System.Linq;

using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Labels;

public sealed class LabelPruneDryRunTests : IDisposable
{
    private static readonly string[] _liveOnly = { "live" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "shelftransfer-dryrun-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static LabelStore NewStore(ILabelFileSystem fs, string? world = "w1")
    {
        var store = new LabelStore(fs, world);
        store.Set("live", "Logs", null);
        store.Set("gone", "Stones", "Base");
        return store;
    }

    [Fact]
    public void Prune_DryRunFirstScanMissingKey_IsPendingNotACandidate()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());

        LabelPruneResult result = new LabelPruneGuard(LabelPruneMode.DryRun).Prune(store, _liveOnly, "src");

        Assert.Equal(LabelPruneOutcome.DryRun, result.Outcome);
        Assert.Equal(1, result.Pending);
        Assert.Equal(0, result.Candidates);
        Assert.Empty(result.NewCandidates);
    }

    [Fact]
    public void Prune_DryRunDueKey_ReportsItAndLeavesStoreAndFileSystemUntouched()
    {
        var fs = new FakeLabelFileSystem();
        LabelStore store = NewStore(fs);
        var guard = new LabelPruneGuard(LabelPruneMode.DryRun);
        guard.Prune(store, _liveOnly, "src");

        LabelPruneResult result = guard.Prune(store, _liveOnly, "src");

        Assert.Equal(LabelPruneOutcome.DryRun, result.Outcome);
        Assert.Empty(result.Pruned);
        Assert.Equal(1, result.Candidates);
        Assert.Equal(0, result.Pending);
        PrunedLabel candidate = Assert.Single(result.NewCandidates);
        Assert.Equal("gone", candidate.Key);
        Assert.Equal(new ShelfLabel("Stones", "Base"), candidate.Label);
        Assert.Equal(new ShelfLabel("Stones", "Base"), store.Get("gone"));
        Assert.Equal(2, store.Count);
        Assert.Empty(fs.Appends);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void Prune_DryRunKeyStillDueOnLaterScans_IsReportedOnlyOnce()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());
        var guard = new LabelPruneGuard(LabelPruneMode.DryRun);
        guard.Prune(store, _liveOnly, "src");
        guard.Prune(store, _liveOnly, "src");

        LabelPruneResult third = guard.Prune(store, _liveOnly, "src");
        LabelPruneResult fourth = guard.Prune(store, _liveOnly, "src");

        Assert.Equal(1, third.Candidates);
        Assert.Empty(third.NewCandidates);
        Assert.Equal(1, fourth.Candidates);
        Assert.Empty(fourth.NewCandidates);
        Assert.NotNull(store.Get("gone"));
    }

    [Fact]
    public void Prune_DryRunKeySeenThenMissingTwiceAgain_IsNotReportedAgainInTheSameWorld()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());
        var guard = new LabelPruneGuard(LabelPruneMode.DryRun);
        guard.Prune(store, _liveOnly, "src");
        guard.Prune(store, _liveOnly, "src");
        guard.Prune(store, new[] { "live", "gone" }, "src");
        guard.Prune(store, _liveOnly, "src");

        LabelPruneResult result = guard.Prune(store, _liveOnly, "src");

        Assert.Equal(1, result.Candidates);
        Assert.Empty(result.NewCandidates);
    }

    [Fact]
    public void Prune_DryRunNewStoreInstance_ReportsTheKeyAgain()
    {
        var guard = new LabelPruneGuard(LabelPruneMode.DryRun);
        LabelStore first = NewStore(new FakeLabelFileSystem());
        guard.Prune(first, _liveOnly, "src");
        guard.Prune(first, _liveOnly, "src");
        LabelStore reentered = NewStore(new FakeLabelFileSystem());
        guard.Prune(reentered, _liveOnly, "src");

        LabelPruneResult result = guard.Prune(reentered, _liveOnly, "src");

        Assert.Equal("gone", Assert.Single(result.NewCandidates).Key);
    }

    [Fact]
    public void Prune_DryRunFallbackWorld_SkipsWithNoCandidates()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem(), world: null);
        var guard = new LabelPruneGuard(LabelPruneMode.DryRun);
        guard.Prune(store, _liveOnly, "src");

        LabelPruneResult result = guard.Prune(store, _liveOnly, "src");

        Assert.Equal(LabelPruneOutcome.SkippedUnknownWorld, result.Outcome);
        Assert.Equal(0, result.Candidates);
        Assert.Empty(result.NewCandidates);
    }

    [Fact]
    public void Prune_DryRunOnDisk_WritesNoArchiveAndLeavesTheLabelFileByteIdentical()
    {
        var fs = new JsonLabelFileSystem(_root, new FakeModLog());
        var store = new LabelStore(fs, "w1");
        store.Load();
        store.Set("live", "Logs", null);
        store.Set("gone", "Stones", "Base");
        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);
        string labelPath = Path.Combine(_root, "w1.json");
        byte[] before = File.ReadAllBytes(labelPath);
        var guard = new LabelPruneGuard(LabelPruneMode.DryRun);

        LabelPruneResult[] results = Enumerable.Range(0, 3).Select(_ => guard.Prune(store, _liveOnly, "src")).ToArray();

        Assert.All(results, result => Assert.Empty(result.Pruned));
        Assert.Equal(1, results[1].Candidates);
        Assert.False(File.Exists(Path.Combine(_root, "w1.pruned.json")));
        Assert.Equal(before, File.ReadAllBytes(labelPath));
        Assert.Equal(new[] { "gone", "live" }, store.Entries.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void Prune_ArchiveModeDueKey_ReportsItAsACandidateAndPrunesIt()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());
        var guard = new LabelPruneGuard(LabelPruneMode.Archive);
        guard.Prune(store, _liveOnly, "src");

        LabelPruneResult result = guard.Prune(store, _liveOnly, "src");

        Assert.Equal(LabelPruneOutcome.Checked, result.Outcome);
        Assert.Equal(1, result.Candidates);
        Assert.Empty(result.NewCandidates);
        Assert.Equal("gone", Assert.Single(result.Pruned).Key);
        Assert.Null(store.Get("gone"));
    }
}
