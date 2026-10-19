// purpose: pins the transfer target parser: first-word split, index detection, and case-insensitive whole-label lookup
// owns:    TargetQueryTests
// deps:    TargetQuery, ShelfRow, ShelfRef, ShelfLabel

using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Shelves;

using Xunit;

namespace ShelfTransfer.Core.Tests.Shelves;

public sealed class TargetQueryTests
{
    private static ShelfRow Row(string key) =>
        new(new ShelfRef(key, "StorageShelf", new WorldPosition(0f, 0f, 0f)), "", 1f, "N", 5, true);

    private static List<ShelfRow> Rows(params string[] keys) => keys.Select(Row).ToList();

    private static Dictionary<string, ShelfLabel> Labels(params (string Key, string Label, string? Group)[] entries)
    {
        Dictionary<string, ShelfLabel> labels = new();
        foreach ((string key, string label, string? group) in entries)
        {
            labels[key] = new ShelfLabel(label, group);
        }

        return labels;
    }

    // ---- SplitFirstWord

    [Fact]
    public void SplitFirstWord_CountAndTarget()
    {
        Assert.Equal(("3", "2"), TargetQuery.SplitFirstWord("3 2"));
    }

    [Fact]
    public void SplitFirstWord_NumericLabelRemainderKeepsSpaces()
    {
        Assert.Equal(("3", "1 2"), TargetQuery.SplitFirstWord("3 1 2"));
    }

    [Fact]
    public void SplitFirstWord_KeepsInnerWhitespaceAsTyped()
    {
        Assert.Equal(("5", "Fish  Rack"), TargetQuery.SplitFirstWord("5 Fish  Rack"));
    }

    [Fact]
    public void SplitFirstWord_TrimsBothEnds()
    {
        Assert.Equal(("5", "Fish Rack"), TargetQuery.SplitFirstWord("   5   Fish Rack   "));
    }

    [Fact]
    public void SplitFirstWord_SingleWord_EmptyRemainder()
    {
        Assert.Equal(("list", ""), TargetQuery.SplitFirstWord("list"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void SplitFirstWord_BlankInput_BothEmpty(string text)
    {
        Assert.Equal(("", ""), TargetQuery.SplitFirstWord(text));
    }

    [Fact]
    public void SplitFirstWord_TabSeparator()
    {
        Assert.Equal(("2", "Fish"), TargetQuery.SplitFirstWord("2\tFish"));
    }

    // ---- IsIndex

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", true)]
    [InlineData("007", true)]
    [InlineData("99999999999999999999", true)]
    [InlineData("", false)]
    [InlineData("1a", false)]
    [InlineData("a1", false)]
    [InlineData("1 2", false)]
    [InlineData("-1", false)]
    [InlineData("+1", false)]
    [InlineData("1.5", false)]
    [InlineData("Fish", false)]
    public void IsIndex_AsciiDigitsOnly(string target, bool expected)
    {
        Assert.Equal(expected, TargetQuery.IsIndex(target));
    }

    [Fact]
    public void IsIndex_NonAsciiDigits_AreNotAnIndex()
    {
        // Arabic-Indic and fullwidth digits are char.IsDigit but must be read as labels.
        Assert.False(TargetQuery.IsIndex("٣"));
        Assert.False(TargetQuery.IsIndex("１"));
    }

    // ---- FindByLabel

    [Fact]
    public void FindByLabel_ReturnsOneBasedPositions()
    {
        List<ShelfRow> rows = Rows("a", "b", "c");

        IReadOnlyList<int> found = TargetQuery.FindByLabel(rows, Labels(("b", "Fish", null)), "Fish");

        Assert.Equal(new[] { 2 }, found);
    }

    [Theory]
    [InlineData("fish")]
    [InlineData("FISH")]
    [InlineData("fIsH")]
    public void FindByLabel_CaseInsensitive(string query)
    {
        Assert.Equal(new[] { 1 }, TargetQuery.FindByLabel(Rows("a"), Labels(("a", "Fish", null)), query));
    }

    [Fact]
    public void FindByLabel_TrimsQueryAndStoredLabel()
    {
        Assert.Equal(new[] { 1 }, TargetQuery.FindByLabel(Rows("a"), Labels(("a", " Fish ", null)), "  Fish\t"));
    }

    [Fact]
    public void FindByLabel_DoubleSpaceLabel_DoesNotMatchSingleSpaceQuery()
    {
        Dictionary<string, ShelfLabel> labels = Labels(("a", "Fish  Rack", null));

        Assert.Empty(TargetQuery.FindByLabel(Rows("a"), labels, "Fish Rack"));
        Assert.Equal(new[] { 1 }, TargetQuery.FindByLabel(Rows("a"), labels, "Fish  Rack"));
    }

    [Fact]
    public void FindByLabel_SingleSpaceLabel_DoesNotMatchDoubleSpaceQuery()
    {
        Assert.Empty(TargetQuery.FindByLabel(Rows("a"), Labels(("a", "Fish Rack", null)), "Fish  Rack"));
    }

    [Fact]
    public void FindByLabel_PartialLabel_DoesNotMatch()
    {
        Assert.Empty(TargetQuery.FindByLabel(Rows("a"), Labels(("a", "Fish Rack", null)), "Fish"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void FindByLabel_EmptyQuery_MatchesNothing(string query)
    {
        Assert.Empty(TargetQuery.FindByLabel(Rows("a", "b"), Labels(("a", "", "Food"), ("b", "  ", null)), query));
    }

    [Fact]
    public void FindByLabel_GroupOnlyEntry_NeverMatches()
    {
        // A group-only entry stores an empty label; neither its group nor its type name is a label.
        Dictionary<string, ShelfLabel> labels = Labels(("a", "", "Food"));

        Assert.Empty(TargetQuery.FindByLabel(Rows("a"), labels, "Food"));
        Assert.Empty(TargetQuery.FindByLabel(Rows("a"), labels, "StorageShelf"));
    }

    [Fact]
    public void FindByLabel_GroupNameIsNotMatched()
    {
        Assert.Empty(TargetQuery.FindByLabel(Rows("a"), Labels(("a", "Fish", "Food")), "Food"));
    }

    [Fact]
    public void FindByLabel_Multiple_ReturnsAllInRowOrder()
    {
        List<ShelfRow> rows = Rows("a", "b", "c", "d");
        Dictionary<string, ShelfLabel> labels = Labels(("a", "Fish", null), ("c", "FISH", "Food"), ("d", "Meat", null));

        Assert.Equal(new[] { 1, 3 }, TargetQuery.FindByLabel(rows, labels, "fish"));
    }

    [Fact]
    public void FindByLabel_NoMatch_Empty()
    {
        Assert.Empty(TargetQuery.FindByLabel(Rows("a"), Labels(("a", "Fish", null)), "Meat"));
    }

    [Fact]
    public void FindByLabel_UnlabelledRows_Skipped()
    {
        Assert.Empty(TargetQuery.FindByLabel(Rows("a", "b"), Labels(), "Fish"));
    }

    [Fact]
    public void FindByLabel_LabelOfShelfNotInRows_NotMatched()
    {
        // The source shelf is left out of rows by the scan, so its label can never be a target.
        Dictionary<string, ShelfLabel> labels = Labels(("src", "Fish", null));

        Assert.Empty(TargetQuery.FindByLabel(Rows("a"), labels, "Fish"));
    }

    [Fact]
    public void FindByLabel_KeysAreOrdinal()
    {
        Assert.Empty(TargetQuery.FindByLabel(Rows("A"), Labels(("a", "Fish", null)), "Fish"));
    }

    [Fact]
    public void FindByLabel_EmptyRows_Empty()
    {
        Assert.Empty(TargetQuery.FindByLabel(Rows(), Labels(("a", "Fish", null)), "Fish"));
    }

    [Fact]
    public void FindByLabel_NumericLabelWithSpace_MatchesWhole()
    {
        // `3 1 2` splits to count 3, target "1 2": not all digits, so a label lookup for "1 2".
        (string count, string target) = TargetQuery.SplitFirstWord("3 1 2");
        Assert.Equal("3", count);
        Assert.False(TargetQuery.IsIndex(target));

        Assert.Equal(new[] { 1 }, TargetQuery.FindByLabel(Rows("a"), Labels(("a", "1 2", null)), target));
    }

    [Fact]
    public void IsIndex_PureDigitTarget_ClaimedAsIndexEvenWhenAShelfCarriesThatLabel()
    {
        // A shelf labelled "2" is never resolved by label, because IsIndex claims "2" first (documented behaviour).
        Assert.True(TargetQuery.IsIndex("2"));
    }

    [Theory]
    [InlineData("Käse", "KÄSE")]
    [InlineData("Ölfass", "ölfass")]
    [InlineData("魚棚", "魚棚")]
    [InlineData("Σίσυφος", "ΣΊΣΥΦΟΣ")]
    public void FindByLabel_UnicodeLabels(string stored, string query)
    {
        Assert.Equal(new[] { 1 }, TargetQuery.FindByLabel(Rows("a"), Labels(("a", stored, null)), query));
    }

    [Fact]
    public void FindByLabel_UnicodeNotNormalised_PrecomposedVsDecomposedDiffer()
    {
        // Ordinal comparison: "e" + combining acute is not "é". Pinned, not endorsed.
        Assert.Empty(TargetQuery.FindByLabel(Rows("a"), Labels(("a", "café", null)), "café"));
    }

    [Fact]
    public void FindByLabel_DoesNotMutateLabels()
    {
        Dictionary<string, ShelfLabel> labels = Labels(("a", " Fish ", "Food"));

        TargetQuery.FindByLabel(Rows("a"), labels, "fish");

        Assert.Equal(new ShelfLabel(" Fish ", "Food"), labels["a"]);
        Assert.Single(labels);
    }
}
