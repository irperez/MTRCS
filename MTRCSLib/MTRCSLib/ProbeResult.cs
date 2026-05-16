using System.Net;

namespace MTRCSLib;

/// <summary>
/// The result of a single ICMP probe to one TTL hop.
/// A readonly record struct keeps this on the stack and avoids heap allocation.
/// </summary>
public readonly record struct ProbeResult
{
    /// <summary>TTL value used for this probe (1-based hop number).</summary>
    public int Ttl { get; init; }

    /// <summary>IP address of the node that responded, or <see langword="null"/> on timeout/error.</summary>
    public IPAddress? Address { get; init; }

    /// <summary>Round-trip time in milliseconds. 0 when <see cref="Status"/> is not <see cref="PingStatus.Success"/> or <see cref="PingStatus.TtlExpired"/>.</summary>
    public double RoundTripMs { get; init; }

    /// <summary>Outcome of the probe.</summary>
    public PingStatus Status { get; init; }

    /// <summary>Sequence number of this probe within the session (monotonically increasing).</summary>
    public ushort Sequence { get; init; }

    /// <summary>UTC timestamp at which the probe was sent.</summary>
    public long SentAtTicks { get; init; }

    /// <summary>
    /// Creates a successful or TTL-expired probe result.
    /// </summary>
    public static ProbeResult FromResponse(int ttl, IPAddress address, double rttMs, PingStatus status, ushort sequence) =>
        new()
        {
            Ttl = ttl,
            Address = address,
            RoundTripMs = rttMs,
            Status = status,
            Sequence = sequence,
            SentAtTicks = DateTime.UtcNow.Ticks,
        };

    /// <summary>
    /// Creates a timeout or error probe result (no address, no RTT).
    /// </summary>
    public static ProbeResult FromTimeout(int ttl, PingStatus status, ushort sequence) =>
        new()
        {
            Ttl = ttl,
            Address = null,
            RoundTripMs = 0,
            Status = status,
            Sequence = sequence,
            SentAtTicks = DateTime.UtcNow.Ticks,
        };
}
