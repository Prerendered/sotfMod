// purpose: pins the overlay list builder: grouping, ordering, labels, fit counts, selectability, stale-label report
// owns:    ShelfListBuilderTests
// deps:    ShelfListBuilder, ShelfCandidate, ShelfLabel, ShelfRef, WorldPosition

using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Shelves;

using Xunit;

namespace ShelfTransfer.Core.Tests.Shelves;

public sealed class ShelfListBuilderTests
{
    private const string _sourceKey = "src";
    private static readonly WorldPosition _player = new(0f, 0f, 0f);
    private static readonly IReadOnlyDictionary<string, ShelfLabel> _noLabels = new Dictionary<string, ShelfLabel>();

    private static ShelfCandidate Shelf(string key, float x, float z, int free = 100, bool canHold = true, float y = 0f) =>
        new(new ShelfRef(key, "StorageShelf", new WorldPosition(x, y, z)), free, canHold);

    private static ShelfList Build(
        IReadOnlyList<ShelfCandidate> shelves,
        IReadOnlyDictionary<string, ShelfLabel>? labels = null,
        int sourceCount = 10,
        WorldPosition? player = null) =>
        ShelfListBuilder.Build(shelves, _sourceKey, sourceCount, player ?? _player, labels ?? _noLabels);

    private static List<ShelfRow> AllRows(ShelfList list) => list.Groups.SelectMany(g => g.Rows).ToList();

    [Theory]
    [InlineData(53, 10, 50)]
    [InlineData(53, 1, 53)]
    [InlineData(9, 10, 0)]
    [InlineData(53, 0, 53)] // unreadable unit: the runner clamps it as 1, so must the list
    public void Build_Fits_RoundsDownToWholeChunks(int free, int unit, int expected)
    {
        ShelfList list = ShelfListBuilder.Build(
            new[] { Shelf("a", 1, 0, free: free) }, _sourceKey, 100, _player, _noLabels, unit);

        ShelfRow row = Assert.Single(AllRows(list));
        Assert.Equal(expected, row.Fits);
        Assert.Equal(expected > 0, row.Selectable);
    }

    // ---- criterion 1

    [Fact]
    public void Build_ListsAllShelvesExceptSource()
    {
        ShelfList list = Build(new[] { Shelf(_sourceKey, 1, 1), Shelf("a", 2, 0), Shelf("b", 3, 0) });

        Assert.Equal(new[] { "a", "b" }, AllRows(list).Select(r => r.Shelf.Key));
    }

    [Fact]
    public void Build_SourceNotAmongCandidates_ListsEveryCandidate()
    {
        ShelfList list = Build(new[] { Shelf("a", 2, 0), Shelf("b", 3, 0) });

        Assert.Equal(2, AllRows(list).Count);
    }

    [Fact]
    public void Build_SourceMatchedByOrdinalKey()
    {
        ShelfList list = Build(new[] { Shelf("SRC", 1, 0), Shelf(_sourceKey, 2, 0) });

        Assert.Equal("SRC", Assert.Single(AllRows(list)).Shelf.Key);
    }

    [Fact]
    public void Build_RowCarriesLabelDistanceBearingAndFits()
    {
        var labels = new Dictionary<string, ShelfLabel> { ["a"] = new ShelfLabel("Ammo", null) };

        ShelfRow row = Assert.Single(AllRows(Build(new[] { Shelf("a", 0, 5, free: 4) }, labels, sourceCount: 10)));

        Assert.Equal("Ammo", row.Label);
        Assert.Equal(5f, row.Distance, 3);
        Assert.Equal("N", row.Bearing);
        Assert.Equal(4, row.Fits);
        Assert.Equal("a", row.Shelf.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Build_BlankOrMissingLabel_ShowsDefault(string? stored)
    {
        var labels = new Dictionary<string, ShelfLabel>();
        if (stored != null)
        {
            labels["a"] = new ShelfLabel(stored, null);
        }

        ShelfRow row = Assert.Single(AllRows(Build(new[] { Shelf("a", 1, 0) }, labels)));

        Assert.Equal(ShelfListBuilder.DefaultLabel, row.Label);
        Assert.Equal("Shelf", ShelfListBuilder.DefaultLabel);
    }

    [Fact]
    public void Build_LabelIsTrimmed()
    {
        var labels = new Dictionary<string, ShelfLabel> { ["a"] = new ShelfLabel("  Food  ", null) };

        Assert.Equal("Food", Assert.Single(AllRows(Build(new[] { Shelf("a", 1, 0) }, labels))).Label);
    }

    [Fact]
    public void Build_DistanceIsThreeDimensional()
    {
        ShelfRow row = Assert.Single(AllRows(Build(new[] { Shelf("a", 3, 0, y: 4f) })));

        Assert.Equal(5f, row.Distance, 3);
    }

    [Fact]
    public void Build_HeightChangesDistanceButNotBearing()
    {
        ShelfRow flat = Assert.Single(AllRows(Build(new[] { Shelf("a", 3, 3) })));
        ShelfRow high = Assert.Single(AllRows(Build(new[] { Shelf("a", 3, 3, y: 50f) })));

        Assert.True(high.Distance > flat.Distance);
        Assert.Equal(flat.Bearing, high.Bearing);
        Assert.Equal("NE", high.Bearing);
    }

    [Fact]
    public void Build_DistanceMeasuredFromPlayerPosition()
    {
        ShelfRow row = Assert.Single(AllRows(Build(new[] { Shelf("a", 10, 0) }, player: new WorldPosition(7f, 0f, 0f))));

        Assert.Equal(3f, row.Distance, 3);
        Assert.Equal("E", row.Bearing);
    }

    [Theory]
    [InlineData(10, 4, 4)]
    [InlineData(4, 10, 4)]
    [InlineData(5, 5, 5)]
    [InlineData(10, 1, 1)]
    public void Build_FitsIsMinOfSourceCountAndFree(int sourceCount, int free, int expected)
    {
        ShelfRow row = Assert.Single(AllRows(Build(new[] { Shelf("a", 1, 0, free: free) }, sourceCount: sourceCount)));

        Assert.Equal(expected, row.Fits);
        Assert.True(row.Selectable);
    }

    [Fact]
    public void Build_IntMaxFree_FitsEqualsSourceCount()
    {
        ShelfRow row = Assert.Single(AllRows(Build(new[] { Shelf("a", 1, 0, free: int.MaxValue) }, sourceCount: 7)));

        Assert.Equal(7, row.Fits);
    }

    [Fact]
    public void Build_IntMaxSourceAndFree_FitsIsIntMax()
    {
        ShelfRow row = Assert.Single(AllRows(Build(new[] { Shelf("a", 1, 0, free: int.MaxValue) }, sourceCount: int.MaxValue)));

        Assert.Equal(int.MaxValue, row.Fits);
    }

    [Fact]
    public void Build_DoesNotMutateInputs()
    {
        var shelves = new[] { Shelf("b", 5, 0), Shelf("a", 1, 0) };

        Build(shelves);

        Assert.Equal(new[] { "b", "a" }, shelves.Select(s => s.Shelf.Key));
    }

    // ---- criterion 2

    [Fact]
    public void Build_GroupsSortedByNearestShelf_NearestExpanded()
    {
        var labels = new Dictionary<string, ShelfLabel>
        {
            ["far1"] = new ShelfLabel("f1", "Far"),
            ["far2"] = new ShelfLabel("f2", "Far"),
            ["near"] = new ShelfLabel("n", "Near"),
            ["mid"] = new ShelfLabel("m", "Mid"),
        };
        ShelfList list = Build(new[] { Shelf("far1", 50, 0), Shelf("far2", 2, 0), Shelf("near", 1, 0), Shelf("mid", 10, 0) }, labels);

        Assert.Equal(new[] { "Near", "Far", "Mid" }, list.Groups.Select(g => g.Name));
        Assert.Equal(new[] { true, false, false }, list.Groups.Select(g => g.Expanded));
    }

    [Fact]
    public void Build_GroupOrderUsesNearestMemberNotFirstMember()
    {
        var labels = new Dictionary<string, ShelfLabel>
        {
            ["a"] = new ShelfLabel("x", "A"),
            ["b"] = new ShelfLabel("x", "B"),
            ["b2"] = new ShelfLabel("x", "B"),
        };
        ShelfList list = Build(new[] { Shelf("a", 5, 0), Shelf("b", 20, 0), Shelf("b2", 1, 0) }, labels);

        Assert.Equal("B", list.Groups[0].Name);
        Assert.True(list.Groups[0].Expanded);
    }

    [Fact]
    public void Build_UngroupedShelvesSitUnderOther()
    {
        var labels = new Dictionary<string, ShelfLabel> { ["a"] = new ShelfLabel("x", null) };
        ShelfList list = Build(new[] { Shelf("a", 1, 0), Shelf("b", 2, 0) }, labels);

        ShelfGroup group = Assert.Single(list.Groups);
        Assert.Equal("Other", group.Name);
        Assert.Equal("Other", ShelfListBuilder.OtherGroupName);
        Assert.Equal(2, group.Rows.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Build_BlankGroup_TreatedAsOther(string group)
    {
        var labels = new Dictionary<string, ShelfLabel> { ["a"] = new ShelfLabel("x", group) };

        Assert.Equal("Other", Assert.Single(Build(new[] { Shelf("a", 1, 0) }, labels).Groups).Name);
    }

    [Fact]
    public void Build_GroupNameIsTrimmedAndMergesVariants()
    {
        var labels = new Dictionary<string, ShelfLabel>
        {
            ["a"] = new ShelfLabel("x", " Food "),
            ["b"] = new ShelfLabel("y", "Food"),
        };

        ShelfGroup group = Assert.Single(Build(new[] { Shelf("a", 1, 0), Shelf("b", 2, 0) }, labels).Groups);

        Assert.Equal("Food", group.Name);
        Assert.Equal(2, group.Rows.Count);
    }

    [Fact]
    public void Build_UserGroupNamedOther_MergesWithUngrouped()
    {
        var labels = new Dictionary<string, ShelfLabel> { ["a"] = new ShelfLabel("x", "Other") };

        ShelfGroup group = Assert.Single(Build(new[] { Shelf("a", 1, 0), Shelf("b", 2, 0) }, labels).Groups);

        Assert.Equal(2, group.Rows.Count);
    }

    [Fact]
    public void Build_GroupNamesDifferingOnlyByCase_AreSeparateGroups()
    {
        var labels = new Dictionary<string, ShelfLabel>
        {
            ["a"] = new ShelfLabel("x", "food"),
            ["b"] = new ShelfLabel("y", "Food"),
        };

        ShelfList list = Build(new[] { Shelf("a", 1, 0), Shelf("b", 2, 0) }, labels);

        Assert.Equal(new[] { "food", "Food" }, list.Groups.Select(g => g.Name));
    }

    [Fact]
    public void Build_RowsWithinGroupSortedByDistance()
    {
        ShelfList list = Build(new[] { Shelf("a", 30, 0), Shelf("b", 10, 0), Shelf("c", 20, 0) });

        Assert.Equal(new[] { "b", "c", "a" }, list.Groups[0].Rows.Select(r => r.Shelf.Key));
    }

    [Fact]
    public void Build_EqualDistances_BreakTiesByKeyOrdinal()
    {
        ShelfList list = Build(new[] { Shelf("b", 5, 0), Shelf("B", 0, 5), Shelf("a", -5, 0) });

        Assert.Equal(new[] { "B", "a", "b" }, list.Groups[0].Rows.Select(r => r.Shelf.Key));
    }

    [Fact]
    public void Build_EqualGroupDistances_BreakTiesByNameOrdinal()
    {
        var labels = new Dictionary<string, ShelfLabel>
        {
            ["a"] = new ShelfLabel("x", "Zed"),
            ["b"] = new ShelfLabel("y", "Alpha"),
            ["c"] = new ShelfLabel("z", "alpha"),
        };

        ShelfList list = Build(new[] { Shelf("a", 5, 0), Shelf("b", 0, 5), Shelf("c", -5, 0) }, labels);

        Assert.Equal(new[] { "Alpha", "Zed", "alpha" }, list.Groups.Select(g => g.Name));
        Assert.True(list.Groups[0].Expanded);
    }

    [Fact]
    public void Build_SingleShelf_OneExpandedGroupOneRow()
    {
        ShelfList list = Build(new[] { Shelf("a", 1, 0) });

        ShelfGroup group = Assert.Single(list.Groups);
        Assert.True(group.Expanded);
        Assert.Single(group.Rows);
    }

    [Fact]
    public void Build_NaNPlayerPosition_IsDeterministicWithEmptyBearing()
    {
        var player = new WorldPosition(float.NaN, 0f, 0f);
        var shelves = new[] { Shelf("c", 1, 0), Shelf("a", 2, 0), Shelf("b", 3, 0) };

        ShelfList first = Build(shelves, player: player);
        ShelfList second = Build(shelves.Reverse().ToArray(), player: player);

        Assert.Equal(new[] { "a", "b", "c" }, AllRows(first).Select(r => r.Shelf.Key));
        Assert.Equal(AllRows(first).Select(r => r.Shelf.Key), AllRows(second).Select(r => r.Shelf.Key));
        Assert.All(AllRows(first), r => Assert.Equal(string.Empty, r.Bearing));
    }

    // ---- criterion 3

    [Fact]
    public void Build_CannotHold_NotSelectableAndZeroFits()
    {
        ShelfRow row = Assert.Single(AllRows(Build(new[] { Shelf("a", 1, 0, free: 50, canHold: false) })));

        Assert.False(row.Selectable);
        Assert.Equal(0, row.Fits);
    }

    [Fact]
    public void Build_CannotHoldWithFreeCapacity_StillZero()
    {
        ShelfRow row = Assert.Single(AllRows(Build(new[] { Shelf("a", 1, 0, free: int.MaxValue, canHold: false) }, sourceCount: 99)));

        Assert.Equal(0, row.Fits);
        Assert.False(row.Selectable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Build_FullShelfThatCanHold_ZeroFitsNotSelectable(int free)
    {
        ShelfRow row = Assert.Single(AllRows(Build(new[] { Shelf("a", 1, 0, free: free, canHold: true) })));

        Assert.Equal(0, row.Fits);
        Assert.False(row.Selectable);
    }

    [Fact]
    public void Build_UnselectableRowsStillListed()
    {
        ShelfList list = Build(new[] { Shelf("a", 1, 0, canHold: false), Shelf("b", 2, 0) });

        Assert.Equal(2, AllRows(list).Count);
        Assert.Equal(new[] { false, true }, list.Groups[0].Rows.Select(r => r.Selectable));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_SourceCountNotPositive_AllFitsZeroNoneSelectable(int sourceCount)
    {
        ShelfList list = Build(new[] { Shelf("a", 1, 0, free: 50), Shelf("b", 2, 0, free: 5) }, sourceCount: sourceCount);

        Assert.Equal(2, AllRows(list).Count);
        Assert.All(AllRows(list), r =>
        {
            Assert.Equal(0, r.Fits);
            Assert.False(r.Selectable);
        });
    }

    // ---- criterion 5

    [Fact]
    public void Build_EmptyShelfList_IsEmptyNotNull()
    {
        ShelfList list = Build(new List<ShelfCandidate>());

        Assert.NotNull(list.Groups);
        Assert.NotNull(list.StaleLabelKeys);
        Assert.Empty(list.Groups);
        Assert.True(list.IsEmpty);
    }

    [Fact]
    public void Build_OnlySourceShelf_IsEmpty()
    {
        ShelfList list = Build(new[] { Shelf(_sourceKey, 1, 0) });

        Assert.True(list.IsEmpty);
    }

    [Fact]
    public void Build_NonEmpty_IsEmptyFalse()
    {
        Assert.False(Build(new[] { Shelf("a", 1, 0) }).IsEmpty);
    }

    // ---- criterion 6

    [Fact]
    public void Build_LabelForKeyNoLongerLive_NotShownAndReportedStale()
    {
        var labels = new Dictionary<string, ShelfLabel>
        {
            ["gone"] = new ShelfLabel("Ghost", "GhostGroup"),
            ["a"] = new ShelfLabel("Real", null),
        };

        ShelfList list = Build(new[] { Shelf("a", 1, 0) }, labels);

        Assert.Equal(new[] { "gone" }, list.StaleLabelKeys);
        Assert.DoesNotContain(AllRows(list), r => r.Label == "Ghost");
        Assert.DoesNotContain(list.Groups, g => g.Name == "GhostGroup");
    }

    [Fact]
    public void Build_StaleKeysInOrdinalOrder()
    {
        var labels = new Dictionary<string, ShelfLabel>
        {
            ["b"] = new ShelfLabel("x", null),
            ["B"] = new ShelfLabel("x", null),
            ["a"] = new ShelfLabel("x", null),
        };

        ShelfList list = Build(new List<ShelfCandidate>(), labels);

        Assert.Equal(new[] { "B", "a", "b" }, list.StaleLabelKeys);
    }

    [Fact]
    public void Build_SourceOwnLabel_NotStale()
    {
        var labels = new Dictionary<string, ShelfLabel> { [_sourceKey] = new ShelfLabel("Mine", null) };

        ShelfList list = Build(new[] { Shelf("a", 1, 0) }, labels);

        Assert.Empty(list.StaleLabelKeys);
        Assert.DoesNotContain(AllRows(list), r => r.Label == "Mine");
    }

    [Fact]
    public void Build_SourceOwnLabel_NotStaleWhenSourceAbsentFromCandidates()
    {
        var labels = new Dictionary<string, ShelfLabel> { [_sourceKey] = new ShelfLabel("Mine", null) };

        Assert.Empty(Build(new List<ShelfCandidate>(), labels).StaleLabelKeys);
    }

    [Fact]
    public void Build_AllLabelsLive_NoStaleKeys()
    {
        var labels = new Dictionary<string, ShelfLabel> { ["a"] = new ShelfLabel("x", null) };

        Assert.Empty(Build(new[] { Shelf("a", 1, 0) }, labels).StaleLabelKeys);
    }

    [Fact]
    public void Build_StaleKeyMatchIsOrdinal()
    {
        var labels = new Dictionary<string, ShelfLabel> { ["A"] = new ShelfLabel("x", null) };

        ShelfList list = Build(new[] { Shelf("a", 1, 0) }, labels);

        Assert.Equal(new[] { "A" }, list.StaleLabelKeys);
        Assert.Equal("Shelf", AllRows(list)[0].Label);
    }

    [Fact]
    public void Build_NeverMutatesLabels()
    {
        var labels = new Dictionary<string, ShelfLabel> { ["gone"] = new ShelfLabel("x", null) };

        Build(new List<ShelfCandidate>(), labels);

        Assert.Single(labels);
    }
}
