// purpose: card 80 criterion 1: the overlay's rows and the typed prompt's list come from one scan, so they agree on shelves and fits
// owns:    ShelfTargetScanParityTests
// deps:    ShelfTargetScan, ShelfListBuilder, ShelfLabel, FakeShelfGateway
// caution: mirrors the two plugin call sites: the prompt builds with no labels, the overlay with labels. Only the label
//          text and the grouping may differ; keys, fits, distance and bearing must not

using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Shelves;

public sealed class ShelfTargetScanParityTests
{
    private static readonly ItemId _item = new(7);
    private static readonly WorldPosition _player = new(0f, 0f, 0f);

    private static ShelfRef Ref(string key, float x, float z, string type = "ShelfStructure") => new(key, type, new WorldPosition(x, 0f, z));

    private static (List<ShelfRow> Prompt, List<ShelfRow> Overlay) Both(int unit, int sourceCount, IReadOnlyDictionary<string, ShelfLabel> labels)
    {
        FakeShelfGateway gateway = new() { TransferUnit = unit };
        ShelfRef src = Ref("src", 0, 1);
        ShelfRef[] registry =
        {
            src, Ref("near", 0, 3), Ref("mann", 0, 2, "Mannequin"), Ref("far", 0, 9), Ref("full", 5, 0), Ref("no", 6, 0),
            Ref("rack", 1, 1, "StorageArrows"),
        };
        gateway.Add(src, sourceCount);
        gateway.Add(registry[1], 0).Capacity = 53;
        gateway.Add(registry[3], 0).Capacity = 4;
        gateway.Add(registry[4], 10).Capacity = 10;
        gateway.Add(registry[5], 0).CanHold = false;

        IReadOnlyList<ShelfCandidate> promptScan = ShelfTargetScan.ReadCandidates(gateway, registry, _item);
        ShelfList prompt = ShelfListBuilder.Build(promptScan, src.Key, sourceCount, _player, new Dictionary<string, ShelfLabel>(), unit);
        IReadOnlyList<ShelfCandidate> overlayScan = ShelfTargetScan.ReadCandidates(gateway, registry, _item);
        ShelfList overlay = ShelfListBuilder.Build(overlayScan, src.Key, sourceCount, _player, labels, unit);
        return (prompt.Groups.SelectMany(g => g.Rows).ToList(), overlay.Groups.SelectMany(g => g.Rows).ToList());
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(10, 100)]
    [InlineData(10, 95)]
    public void PromptListAndOverlayRows_AgreeOnShelvesAndFits(int unit, int sourceCount)
    {
        var labels = new Dictionary<string, ShelfLabel>
        {
            ["near"] = new ShelfLabel("Ammo", "Base A"),
            ["far"] = new ShelfLabel("Spare", null),
        };

        (List<ShelfRow> prompt, List<ShelfRow> overlay) = Both(unit, sourceCount, labels);

        Assert.Equal(prompt.Select(r => r.Shelf.Key).OrderBy(k => k), overlay.Select(r => r.Shelf.Key).OrderBy(k => k));
        foreach (ShelfRow p in prompt)
        {
            ShelfRow o = overlay.Single(r => r.Shelf.Key == p.Shelf.Key);
            Assert.Equal((p.Fits, p.Selectable, p.Distance, p.Bearing), (o.Fits, o.Selectable, o.Distance, o.Bearing));
        }

        Assert.DoesNotContain(overlay, r => r.Shelf.Key is "mann" or "rack" or "src");
    }

    [Fact]
    public void ChunkRounding_AppliesToBothLists_Unit10()
    {
        (List<ShelfRow> prompt, List<ShelfRow> overlay) = Both(10, 100, new Dictionary<string, ShelfLabel>());

        Assert.Equal(50, prompt.Single(r => r.Shelf.Key == "near").Fits);
        Assert.Equal(50, overlay.Single(r => r.Shelf.Key == "near").Fits);
        Assert.Equal(0, overlay.Single(r => r.Shelf.Key == "far").Fits);
    }

    [Fact]
    public void OnlyTheLabelTextDiffers_WhenLabelsAreStored()
    {
        var labels = new Dictionary<string, ShelfLabel> { ["near"] = new ShelfLabel("Ammo", "Base A") };

        (List<ShelfRow> prompt, List<ShelfRow> overlay) = Both(1, 100, labels);

        Assert.Equal("Shelf", prompt.Single(r => r.Shelf.Key == "near").Label);
        Assert.Equal("Ammo", overlay.Single(r => r.Shelf.Key == "near").Label);
    }
}
