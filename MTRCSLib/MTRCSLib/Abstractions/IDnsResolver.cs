using System.Net;

namespace MTRCSLib.Abstractions;

/// <summary>
/// Performs reverse-DNS (PTR) lookup for a given IP address.
/// Abstracted so tests can supply canned results without network I/O.
/// </summary>
public interface IDnsResolver
{
    /// <summary>
    /// Resolves the hostname for <paramref name="address"/>.
    /// Returns <see langword="null"/> if no PTR record exists or resolution fails.
    /// Must not throw — callers treat a null result as "unresolved".
    /// </summary>
    ValueTask<string?> ResolveAsync(IPAddress address, CancellationToken cancellationToken = default);
}
