// purpose: pins the player-facing stop-reason wording: a line for every StopReason, never "lost" (D4)
// owns:    StopReasonTextTests
// deps:    StopReasonText, ResultLine, TransferReport, StopReason

using System;
using System.Globalization;
using System.Linq;

using ShelfTransfer.Core.Transfer;
using ShelfTransfer.Core.Ui;

using Xunit;

namespace ShelfTransfer.Core.Tests.Ui;

public sealed class StopReasonTextTests
{
    private static TransferReport Report(StopReason reason, int moved = 3, int remaining = 2, int stranded = 0) =>
        new(reason, moved + remaining, moved, remaining, stranded, null);

    // ---- criterion 6

    [Fact]
    public void Format_Completed_IsMovedRemaining_AutoCloseTrue()
    {
        ResultLine line = StopReasonText.Format(Report(StopReason.Completed, moved: 6, remaining: 0));

        Assert.Equal("Moved 6, remaining 0", line.Text);
        Assert.True(line.AutoClose);
    }

    [Fact]
    public void Format_Completed_IgnoresStrandedCount()
    {
        ResultLine line = StopReasonText.Format(Report(StopReason.Completed, moved: 4, remaining: 1, stranded: 0));

        Assert.Equal("Moved 4, remaining 1", line.Text);
    }

    [Fact]
    public void Format_EveryNonCompletedReason_AutoCloseFalse_AndHasReasonAndCounts()
    {
        foreach (StopReason reason in Enum.GetValues<StopReason>().Where(r => r != StopReason.Completed))
        {
            ResultLine line = StopReasonText.Format(Report(reason, moved: 3, remaining: 2));

            Assert.False(line.AutoClose);
            Assert.StartsWith(StopReasonText.StoppedPrefix, line.Text, StringComparison.Ordinal);
            Assert.Contains(StopReasonText.GetReasonText(reason), line.Text, StringComparison.Ordinal);
            Assert.Contains("Moved 3, remaining 2", line.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GetReasonText_EveryStopReason_IsNonEmpty_AndNeverSaysLost()
    {
        foreach (StopReason reason in Enum.GetValues<StopReason>())
        {
            string text = StopReasonText.GetReasonText(reason);

            Assert.False(string.IsNullOrWhiteSpace(text), reason.ToString());
            Assert.DoesNotContain("lost", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Format_EveryStopReason_AndStrandedCount_NeverSaysLost_AndIsNonEmpty()
    {
        foreach (StopReason reason in Enum.GetValues<StopReason>())
        {
            foreach (int stranded in new[] { 0, 1, 2, 5 })
            {
                ResultLine line = StopReasonText.Format(Report(reason, stranded: stranded));

                Assert.False(string.IsNullOrWhiteSpace(line.Text), $"{reason}/{stranded}");
                Assert.DoesNotContain("lost", line.Text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void GetReasonText_EveryReason_IsDistinct()
    {
        StopReason[] reasons = Enum.GetValues<StopReason>();

        Assert.Equal(reasons.Length, reasons.Select(StopReasonText.GetReasonText).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void GetReasonText_Pins_TheKnownPhrases()
    {
        Assert.Equal("target full", StopReasonText.GetReasonText(StopReason.StoreRefused));
        Assert.Equal("cancelled", StopReasonText.GetReasonText(StopReason.Cancelled));
        Assert.Equal("completed", StopReasonText.GetReasonText(StopReason.Completed));
    }

    [Fact]
    public void Format_StrandedOne_SaysInInventory_RestOnSource_Literal()
    {
        ResultLine line = StopReasonText.Format(Report(StopReason.StoreRefused, moved: 3, remaining: 6, stranded: 1));

        Assert.Equal(
            "Stopped: target full. Moved 3, remaining 6. 1 item is in your inventory, the rest is on the source.",
            line.Text);
        Assert.Contains("1 item is in your inventory, the rest is on the source", line.Text, StringComparison.Ordinal);
        Assert.Equal("1 item is in your inventory, the rest is on the source", StopReasonText.StrandedOne);
        Assert.DoesNotContain("lost", line.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(line.AutoClose);
    }

    [Fact]
    public void Format_StrandedZero_SaysNoItemLeftInInventory()
    {
        ResultLine line = StopReasonText.Format(Report(StopReason.Cancelled, moved: 3, remaining: 2, stranded: 0));

        Assert.Equal("Stopped: cancelled. Moved 3, remaining 2, no item left in your inventory.", line.Text);
        Assert.Equal("no item left in your inventory", StopReasonText.StrandedNone);
    }

    [Fact]
    public void Format_StrandedMany_IsPlural_NeverLost()
    {
        ResultLine line = StopReasonText.Format(Report(StopReason.InventoryMismatch, moved: 1, remaining: 1, stranded: 2));

        Assert.Contains("2 items are in your inventory", line.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("lost", line.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_NegativeStranded_TreatedAsNone()
    {
        ResultLine line = StopReasonText.Format(Report(StopReason.Cancelled, stranded: -1));

        Assert.EndsWith(", no item left in your inventory.", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_UsesTheReportsNumbers_WithoutRederiving()
    {
        // Requested/Moved/Remaining deliberately inconsistent: the text must echo Moved and Remaining only.
        var report = new TransferReport(StopReason.Cancelled, 99, 7, 11, 0, "detail");

        Assert.Contains("Moved 7, remaining 11", StopReasonText.Format(report).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ZeroMoved_Completed_IsLiteral()
    {
        Assert.Equal("Moved 0, remaining 0", StopReasonText.Format(Report(StopReason.Completed, moved: 0, remaining: 0)).Text);
    }

    [Fact]
    public void Format_IsCultureInvariant_NoGroupSeparators()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal(
                "Moved 1234567, remaining 8901",
                StopReasonText.Format(Report(StopReason.Completed, moved: 1234567, remaining: 8901)).Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Format_OutputIsAscii()
    {
        foreach (StopReason reason in Enum.GetValues<StopReason>())
        {
            Assert.All(StopReasonText.Format(Report(reason, stranded: 1)).Text, c => Assert.True(c < 128));
        }
    }
}
