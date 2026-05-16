using System.Net;

namespace MTRCSLib.Abstractions;

/// <summary>
/// A live traceroute session that continuously probes each TTL hop and accumulates statistics.
/// </summary>
public interface ITracerouteSession : IAsyncDisposable
{
    /// <summary>Configuration this session was created with.</summary>
    TracerouteOptions Options { get; }

    /// <summary>
    /// Starts the background probe loop.
    /// The loop runs until <see cref="StopAsync"/> is called or the <paramref name="cancellationToken"/> fires.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals the background loop to stop and waits for it to drain.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Number of hops that have responded in the most-recent probe cycle (1-based).
    /// Returns 0 before the first cycle completes.
    /// </summary>
    int ActiveHopCount { get; }

    /// <summary>
    /// Copies a snapshot of the current statistics for all active hops into <paramref name="destination"/>.
    /// The caller supplies a pre-allocated span — no heap allocation occurs inside this method.
    /// Returns the number of hops written (≤ <paramref name="destination"/>.Length).
    /// </summary>
    int SnapshotHops(Span<HopStats> destination);

    /// <summary>
    /// Reads the statistics for a single hop (0-based index) without copying the full array.
    /// Returns <see langword="false"/> if <paramref name="hopIndex"/> is out of range.
    /// </summary>
    bool TryGetHop(int hopIndex, out HopStats stats);
}
