using System.Net;
using Shouldly;

namespace MTRCSLib.Tests;

file static class HopStatsHelpers
{
    internal static readonly IPAddress Ip = IPAddress.Loopback;

    internal static HopStats WithSuccesses(params double[] rtts)
    {
        HopStats stats = HopStats.Create();
        foreach (double rtt in rtts)
            stats.RecordSuccess(Ip, rtt);
        return stats;
    }
}

// ── Jitter ────────────────────────────────────────────────────────────────────

public class HopStats_JitterTests
{
    [Fact]
    public void WhenCreated_JitterIsNaN()
    {
        HopStats.Create().Jitter.ShouldBe(double.NaN);
    }

    [Fact]
    public void AfterOneSuccess_JitterIsStillNaN()
    {
        var stats = HopStatsHelpers.WithSuccesses(10.0);
        stats.Jitter.ShouldBe(double.NaN);
    }

    [Fact]
    public void AfterTwoSuccesses_JitterIsAbsDifference()
    {
        var stats = HopStatsHelpers.WithSuccesses(10.0, 30.0);
        stats.Jitter.ShouldBe(20.0);
    }

    [Fact]
    public void AfterTwoSuccesses_JitterIsAlwaysNonNegative()
    {
        var stats = HopStatsHelpers.WithSuccesses(30.0, 10.0);
        stats.Jitter.ShouldBe(20.0);
    }

    [Fact]
    public void AfterThirdSuccess_JitterReflectsLastTwoRtts()
    {
        // 10 → 30 → 15: last two are 30 and 15, jitter = |15 - 30| = 15
        var stats = HopStatsHelpers.WithSuccesses(10.0, 30.0, 15.0);
        stats.Jitter.ShouldBe(15.0);
    }

    [Fact]
    public void WhenAllRttsIdentical_JitterIsZero()
    {
        var stats = HopStatsHelpers.WithSuccesses(20.0, 20.0, 20.0);
        stats.Jitter.ShouldBe(0.0);
    }

    [Fact]
    public void AfterReset_JitterIsNaN()
    {
        var stats = HopStatsHelpers.WithSuccesses(10.0, 20.0);
        stats.Reset();
        stats.Jitter.ShouldBe(double.NaN);
    }
}

// ── P95 / P99 ─────────────────────────────────────────────────────────────────

public class HopStats_PercentileTests
{
    [Fact]
    public void WhenFewerThanTwoSamples_P95IsNaN()
    {
        HopStats.Create().P95.ShouldBe(double.NaN);
    }

    [Fact]
    public void WhenFewerThanTwoSamples_P99IsNaN()
    {
        HopStats.Create().P99.ShouldBe(double.NaN);
    }

    [Fact]
    public void WhenOneSample_P95IsNaN()
    {
        HopStatsHelpers.WithSuccesses(10.0).P95.ShouldBe(double.NaN);
    }

    [Fact]
    public void WhenTwoSamples_P95ReturnsHigherValue()
    {
        // nearest-rank: ceil(0.95 * 2) - 1 = ceil(1.9) - 1 = 2 - 1 = 1 → sorted[1] = 20
        var stats = HopStatsHelpers.WithSuccesses(10.0, 20.0);
        stats.P95.ShouldBe(20.0);
    }

    [Fact]
    public void WhenFiveSamples_P95ReturnsNearestRankValue()
    {
        // sorted: [10, 20, 30, 40, 50]
        // ceil(0.95 * 5) - 1 = ceil(4.75) - 1 = 5 - 1 = 4 → sorted[4] = 50
        var stats = HopStatsHelpers.WithSuccesses(30.0, 10.0, 50.0, 20.0, 40.0);
        stats.P95.ShouldBe(50.0);
    }

    [Fact]
    public void WhenFiveSamples_P99ReturnsNearestRankValue()
    {
        // ceil(0.99 * 5) - 1 = ceil(4.95) - 1 = 5 - 1 = 4 → sorted[4] = 50
        var stats = HopStatsHelpers.WithSuccesses(30.0, 10.0, 50.0, 20.0, 40.0);
        stats.P99.ShouldBe(50.0);
    }

    [Fact]
    public void WhenTenSamples_P95ReturnsCorrectValue()
    {
        // sorted: [1..10], ceil(0.95*10)-1 = ceil(9.5)-1 = 10-1 = 9 → sorted[9] = 10
        var stats = HopStats.Create();
        for (int i = 1; i <= 10; i++)
            stats.RecordSuccess(HopStatsHelpers.Ip, i);
        stats.P95.ShouldBe(10.0);
    }

    [Fact]
    public void WhenNewSampleAdded_PercentilesRecompute()
    {
        var stats = HopStatsHelpers.WithSuccesses(10.0, 20.0);
        double p95Before = stats.P95;

        stats.RecordSuccess(HopStatsHelpers.Ip, 100.0);
        double p95After = stats.P95;

        p95After.ShouldBeGreaterThan(p95Before);
    }

    [Fact]
    public void AfterReset_P95IsNaN()
    {
        var stats = HopStatsHelpers.WithSuccesses(10.0, 20.0, 30.0);
        stats.Reset();
        stats.P95.ShouldBe(double.NaN);
    }

    [Fact]
    public void AfterReset_P99IsNaN()
    {
        var stats = HopStatsHelpers.WithSuccesses(10.0, 20.0, 30.0);
        stats.Reset();
        stats.P99.ShouldBe(double.NaN);
    }
}

// ── Reset — gaps in existing coverage ────────────────────────────────────────

public class HopStats_ResetGapTests
{
    [Fact]
    public void AfterReset_BestReturnsToMaxValue()
    {
        var stats = HopStatsHelpers.WithSuccesses(10.0);
        stats.Reset();
        stats.Best.ShouldBe(double.MaxValue);
    }

    [Fact]
    public void AfterReset_WorstReturnsToMinValue()
    {
        var stats = HopStatsHelpers.WithSuccesses(10.0);
        stats.Reset();
        stats.Worst.ShouldBe(double.MinValue);
    }

    [Fact]
    public void AfterReset_LostIsZero()
    {
        var stats = HopStats.Create();
        stats.RecordLoss();
        stats.Reset();
        stats.Lost.ShouldBe(0);
    }

    [Fact]
    public void AfterReset_AsnIsNull()
    {
        var stats = HopStats.Create();
        stats.SetAsn(new AsnInfo("AS15169", "GOOGLE"));
        stats.Reset();
        stats.Asn.ShouldBeNull();
    }

    [Fact]
    public void AfterReset_AsnResolvedIsFalse()
    {
        var stats = HopStats.Create();
        stats.SetAsn(new AsnInfo("AS15169", "GOOGLE"));
        stats.Reset();
        stats.AsnResolved.ShouldBeFalse();
    }

    [Fact]
    public void AfterReset_DnsResolvedIsFalse()
    {
        var stats = HopStats.Create();
        stats.SetHostName("router.local");
        stats.Reset();
        stats.DnsResolved.ShouldBeFalse();
    }

    [Fact]
    public void AfterReset_HostNameIsNull()
    {
        var stats = HopStats.Create();
        stats.SetHostName("router.local");
        stats.Reset();
        stats.HostName.ShouldBeNull();
    }

    [Fact]
    public void AfterReset_AverageIsZero()
    {
        var stats = HopStatsHelpers.WithSuccesses(10.0, 20.0);
        stats.Reset();
        stats.Average.ShouldBe(0.0);
    }

    [Fact]
    public void AfterReset_CanAccumulateNewSamples()
    {
        var stats = HopStatsHelpers.WithSuccesses(10.0, 20.0);
        stats.Reset();
        stats.RecordSuccess(HopStatsHelpers.Ip, 5.0);
        stats.Sent.ShouldBe(1);
        stats.Average.ShouldBe(5.0);
    }
}

// ── SetAsn / AsnResolved ──────────────────────────────────────────────────────

public class HopStats_AsnTests
{
    [Fact]
    public void WhenCreated_AsnIsNull()
    {
        HopStats.Create().Asn.ShouldBeNull();
    }

    [Fact]
    public void WhenCreated_AsnResolvedIsFalse()
    {
        HopStats.Create().AsnResolved.ShouldBeFalse();
    }

    [Fact]
    public void AfterSetAsn_AsnResolvedIsTrue()
    {
        var stats = HopStats.Create();
        stats.SetAsn(new AsnInfo("AS15169", "GOOGLE"));
        stats.AsnResolved.ShouldBeTrue();
    }

    [Fact]
    public void AfterSetAsn_AsnIsReturned()
    {
        var stats = HopStats.Create();
        var asn = new AsnInfo("AS15169", "GOOGLE");
        stats.SetAsn(asn);
        stats.Asn.ShouldBe(asn);
    }

    [Fact]
    public void AfterSetAsnWithNull_AsnResolvedIsStillTrue()
    {
        // null result means lookup was attempted but found nothing
        var stats = HopStats.Create();
        stats.SetAsn(null);
        stats.AsnResolved.ShouldBeTrue();
    }

    [Fact]
    public void AfterSetAsnWithNull_AsnIsNull()
    {
        var stats = HopStats.Create();
        stats.SetAsn(null);
        stats.Asn.ShouldBeNull();
    }
}

// ── ECMP / alternate address tracking ────────────────────────────────────────

public class HopStats_EcmpTests
{
    private static readonly IPAddress Primary = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Alt1    = IPAddress.Parse("10.0.0.2");
    private static readonly IPAddress Alt2    = IPAddress.Parse("10.0.0.3");

    [Fact]
    public void WhenCreated_AltAddressCountIsZero()
    {
        HopStats.Create().AltAddressCount.ShouldBe(0);
    }

    [Fact]
    public void WhenNewAltAddressRegistered_ReturnsTrueAndCountIncreases()
    {
        var stats = HopStats.Create();
        stats.RecordSuccess(Primary, 10.0);

        bool added = stats.TryRegisterAltAddress(Alt1);

        added.ShouldBeTrue();
        stats.AltAddressCount.ShouldBe(1);
    }

    [Fact]
    public void WhenSameAltAddressRegisteredTwice_ReturnsFalseSecondTime()
    {
        var stats = HopStats.Create();
        stats.RecordSuccess(Primary, 10.0);
        stats.TryRegisterAltAddress(Alt1);

        bool addedAgain = stats.TryRegisterAltAddress(Alt1);

        addedAgain.ShouldBeFalse();
        stats.AltAddressCount.ShouldBe(1);
    }

    [Fact]
    public void WhenPrimaryAddressRegisteredAsAlt_ReturnsFalse()
    {
        var stats = HopStats.Create();
        stats.RecordSuccess(Primary, 10.0);

        bool added = stats.TryRegisterAltAddress(Primary);

        added.ShouldBeFalse();
        stats.AltAddressCount.ShouldBe(0);
    }

    [Fact]
    public void WhenEightAltsRegistered_NinthReturnsFalse()
    {
        var stats = HopStats.Create();
        stats.RecordSuccess(Primary, 10.0);

        for (int i = 1; i <= 8; i++)
            stats.TryRegisterAltAddress(IPAddress.Parse($"10.0.1.{i}"));

        bool ninth = stats.TryRegisterAltAddress(IPAddress.Parse("10.0.2.1"));

        ninth.ShouldBeFalse();
        stats.AltAddressCount.ShouldBe(8);
    }

    [Fact]
    public void GetAltAddress_ReturnsCorrectAddress()
    {
        var stats = HopStats.Create();
        stats.RecordSuccess(Primary, 10.0);
        stats.TryRegisterAltAddress(Alt1);
        stats.TryRegisterAltAddress(Alt2);

        stats.GetAltAddress(0).ShouldBe(Alt1);
        stats.GetAltAddress(1).ShouldBe(Alt2);
    }

    [Fact]
    public void WhenAltRegistered_HostNameIsNullAndDnsNotResolved()
    {
        var stats = HopStats.Create();
        stats.RecordSuccess(Primary, 10.0);
        stats.TryRegisterAltAddress(Alt1);

        stats.GetAltHostName(0).ShouldBeNull();
        stats.IsAltDnsResolved(0).ShouldBeFalse();
    }

    [Fact]
    public void AfterSetAltHostName_HostNameStoredAndDnsResolved()
    {
        var stats = HopStats.Create();
        stats.RecordSuccess(Primary, 10.0);
        stats.TryRegisterAltAddress(Alt1);

        stats.SetAltHostName(0, "alt1.local");

        stats.GetAltHostName(0).ShouldBe("alt1.local");
        stats.IsAltDnsResolved(0).ShouldBeTrue();
    }

    [Fact]
    public void AfterMarkAltDnsScheduled_IsAltDnsResolvedIsTrue()
    {
        var stats = HopStats.Create();
        stats.RecordSuccess(Primary, 10.0);
        stats.TryRegisterAltAddress(Alt1);

        stats.MarkAltDnsScheduled(0);

        stats.IsAltDnsResolved(0).ShouldBeTrue();
    }

    [Fact]
    public void SetAltHostName_WithOutOfRangeIndex_DoesNotThrow()
    {
        var stats = HopStats.Create();
        Should.NotThrow(() => stats.SetAltHostName(99, "whatever"));
    }

    [Fact]
    public void AfterReset_AltAddressCountIsZero()
    {
        var stats = HopStats.Create();
        stats.RecordSuccess(Primary, 10.0);
        stats.TryRegisterAltAddress(Alt1);
        stats.Reset();

        stats.AltAddressCount.ShouldBe(0);
    }
}

// ── Null address guard on RecordSuccess ───────────────────────────────────────

public class HopStats_NullAddressGuardTests
{
    [Fact]
    public void WhenNullAddressPassed_ThrowsArgumentNullException()
    {
        var stats = HopStats.Create();
        Should.Throw<ArgumentNullException>(() => stats.RecordSuccess(null!, 10.0));
    }
}
