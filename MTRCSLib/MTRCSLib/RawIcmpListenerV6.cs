using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// IPv6 implementation of <see cref="IRawIcmpListener"/>.
/// Listens on a raw ICMPv6 socket for ICMPv6 Time Exceeded (type 3) and
/// Destination Unreachable (type 1) messages and routes them to waiting
/// callers keyed by the source port embedded in the quoted original
/// IPv6 + TCP/UDP header.
///
/// On IPv6 raw sockets the OS strips the outer IPv6 header before delivery,
/// so received data begins directly at the ICMPv6 header (no outer IP
/// header to skip, unlike the IPv4 case).
///
/// One instance is shared per probe session so all concurrent TCP/UDP
/// pingers use a single raw socket.
/// </summary>
internal sealed class RawIcmpListenerV6 : IRawIcmpListener
{
    private readonly ConcurrentDictionary<ushort, PendingProbe> _pending = new();
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;
    private bool _disposed;

    // Fixed IPv6 header size (no options/extension headers assumed for the quoted packet).
    private const int IPv6HeaderSize = 40;

    public RawIcmpListenerV6()
    {
        _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Raw, ProtocolType.IcmpV6);
        _socket.ReceiveTimeout = 0; // non-blocking managed by the loop
        _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

        _receiveLoop = Task.Factory.StartNew(
            ReceiveLoopAsync,
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <inheritdoc/>
    public Task<IcmpReply?> WaitForReplyAsync(ushort sourcePort, int timeoutMs, CancellationToken ct)
    {
        var probe = new PendingProbe();
        _pending[sourcePort] = probe;

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
        byte[] buf = new byte[1500];
        Memory<byte> memory = buf.AsMemory();
        EndPoint anyEp = new IPEndPoint(IPAddress.IPv6Any, 0);

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
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            int received = result.ReceivedBytes;
            // ICMPv6 header (8 bytes) + quoted IPv6 header (40 bytes) + 8 bytes transport minimum.
            if (received < 8 + IPv6HeaderSize + 8)
                continue;

            IPAddress responder = ((IPEndPoint)result.RemoteEndPoint).Address;
            TryDispatch(buf.AsSpan(0, received), responder);
        }
    }

    /// <summary>
    /// Parses an incoming ICMPv6 error packet (data starts at ICMPv6 header — no outer IP header
    /// on IPv6 raw sockets) and dispatches to a waiting probe.
    /// </summary>
    private void TryDispatch(ReadOnlySpan<byte> icmp, IPAddress responder)
    {
        if (icmp.Length < 8)
            return;

        byte icmpType = icmp[0];
        // ICMPv6 Type 3 = Time Exceeded, Type 1 = Destination Unreachable
        if (icmpType is not 3 and not 1)
            return;

        // ICMPv6 error body: 4 bytes unused + quoted original IPv6 header + first 8 bytes transport.
        ReadOnlySpan<byte> quotedIpv6 = icmp[8..];
        if (quotedIpv6.Length < IPv6HeaderSize + 8)
            return;

        // IPv6 header is fixed 40 bytes (we don't follow extension headers here;
        // the quoted packet is truncated to 8 transport bytes anyway).
        ReadOnlySpan<byte> quotedTransport = quotedIpv6[IPv6HeaderSize..];

        if (quotedTransport.Length < 4)
            return;

        // Source port is bytes 0-1 of TCP/UDP header (both share this layout).
        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(quotedTransport);

        if (!_pending.TryRemove(sourcePort, out var probe))
            return;

        byte icmpCode = icmp[1];
        probe.TrySetResult(new IcmpReply(responder, icmpType, icmpCode));
    }

    /// <inheritdoc/>
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
