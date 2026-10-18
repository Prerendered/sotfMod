// purpose: criteria 1-4 and 6 (card 13) plus attack tests for LabelStore: set/get, groups, prune, load/save round-trip, guards
// owns:    LabelStoreTests
// deps:    LabelStore, FakeLabelFileSystem

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Labels;

public class LabelStoreTests
{
    private static LabelStore NewStore(FakeLabelFileSystem? fs = null, string? world = "w1") =>
        new(fs ?? new FakeLabelFileSystem(), world);

    private static LabelFile FileWith(int version, params (string Key, ShelfLabel? Label)[] entries)
    {
        var file = new LabelFile { Version = version, WorldId = "w1" };
        foreach ((string key, ShelfLabel? label) in entries)
        {
            file.Shelves[key] = label!;
        }

        return file;
    }

    // ---- criterion 1: Set / Get ----

    [Fact]
    public void SetThenGet_ReturnsLabelAndGroup()
    {
        LabelStore store = NewStore();
        store.Set("k1", "Logs", "Wood");

        Assert.Equal(new ShelfLabel("Logs", "Wood"), store.Get("k1"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Get_UnknownKey_IsNull() => Assert.Null(NewStore().Get("nope"));

    [Fact]
    public void Set_NullGroup_KeepsLabel()
    {
        LabelStore store = NewStore();
        store.Set("k1", "Logs", "Wood");
        store.Set("k1", "Logs", null);

        Assert.Equal(new ShelfLabel("Logs", null), store.Get("k1"));
    }

    [Fact]
    public void Set_BothCleared_RemovesEntry()
    {
        LabelStore store = NewStore();
        store.Set("k1", "Logs", "Wood");
        store.Set("k1", null, null);

        Assert.Null(store.Get("k1"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Set_BlankLabelAndGroup_RemovesEntry()
    {
        LabelStore store = NewStore();
        store.Set("k1", "Logs", "Wood");
        store.Set("k1", "   ", "\t");

        Assert.Null(store.Get("k1"));
    }

    [Fact]
    public void Set_EmptyLabelWithGroup_StoredAsEmptyLabel()
    {
        LabelStore store = NewStore();
        store.Set("k1", "", "Wood");
        store.Set("k2", null, "Stone");

        Assert.Equal(new ShelfLabel("", "Wood"), store.Get("k1"));
        Assert.Equal(new ShelfLabel("", "Stone"), store.Get("k2"));
    }

    [Fact]
    public void Set_TrimsLabelAndGroup()
    {
        LabelStore store = NewStore();
        store.Set("k1", "  Logs  ", "  Wood ");

        Assert.Equal(new ShelfLabel("Logs", "Wood"), store.Get("k1"));
    }

    [Fact]
    public void Set_WhitespaceOnlyGroup_IsNullGroup()
    {
        LabelStore store = NewStore();
        store.Set("k1", "Logs", "   ");

        Assert.Equal(new ShelfLabel("Logs", null), store.Get("k1"));
        Assert.Empty(store.ListGroups());
    }

    [Fact]
    public void Set_LabelBlankGroupKept_ThenGroupCleared_RemovesEntry()
    {
        LabelStore store = NewStore();
        store.Set("k1", "", "Wood");
        store.Set("k1", "", null);

        Assert.Null(store.Get("k1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Set_BlankKey_ThrowsArgumentException(string? key)
    {
        LabelStore store = NewStore();

        Assert.Throws<ArgumentException>(() => store.Set(key!, "x", "y"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Set_Overwrite_ReplacesPreviousValue()
    {
        LabelStore store = NewStore();
        store.Set("k1", "A", "G1");
        store.Set("k1", "B", "G2");

        Assert.Equal(new ShelfLabel("B", "G2"), store.Get("k1"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Keys_AreCaseSensitive()
    {
        LabelStore store = NewStore();
        store.Set("Key", "A", null);

        Assert.Null(store.Get("key"));
    }

    [Fact]
    public void Set_UnicodeAndEmoji_Preserved()
    {
        LabelStore store = NewStore();
        string label = "Caf\u00e9 \U0001F333 \u65e5\u672c";
        store.Set("k1", label, "\U0001F4E6 Box");

        Assert.Equal(new ShelfLabel(label, "\U0001F4E6 Box"), store.Get("k1"));
    }

    [Fact]
    public void Set_VeryLongLabel_NoCapApplied()
    {
        LabelStore store = NewStore();
        string label = new('x', 100_000);
        store.Set("k1", label, null);

        Assert.Equal(100_000, store.Get("k1")!.Label.Length);
    }

    // ---- criterion 2: groups ----

    [Fact]
    public void ListGroups_DistinctNonNull_OrdinalSorted()
    {
        LabelStore store = NewStore();
        store.Set("a", "1", "Wood");
        store.Set("b", "2", "Wood");
        store.Set("c", "3", "Ammo");
        store.Set("d", "4", null);

        Assert.Equal(new[] { "Ammo", "Wood" }, store.ListGroups());
    }

    [Fact]
    public void ListGroups_IsCaseSensitive_AndOrdinalOrder()
    {
        LabelStore store = NewStore();
        store.Set("a", "1", "wood");
        store.Set("b", "2", "Wood");
        store.Set("c", "3", "_x");

        Assert.Equal(new[] { "Wood", "_x", "wood" }, store.ListGroups());
    }

    [Fact]
    public void ListGroups_GroupWithNoShelves_Disappears()
    {
        LabelStore store = NewStore();
        store.Set("a", "1", "Wood");
        Assert.Equal(new[] { "Wood" }, store.ListGroups());

        store.Set("a", "1", null);
        Assert.Empty(store.ListGroups());

        store.Set("a", "1", "Wood");
        store.Set("a", null, null);
        Assert.Empty(store.ListGroups());
    }

    [Fact]
    public void ListGroups_NewGroupCreatedByAssigning()
    {
        LabelStore store = NewStore();
        Assert.Empty(store.ListGroups());

        store.Set("a", "1", "Brand New");

        Assert.Equal(new[] { "Brand New" }, store.ListGroups());
    }

    [Fact]
    public void ListGroups_EmptyLabelGroupOnly_StillCounts()
    {
        LabelStore store = NewStore();
        store.Set("a", null, "OnlyGroup");

        Assert.Equal(new[] { "OnlyGroup" }, store.ListGroups());
    }

    [Fact]
    public void ListGroups_MovingLastShelfToOtherGroup_DropsOldGroup()
    {
        LabelStore store = NewStore();
        store.Set("a", "1", "Old");
        store.Set("a", "1", "New");

        Assert.Equal(new[] { "New" }, store.ListGroups());
    }

    // ---- criterion 3: prune ----

    [Fact]
    public void Prune_DropsDeadKeys_KeepsLive_ReturnsRemovedOrdinal()
    {
        LabelStore store = NewStore();
        store.Set("c", "C", null);
        store.Set("a", "A", null);
        store.Set("b", "B", "G");
        store.Set("live", "L", null);

        IReadOnlyList<string> removed = store.Prune(new[] { "live" });

        Assert.Equal(new[] { "a", "b", "c" }, removed);
        Assert.Equal(1, store.Count);
        Assert.NotNull(store.Get("live"));
        Assert.Null(store.Get("a"));
        Assert.Empty(store.ListGroups());
    }

    [Fact]
    public void Prune_AllLive_RemovesNothing()
    {
        LabelStore store = NewStore();
        store.Set("a", "A", null);

        Assert.Empty(store.Prune(new[] { "a" }));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Prune_LiveKeysNotInStore_Ignored()
    {
        LabelStore store = NewStore();
        store.Set("a", "A", null);

        Assert.Empty(store.Prune(new[] { "a", "x", "y" }));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Prune_EmptyLiveSet_RemovesEverything_CurrentBehaviour()
    {
        // Documented: the empty-registry guard lives in card 21, not here.
        LabelStore store = NewStore();
        store.Set("a", "A", null);
        store.Set("b", "B", null);

        IReadOnlyList<string> removed = store.Prune(Array.Empty<string>());

        Assert.Equal(new[] { "a", "b" }, removed);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Prune_OnEmptyStore_ReturnsEmpty() =>
        Assert.Empty(NewStore().Prune(new[] { "a" }));

    [Fact]
    public void Prune_IsCaseSensitive()
    {
        LabelStore store = NewStore();
        store.Set("Key", "A", null);

        Assert.Equal(new[] { "Key" }, store.Prune(new[] { "key" }));
    }

    [Fact]
    public void Prune_LiveKeysEnumeratedOnce_DuplicatesTolerated()
    {
        LabelStore store = NewStore();
        store.Set("a", "A", null);

        Assert.Empty(store.Prune(new[] { "a", "a", "a" }));
    }

    [Fact]
    public void Prune_LazyEnumerable_Works()
    {
        LabelStore store = NewStore();
        store.Set("a", "A", null);
        store.Set("b", "B", null);

        IEnumerable<string> live = Enumerable.Range(0, 1).Select(_ => "a");

        Assert.Equal(new[] { "b" }, store.Prune(live));
    }

    // ---- criterion 4: save / load ----

    [Fact]
    public void SaveThenLoad_RoundTripsThroughFake()
    {
        var fs = new FakeLabelFileSystem();
        LabelStore writer = NewStore(fs);
        Assert.Equal(LabelLoadStatus.NoFile, writer.Load().Status);
        writer.Set("k1", "Logs", "Wood");
        writer.Set("k2", "Rocks", null);
        writer.Set("k3", "", "Misc");

        LabelSaveResult saved = writer.Save();

        Assert.Equal(LabelSaveStatus.Saved, saved.Status);
        Assert.Equal(3, saved.Count);
        Assert.Null(saved.Detail);
        LabelFile written = Assert.Single(fs.Writes);
        Assert.Equal(1, written.Version);
        Assert.Equal("w1", written.WorldId);
        Assert.Equal(3, written.Shelves.Count);

        LabelStore reader = NewStore(fs);
        LabelLoadResult loaded = reader.Load();

        Assert.Equal(LabelLoadStatus.Loaded, loaded.Status);
        Assert.Equal(3, loaded.Count);
        Assert.Null(loaded.Detail);
        Assert.Equal(new ShelfLabel("Logs", "Wood"), reader.Get("k1"));
        Assert.Equal(new ShelfLabel("Rocks", null), reader.Get("k2"));
        Assert.Equal(new ShelfLabel("", "Misc"), reader.Get("k3"));
        Assert.Equal(new[] { "Misc", "Wood" }, reader.ListGroups());
    }

    [Fact]
    public void RoundTrip_UnicodeEmojiPreserved()
    {
        var fs = new FakeLabelFileSystem();
        LabelStore a = NewStore(fs);
        a.Load();
        a.Set("k", "Caf\u00e9 \U0001F333", "\u65e5\u672c \U0001F4E6");
        a.Save();

        LabelStore b = NewStore(fs);
        b.Load();

        Assert.Equal(new ShelfLabel("Caf\u00e9 \U0001F333", "\u65e5\u672c \U0001F4E6"), b.Get("k"));
    }

    [Fact]
    public void SaveSnapshot_IsACopy_LaterSetDoesNotMutateWrittenFile()
    {
        var fs = new FakeLabelFileSystem();
        LabelStore store = NewStore(fs);
        store.Load();
        store.Set("k1", "A", null);
        store.Save();

        store.Set("k2", "B", null);

        Assert.Single(fs.Writes[0].Shelves);
    }

    [Fact]
    public void Load_PassesSanitisedWorldIdToFileSystem()
    {
        var fs = new FakeLabelFileSystem();
        var store = new LabelStore(fs, "../My World");
        store.Load();
        store.Save();

        Assert.Equal("___My_World", store.SanitisedWorldId);
        Assert.Equal("___My_World", Assert.Single(fs.ReadWorldIds));
        Assert.Equal("___My_World", Assert.Single(fs.Writes).WorldId);
    }

    [Fact]
    public void NullWorldId_UsesFallback()
    {
        var fs = new FakeLabelFileSystem();
        var store = new LabelStore(fs, null);
        store.Load();

        Assert.Equal("unknown", store.SanitisedWorldId);
        Assert.Equal("unknown", Assert.Single(fs.ReadWorldIds));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Load_UnsupportedVersion_RejectedWithReason_NothingApplied(int version)
    {
        var fs = new FakeLabelFileSystem().Store(FileWith(version, ("k1", new ShelfLabel("A", null))));
        LabelStore store = NewStore(fs);

        LabelLoadResult result = store.Load();

        Assert.Equal(LabelLoadStatus.Version, result.Status);
        Assert.Equal(0, result.Count);
        Assert.Equal($"version {version} not supported (expected 1)", result.Detail);
        Assert.Equal(0, store.Count);
        Assert.Null(store.Get("k1"));
    }

    [Fact]
    public void Load_NoFile_EmptyAndNoFileStatus()
    {
        LabelStore store = NewStore(new FakeLabelFileSystem());

        LabelLoadResult result = store.Load();

        Assert.Equal(LabelLoadStatus.NoFile, result.Status);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void Load_Corrupt_PropagatesDetail()
    {
        var fs = new FakeLabelFileSystem { ReadResult = LabelReadResult.Corrupt("bad json at 3") };

        LabelLoadResult result = NewStore(fs).Load();

        Assert.Equal(LabelLoadStatus.Corrupt, result.Status);
        Assert.Equal("bad json at 3", result.Detail);
    }

    [Fact]
    public void Load_FileSystemFailed_PropagatesDetail()
    {
        var fs = new FakeLabelFileSystem { ReadResult = LabelReadResult.Failed("locked") };

        LabelLoadResult result = NewStore(fs).Load();

        Assert.Equal(LabelLoadStatus.Failed, result.Status);
        Assert.Equal("locked", result.Detail);
    }

    [Fact]
    public void Load_FileSystemThrows_BecomesFailedWithTypeAndMessage()
    {
        var fs = new FakeLabelFileSystem { ThrowOnRead = new InvalidOperationException("boom") };
        LabelStore store = NewStore(fs);

        LabelLoadResult result = store.Load();

        Assert.Equal(LabelLoadStatus.Failed, result.Status);
        Assert.Equal("InvalidOperationException: boom", result.Detail);
    }

    [Fact]
    public void Load_OkWithNullFile_IsCorrupt()
    {
        var fs = new FakeLabelFileSystem { ReadResult = new LabelReadResult(LabelReadStatus.Ok, null, null) };

        Assert.Equal(LabelLoadStatus.Corrupt, NewStore(fs).Load().Status);
    }

    [Fact]
    public void Load_NullShelves_IsCorrupt()
    {
        var file = new LabelFile { Version = 1 };
        file.Shelves = null!;
        var fs = new FakeLabelFileSystem().Store(file);

        LabelLoadResult result = NewStore(fs).Load();

        Assert.Equal(LabelLoadStatus.Corrupt, result.Status);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void Load_BadEntries_SkippedAndCounted_GoodEntriesKept()
    {
        var file = FileWith(
            1,
            ("good", new ShelfLabel("Logs", "Wood")),
            ("nullentry", null),
            ("blankboth", new ShelfLabel("  ", "  ")),
            ("padded", new ShelfLabel("  Pad ", " G ")),
            (" ", new ShelfLabel("x", null)));
        var fs = new FakeLabelFileSystem().Store(file);
        LabelStore store = NewStore(fs);

        LabelLoadResult result = store.Load();

        Assert.Equal(LabelLoadStatus.Loaded, result.Status);
        Assert.Equal(2, result.Count);
        Assert.Equal("skipped 3 invalid entries", result.Detail);
        Assert.Equal(new ShelfLabel("Logs", "Wood"), store.Get("good"));
        Assert.Equal(new ShelfLabel("Pad", "G"), store.Get("padded"));
    }

    [Fact]
    public void Load_EmptyShelves_LoadedWithZero()
    {
        var fs = new FakeLabelFileSystem().Store(FileWith(1));

        LabelLoadResult result = NewStore(fs).Load();

        Assert.Equal(LabelLoadStatus.Loaded, result.Status);
        Assert.Equal(0, result.Count);
        Assert.Null(result.Detail);
    }

    [Fact]
    public void Load_Twice_ClearsMapFirst()
    {
        var fs = new FakeLabelFileSystem().Store(FileWith(1, ("a", new ShelfLabel("A", null))));
        LabelStore store = NewStore(fs);
        store.Load();
        store.Set("local", "L", null);

        fs.Store(FileWith(1, ("b", new ShelfLabel("B", null))));
        store.Load();

        Assert.Null(store.Get("a"));
        Assert.Null(store.Get("local"));
        Assert.NotNull(store.Get("b"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Load_AfterFailure_ClearsMapToEmpty()
    {
        var fs = new FakeLabelFileSystem().Store(FileWith(1, ("a", new ShelfLabel("A", null))));
        LabelStore store = NewStore(fs);
        store.Load();

        fs.ReadResult = LabelReadResult.Failed("x");
        store.Load();

        Assert.Equal(0, store.Count);
    }

    // ---- save guards ----

    [Fact]
    public void Save_BeforeAnyLoad_Blocked_NothingWritten()
    {
        var fs = new FakeLabelFileSystem();
        LabelStore store = NewStore(fs);
        store.Set("k", "A", null);

        LabelSaveResult result = store.Save();

        Assert.Equal(LabelSaveStatus.Blocked, result.Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void Save_AfterCorruptLoad_Allowed()
    {
        var fs = new FakeLabelFileSystem { ReadResult = LabelReadResult.Corrupt("junk") };
        LabelStore store = NewStore(fs);
        store.Load();
        store.Set("k", "A", null);

        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);
        Assert.Single(fs.Writes);
    }

    [Fact]
    public void Save_AfterNoFileLoad_Allowed()
    {
        var fs = new FakeLabelFileSystem();
        LabelStore store = NewStore(fs);
        store.Load();

        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);
    }

    [Fact]
    public void Save_AfterVersionLoad_Blocked_NothingWritten()
    {
        var fs = new FakeLabelFileSystem().Store(FileWith(2));
        LabelStore store = NewStore(fs);
        store.Load();
        store.Set("k", "A", null);

        LabelSaveResult result = store.Save();

        Assert.Equal(LabelSaveStatus.Blocked, result.Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void Save_AfterFailedLoad_Blocked_NothingWritten()
    {
        var fs = new FakeLabelFileSystem { ReadResult = LabelReadResult.Failed("locked") };
        LabelStore store = NewStore(fs);
        store.Load();
        store.Set("k", "A", null);

        Assert.Equal(LabelSaveStatus.Blocked, store.Save().Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void Save_AfterThrownLoad_Blocked()
    {
        var fs = new FakeLabelFileSystem { ThrowOnRead = new System.IO.IOException("x") };
        LabelStore store = NewStore(fs);
        store.Load();

        Assert.Equal(LabelSaveStatus.Blocked, store.Save().Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void Save_LaterGoodLoadUnblocks()
    {
        var fs = new FakeLabelFileSystem().Store(FileWith(2));
        LabelStore store = NewStore(fs);
        store.Load();
        Assert.Equal(LabelSaveStatus.Blocked, store.Save().Status);

        fs.Store(FileWith(1));
        store.Load();

        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);
    }

    [Fact]
    public void Save_WriteFails_ReportsFailedWithDetail()
    {
        var fs = new FakeLabelFileSystem { WriteResult = LabelWriteResult.Failed("disk full") };
        LabelStore store = NewStore(fs);
        store.Load();
        store.Set("k", "A", null);

        LabelSaveResult result = store.Save();

        Assert.Equal(LabelSaveStatus.Failed, result.Status);
        Assert.Equal("disk full", result.Detail);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void Save_WriteThrows_BecomesFailed_NeverThrows()
    {
        var fs = new FakeLabelFileSystem { ThrowOnWrite = new UnauthorizedAccessException("denied") };
        LabelStore store = NewStore(fs);
        store.Load();

        LabelSaveResult result = store.Save();

        Assert.Equal(LabelSaveStatus.Failed, result.Status);
        Assert.Equal("UnauthorizedAccessException: denied", result.Detail);
    }

    [Fact]
    public void Save_EmptyStore_WritesEmptyShelves()
    {
        var fs = new FakeLabelFileSystem();
        LabelStore store = NewStore(fs);
        store.Load();

        store.Save();

        Assert.Empty(Assert.Single(fs.Writes).Shelves);
    }

    // ---- criterion 6: Labels namespace does not reference Gateway ----

    [Fact]
    public void LabelsNamespace_PublicSurfaceReferencesNoGatewayType()
    {
        // Signature-level check only; unused using directives are reviewer-confirmed.
        Type[] labelTypes = typeof(LabelStore).Assembly.GetTypes()
            .Where(t => t.Namespace == "ShelfTransfer.Core.Labels")
            .ToArray();
        Assert.NotEmpty(labelTypes);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var referenced = new List<Type>();
        foreach (Type t in labelTypes)
        {
            referenced.AddRange(t.GetFields(all).Select(f => f.FieldType));
            referenced.AddRange(t.GetProperties(all).Select(p => p.PropertyType));
            foreach (MethodBase m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            {
                referenced.AddRange(m.GetParameters().Select(p => p.ParameterType));
                if (m is MethodInfo mi)
                {
                    referenced.Add(mi.ReturnType);
                }
            }
        }

        foreach (Type r in referenced.SelectMany(Flatten))
        {
            Assert.False(
                r.Namespace?.StartsWith("ShelfTransfer.Core.Gateway", StringComparison.Ordinal) ?? false,
                $"Labels references Gateway type {r.FullName}");
        }
    }

    private static IEnumerable<Type> Flatten(Type t)
    {
        yield return t;
        if (t.HasElementType)
        {
            foreach (Type e in Flatten(t.GetElementType()!))
            {
                yield return e;
            }
        }

        if (t.IsGenericType)
        {
            foreach (Type a in t.GetGenericArguments().SelectMany(Flatten))
            {
                yield return a;
            }
        }
    }
}
