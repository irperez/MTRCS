using System.Net;
using MTRCSLib;
using MTRCSLib.Abstractions;

namespace MTRCSLib.Tests;

// ── test doubles ──────────────────────────────────────────────────────────────

/// <summary>
/// Fake pinger whose reply sequence is pre-programmed via a queue.
/// </summary>
internal sealed class FakePinger : IPinger
{
    private readonly Queue<ProbeResult> _results;

    public FakePinger(IEnumerable<ProbeResult> results) =>
        _results = new Queue<ProbeResult>(results);

    public int CallCount { get; private set; }

    public ValueTask<ProbeResult> SendProbeAsync(
        IPAddress target, int ttl, ushort sequence, int timeoutMs, int payloadBytes,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (_results.TryDequeue(out ProbeResult result))
            return ValueTask.FromResult(result);

        // Default: timeout
        return ValueTask.FromResult(ProbeResult.FromTimeout(ttl, PingStatus.Timeout, sequence));
    }

    public void Dispose() { }
}

internal sealed class FakePingerFactory : IPingerFactory
{
    private readonly Queue<FakePinger> _pingers;

    public FakePingerFactory(IEnumerable<FakePinger> pingers) =>
        _pingers = new Queue<FakePinger>(pingers);

    public IPinger Create() =>
        _pingers.TryDequeue(out FakePinger? p) ? p : new FakePinger([]);
}

internal sealed class FakeDnsResolver : IDnsResolver
{
    private readonly Dictionary<string, string?> _map;

    public FakeDnsResolver(Dictionary<string, string?>? map = null) =>
        _map = map ?? [];

    public int CallCount { get; private set; }

    public ValueTask<string?> ResolveAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        CallCount++;
        _map.TryGetValue(address.ToString(), out string? name);
        return ValueTask.FromResult(name);
    }
}

// ── helpers ───────────────────────────────────────────────────────────────────

internal static class SessionFactory
{
    private static readonly IPAddress Target = IPAddress.Parse("8.8.8.8");

    public static TracerouteOptions Options(int maxHops = 4, int intervalMs = 50, int timeoutMs = 100) =>
        TracerouteOptions.Create(Target, "8.8.8.8", maxHops, intervalMs, timeoutMs);

    public static TracerouteSession Create(
        IEnumerable<FakePinger> pingers,
        IDnsResolver? dns = null,
        int maxHops = 4) =>
        new(Options(maxHops), new FakePingerFactory(pingers), dns ?? new FakeDnsResolver());
}

// ── tests ─────────────────────────────────────────────────────────────────────

public class TracerouteSessionTests
{
    private static readonly IPAddress Hop1 = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Hop2 = IPAddress.Parse("10.0.0.2");
    private static readonly IPAddress Dest = IPAddress.Parse("8.8.8.8");

    // ── ActiveHopCount ────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenCreated_ActiveHopCountIsZero()
    {
        await using TracerouteSession session = SessionFactory.Create([]);
        Assert.Equal(0, session.ActiveHopCount);
    }

    [Fact]
    public async Task AfterOneCycle_ActiveHopCountReflectsReachedDestination()
    {
        // 2-hop path: hop1 → dest (Success stops the loop at TTL 2)
        FakePinger pinger = new(
        [
            ProbeResult.FromResponse(1, Hop1, 5.0, PingStatus.TtlExpired, 0),
            ProbeResult.FromResponse(2, Dest, 10.0, PingStatus.Success, 1),
        ]);

        await using TracerouteSession session = SessionFactory.Create([pinger], maxHops: 4);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));

        await session.StartAsync(cts.Token);
        await WaitForCycleAsync(session, expectedActiveHops: 2, cts.Token);
        await session.StopAsync();

        Assert.Equal(2, session.ActiveHopCount);
    }

    // ── SnapshotHops ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AfterOneCycle_SnapshotContainsCorrectRtt()
    {
        FakePinger pinger = new(
        [
            ProbeResult.FromResponse(1, Hop1, 7.5, PingStatus.TtlExpired, 0),
            ProbeResult.FromResponse(2, Dest, 12.5, PingStatus.Success, 1),
        ]);

        await using TracerouteSession session = SessionFactory.Create([pinger], maxHops: 4);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));

        await session.StartAsync(cts.Token);
        await WaitForCycleAsync(session, expectedActiveHops: 2, cts.Token);
        await session.StopAsync();

        HopStats[] buf = new HopStats[4];
        int count = session.SnapshotHops(buf);

        Assert.Equal(2, count);
        Assert.Equal(7.5, buf[0].Last);
        Assert.Equal(12.5, buf[1].Last);
    }

    [Fact]
    public async Task SnapshotHops_DoesNotExceedDestinationLength()
    {
        FakePinger pinger = new(
        [
            ProbeResult.FromResponse(1, Hop1, 5.0, PingStatus.TtlExpired, 0),
            ProbeResult.FromResponse(2, Dest, 10.0, PingStatus.Success, 1),
        ]);

        await using TracerouteSession session = SessionFactory.Create([pinger], maxHops: 4);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));

        await session.StartAsync(cts.Token);
        await WaitForCycleAsync(session, expectedActiveHops: 2, cts.Token);
        await session.StopAsync();

        // Supply a destination of only 1 slot.
        HopStats[] buf = new HopStats[1];
        int count = session.SnapshotHops(buf);

        Assert.Equal(1, count);
    }

    // ── TryGetHop ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryGetHop_ReturnsCorrectStatsForValidIndex()
    {
        FakePinger pinger = new(
        [
            ProbeResult.FromResponse(1, Hop1, 5.0, PingStatus.TtlExpired, 0),
            ProbeResult.FromResponse(2, Dest, 10.0, PingStatus.Success, 1),
        ]);

        await using TracerouteSession session = SessionFactory.Create([pinger], maxHops: 4);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));

        await session.StartAsync(cts.Token);
        await WaitForCycleAsync(session, expectedActiveHops: 2, cts.Token);
        await session.StopAsync();

        bool found = session.TryGetHop(0, out HopStats hop);
        Assert.True(found);
        Assert.Equal(5.0, hop.Last);
    }

    [Fact]
    public async Task TryGetHop_ReturnsFalseForOutOfRangeIndex()
    {
        await using TracerouteSession session = SessionFactory.Create([]);
        bool found = session.TryGetHop(99, out _);
        Assert.False(found);
    }

    // ── Loss ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenHopTimesOut_LossIsRecorded()
    {
        // Hop 1 times out; hop 2 is destination.
        // Need TWO cycles worth of pingers so the loop runs twice.
        FakePinger cycle1 = new(
        [
            ProbeResult.FromTimeout(1, PingStatus.Timeout, 0),
            ProbeResult.FromResponse(2, Dest, 10.0, PingStatus.Success, 1),
        ]);
        FakePinger cycle2 = new(
        [
            ProbeResult.FromTimeout(1, PingStatus.Timeout, 2),
            ProbeResult.FromResponse(2, Dest, 10.0, PingStatus.Success, 3),
        ]);

        await using TracerouteSession session = SessionFactory.Create([cycle1, cycle2], maxHops: 4);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));

        await session.StartAsync(cts.Token);
        await WaitForSentCountAsync(session, hopIndex: 0, minimumSent: 2, cts.Token);
        await session.StopAsync();

        session.TryGetHop(0, out HopStats hop0);
        Assert.True(hop0.Lost > 0);
    }

    // ── DNS ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenHopResponds_DnsResolutionIsTriggered()
    {
        FakeDnsResolver dns = new(new Dictionary<string, string?>
        {
            [Hop1.ToString()] = "hop1.test",
            [Dest.ToString()] = "google.test",
        });

        FakePinger pinger = new(
        [
            ProbeResult.FromResponse(1, Hop1, 5.0, PingStatus.TtlExpired, 0),
            ProbeResult.FromResponse(2, Dest, 10.0, PingStatus.Success, 1),
        ]);

        await using TracerouteSession session = SessionFactory.Create([pinger], dns, maxHops: 4);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));

        await session.StartAsync(cts.Token);
        await WaitForDnsAsync(session, hopIndex: 0, cts.Token);
        await session.StopAsync();

        session.TryGetHop(0, out HopStats hop0);
        Assert.True(hop0.DnsResolved);
        Assert.Equal("hop1.test", hop0.HostName);
    }

    // ── Multiple cycles / accumulation ───────────────────────────────────────

    [Fact]
    public async Task AfterMultipleCycles_SentCountAccumulates()
    {
        const int cycles = 3;

        FakePinger[] pingers = Enumerable.Range(0, cycles).Select(_ => new FakePinger(
        [
            ProbeResult.FromResponse(1, Hop1, 5.0, PingStatus.TtlExpired, 0),
            ProbeResult.FromResponse(2, Dest, 10.0, PingStatus.Success, 1),
        ])).ToArray();

        await using TracerouteSession session = SessionFactory.Create(pingers, maxHops: 4);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await session.StartAsync(cts.Token);
        await WaitForSentCountAsync(session, hopIndex: 0, minimumSent: cycles, cts.Token);
        await session.StopAsync();

        session.TryGetHop(0, out HopStats hop0);
        Assert.True(hop0.Sent >= cycles);
    }

    // ── Options ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Options_AreRetainedOnSession()
    {
        TracerouteOptions opts = SessionFactory.Options(maxHops: 10, intervalMs: 500);
        await using TracerouteSession session = new(opts, new FakePingerFactory([]), new FakeDnsResolver());
        Assert.Equal(10, session.Options.MaxHops);
        Assert.Equal(500, session.Options.IntervalMs);
    }

    // ── Cannot double-start ───────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ThrowsWhenAlreadyStarted()
    {
        await using TracerouteSession session = SessionFactory.Create([]);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        await session.StartAsync(cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(cts.Token));

        await session.StopAsync();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task WaitForCycleAsync(
        TracerouteSession session,
        int expectedActiveHops,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && session.ActiveHopCount < expectedActiveHops)
            await Task.Delay(10, ct).ConfigureAwait(false);
    }

    private static async Task WaitForSentCountAsync(
        TracerouteSession session,
        int hopIndex,
        int minimumSent,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (session.TryGetHop(hopIndex, out HopStats h) && h.Sent >= minimumSent)
                return;
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    private static async Task WaitForDnsAsync(
        TracerouteSession session,
        int hopIndex,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (session.TryGetHop(hopIndex, out HopStats h) && h.DnsResolved)
                return;
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }
}
