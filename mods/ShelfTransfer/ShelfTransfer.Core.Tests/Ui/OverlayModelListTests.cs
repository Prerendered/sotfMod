// purpose: overlay list behaviour of OverlayModel for card 21: group expand/collapse, ExpandGroupOf, selection across refreshes, I4
// owns:    OverlayModelListTests
// deps:    OverlayModel, ShelfListBuilder, ShelfList, ShelfGroup, ShelfRow, AimTarget
// caution: builds real lists with ShelfListBuilder so defaults (nearest expanded, "Other") are the production ones

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Core.Ui;

using Xunit;

namespace ShelfTransfer.Core.Tests.Ui;

public sealed class OverlayModelListTests
{
    private static readonly ShelfRef _src = new("src", "ShelfStructure", new WorldPosition(0f, 0f, 0f));
    private static readonly AimTarget _aim = new(_src, new SlotRef(0), new ItemId(1), 10);

    private static readonly Dictionary<string, ShelfLabel> _twoGroups = new()
    {
        ["a"] = new ShelfLabel("A", "Home"),
        ["b"] = new ShelfLabel("B", "Camp"),
    };

    private static ShelfCandidate Cand(string key, float z, int free = 100, bool canHold = true) =>
        new(new ShelfRef(key, "ShelfStructure", new WorldPosition(0f, 0f, z)), free, canHold);

    private static ShelfList Build(IReadOnlyList<ShelfCandidate> candidates, Dictionary<string, ShelfLabel>? labels = null) =>
        ShelfListBuilder.Build(candidates, "src", 10, new WorldPosition(0f, 0f, 0f), labels ?? new Dictionary<string, ShelfLabel>());

    private static ShelfCandidate[] Three() => new[] { Cand("a", 1), Cand("b", 5), Cand("c", 9) };

    private static OverlayModel Ready(ShelfList list)
    {
        OverlayModel model = new(_aim);
        model.OnShelfList(list);
        return model;
    }

    private static ShelfGroup G(OverlayModel model, string name) => model.Groups.Single(g => g.Name == name);

    [Fact]
    public void Defaults_NearestExpanded_OthersCollapsed_UnassignedUnderOther_SourceExcluded()
    {
        OverlayModel m = Ready(Build(new[] { Cand("src", 0), Cand("a", 1), Cand("b", 5), Cand("c", 9) }, _twoGroups));

        Assert.Equal(new[] { "Home", "Camp", "Other" }, m.Groups.Select(g => g.Name));
        Assert.Equal(new[] { true, false, false }, m.Groups.Select(g => g.Expanded));
        Assert.DoesNotContain(m.Groups.SelectMany(g => g.Rows), r => r.Shelf.Key == "src");
        Assert.Equal("c", Assert.Single(G(m, "Other").Rows).Shelf.Key);
    }

    [Fact]
    public void ExpandGroupOf_CollapsedGroup_Expands()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));

        m.ExpandGroupOf("b");

        Assert.True(G(m, "Camp").Expanded);
        Assert.True(G(m, "Home").Expanded);
    }

    [Fact]
    public void ExpandGroupOf_AbsentRow_IsNoOp()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        IReadOnlyList<ShelfGroup> before = m.Groups;

        m.ExpandGroupOf("nope");

        Assert.Same(before, m.Groups);
    }

    [Fact]
    public void ExpandGroupOf_AlreadyExpanded_DoesNotRebuild()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        IReadOnlyList<ShelfGroup> before = m.Groups;

        m.ExpandGroupOf("a");

        Assert.Same(before, m.Groups);
    }

    [Fact]
    public void ExpandGroupOf_UserCollapsedGroup_ReopensIt()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        m.ToggleGroup("Home");
        Assert.False(G(m, "Home").Expanded);

        m.ExpandGroupOf("a");

        Assert.True(G(m, "Home").Expanded);
    }

    [Fact]
    public void ExpandGroupOf_Loading_IsNoOp()
    {
        OverlayModel m = new(_aim);

        m.ExpandGroupOf("a");

        Assert.Empty(m.Groups);
        Assert.Equal(OverlayState.Loading, m.State);
    }

    [Fact]
    public void ExpandGroupOf_Running_IsNoOp()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        Assert.True(m.SelectRow("a"));
        Assert.NotNull(m.Confirm());
        IReadOnlyList<ShelfGroup> before = m.Groups;

        m.ExpandGroupOf("b");

        Assert.Equal(OverlayState.Running, m.State);
        Assert.Same(before, m.Groups);
        Assert.False(G(m, "Camp").Expanded);
    }

    [Fact]
    public void ExpandGroupOf_Closed_IsNoOp()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        m.Escape();
        IReadOnlyList<ShelfGroup> before = m.Groups;

        m.ExpandGroupOf("b");

        Assert.Equal(OverlayState.Closed, m.State);
        Assert.Same(before, m.Groups);
    }

    [Fact]
    public void ToggleGroup_UnknownGroup_IsNoOp()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        IReadOnlyList<ShelfGroup> before = m.Groups;

        m.ToggleGroup("Nope");

        Assert.Same(before, m.Groups);
    }

    [Fact]
    public void Toggle_SurvivesRefresh_AndAVanishedGroupForgetsItsToggle()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        m.ToggleGroup("Camp");
        m.OnShelfList(Build(Three(), _twoGroups));
        Assert.True(G(m, "Camp").Expanded);

        var merged = new Dictionary<string, ShelfLabel> { ["a"] = new ShelfLabel("A", "Home"), ["b"] = new ShelfLabel("B", "Home") };
        m.OnShelfList(Build(Three(), merged));
        m.OnShelfList(Build(Three(), _twoGroups));

        Assert.False(G(m, "Camp").Expanded);
    }

    [Fact]
    public void Selection_KeptAcrossRefresh_WhileStillSelectable()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        Assert.True(m.SelectRow("b"));

        m.OnShelfList(Build(Three(), _twoGroups));

        Assert.Equal("b", m.SelectedRow?.Shelf.Key);
        Assert.True(m.ConfirmEnabled);
    }

    [Fact]
    public void Selection_ClearedWhenRowBecomesDimmed()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        Assert.True(m.SelectRow("b"));

        m.OnShelfList(Build(new[] { Cand("a", 1), Cand("b", 5, canHold: false), Cand("c", 9) }, _twoGroups));

        Assert.Null(m.SelectedRow);
        Assert.Equal(0, m.Amount);
        Assert.Equal(OverlayModel.SelectTargetReason, m.ConfirmDisabledReason);
        Assert.Null(m.Confirm());
    }

    [Fact]
    public void Selection_ClearedWhenRowDisappears()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        Assert.True(m.SelectRow("c"));

        m.OnShelfList(Build(new[] { Cand("a", 1), Cand("b", 5) }, _twoGroups));

        Assert.Null(m.SelectedRow);
    }

    [Fact]
    public void Selection_KeptAndFitsUpdated_WhenFreeSpaceChanges()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        Assert.True(m.SelectRow("a"));

        m.OnShelfList(Build(new[] { Cand("a", 1, free: 4), Cand("b", 5), Cand("c", 9) }, _twoGroups));

        Assert.Equal(4, m.SelectedRow?.Fits);
        Assert.Equal(4, m.Amount);
    }

    [Fact]
    public void Selection_KeptWhenRowRegrouped_AndItsGroupCanBeExpanded()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));
        Assert.True(m.SelectRow("a"));
        var moved = new Dictionary<string, ShelfLabel> { ["a"] = new ShelfLabel("A", "Camp"), ["b"] = new ShelfLabel("B", "Home") };

        m.OnShelfList(Build(Three(), moved));
        m.ExpandGroupOf("a");

        Assert.Equal("a", m.SelectedRow?.Shelf.Key);
        Assert.True(G(m, "Camp").Expanded);
    }

    [Fact]
    public void DimmedRow_NeverSelectable_AndRefusalKeepsTheSelection()
    {
        OverlayModel m = Ready(Build(new[] { Cand("a", 1, canHold: false), Cand("b", 5) }));
        Assert.True(m.SelectRow("b"));

        Assert.False(m.SelectRow("a"));
        Assert.Equal("b", m.SelectedRow?.Shelf.Key);
    }

    [Fact]
    public void RowMarkedUnselectable_EvenWithFits_IsRefused()
    {
        var row = new ShelfRow(new ShelfRef("x", "ShelfStructure", default), "x", 1f, "N", 5, false);
        OverlayModel m = Ready(new ShelfList(new[] { new ShelfGroup("g", true, new[] { row }) }, Array.Empty<string>()));

        Assert.False(m.SelectRow("x"));
        Assert.Null(m.Confirm());
    }

    [Fact]
    public void AllDimmed_ShowsNoTargetText_AndCannotConfirm()
    {
        OverlayModel m = Ready(Build(new[] { Cand("a", 1, canHold: false), Cand("b", 5, free: 0) }));

        Assert.Equal(OverlayModel.NoTargetText, m.StatusText);
        Assert.False(m.ConfirmEnabled);
        Assert.False(m.SelectRow("a"));
        Assert.False(m.SelectRow("b"));
    }

    [Fact]
    public void SelectRow_UnknownKeyOrSource_Refused()
    {
        OverlayModel m = Ready(Build(new[] { Cand("src", 0), Cand("a", 1) }));

        Assert.False(m.SelectRow("src"));
        Assert.False(m.SelectRow(""));
        Assert.False(m.SelectRow("zzz"));
    }

    [Fact]
    public void SelectRow_InACollapsedGroup_Succeeds_SoTheRendererMustGateHiddenRows()
    {
        OverlayModel m = Ready(Build(Three(), _twoGroups));

        Assert.False(G(m, "Camp").Expanded);
        Assert.True(m.SelectRow("b"));
    }
}
