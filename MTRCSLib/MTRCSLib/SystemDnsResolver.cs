using System.Net;
using System.Net.Sockets;
using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// Production <see cref="IDnsResolver"/> that performs a reverse-DNS (PTR) lookup
/// via <see cref="Dns.GetHostEntryAsync(IPAddress)"/>.
/// Never throws — returns <see langword="null"/> on any failure.
/// </summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    /// <inheritdoc/>
    public async ValueTask<string?> ResolveAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        try
        {
            IPHostEntry entry = await Dns
                .GetHostEntryAsync(address.ToString(), AddressFamily.InterNetwork, cancellationToken)
                .ConfigureAwait(false);

            string hostName = entry.HostName;

            // If the resolver just echoed back the IP string, treat it as unresolved.
            return string.Equals(hostName, address.ToString(), StringComparison.Ordinal)
                ? null
                : hostName;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // DNS failure is non-fatal — the hop simply shows the IP.
            return null;
        }
    }
}
