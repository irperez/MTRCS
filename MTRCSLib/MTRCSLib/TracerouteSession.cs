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

        int maxActiveHop = 0;

        for (int ttl = 1; ttl <= Options.MaxHops && !ct.IsCancellationRequested; ttl++)
        {
            ushort seq = unchecked(_sequence++);

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
                    // result.Address is non-null for these statuses (set by ProbeResult.FromResponse).
                    hop.RecordSuccess(result.Address!, result.RoundTripMs);
                    maxActiveHop = ttl;

                    // Fire DNS resolution on first sighting of this address.
                    if (!hop.DnsResolved)
                        ScheduleDnsResolution(hopIndex, result.Address!, ct);

                    // Fire ASN resolution on first sighting of this address (if enabled).
                    if (Options.EnableAsn && _asnResolver is not null && !hop.AsnResolved)
                        ScheduleAsnResolution(hopIndex, result.Address!, ct);
                }
                else
                {
                    hop.RecordLoss();
                    // Still count non-responding hops up to this TTL.
                    if (ttl > maxActiveHop) maxActiveHop = ttl;
                }
            }

            // Stop TTL loop once we've reached the destination.
            if (result.Status == PingStatus.Success)
                break;
        }

        // Update the active hop count so the UI knows how many rows to render.
        Volatile.Write(ref _activeHopCount, maxActiveHop);
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
