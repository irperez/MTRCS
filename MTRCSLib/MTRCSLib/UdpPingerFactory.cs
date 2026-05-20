using System.Net.Sockets;
using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// <see cref="IPingerFactory"/> that creates <see cref="UdpPinger"/> instances,
/// all sharing a single <see cref="IRawIcmpListener"/> owned by this factory.
/// Supports both IPv4 and IPv6 targets.
/// </summary>
public sealed class UdpPingerFactory : IPingerFactory, IDisposable
{
    private readonly IRawIcmpListener _listener;
    private readonly int _destPort;
    private bool _disposed;

    /// <param name="destPort">UDP destination port (default 33434, matching MTR convention).</param>
    /// <param name="addressFamily">Address family of the target; determines ICMPv4 vs ICMPv6 listener.</param>
    public UdpPingerFactory(int destPort = UdpPinger.DefaultUdpPort, AddressFamily addressFamily = AddressFamily.InterNetwork)
    {
        if (destPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(destPort), "Must be between 1 and 65535.");
        _destPort = destPort;
        _listener = RawIcmpListenerFactory.Create(addressFamily);
    }

    /// <inheritdoc/>
    public IPinger Create()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new UdpPinger(_listener, _destPort);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Dispose();
    }
}
