using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// <see cref="IPinger"/> that probes each hop by sending a TCP SYN packet
/// with the requested TTL.  Intermediate routers respond with ICMP Time
/// Exceeded; the destination may respond with SYN-ACK (or RST).
///
/// Uses a raw ICMP socket via <see cref="IRawIcmpListener"/> to receive
/// TTL-expired messages (which do not arrive on the TCP socket itself),
/// and a non-blocking TCP connect to detect a live destination.
/// Supports both IPv4 and IPv6 targets.
/// </summary>
internal sealed class TcpPinger : IPinger
{
    private readonly IRawIcmpListener _listener;
    private readonly int _destPort;
    private readonly int _dscpValue;
    private bool _disposed;

    public TcpPinger(IRawIcmpListener listener, int destPort = 80, int dscpValue = 0)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listener = listener;
        _destPort = destPort;
        _dscpValue = dscpValue;
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

        // Allocate a local ephemeral port; bind so we know the port before connecting.
        using Socket tcp = new(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        if (target.AddressFamily == AddressFamily.InterNetworkV6)
            tcp.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.HopLimit, ttl);
        else
            tcp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);

        // DSCP is encoded in the upper 6 bits of the Traffic Class / TOS byte (value << 2).
        if (_dscpValue > 0)
        {
            int tos = _dscpValue << 2;
            if (target.AddressFamily == AddressFamily.InterNetworkV6)
                tcp.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.TypeOfService, tos);
            else
                tcp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, tos);
        }

        tcp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        tcp.Blocking = false;

        // Bind to :0 to get an OS-assigned ephemeral port.
        IPEndPoint localEp = target.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
        tcp.Bind(localEp);
        ushort srcPort = (ushort)((IPEndPoint)tcp.LocalEndPoint!).Port;

        // Register with the ICMP listener BEFORE sending so we don't miss a fast reply.
        Task<IcmpReply?> icmpWait = _listener.WaitForReplyAsync(srcPort, timeoutMs, cancellationToken);

        long sentTicks = Stopwatch.GetTimestamp();

        // Initiate a non-blocking connect (sends the SYN).
        bool connected = false;
        try
        {
            using CancellationTokenSource connectCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(timeoutMs);

            await tcp.ConnectAsync(new IPEndPoint(target, _destPort), connectCts.Token)
                .ConfigureAwait(false);
            connected = true;
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode is SocketError.ConnectionRefused
                                or SocketError.ConnectionReset)
        {
            // RST from destination — we reached it (TTL was sufficient).
            connected = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out waiting for connect — fall through and check ICMP.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore other errors; check ICMP.
        }

        double rttMs = Stopwatch.GetElapsedTime(sentTicks).TotalMilliseconds;

        if (connected)
        {
            return ProbeResult.FromResponse(ttl, target, rttMs, PingStatus.Success, sequence);
        }

        // Wait for ICMP reply (may already be resolved).
        IcmpReply? reply = await icmpWait.ConfigureAwait(false);

        if (reply is null)
            return ProbeResult.FromTimeout(ttl, PingStatus.Timeout, sequence);

        rttMs = Stopwatch.GetElapsedTime(sentTicks).TotalMilliseconds;
        PingStatus status = reply.Value.IcmpType == 11
            ? PingStatus.TtlExpired
            : PingStatus.DestinationUnreachable;

        return ProbeResult.FromResponse(ttl, reply.Value.From, rttMs, status, sequence);
    }

    /// <inheritdoc/>
    public void Dispose() => _disposed = true; // RawIcmpListener is owned by the factory
}
