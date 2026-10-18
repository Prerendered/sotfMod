// purpose: card 16 - the Core behaviour the label editor relies on, pinned end to end over the real JSON file (F3, I5)
// owns:    LabelEditorWriteTests
// deps:    LabelStore, JsonLabelFileSystem, FakeModLog
// caution: real disk, one temp directory per test. The Plugin editor is IMGUI and untestable here; these cover what it
//          hands to Core (set, clear, new group assigned, reload preselection) and what Core does with text the editor
//          cannot rule out (a cut surrogate pair from a clipped paste).

using System;
using System.IO;
using System.Linq;

using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Labels;

public sealed class LabelEditorWriteTests : IDisposable
{
    private const string _world = "w1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shelftransfer-edit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // best effort: a leftover temp folder must not fail a test run
        }
    }

    private LabelStore Open()
    {
        var store = new LabelStore(new JsonLabelFileSystem(_root, new FakeModLog()), _world);
        store.Load();
        return store;
    }

    private string FileText() => File.ReadAllText(Path.Combine(_root, _world + ".json"));

    [Fact]
    public void SetAmmo_Save_FileHoldsLabel_AndReloadsAfterRestart()
    {
        LabelStore store = Open();
        store.Set("shelf-1", "Ammo", null);
        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);

        Assert.Contains("Ammo", FileText());
        Assert.Equal(new ShelfLabel("Ammo", null), Open().Get("shelf-1"));
    }

    [Fact]
    public void NewGroup_CreatedInline_AssignedAndPreselectedOnReopen()
    {
        LabelStore store = Open();
        store.Set("shelf-1", "Ammo", "Main base");
        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);

        LabelStore reopened = Open();
        Assert.Equal("Main base", reopened.Get("shelf-1")?.Group);
        Assert.Equal(new[] { "Main base" }, reopened.ListGroups().ToArray());
    }

    [Fact]
    public void EmptyNameNoGroup_ClearsLabel_FileNoLongerHoldsKey()
    {
        LabelStore store = Open();
        store.Set("shelf-1", "Ammo", "Main base");
        store.Set("shelf-2", "Food", null);
        store.Save();

        store.Set("shelf-1", "  ", null);
        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);

        Assert.DoesNotContain("shelf-1", FileText());
        Assert.Null(Open().Get("shelf-1"));
        Assert.Equal("Food", Open().Get("shelf-2")?.Label);
    }

    [Fact]
    public void EmptyNameWithGroup_KeepsGroupOnlyEntry_AcrossReload()
    {
        LabelStore store = Open();
        store.Set("shelf-1", string.Empty, "Main base");
        store.Save();

        Assert.Equal(new ShelfLabel(string.Empty, "Main base"), Open().Get("shelf-1"));
    }

    [Fact]
    public void FortyCharName_RoundTripsExactly()
    {
        string name = new('a', 40);
        LabelStore store = Open();
        store.Set("shelf-1", name, name);
        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);

        Assert.Equal(new ShelfLabel(name, name), Open().Get("shelf-1"));
    }

    [Fact]
    public void ReassigningLastShelfOfGroup_GroupDisappearsFromPickerAfterReload()
    {
        LabelStore store = Open();
        store.Set("shelf-1", "Ammo", "Main base");
        store.Save();
        store.Set("shelf-1", "Ammo", "Cave");
        store.Save();

        Assert.Equal(new[] { "Cave" }, Open().ListGroups().ToArray());
    }

    // A pasted line over the 40-char cap is clipped by IMGUI at the limit; if the cut lands inside an emoji the
    // label ends in a lone high surrogate. IsValidName lets it through. Whatever Core does, the file on disk must
    // stay readable.
    [Fact]
    public void LoneSurrogateLabel_NeverLeavesAnUnreadableFile()
    {
        LabelStore store = Open();
        store.Set("shelf-0", "Fine", null);
        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);

        store.Set("shelf-1", new string('a', 39) + "\uD83D", null);
        store.Save();

        LabelLoadResult reloaded = new LabelStore(new JsonLabelFileSystem(_root, new FakeModLog()), _world).Load();
        Assert.Equal(LabelLoadStatus.Loaded, reloaded.Status);
        Assert.True(reloaded.Count >= 1);
    }

    // One unwritable name must not stop every later label from saving for the rest of the session.
    [Fact]
    public void LoneSurrogateLabel_DoesNotPoisonLaterSavesOfOtherShelves()
    {
        LabelStore store = Open();
        store.Set("shelf-1", new string('a', 39) + "\uD83D", null);
        store.Save();

        store.Set("shelf-2", "Food", null);
        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);
        Assert.Equal("Food", Open().Get("shelf-2")?.Label);
    }
}
