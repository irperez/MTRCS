namespace MTRCSLib.Abstractions;

/// <summary>
/// Creates <see cref="IPinger"/> instances.
/// A factory is used so the session can create one pinger per TTL hop (or per cycle)
/// and the factory itself is injectable for testing.
/// </summary>
public interface IPingerFactory
{
    /// <summary>Creates a new <see cref="IPinger"/> ready for use.</summary>
    IPinger Create();
}
