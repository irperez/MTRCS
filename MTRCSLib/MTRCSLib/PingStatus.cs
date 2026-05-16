namespace MTRCSLib;

/// <summary>
/// Outcome of a single ICMP probe attempt.
/// </summary>
public enum PingStatus : byte
{
    /// <summary>ICMP Echo Reply received within the timeout.</summary>
    Success = 0,

    /// <summary>TTL expired in transit — intermediate hop responded.</summary>
    TtlExpired = 1,

    /// <summary>No reply received before the timeout elapsed.</summary>
    Timeout = 2,

    /// <summary>Destination host unreachable (ICMP type 3).</summary>
    DestinationUnreachable = 3,

    /// <summary>The probe could not be sent (socket error, permissions, etc.).</summary>
    Error = 4,
}
