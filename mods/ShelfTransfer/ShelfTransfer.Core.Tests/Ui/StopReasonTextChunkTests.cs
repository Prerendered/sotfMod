// purpose: pins the stop-line wording when a chunk stops part-way (card 68): 2..10 items in the backpack, where each part is
// owns:    StopReasonTextChunkTests
// deps:    StopReasonText, TransferReport

using ShelfTransfer.Core.Transfer;
using ShelfTransfer.Core.Ui;

using Xunit;

namespace ShelfTransfer.Core.Tests.Ui;

public sealed class StopReasonTextChunkTests
{
    private static TransferReport Report(int moved, int remaining, int stranded, StopReason reason = StopReason.StoreRefused) =>
        new(reason, moved + remaining + stranded, moved, remaining, stranded, null);

    [Fact]
    public void Format_SevenStrandedThreeMoved_SaysWhereEachPartIs()
    {
        ResultLine line = StopReasonText.Format(Report(moved: 3, remaining: 20, stranded: 7));

        Assert.Equal(
            "Stopped: target full. Moved 3, remaining 20. 7 items are in your inventory, 3 already on the target, the rest is on the source.",
            line.Text);
        Assert.False(line.AutoClose);
    }

    [Fact]
    public void Format_StrandedTwoToTen_WithMovedIncludesAlreadyOnTheTarget()
    {
        for (int stranded = 2; stranded <= 10; stranded++)
        {
            string text = StopReasonText.Format(Report(moved: 4, remaining: 16, stranded: stranded)).Text;

            Assert.Contains($"{stranded} items are in your inventory, 4 already on the target, the rest is on the source", text);
            Assert.DoesNotContain("lost", text, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Format_StrandedWithNothingMoved_OmitsTheTargetClause()
    {
        for (int stranded = 2; stranded <= 10; stranded++)
        {
            string text = StopReasonText.Format(Report(moved: 0, remaining: 20, stranded: stranded, reason: StopReason.InventoryMismatch)).Text;

            Assert.Contains($"{stranded} items are in your inventory, the rest is on the source", text);
            Assert.DoesNotContain("already on the target", text);
        }
    }

    // Unit-1 wording is unchanged (criterion 5)
    [Fact]
    public void Format_StrandedOne_KeepsTheUnitOneSentence()
    {
        string text = StopReasonText.Format(Report(moved: 3, remaining: 6, stranded: 1)).Text;

        Assert.EndsWith(". 1 item is in your inventory, the rest is on the source.", text);
        Assert.DoesNotContain("already on the target", text);
    }

    [Fact]
    public void Format_StrandedWithMovedAndEveryReason_NeverSaysLostAndNeverThrows()
    {
        foreach (StopReason reason in System.Enum.GetValues<StopReason>())
        {
            if (reason == StopReason.Completed)
            {
                continue;
            }

            string text = StopReasonText.Format(Report(moved: 10, remaining: 10, stranded: 10, reason: reason)).Text;
            Assert.DoesNotContain("lost", text, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("10 items are in your inventory, 10 already on the target", text);
        }
    }
}
