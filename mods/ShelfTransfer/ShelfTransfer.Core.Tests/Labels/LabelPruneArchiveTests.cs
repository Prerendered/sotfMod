// purpose: card 20 security fix: a pruned label is archived before it leaves the store, a failed archive prunes
//          nothing, a failed save puts the entries back, and <id>.pruned.json only ever grows
// owns:    LabelPruneArchiveTests
// deps:    LabelPruneGuard, LabelStore, JsonLabelFileSystem, FakeLabelFileSystem, FakeModLog
// caution: the JsonLabelFileSystem cases use real disk, one temp directory per test

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Labels;

public sealed class LabelPruneArchiveTests : IDisposable
{
    private static readonly ShelfLabel _goneLabel = new("Stones", "Base");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "shelftransfer-prune-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static LabelStore NewStore(ILabelFileSystem fs)
    {
        var store = new LabelStore(fs, "w1");
        store.Set("live", "Logs", null);
        store.Set("gone", _goneLabel.Label, _goneLabel.Group);
        return store;
    }

    private static LabelPruneResult PruneTwice(LabelPruneGuard guard, LabelStore store)
    {
        guard.Prune(store, new[] { "live" }, "src");
        return guard.Prune(store, new[] { "live" }, "src");
    }

    [Fact]
    public void Prune_DueKey_IsArchivedWhileStillInTheStoreThenRemoved()
    {
        var fs = new FakeLabelFileSystem();
        LabelStore store = NewStore(fs);
        ShelfLabel? atArchive = null;
        fs.OnAppend = () => atArchive = store.Get("gone");

        LabelPruneResult result = PruneTwice(new LabelPruneGuard(), store);

        Assert.Equal(_goneLabel, atArchive);
        (string world, var entries) = Assert.Single(fs.Appends);
        Assert.Equal("w1", world);
        Assert.Equal(new PrunedLabel("gone", _goneLabel), Assert.Single(entries));
        Assert.Equal(LabelPruneOutcome.Checked, result.Outcome);
        Assert.Null(store.Get("gone"));
    }

    [Fact]
    public void Prune_ArchiveFails_RemovesNothingAndRetriesOnTheNextScan()
    {
        var fs = new FakeLabelFileSystem { AppendResult = LabelWriteResult.Failed("disk full") };
        LabelStore store = NewStore(fs);
        var guard = new LabelPruneGuard();

        LabelPruneResult failed = PruneTwice(guard, store);
        fs.AppendResult = LabelWriteResult.Ok;
        LabelPruneResult retried = guard.Prune(store, new[] { "live" }, "src");

        Assert.Equal(LabelPruneOutcome.ArchiveFailed, failed.Outcome);
        Assert.Empty(failed.Pruned);
        Assert.Equal(1, failed.Pending);
        Assert.Equal("disk full", failed.Detail);
        Assert.Equal("gone", Assert.Single(retried.Pruned).Key);
    }

    [Fact]
    public void Prune_ArchiveThrows_RemovesNothing()
    {
        var fs = new FakeLabelFileSystem();
        fs.OnAppend = () => throw new IOException("locked");
        LabelStore store = NewStore(fs);

        LabelPruneResult result = PruneTwice(new LabelPruneGuard(), store);

        Assert.Equal(LabelPruneOutcome.ArchiveFailed, result.Outcome);
        Assert.Equal(_goneLabel, store.Get("gone"));
    }

    [Theory]
    [InlineData(LabelSaveStatus.Failed)]
    [InlineData(LabelSaveStatus.Blocked)]
    public void SavePruned_SaveNotSaved_PutsTheEntriesBack(LabelSaveStatus status)
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());
        LabelPruneResult result = PruneTwice(new LabelPruneGuard(), store);

        LabelSaveResult? saved = LabelPruneGuard.SavePruned(store, result.Pruned, () => new LabelSaveResult(status, 1, null));

        Assert.Equal(status, saved?.Status);
        Assert.Equal(_goneLabel, store.Get("gone"));
    }

    [Fact]
    public void SavePruned_NoStoreToSave_PutsTheEntriesBackAsFailed()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());
        LabelPruneResult result = PruneTwice(new LabelPruneGuard(), store);

        LabelSaveResult? saved = LabelPruneGuard.SavePruned(store, result.Pruned, () => null);

        Assert.Equal(LabelSaveStatus.Failed, saved?.Status);
        Assert.Equal(_goneLabel, store.Get("gone"));
    }

    [Fact]
    public void SavePruned_Saved_KeepsThemRemoved()
    {
        var fs = new FakeLabelFileSystem();
        LabelStore store = NewStore(fs);
        store.Load();
        store.Set("live", "Logs", null);
        store.Set("gone", _goneLabel.Label, _goneLabel.Group);
        LabelPruneResult result = PruneTwice(new LabelPruneGuard(), store);

        LabelSaveResult? saved = LabelPruneGuard.SavePruned(store, result.Pruned, store.Save);

        Assert.Equal(LabelSaveStatus.Saved, saved?.Status);
        Assert.Null(store.Get("gone"));
        Assert.False(Assert.Single(fs.Writes).Shelves.ContainsKey("gone"));
    }

    [Fact]
    public void SavePruned_NothingPruned_DoesNotSave()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());
        bool isCalled = false;

        LabelSaveResult? saved = LabelPruneGuard.SavePruned(store, Array.Empty<PrunedLabel>(), () =>
        {
            isCalled = true;
            return null;
        });

        Assert.Null(saved);
        Assert.False(isCalled);
    }

    [Fact]
    public void AppendPruned_TwoCalls_KeepsEveryEntryInPrunedJson()
    {
        var fs = new JsonLabelFileSystem(_root, new FakeModLog());

        LabelWriteResult first = fs.AppendPruned("w1", new[] { new PrunedLabel("a", new ShelfLabel("A", "G")) });
        LabelWriteResult second = fs.AppendPruned("w1", new[] { new PrunedLabel("a", new ShelfLabel("A2", null)) });

        Assert.True(first.IsOk);
        Assert.True(second.IsOk);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "w1.pruned.json")));
        JsonElement root = document.RootElement;
        Assert.Equal(PrunedLabelArchive.CurrentVersion, root.GetProperty("version").GetInt32());
        Assert.Equal("w1", root.GetProperty("worldId").GetString());
        JsonElement pruned = root.GetProperty("pruned");
        Assert.Equal(2, pruned.GetArrayLength());
        Assert.Equal("G", pruned[0].GetProperty("group").GetString());
        Assert.Equal("A2", pruned[1].GetProperty("label").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", pruned[1].GetProperty("prunedAt").GetString());
        Assert.False(File.Exists(Path.Combine(_root, "w1.json")));
    }

    [Fact]
    public void AppendPruned_UnreadableArchive_FailsAndLeavesItUntouched()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "w1.pruned.json");
        const string corrupt = "{\"version\":1,\"pruned\":[";
        File.WriteAllText(path, corrupt, new UTF8Encoding(false));
        var fs = new JsonLabelFileSystem(_root, new FakeModLog());

        LabelWriteResult result = fs.AppendPruned("w1", new[] { new PrunedLabel("a", new ShelfLabel("A", null)) });

        Assert.False(result.IsOk);
        Assert.Equal(corrupt, File.ReadAllText(path));
    }

    [Fact]
    public void SavePruned_KeySetAgainBeforeFailedSave_NewerValueWins()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());
        LabelPruneResult result = PruneTwice(new LabelPruneGuard(), store);
        store.Set("gone", "Newer", "Other");

        LabelPruneGuard.SavePruned(store, result.Pruned, () => new LabelSaveResult(LabelSaveStatus.Failed, 1, null));

        Assert.Equal(new ShelfLabel("Newer", "Other"), store.Get("gone"));
    }

    [Theory]
    [InlineData(LabelSaveStatus.Failed)]
    [InlineData(LabelSaveStatus.Blocked)]
    public void SavePruned_KeySetAgainInsideTheSave_NewerValueWins(LabelSaveStatus status)
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());
        LabelPruneResult result = PruneTwice(new LabelPruneGuard(), store);

        LabelPruneGuard.SavePruned(store, result.Pruned, () =>
        {
            store.Set("gone", "Newer", null);
            return new LabelSaveResult(status, 1, null);
        });

        Assert.Equal(new ShelfLabel("Newer", null), store.Get("gone"));
    }

    // Pins current behaviour, not a goal: Restore cannot tell "deleted" from "never set", so it brings the label back.
    // Unreachable today: prune and SavePruned run on the same main-thread frame, so the User cannot edit in between.
    [Fact]
    public void SavePruned_KeyDeletedBeforeFailedSave_IsResurrected_PinnedBehaviour()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());
        LabelPruneResult result = PruneTwice(new LabelPruneGuard(), store);
        store.Set("gone", null, null);

        LabelPruneGuard.SavePruned(store, result.Pruned, () => new LabelSaveResult(LabelSaveStatus.Failed, 1, null));

        Assert.Equal(_goneLabel, store.Get("gone"));
    }

    private static readonly PrunedLabel[] _oneEntry = { new("a", new ShelfLabel("A", null)) };

    private string ArchivePath(string id = "w1") => Path.Combine(_root, id + ".pruned.json");

    private string PlantLabelFile()
    {
        Directory.CreateDirectory(_root);
        string labelPath = Path.Combine(_root, "w1.json");
        File.WriteAllText(labelPath, "{\"version\":1,\"shelves\":{}}", new UTF8Encoding(false));
        return labelPath;
    }

    private static string ArchiveJson(int version, int padding = 0, bool hasPruned = true)
    {
        string pruned = hasPruned
            ? ",\"pruned\":[{\"key\":\"k\",\"label\":\"" + new string('x', padding) + "\",\"group\":null,\"prunedAt\":\"2026-01-01T00:00:00Z\"}]"
            : string.Empty;
        return "{\"version\":" + version + ",\"worldId\":\"w1\"" + pruned + "}";
    }

    public static TheoryData<string, string> RefusedArchives => new()
    {
        { "over the 1 MiB cap", ArchiveJson(1, (int)JsonLabelFileSystem.MaxFileBytes) },
        { "version 2", ArchiveJson(2) },
        { "valid JSON without pruned", ArchiveJson(1, hasPruned: false) },
        { "append would pass the cap", ArchiveJson(1, (int)JsonLabelFileSystem.MaxFileBytes - 200) },
    };

    [Theory]
    [MemberData(nameof(RefusedArchives))]
    public void AppendPruned_RefusedArchive_FailsAndArchiveAndLabelFileAreByteIdentical(string why, string archiveJson)
    {
        string labelPath = PlantLabelFile();
        byte[] before = new UTF8Encoding(false).GetBytes(archiveJson);
        File.WriteAllBytes(ArchivePath(), before);
        byte[] labelBefore = File.ReadAllBytes(labelPath);
        var fs = new JsonLabelFileSystem(_root, new FakeModLog());
        var entries = new[] { new PrunedLabel("a", new ShelfLabel(new string('y', 400), null)) };

        LabelWriteResult result = fs.AppendPruned("w1", entries);

        Assert.False(result.IsOk, why);
        Assert.Equal(before, File.ReadAllBytes(ArchivePath()));
        Assert.Equal(labelBefore, File.ReadAllBytes(labelPath));
        Assert.False(File.Exists(ArchivePath() + ".tmp"));
    }

    [Fact]
    public void AppendPruned_TwoCalls_KeepsBothEntriesInOrderWithPrunedAtAndLeavesTheLabelFile()
    {
        string labelPath = PlantLabelFile();
        byte[] labelBefore = File.ReadAllBytes(labelPath);
        var fs = new JsonLabelFileSystem(_root, new FakeModLog());

        fs.AppendPruned("w1", new[] { new PrunedLabel("k1", new ShelfLabel("One", null)), new PrunedLabel("k2", new ShelfLabel("Two", "G")) });
        fs.AppendPruned("w1", new[] { new PrunedLabel("k3", new ShelfLabel("Three", null)) });

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ArchivePath()));
        JsonElement pruned = document.RootElement.GetProperty("pruned");
        Assert.Equal(new[] { "k1", "k2", "k3" }, pruned.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray());
        Assert.All(pruned.EnumerateArray(), e => Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", e.GetProperty("prunedAt").GetString()));
        Assert.Equal(labelBefore, File.ReadAllBytes(labelPath));
    }

    [Fact]
    public void AppendPruned_TempPathIsReparsePoint_FailsAndWritesNothingThroughIt()
    {
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string tempLink = ArchivePath() + ".tmp";
        MakeDirLink(tempLink, outside);
        var fs = new JsonLabelFileSystem(_root, new FakeModLog());

        LabelWriteResult result = fs.AppendPruned("w1", _oneEntry);

        Assert.False(result.IsOk);
        Assert.False(File.Exists(ArchivePath()));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.True(new DirectoryInfo(tempLink).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Directory.Delete(tempLink); // non-recursive: removes the link itself so Dispose can clean the folder
    }

    [Theory]
    [InlineData("w1")]
    [InlineData("My World 7")]
    [InlineData("a.json")]
    [InlineData("..\\x")]
    [InlineData("w1.json.bad")]
    [InlineData("CON")]
    [InlineData("")]
    public void AppendPruned_SanitisedId_ArchiveNeverEqualsLabelFileOrBadCopy(string raw)
    {
        string id = WorldId.Sanitise(raw);
        var fs = new JsonLabelFileSystem(_root, new FakeModLog());

        LabelWriteResult result = fs.AppendPruned(id, _oneEntry);

        Assert.True(result.IsOk);
        string[] files = Directory.GetFiles(_root).Select(path => Path.GetFileName(path)).ToArray();
        Assert.Equal(new[] { id + ".pruned.json" }, files);
        Assert.NotEqual(id + ".json", files[0]);
        Assert.DoesNotContain(".bad", files[0], StringComparison.Ordinal);
    }

    // Directory link: junction on Windows (no privilege), symlink on Unix.
    private static void MakeDirLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "cannot create junction (test environment problem): " + output);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }
}
