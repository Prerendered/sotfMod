// purpose: pins the per-session log byte budget (card 32 criterion 2, F4 log.cap)
// owns:    LogSizeCapTests
// deps:    LogSizeCap

using System;

using ShelfTransfer.Core.Logging;

using Xunit;

namespace ShelfTransfer.Core.Tests.Logging;

public sealed class LogSizeCapTests
{
    [Fact]
    public void MaxSessionBytes_IsTenMebibytes()
    {
        Assert.Equal(10_485_760L, LogSizeCap.MaxSessionBytes);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Constructor_NonPositiveCap_Throws(long cap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogSizeCap(cap));
    }

    [Fact]
    public void Constructor_CapOfOne_Accepted()
    {
        LogSizeCap cap = new(1);

        Assert.Equal(LogSizeCap.Admission.Write, cap.Admit(1));
    }

    [Fact]
    public void Admit_BelowCap_WritesAndCounts()
    {
        LogSizeCap cap = new(100);

        Assert.Equal(LogSizeCap.Admission.Write, cap.Admit(40));
        Assert.Equal(LogSizeCap.Admission.Write, cap.Admit(40));
        Assert.Equal(80, cap.WrittenBytes);
    }

    [Fact]
    public void Admit_ExactlyReachingCap_IsWrite_ThenNextOverflowGivesNotice()
    {
        LogSizeCap cap = new(10);

        Assert.Equal(LogSizeCap.Admission.Write, cap.Admit(10));
        Assert.Equal(10, cap.WrittenBytes);
        Assert.Equal(LogSizeCap.Admission.WriteCapNotice, cap.Admit(1));
    }

    [Fact]
    public void Admit_OneByteOverCap_GivesNoticeNotWrite()
    {
        LogSizeCap cap = new(10);

        Assert.Equal(LogSizeCap.Admission.Write, cap.Admit(9));
        Assert.Equal(LogSizeCap.Admission.WriteCapNotice, cap.Admit(2));
    }

    [Fact]
    public void Admit_NoticeIsNotCounted_AndOverflowEventIsNot()
    {
        LogSizeCap cap = new(10);
        cap.Admit(6);

        cap.Admit(5);

        Assert.Equal(6, cap.WrittenBytes);
    }

    [Fact]
    public void Admit_AfterNotice_EverythingDropsEvenIfItWouldFit()
    {
        LogSizeCap cap = new(10);
        cap.Admit(6);
        cap.Admit(5);

        Assert.Equal(LogSizeCap.Admission.Drop, cap.Admit(1));
        Assert.Equal(LogSizeCap.Admission.Drop, cap.Admit(0));
        Assert.Equal(LogSizeCap.Admission.Drop, cap.Admit(100));
        Assert.Equal(6, cap.WrittenBytes);
    }

    [Fact]
    public void Admit_NoticeReturnedExactlyOnce()
    {
        LogSizeCap cap = new(1);
        int notices = 0;

        for (int i = 0; i < 50; i++)
        {
            if (cap.Admit(5) == LogSizeCap.Admission.WriteCapNotice)
            {
                notices++;
            }
        }

        Assert.Equal(1, notices);
    }

    [Fact]
    public void Admit_ZeroBytesWhenExactlyFull_StillWrites()
    {
        LogSizeCap cap = new(10);
        cap.Admit(10);

        Assert.Equal(LogSizeCap.Admission.Write, cap.Admit(0));
    }

    [Fact]
    public void Admit_FirstEventLargerThanCap_NoticeAndNothingCounted()
    {
        LogSizeCap cap = new(10);

        Assert.Equal(LogSizeCap.Admission.WriteCapNotice, cap.Admit(11));
        Assert.Equal(0, cap.WrittenBytes);
        Assert.Equal(LogSizeCap.Admission.Drop, cap.Admit(1));
    }

    [Fact]
    public void Admit_LongMaxValue_DoesNotOverflow()
    {
        LogSizeCap cap = new(LogSizeCap.MaxSessionBytes);
        cap.Admit(LogSizeCap.MaxSessionBytes - 1);

        Assert.Equal(LogSizeCap.Admission.WriteCapNotice, cap.Admit(long.MaxValue));
        Assert.Equal(LogSizeCap.MaxSessionBytes - 1, cap.WrittenBytes);
    }

    [Fact]
    public void Admit_LongMaxCapAndLongMaxCount_FitsThenOverflows()
    {
        LogSizeCap cap = new(long.MaxValue);

        Assert.Equal(LogSizeCap.Admission.Write, cap.Admit(long.MaxValue));
        Assert.Equal(LogSizeCap.Admission.WriteCapNotice, cap.Admit(1));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Admit_NegativeCount_Throws_AndLeavesStateUntouched(long count)
    {
        LogSizeCap cap = new(10);
        cap.Admit(3);

        Assert.Throws<ArgumentOutOfRangeException>(() => cap.Admit(count));
        Assert.Equal(3, cap.WrittenBytes);
        Assert.Equal(LogSizeCap.Admission.Write, cap.Admit(7));
    }

    [Fact]
    public void Admit_NegativeCountAfterCapped_StillThrows()
    {
        LogSizeCap cap = new(1);
        cap.Admit(5);

        Assert.Throws<ArgumentOutOfRangeException>(() => cap.Admit(-1));
    }
}
