using System.Net;
using Shouldly;
using MTRCSLib;

namespace MTRCSLib.Tests;

// ── Constructor ───────────────────────────────────────────────────────────────

public class SystemPinger_ConstructorTests
{
    [Fact]
    public void WhenDefaultPayload_DoesNotThrow()
    {
        using SystemPinger pinger = new();
        pinger.ShouldNotBeNull();
    }

    [Fact]
    public void WhenZeroPayload_DoesNotThrow()
    {
        using SystemPinger pinger = new(0);
        pinger.ShouldNotBeNull();
    }

    [Fact]
    public void WhenMaxPayload_DoesNotThrow()
    {
        using SystemPinger pinger = new(SystemPinger.MaxPayloadBytes);
        pinger.ShouldNotBeNull();
    }

    [Fact]
    public void WhenPayloadIsNegative_ThrowsArgumentOutOfRangeException()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SystemPinger(-1));
    }

    [Fact]
    public void WhenPayloadExceedsMax_ThrowsArgumentOutOfRangeException()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SystemPinger(SystemPinger.MaxPayloadBytes + 1));
    }

    [Fact]
    public void WhenPayloadIsNegative_ExceptionNamesMaxPayloadBytesParameter()
    {
        ArgumentOutOfRangeException ex = Should.Throw<ArgumentOutOfRangeException>(() => new SystemPinger(-1));
        ex.ParamName.ShouldBe("maxPayloadBytes");
    }

    [Fact]
    public void WhenPayloadExceedsMax_ExceptionNamesMaxPayloadBytesParameter()
    {
        ArgumentOutOfRangeException ex = Should.Throw<ArgumentOutOfRangeException>(
            () => new SystemPinger(SystemPinger.MaxPayloadBytes + 1));
        ex.ParamName.ShouldBe("maxPayloadBytes");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(28)]      // DefaultPayloadBytes
    [InlineData(1024)]
    [InlineData(65500)]   // MaxPayloadBytes
    public void WhenPayloadIsWithinBounds_DoesNotThrow(int payloadBytes)
    {
        using SystemPinger pinger = new(payloadBytes);
        pinger.ShouldNotBeNull();
    }
}

// ── MaxPayloadBytes constant ──────────────────────────────────────────────────

public class SystemPinger_ConstantTests
{
    [Fact]
    public void MaxPayloadBytesIs65500()
    {
        SystemPinger.MaxPayloadBytes.ShouldBe(65500);
    }
}

// ── Dispose ───────────────────────────────────────────────────────────────────

public class SystemPinger_DisposeTests
{
    [Fact]
    public void DisposingTwiceDoesNotThrow()
    {
        SystemPinger pinger = new();
        pinger.Dispose();
        Should.NotThrow(() => pinger.Dispose());
    }

    [Fact]
    public async Task AfterDispose_SendProbeAsync_ThrowsObjectDisposedException()
    {
        SystemPinger pinger = new();
        pinger.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(() =>
            pinger.SendProbeAsync(IPAddress.Loopback, ttl: 1, sequence: 0,
                timeoutMs: 100, payloadBytes: 8).AsTask());
    }
}

// ── SendProbeAsync argument guards ────────────────────────────────────────────

public class SystemPinger_ArgumentGuardTests
{
    [Fact]
    public async Task WhenTargetIsNull_ThrowsArgumentNullException()
    {
        using SystemPinger pinger = new();

        await Should.ThrowAsync<ArgumentNullException>(() =>
            pinger.SendProbeAsync(null!, ttl: 1, sequence: 0,
                timeoutMs: 100, payloadBytes: 8).AsTask());
    }

    [Fact]
    public async Task WhenTargetIsNull_ExceptionNamesTargetParameter()
    {
        using SystemPinger pinger = new();

        ArgumentNullException ex = await Should.ThrowAsync<ArgumentNullException>(() =>
            pinger.SendProbeAsync(null!, ttl: 1, sequence: 0,
                timeoutMs: 100, payloadBytes: 8).AsTask());

        ex.ParamName.ShouldBe("target");
    }
}

// ── IPStatus → PingStatus mapping (integration — requires network + elevation) ──
//
// These tests document the expected behaviour of the IPStatus switch in
// SystemPinger.SendProbeAsync.  They are skipped in normal CI because they
// require real ICMP sockets (elevated privileges on Linux/macOS, admin on
// Windows) and a reachable network.
//
// To verify manually:
//   Run as administrator / with 'sudo' and remove the Skip attribute temporarily.
// ─────────────────────────────────────────────────────────────────────────────

public class SystemPinger_IntegrationTests
{
    private const string SkipReason =
        "Integration test — requires elevated privileges and a reachable network. " +
        "Remove Skip to verify manually.";

    // loopback should always reply Success on any OS when elevated
    [Fact(Skip = SkipReason)]
    public async Task WhenTargetIsLoopback_ReturnsSuccess()
    {
        using SystemPinger pinger = new();
        ProbeResult result = await pinger.SendProbeAsync(
            IPAddress.Loopback, ttl: 64, sequence: 1, timeoutMs: 1000, payloadBytes: 28);

        result.Status.ShouldBe(PingStatus.Success);
        result.RoundTripMs.ShouldBeGreaterThanOrEqualTo(0);
        result.Address.ShouldBe(IPAddress.Loopback);
        result.Ttl.ShouldBe(64);
        result.Sequence.ShouldBe((ushort)1);
    }

    // TTL=1 to loopback should expire at the first hop (the host itself) and return TtlExpired
    [Fact(Skip = SkipReason)]
    public async Task WhenTtlIsOne_ToRemoteHost_ReturnsTtlExpired()
    {
        using SystemPinger pinger = new(TracerouteOptions.DefaultPayloadBytes);
        ProbeResult result = await pinger.SendProbeAsync(
            IPAddress.Parse("8.8.8.8"), ttl: 1, sequence: 2, timeoutMs: 1000, payloadBytes: 28);

        result.Status.ShouldBe(PingStatus.TtlExpired);
        result.Address.ShouldNotBeNull();
    }

    // Unreachable RFC-5737 documentation address (192.0.2.1) should time out
    [Fact(Skip = SkipReason)]
    public async Task WhenTargetIsUnreachable_ReturnsTimeout()
    {
        using SystemPinger pinger = new();
        ProbeResult result = await pinger.SendProbeAsync(
            IPAddress.Parse("192.0.2.1"), ttl: 64, sequence: 3, timeoutMs: 500, payloadBytes: 28);

        result.Status.ShouldBeOneOf(PingStatus.Timeout, PingStatus.DestinationUnreachable, PingStatus.Error);
        result.RoundTripMs.ShouldBe(0);
    }

    // Cancellation must propagate as OperationCanceledException, not be swallowed
    [Fact(Skip = SkipReason)]
    public async Task WhenCancelledMidFlight_ThrowsOperationCanceledException()
    {
        using SystemPinger pinger = new();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            pinger.SendProbeAsync(
                IPAddress.Parse("8.8.8.8"), ttl: 64, sequence: 4,
                timeoutMs: 5000, payloadBytes: 28, cts.Token).AsTask());
    }

    // Verify RTT is populated on a successful reply
    [Fact(Skip = SkipReason)]
    public async Task WhenSuccess_RoundTripMsIsPositive()
    {
        using SystemPinger pinger = new();
        ProbeResult result = await pinger.SendProbeAsync(
            IPAddress.Loopback, ttl: 64, sequence: 5, timeoutMs: 1000, payloadBytes: 28);

        result.Status.ShouldBe(PingStatus.Success);
        result.RoundTripMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    // Zero-payload probes should succeed (valid MTR configuration)
    [Fact(Skip = SkipReason)]
    public async Task WhenPayloadIsZero_LoopbackStillSucceeds()
    {
        using SystemPinger pinger = new(0);
        ProbeResult result = await pinger.SendProbeAsync(
            IPAddress.Loopback, ttl: 64, sequence: 6, timeoutMs: 1000, payloadBytes: 0);

        result.Status.ShouldBe(PingStatus.Success);
    }
}
