using System.Net;

namespace MTRCSLib.Abstractions;

/// <summary>
/// Listens on a raw ICMP/ICMPv6 socket for error messages (Time Exceeded, Destination
/// Unreachable) and routes replies to waiting callers keyed by source port.
/// </summary>
internal interface IRawIcmpListener : IDisposable
{
    /// <summary>
    /// Registers a pending probe keyed by <paramref name="sourcePort"/> and returns a task
    /// that completes when a matching ICMP error arrives, the probe times out, or
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<IcmpReply?> WaitForReplyAsync(ushort sourcePort, int timeoutMs, CancellationToken ct);
}
