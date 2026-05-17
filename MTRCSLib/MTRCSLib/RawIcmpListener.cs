using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace MTRCSLib;

/// <summary>
/// Listens on a raw ICMP socket for ICMP Time Exceeded (type 11) and
/// ICMP Destination Unreachable (type 3) messages and routes them to
/// waiting callers keyed by the source port embedded in the quoted
/// original IP+TCP/UDP header.
///
/// One instance is shared per probe session so all concurrent TCP/UDP
/// pingers use a single raw socket.
/// </summary>
internal sealed class RawIcmpListener : IDisposable
{
    // Each pending probe registers a completion source here, keyed by the
    // source port it used.  We use the source port (uint16) as the key because
    // it uniquely identifies an outbound probe within a session.
    private readonly ConcurrentDictionary<ushort, PendingProbe> _pending = new();
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;
    private bool _disposed;

    // IP header is 20 bytes (no options assumed for the quoted packet).
    private const int IpHeaderSize = 20;

    public RawIcmpListener()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
        _socket.ReceiveTimeout = 0; // non-blocking managed by the loop
        // Bind to any interface
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        _receiveLoop = Task.Factory.StartNew(
            ReceiveLoopAsync,
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Registers a pending probe keyed by <paramref name="sourcePort"/> and
    /// returns a task that completes when a matching ICMP error arrives or
    /// the probe times out.
    /// </summary>
    public Task<IcmpReply?> WaitForReplyAsync(ushort sourcePort, int timeoutMs, CancellationToken ct)
    {
        var probe = new PendingProbe();
        _pending[sourcePort] = probe;

        // Schedule automatic cleanup on timeout or cancellation.
        _ = Task.Delay(timeoutMs, ct).ContinueWith(_ =>
        {
            if (_pending.TryRemove(sourcePort, out var p))
                p.TrySetResult(null);
        }, TaskScheduler.Default);

        ct.Register(() =>
        {
            if (_pending.TryRemove(sourcePort, out var p))
                p.TrySetResult(null);
        });

        return probe.Task;
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buf = new byte[1500]; // full Ethernet MTU — safer than the 576-byte minimum
        Memory<byte> memory = buf.AsMemory();
        EndPoint anyEp = new IPEndPoint(IPAddress.Any, 0);

        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket
                    .ReceiveFromAsync(memory, SocketFlags.None, anyEp, _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.OperationAborted
                                   or SocketError.Interrupted
                                   or SocketError.Shutdown)
            {
                // Socket was closed — stop the loop.
                break;
            }
            catch (SocketException)
            {
                // Windows delivers some ICMP errors (e.g. Port Unreachable) as
                // SocketException(ConnectionReset) on the raw socket instead of
                // as a receivable packet.  Ignore and keep listening.
                continue;
            }

            int received = result.ReceivedBytes;
            if (received < IpHeaderSize + 8)
                continue;

            // ReceiveFromAsync on a raw socket gives us the sender's address
            // directly — no need to parse packet[12..16].
            IPAddress responder = ((IPEndPoint)result.RemoteEndPoint).Address;

            TryDispatch(buf.AsSpan(0, received), responder);
        }
    }

    /// <summary>
    /// Attempts to parse an incoming ICMP packet and dispatch it to a waiting probe.
    /// </summary>
    private void TryDispatch(ReadOnlySpan<byte> packet, IPAddress responder)
    {
        // Skip the outer IP header (length is in low nibble of first byte × 4).
        int outerIpHeaderLen = (packet[0] & 0x0F) * 4;
        ReadOnlySpan<byte> icmp = packet[outerIpHeaderLen..];

        if (icmp.Length < 8)
            return;

        byte icmpType = icmp[0];
        // Type 11 = Time Exceeded, Type 3 = Destination Unreachable
        if (icmpType is not 11 and not 3)
            return;

        // ICMP error body: 4 bytes unused + quoted original IP header + first 8 bytes of original transport.
        ReadOnlySpan<byte> quotedIp = icmp[8..];
        if (quotedIp.Length < IpHeaderSize + 8)
            return;

        int quotedIpHeaderLen = (quotedIp[0] & 0x0F) * 4;
        ReadOnlySpan<byte> quotedTransport = quotedIp[quotedIpHeaderLen..];

        if (quotedTransport.Length < 4)
            return;

        // Source port is bytes 0-1 of TCP/UDP header (both share this layout).
        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(quotedTransport);

        if (!_pending.TryRemove(sourcePort, out var probe))
            return;

        byte icmpCode = icmpType == 3 ? icmp[1] : (byte)0;
        probe.TrySetResult(new IcmpReply(responder, icmpType, icmpCode));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _socket.Dispose();
        _cts.Dispose();
    }

    private sealed class PendingProbe
    {
        private readonly TaskCompletionSource<IcmpReply?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IcmpReply?> Task => _tcs.Task;
        public void TrySetResult(IcmpReply? reply) => _tcs.TrySetResult(reply);
    }
}

/// <summary>Parsed ICMP error reply from an intermediate router.</summary>
internal readonly record struct IcmpReply(IPAddress From, byte IcmpType, byte IcmpCode);
