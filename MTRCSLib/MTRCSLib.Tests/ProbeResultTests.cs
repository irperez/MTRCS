using System.Net;
using Shouldly;

namespace MTRCSLib.Tests;

// ── ProbeResult.FromResponse ──────────────────────────────────────────────────

public class ProbeResult_FromResponseTests
{
    private static readonly IPAddress Address = IPAddress.Parse("10.0.0.1");

    [Fact]
    public void WhenCreated_TtlIsStored()
    {
        var result = ProbeResult.FromResponse(5, Address, 12.5, PingStatus.TtlExpired, 42);
        result.Ttl.ShouldBe(5);
    }

    [Fact]
    public void WhenCreated_AddressIsStored()
    {
        var result = ProbeResult.FromResponse(1, Address, 12.5, PingStatus.TtlExpired, 0);
        result.Address.ShouldBe(Address);
    }

    [Fact]
    public void WhenCreated_RoundTripMsIsStored()
    {
        var result = ProbeResult.FromResponse(1, Address, 99.9, PingStatus.TtlExpired, 0);
        result.RoundTripMs.ShouldBe(99.9);
    }

    [Fact]
    public void WhenCreated_StatusIsStored()
    {
        var result = ProbeResult.FromResponse(1, Address, 5.0, PingStatus.Success, 0);
        result.Status.ShouldBe(PingStatus.Success);
    }

    [Fact]
    public void WhenCreated_SequenceIsStored()
    {
        var result = ProbeResult.FromResponse(1, Address, 5.0, PingStatus.TtlExpired, 1234);
        result.Sequence.ShouldBe((ushort)1234);
    }

    [Fact]
    public void WhenCreated_SentAtTicksIsPositive()
    {
        var result = ProbeResult.FromResponse(1, Address, 5.0, PingStatus.TtlExpired, 0);
        result.SentAtTicks.ShouldBeGreaterThan(0L);
    }

    [Theory]
    [InlineData(PingStatus.Success)]
    [InlineData(PingStatus.TtlExpired)]
    [InlineData(PingStatus.DestinationUnreachable)]
    public void WhenCreatedWithVariousStatuses_StatusIsCorrect(PingStatus status)
    {
        var result = ProbeResult.FromResponse(1, Address, 5.0, status, 0);
        result.Status.ShouldBe(status);
    }
}

// ── ProbeResult.FromTimeout ───────────────────────────────────────────────────

public class ProbeResult_FromTimeoutTests
{
    [Fact]
    public void WhenCreated_TtlIsStored()
    {
        var result = ProbeResult.FromTimeout(7, PingStatus.Timeout, 0);
        result.Ttl.ShouldBe(7);
    }

    [Fact]
    public void WhenCreated_AddressIsNull()
    {
        var result = ProbeResult.FromTimeout(1, PingStatus.Timeout, 0);
        result.Address.ShouldBeNull();
    }

    [Fact]
    public void WhenCreated_RoundTripMsIsZero()
    {
        var result = ProbeResult.FromTimeout(1, PingStatus.Timeout, 0);
        result.RoundTripMs.ShouldBe(0.0);
    }

    [Fact]
    public void WhenCreated_StatusIsStored()
    {
        var result = ProbeResult.FromTimeout(1, PingStatus.Error, 0);
        result.Status.ShouldBe(PingStatus.Error);
    }

    [Fact]
    public void WhenCreated_SequenceIsStored()
    {
        var result = ProbeResult.FromTimeout(1, PingStatus.Timeout, 999);
        result.Sequence.ShouldBe((ushort)999);
    }

    [Fact]
    public void WhenCreated_SentAtTicksIsPositive()
    {
        var result = ProbeResult.FromTimeout(1, PingStatus.Timeout, 0);
        result.SentAtTicks.ShouldBeGreaterThan(0L);
    }

    [Theory]
    [InlineData(PingStatus.Timeout)]
    [InlineData(PingStatus.Error)]
    public void WhenCreatedWithTimeoutStatuses_StatusIsCorrect(PingStatus status)
    {
        var result = ProbeResult.FromTimeout(1, status, 0);
        result.Status.ShouldBe(status);
    }
}

// ── ProbeResult — record struct equality ─────────────────────────────────────

public class ProbeResult_EqualityTests
{
    private static readonly IPAddress Address = IPAddress.Parse("10.0.0.1");

    [Fact]
    public void TwoTimeoutResultsWithSameValues_AreEqual()
    {
        long ticks = DateTime.UtcNow.Ticks;
        var a = new ProbeResult { Ttl = 1, Status = PingStatus.Timeout, Sequence = 5, SentAtTicks = ticks };
        var b = new ProbeResult { Ttl = 1, Status = PingStatus.Timeout, Sequence = 5, SentAtTicks = ticks };
        a.ShouldBe(b);
    }

    [Fact]
    public void TwoResultsWithDifferentTtl_AreNotEqual()
    {
        long ticks = DateTime.UtcNow.Ticks;
        var a = new ProbeResult { Ttl = 1, Status = PingStatus.Timeout, Sequence = 0, SentAtTicks = ticks };
        var b = new ProbeResult { Ttl = 2, Status = PingStatus.Timeout, Sequence = 0, SentAtTicks = ticks };
        a.ShouldNotBe(b);
    }
}
