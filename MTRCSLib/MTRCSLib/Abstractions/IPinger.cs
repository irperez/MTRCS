using System.Net;

namespace MTRCSLib.Abstractions;

/// <summary>
/// Sends a single ICMP Echo Request with the given TTL and returns the probe result.
/// Abstracted for unit-test injection.
/// </summary>
public interface IPinger : IDisposable
{
    /// <summary>
    /// Sends one ICMP Echo probe to <paramref name="target"/> with the specified <paramref name="ttl"/>.
    /// </summary>
    /// <param name="target">IPv4 destination address.</param>
    /// <param name="ttl">IP Time-To-Live for this probe.</param>
    /// <param name="sequence">Probe sequence number (embedded in ICMP payload).</param>
    /// <param name="timeoutMs">Maximum wait time in milliseconds before declaring a timeout.</param>
    /// <param name="payloadBytes">Number of data bytes in the ICMP payload after the 8-byte header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ProbeResult"/> describing the outcome.</returns>
    ValueTask<ProbeResult> SendProbeAsync(
        IPAddress target,
        int ttl,
        ushort sequence,
        int timeoutMs,
        int payloadBytes,
        CancellationToken cancellationToken = default);
}
