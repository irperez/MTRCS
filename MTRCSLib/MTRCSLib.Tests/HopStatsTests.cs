using System.Net;
using MTRCSLib;

namespace MTRCSLib.Tests;

public class HopStatsTests
{
    private static readonly IPAddress LoopbackV4 = IPAddress.Loopback;

    // ── initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void WhenCreated_SentIsZero()
    {
        HopStats stats = HopStats.Create();
        Assert.Equal(0, stats.Sent);
    }

    [Fact]
    public void WhenCreated_LostIsZero()
    {
        HopStats stats = HopStats.Create();
        Assert.Equal(0, stats.Lost);
    }

    [Fact]
    public void WhenCreated_AddressIsNull()
    {
        HopStats stats = HopStats.Create();
        Assert.Null(stats.Address);
    }

    [Fact]
    public void WhenCreated_LossPercentIsZero()
    {
        HopStats stats = HopStats.Create();
        Assert.Equal(0.0, stats.LossPercent);
    }

    [Fact]
    public void WhenCreated_DnsResolvedIsFalse()
    {
        HopStats stats = HopStats.Create();
        Assert.False(stats.DnsResolved);
    }

    // ── RecordSuccess ─────────────────────────────────────────────────────────

    [Fact]
    public void WhenOneSuccess_SentIsOne()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 10.0);
        Assert.Equal(1, stats.Sent);
    }

    [Fact]
    public void WhenOneSuccess_LastEqualsRtt()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 42.5);
        Assert.Equal(42.5, stats.Last);
    }

    [Fact]
    public void WhenOneSuccess_BestEqualsRtt()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 42.5);
        Assert.Equal(42.5, stats.Best);
    }

    [Fact]
    public void WhenOneSuccess_WorstEqualsRtt()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 42.5);
        Assert.Equal(42.5, stats.Worst);
    }

    [Fact]
    public void WhenOneSuccess_AverageEqualsRtt()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 42.5);
        Assert.Equal(42.5, stats.Average);
    }

    [Fact]
    public void WhenOneSuccess_StdDevIsZero()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 42.5);
        Assert.Equal(0.0, stats.StdDev);
    }

    [Fact]
    public void WhenOneSuccess_AddressIsSet()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 10.0);
        Assert.Equal(LoopbackV4, stats.Address);
    }

    // ── Best / Worst ──────────────────────────────────────────────────────────

    [Fact]
    public void WhenMultipleSuccesses_BestIsMinimum()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 30.0);
        stats.RecordSuccess(LoopbackV4, 10.0);
        stats.RecordSuccess(LoopbackV4, 20.0);
        Assert.Equal(10.0, stats.Best);
    }

    [Fact]
    public void WhenMultipleSuccesses_WorstIsMaximum()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 30.0);
        stats.RecordSuccess(LoopbackV4, 10.0);
        stats.RecordSuccess(LoopbackV4, 20.0);
        Assert.Equal(30.0, stats.Worst);
    }

    // ── Average (Welford) ─────────────────────────────────────────────────────

    [Fact]
    public void WhenMultipleSuccesses_AverageIsCorrect()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 10.0);
        stats.RecordSuccess(LoopbackV4, 20.0);
        stats.RecordSuccess(LoopbackV4, 30.0);
        Assert.Equal(20.0, stats.Average, precision: 10);
    }

    [Fact]
    public void WhenTwoSuccesses_StdDevMatchesExpected()
    {
        // sample std dev of {10, 20} = sqrt(((10-15)²+(20-15)²)/1) = sqrt(50) ≈ 7.071
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 10.0);
        stats.RecordSuccess(LoopbackV4, 20.0);
        Assert.Equal(Math.Sqrt(50.0), stats.StdDev, precision: 10);
    }

    [Theory]
    [InlineData(new double[] { 5, 10, 15, 20, 25 }, 15.0)]
    [InlineData(new double[] { 100, 200, 300 }, 200.0)]
    [InlineData(new double[] { 1, 1, 1, 1 }, 1.0)]
    public void WhenKnownRtts_AverageMatchesArithmetic(double[] rtts, double expected)
    {
        HopStats stats = HopStats.Create();
        foreach (double rtt in rtts)
            stats.RecordSuccess(LoopbackV4, rtt);
        Assert.Equal(expected, stats.Average, precision: 10);
    }

    // ── Loss ─────────────────────────────────────────────────────────────────

    [Fact]
    public void WhenAllLost_LossPercentIs100()
    {
        HopStats stats = HopStats.Create();
        stats.RecordLoss();
        stats.RecordLoss();
        Assert.Equal(100.0, stats.LossPercent);
    }

    [Fact]
    public void WhenHalfLost_LossPercentIs50()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 10.0);
        stats.RecordLoss();
        Assert.Equal(50.0, stats.LossPercent);
    }

    [Fact]
    public void WhenNoLoss_LossPercentIsZero()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 10.0);
        stats.RecordSuccess(LoopbackV4, 20.0);
        Assert.Equal(0.0, stats.LossPercent);
    }

    // ── Ring buffer ───────────────────────────────────────────────────────────

    [Fact]
    public void WhenSamplesAddedBelowCapacity_CopyRingSamplesReturnsAllInOrder()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 1.0);
        stats.RecordSuccess(LoopbackV4, 2.0);
        stats.RecordSuccess(LoopbackV4, 3.0);

        Span<double> buf = stackalloc double[10];
        int count = stats.CopyRingSamples(buf);

        Assert.Equal(3, count);
        Assert.Equal(1.0, buf[0]);
        Assert.Equal(2.0, buf[1]);
        Assert.Equal(3.0, buf[2]);
    }

    [Fact]
    public void WhenSamplesExceedRingCapacity_OldestSamplesAreEvicted()
    {
        HopStats stats = HopStats.Create();

        // Fill ring + 1 extra to force wrap
        for (int i = 1; i <= HopStats.RingBufferSize + 1; i++)
            stats.RecordSuccess(LoopbackV4, i);

        Span<double> buf = stackalloc double[HopStats.RingBufferSize];
        int count = stats.CopyRingSamples(buf);

        Assert.Equal(HopStats.RingBufferSize, count);
        // Oldest entry should be 2 (1 was evicted), newest should be RingBufferSize+1
        Assert.Equal(2.0, buf[0]);
        Assert.Equal(HopStats.RingBufferSize + 1.0, buf[HopStats.RingBufferSize - 1]);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AfterReset_SentIsZero()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 10.0);
        stats.RecordLoss();
        stats.Reset();
        Assert.Equal(0, stats.Sent);
    }

    [Fact]
    public void AfterReset_AddressIsNull()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 10.0);
        stats.Reset();
        Assert.Null(stats.Address);
    }

    [Fact]
    public void AfterReset_RingSampleCountIsZero()
    {
        HopStats stats = HopStats.Create();
        stats.RecordSuccess(LoopbackV4, 10.0);
        stats.Reset();
        Assert.Equal(0, stats.RingSampleCount);
    }

    // ── DNS ───────────────────────────────────────────────────────────────────

    [Fact]
    public void WhenHostNameSet_DnsResolvedIsTrue()
    {
        HopStats stats = HopStats.Create();
        stats.SetHostName("example.com");
        Assert.True(stats.DnsResolved);
    }

    [Fact]
    public void WhenHostNameSetToNull_DnsResolvedIsStillTrue()
    {
        HopStats stats = HopStats.Create();
        stats.SetHostName(null);
        Assert.True(stats.DnsResolved);
    }

    [Fact]
    public void WhenHostNameSet_HostNameIsReturned()
    {
        HopStats stats = HopStats.Create();
        stats.SetHostName("router.local");
        Assert.Equal("router.local", stats.HostName);
    }
}
