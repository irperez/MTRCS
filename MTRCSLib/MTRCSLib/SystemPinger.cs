using System.Net;
using System.Net.NetworkInformation;
using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// Production <see cref="IPinger"/> backed by <see cref="System.Net.NetworkInformation.Ping"/>.
/// A single reusable byte array is allocated per instance for the ICMP payload, so each
/// <see cref="SendProbeAsync"/> call is effectively zero-allocation on the hot path.
/// </summary>
internal sealed class SystemPinger : IPinger
{
    // System.Net.NetworkInformation.Ping is not thread-safe — one instance per probe.
    private readonly Ping _ping;
    private readonly byte[] _payloadBuffer;
    private readonly PingOptions _pingOptions;
    private bool _disposed;

    /// <summary>Maximum supported payload in bytes (matches Ping class limit).</summary>
    internal const int MaxPayloadBytes = 65500;

    /// <summary>
    /// Creates a new <see cref="SystemPinger"/> with a pre-allocated payload buffer
    /// sized for the largest payload this session will ever send.
    /// </summary>
    /// <param name="maxPayloadBytes">Maximum ICMP data payload bytes (not including the 8-byte header).</param>
    /// <param name="dscpValue">
    /// DSCP value (0–63) to embed in probe packets.
    /// Note: <see cref="System.Net.NetworkInformation.Ping"/> does not expose a TOS/DSCP setter;
    /// this value is accepted for API consistency but is not applied in ICMP mode.
    /// Use TCP (<c>--tcp</c>) or UDP (<c>--udp</c>) probe modes to enforce DSCP marking.
    /// </param>
    public SystemPinger(int maxPayloadBytes = TracerouteOptions.DefaultPayloadBytes, int dscpValue = 0)
    {
        if (maxPayloadBytes is < 0 or > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));

        _ping = new Ping();
        // +8 for the ICMP header we write into the buffer (WriteIcmpEchoRequest uses this).
        // For System.Net.NetworkInformation.Ping we only pass the data portion — allocate just that.
        _payloadBuffer = maxPayloadBytes > 0 ? new byte[maxPayloadBytes] : [];
        _pingOptions = new PingOptions { DontFragment = false };
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

        _pingOptions.Ttl = ttl;

        // Embed the sequence number into the first two bytes of the payload so
        // the receiver can correlate replies — zero-alloc since we reuse the buffer.
        int useBytes = Math.Min(payloadBytes, _payloadBuffer.Length);
        if (useBytes >= 2)
        {
            _payloadBuffer[0] = (byte)(sequence >> 8);
            _payloadBuffer[1] = (byte)(sequence & 0xFF);
        }

        ReadOnlyMemory<byte> payload = _payloadBuffer.AsMemory(0, useBytes);

        try
        {
            long sentTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            PingReply reply = await _ping
                .SendPingAsync(target, timeoutMs, _payloadBuffer[..useBytes], _pingOptions)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            // System.Net.NetworkInformation.Ping only populates RoundtripTime for Success;
            // for TtlExpired (intermediate hops) it returns 0.  Measure it ourselves.
            double measuredRtt = reply.RoundtripTime > 0
                ? reply.RoundtripTime
                : System.Diagnostics.Stopwatch.GetElapsedTime(sentTicks).TotalMilliseconds;

            return reply.Status switch
            {
                IPStatus.Success =>
                    ProbeResult.FromResponse(ttl, reply.Address, measuredRtt, PingStatus.Success, sequence),

                IPStatus.TtlExpired =>
                    ProbeResult.FromResponse(ttl, reply.Address, measuredRtt, PingStatus.TtlExpired, sequence),

                IPStatus.DestinationHostUnreachable or
                IPStatus.DestinationNetworkUnreachable or
                IPStatus.DestinationPortUnreachable or
                IPStatus.DestinationUnreachable =>
                    ProbeResult.FromTimeout(ttl, PingStatus.DestinationUnreachable, sequence),

                IPStatus.TimedOut =>
                    ProbeResult.FromTimeout(ttl, PingStatus.Timeout, sequence),

                _ =>
                    ProbeResult.FromTimeout(ttl, PingStatus.Error, sequence),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ProbeResult.FromTimeout(ttl, PingStatus.Error, sequence);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ping.Dispose();
    }
}
