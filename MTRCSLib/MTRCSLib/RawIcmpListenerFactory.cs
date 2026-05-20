using System.Net.Sockets;
using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// Creates the appropriate <see cref="IRawIcmpListener"/> implementation based on
/// the target address family.
/// </summary>
internal static class RawIcmpListenerFactory
{
    /// <summary>
    /// Returns a new <see cref="RawIcmpListenerV4"/> for <see cref="AddressFamily.InterNetwork"/>
    /// or a new <see cref="RawIcmpListenerV6"/> for <see cref="AddressFamily.InterNetworkV6"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown for unsupported address families.</exception>
    public static IRawIcmpListener Create(AddressFamily addressFamily) =>
        addressFamily switch
        {
            AddressFamily.InterNetwork      => new RawIcmpListenerV4(),
            AddressFamily.InterNetworkV6    => new RawIcmpListenerV6(),
            _ => throw new ArgumentException($"Unsupported address family: {addressFamily}.", nameof(addressFamily)),
        };
}
