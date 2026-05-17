using System.Net;

namespace MTRCSLib.Abstractions;

/// <summary>
/// Resolves the Autonomous System Number (ASN) and network description for an IPv4 address.
/// </summary>
public interface IAsnResolver
{
    /// <summary>
    /// Returns ASN information for <paramref name="address"/>, or <see langword="null"/> if
    /// the lookup fails or no record is found.
    /// </summary>
    ValueTask<AsnInfo?> ResolveAsync(IPAddress address, CancellationToken cancellationToken = default);
}
