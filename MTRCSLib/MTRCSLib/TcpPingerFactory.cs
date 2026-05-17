using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// <see cref="IPingerFactory"/> that creates <see cref="TcpPinger"/> instances,
/// all sharing a single <see cref="RawIcmpListener"/> owned by this factory.
/// </summary>
public sealed class TcpPingerFactory : IPingerFactory, IDisposable
{
    private readonly RawIcmpListener _listener;
    private readonly int _destPort;
    private bool _disposed;

    /// <param name="destPort">TCP destination port (default 80).</param>
    public TcpPingerFactory(int destPort = 80)
    {
        if (destPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(destPort), "Must be between 1 and 65535.");
        _destPort = destPort;
        _listener = new RawIcmpListener();
    }

    /// <inheritdoc/>
    public IPinger Create()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new TcpPinger(_listener, _destPort);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Dispose();
    }
}
