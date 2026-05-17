using System.Net;
using System.Runtime.CompilerServices;
using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// Runs a continuous MTR-style traceroute: one probe per TTL per cycle, accumulates
/// <see cref="HopStats"/> per hop, and optionally fires async reverse-DNS lookups.
///
/// Design choices for zero/low allocation:
/// <list type="bullet">
///   <item>All hop stats live in a pre-allocated <see cref="HopStats"/> array (MaxHops slots).</item>
///   <item>A single <see cref="IPinger"/> is created per probe cycle and disposed after.</item>
///   <item>Snapshots are written into caller-supplied <see cref="Span{T}"/> — no boxing.</item>
///   <item>A lightweight <see langword="lock"/> guards the stats array between the probe loop
///         and the rendering thread.  A <see cref="System.Threading.ReaderWriterLockSlim"/> was
///         considered but adds more overhead than a simple exclusive lock for this access pattern.</item>
///   <item>DNS tasks are fire-and-forget via <c>_ =</c> — the result is written back through
///         <see cref="HopStats.SetHostName"/> under the same lock.</item>
/// </list>
/// </summary>
public sealed class TracerouteSession : ITracerouteSession
{
    private readonly IPingerFactory _pingerFactory;
    private readonly IDnsResolver _dnsResolver;
    private readonly IAsnResolver? _asnResolver;
    private readonly HopStats[] _hops;       // pre-allocated; index = ttl-1
    private readonly object _statsLock = new();

    private int _activeHopCount;
    private int _destinationTtl;  // TTL at which destination first replied (0 = not yet seen)
    private ushort _sequence;
    private Task? _probeLoop;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <inheritdoc/>
    public TracerouteOptions Options { get; }

    /// <inheritdoc/>
    public int ActiveHopCount => Volatile.Read(ref _activeHopCount);

    /// <param name="options">Session configuration.</param>
    /// <param name="pingerFactory">Factory used to create one pinger per cycle.</param>
    /// <param name="dnsResolver">Resolver used for PTR lookups.</param>
    /// <param name="asnResolver">Optional resolver for ASN lookups (used when <see cref="TracerouteOptions.EnableAsn"/> is true).</param>
    public TracerouteSession(
        TracerouteOptions options,
        IPingerFactory pingerFactory,
        IDnsResolver dnsResolver,
        IAsnResolver? asnResolver = null)
    {
        ArgumentNullException.ThrowIfNull(pingerFactory);
        ArgumentNullException.ThrowIfNull(dnsResolver);

        Options = options;
        _pingerFactory = pingerFactory;
        _dnsResolver = dnsResolver;
        _asnResolver = asnResolver;

        // Pre-allocate all hop slots — ring buffers inside HopStats are allocated here too.
        _hops = new HopStats[options.MaxHops];
        for (int i = 0; i < options.MaxHops; i++)
            _hops[i] = HopStats.Create();
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TracerouteSession));
        if (_probeLoop is not null) throw new InvalidOperationException("Session already started.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _probeLoop = RunProbeLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is null || _probeLoop is null) return;

        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _probeLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SnapshotHops(Span<HopStats> destination)
    {
        int active = ActiveHopCount;
        int count = Math.Min(active, destination.Length);
        if (count == 0) return 0;

        lock (_statsLock)
        {
            for (int i = 0; i < count; i++)
                destination[i] = _hops[i];
        }

        return count;
    }

    /// <inheritdoc/>
    public bool TryGetHop(int hopIndex, out HopStats stats)
    {
        if ((uint)hopIndex >= (uint)ActiveHopCount)
        {
            stats = default;
            return false;
        }

        lock (_statsLock)
        {
            stats = _hops[hopIndex];
        }

        return true;
    }

    /// <inheritdoc/>
    public void ResetStats()
    {
        lock (_statsLock)
        {
            for (int i = 0; i < _hops.Length; i++)
                _hops[i].Reset();
            Volatile.Write(ref _activeHopCount, 0);
            Volatile.Write(ref _destinationTtl, 0);
        }
    }

    // ── probe loop ────────────────────────────────────────────────────────────

    private async Task RunProbeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            long cycleStart = Environment.TickCount64;

            await RunOneCycleAsync(ct).ConfigureAwait(false);

            long elapsed = Environment.TickCount64 - cycleStart;
            int delay = (int)Math.Max(0L, Options.IntervalMs - elapsed);

            if (delay > 0)
            {
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }

            ct.ThrowIfCancellationRequested();
        }
    }

    private async Task RunOneCycleAsync(CancellationToken ct)
    {
        using IPinger pinger = _pingerFactory.Create();

        // Probe TTLs 1..MaxHops sequentially with a single pinger per cycle, stopping
        // as soon as the destination replies (Success).  This avoids sending probes
        // beyond the known destination and matches traditional MTR behaviour.
        // Show the last TTL that received any response (TtlExpired) when destination
        // is not yet reached, or 1 row minimum so the UI is never blank.
        // Cap probing at the known destination TTL once discovered.  Without this, a
        // timeout at the destination hop causes the loop to probe one TTL further,
        // creating a spurious extra row at that TTL.
        int destTtl = Volatile.Read(ref _destinationTtl);
        int hopCount = destTtl > 0 ? destTtl : Options.MaxHops;

        int newActiveHops = 1;
        for (int ttl = 1; ttl <= hopCount; ttl++)
        {
            ushort seq = unchecked(_sequence++);
            ProbeResult result = await ProbeHopAsync(pinger, ttl, seq, ct).ConfigureAwait(false);

            if (result.Status == PingStatus.Success)
            {
                newActiveHops = ttl;
                if (Volatile.Read(ref _destinationTtl) == 0)
                    Volatile.Write(ref _destinationTtl, ttl);
                break;
            }
            if (result.Status == PingStatus.TtlExpired)
                newActiveHops = ttl; // keep extending until no more responses
        }

        // Never let the active hop count shrink between cycles — ECMP probes may
        // reach the destination via a shorter path on some cycles, which would
        // cause rows to flicker in and out.  Use Math.Max so the count is monotone
        // until an explicit ResetStats() call.
        int prev = ActiveHopCount;
        if (newActiveHops > prev)
            Volatile.Write(ref _activeHopCount, newActiveHops);
    }

    private async Task<ProbeResult> ProbeHopAsync(IPinger pinger, int ttl, ushort seq, CancellationToken ct)
    {
        ProbeResult result = await pinger.SendProbeAsync(
            Options.Target,
            ttl,
            seq,
            Options.TimeoutMs,
            Options.PayloadBytes,
            ct).ConfigureAwait(false);

        int hopIndex = ttl - 1;

        lock (_statsLock)
        {
            ref HopStats hop = ref _hops[hopIndex];

            if (result.Status is PingStatus.Success or PingStatus.TtlExpired)
            {
                // ECMP: only intermediate hops (TtlExpired) can have multiple routers at the
                // same TTL.  A Success reply means we reached the destination — never treat
                // that address as an alternate for an intermediate hop.
                bool isAlt = result.Status == PingStatus.TtlExpired
                          && hop.Address is not null
                          && !hop.Address.Equals(result.Address!);

                if (isAlt)
                {
                    if (hop.TryRegisterAltAddress(result.Address!))
                    {
                        int altIndex = hop.AltAddressCount - 1;
                        hop.MarkAltDnsScheduled(altIndex); // guard against double-scheduling
                        ScheduleAltDnsResolution(hopIndex, altIndex, result.Address!, ct);
                    }
                }
                else
                {
                    hop.RecordSuccess(result.Address!, result.RoundTripMs);

                    if (!hop.DnsResolved)
                        ScheduleDnsResolution(hopIndex, result.Address!, ct);

                    if (Options.EnableAsn && _asnResolver is not null && !hop.AsnResolved)
                        ScheduleAsnResolution(hopIndex, result.Address!, ct);
                }
            }
            else
            {
                hop.RecordLoss();
            }
        }

        return result;
    }

    // ── DNS ───────────────────────────────────────────────────────────────────

    private void ScheduleDnsResolution(int hopIndex, IPAddress address, CancellationToken ct)
    {
        // Mark immediately so we don't schedule again while resolution is in-flight.
        _hops[hopIndex].SetHostName(null);

        _ = ResolveDnsAsync(hopIndex, address, ct);
    }

    private async Task ResolveDnsAsync(int hopIndex, IPAddress address, CancellationToken ct)
    {
        string? hostName = await _dnsResolver
            .ResolveAsync(address, ct)
            .ConfigureAwait(false);

        lock (_statsLock)
        {
            _hops[hopIndex].SetHostName(hostName);
        }
    }

    private void ScheduleAltDnsResolution(int hopIndex, int altIndex, IPAddress address, CancellationToken ct)
    {
        _ = ResolveAltDnsAsync(hopIndex, altIndex, address, ct);
    }

    private async Task ResolveAltDnsAsync(int hopIndex, int altIndex, IPAddress address, CancellationToken ct)
    {
        string? hostName = await _dnsResolver
            .ResolveAsync(address, ct)
            .ConfigureAwait(false);

        lock (_statsLock)
        {
            _hops[hopIndex].SetAltHostName(altIndex, hostName);
        }
    }

    // ── ASN ───────────────────────────────────────────────────────────────────

    private void ScheduleAsnResolution(int hopIndex, IPAddress address, CancellationToken ct)
    {
        // Mark immediately so we don't schedule again while resolution is in-flight.
        _hops[hopIndex].SetAsn(null);

        _ = ResolveAsnAsync(hopIndex, address, ct);
    }

    private async Task ResolveAsnAsync(int hopIndex, IPAddress address, CancellationToken ct)
    {
        AsnInfo? asn = await _asnResolver!
            .ResolveAsync(address, ct)
            .ConfigureAwait(false);

        lock (_statsLock)
        {
            _hops[hopIndex].SetAsn(asn);
        }
    }

    // ── disposal ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            if (_probeLoop is not null)
            {
                try { await _probeLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (AggregateException) { }
            }

            _cts.Dispose();
        }
    }
}
