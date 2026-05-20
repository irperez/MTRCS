using System.Net.Sockets;
using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// <see cref="IPingerFactory"/> that creates <see cref="TcpPinger"/> instances,
/// all sharing a single <see cref="IRawIcmpListener"/> owned by this factory.
/// Supports both IPv4 and IPv6 targets.
/// </summary>
public sealed class TcpPingerFactory : IPingerFactory, IDisposable
{
    private readonly IRawIcmpListener _listener;
    private readonly int _destPort;
    private readonly int _dscpValue;
    private bool _disposed;

    /// <param name="destPort">TCP destination port (default 80).</param>
    /// <param name="addressFamily">Address family of the target; determines ICMPv4 vs ICMPv6 listener.</param>
    /// <param name="dscpValue">DSCP value (0–63) to set on probe sockets, or 0 for OS default.</param>
    public TcpPingerFactory(int destPort = 80, AddressFamily addressFamily = AddressFamily.InterNetwork, int dscpValue = 0)
    {
        if (destPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(destPort), "Must be between 1 and 65535.");
        _destPort = destPort;
        _dscpValue = dscpValue;
        _listener = RawIcmpListenerFactory.Create(addressFamily);
    }

    /// <inheritdoc/>
    public IPinger Create()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new TcpPinger(_listener, _destPort, _dscpValue);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Dispose();
    }
}
