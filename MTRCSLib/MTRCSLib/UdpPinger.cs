using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// <see cref="IPinger"/> that probes each hop by sending a UDP datagram
/// with the requested TTL.  Intermediate routers respond with ICMP Time
/// Exceeded; the destination responds with ICMP Port Unreachable (type 3,
/// code 3) when the target port is closed (the MTR/traceroute convention).
///
/// Uses <see cref="RawIcmpListener"/> to receive both types of ICMP reply.
/// </summary>
internal sealed class UdpPinger : IPinger
{
    private readonly RawIcmpListener _listener;
    private readonly int _destPort;
    private bool _disposed;

    // MTR default destination port for UDP probes.
    internal const int DefaultUdpPort = 33434;

    public UdpPinger(RawIcmpListener listener, int destPort = DefaultUdpPort)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listener = listener;
        _destPort = destPort;
    }

    /// <inheritdoc/>
    public async ValueTask<ProbeResult> SendProbeAsync(
        IPAddress target,
        int ttl,
        ushort sequence,
        int timeoutMs,
        int payloadBytes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        using Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);

        // Bind to :0 to get an OS-assigned ephemeral port.
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        ushort srcPort = (ushort)((IPEndPoint)udp.LocalEndPoint!).Port;

        // Register with ICMP listener BEFORE sending.
        Task<IcmpReply?> icmpWait = _listener.WaitForReplyAsync(srcPort, timeoutMs, cancellationToken);

        // Build a small payload (embed sequence in first two bytes).
        int dataLen = Math.Max(payloadBytes, 2);
        byte[] data = new byte[dataLen];
        data[0] = (byte)(sequence >> 8);
        data[1] = (byte)(sequence & 0xFF);

        long sentTicks = Stopwatch.GetTimestamp();

        try
        {
            await udp.SendToAsync(data.AsMemory(), new IPEndPoint(target, _destPort), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ProbeResult.FromTimeout(ttl, PingStatus.Error, sequence);
        }

        IcmpReply? reply = await icmpWait.ConfigureAwait(false);

        if (reply is null)
            return ProbeResult.FromTimeout(ttl, PingStatus.Timeout, sequence);

        double rttMs = Stopwatch.GetElapsedTime(sentTicks).TotalMilliseconds;

        PingStatus status = reply.Value.IcmpType switch
        {
            11 => PingStatus.TtlExpired,
            3 when reply.Value.IcmpCode == 3 => PingStatus.Success,   // Port Unreachable = reached destination
            3 => PingStatus.DestinationUnreachable,
            _ => PingStatus.Error,
        };

        return ProbeResult.FromResponse(ttl, reply.Value.From, rttMs, status, sequence);
    }

    /// <inheritdoc/>
    public void Dispose() => _disposed = true; // RawIcmpListener owned by factory
}
