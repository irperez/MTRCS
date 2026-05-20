using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// Production <see cref="IPingerFactory"/> that creates <see cref="SystemPinger"/> instances.
/// </summary>
public sealed class SystemPingerFactory : IPingerFactory
{
    private readonly int _maxPayloadBytes;
    private readonly int _dscpValue;

    /// <param name="maxPayloadBytes">
    /// Maximum ICMP data payload bytes passed to each <see cref="SystemPinger"/>.
    /// Defaults to <see cref="TracerouteOptions.DefaultPayloadBytes"/>.
    /// </param>
    /// <param name="dscpValue">DSCP value (0–63) embedded in probe packets, or 0 for the OS default.</param>
    public SystemPingerFactory(int maxPayloadBytes = TracerouteOptions.DefaultPayloadBytes, int dscpValue = 0)
    {
        if (maxPayloadBytes is < 0 or > SystemPinger.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        _maxPayloadBytes = maxPayloadBytes;
        _dscpValue = dscpValue;
    }

    /// <inheritdoc/>
    public IPinger Create() => new SystemPinger(_maxPayloadBytes, _dscpValue);
}
