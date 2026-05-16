using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// Production <see cref="IPingerFactory"/> that creates <see cref="SystemPinger"/> instances.
/// </summary>
public sealed class SystemPingerFactory : IPingerFactory
{
    private readonly int _maxPayloadBytes;

    /// <param name="maxPayloadBytes">
    /// Maximum ICMP data payload bytes passed to each <see cref="SystemPinger"/>.
    /// Defaults to <see cref="TracerouteOptions.DefaultPayloadBytes"/>.
    /// </param>
    public SystemPingerFactory(int maxPayloadBytes = TracerouteOptions.DefaultPayloadBytes)
    {
        if (maxPayloadBytes is < 0 or > SystemPinger.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        _maxPayloadBytes = maxPayloadBytes;
    }

    /// <inheritdoc/>
    public IPinger Create() => new SystemPinger(_maxPayloadBytes);
}
