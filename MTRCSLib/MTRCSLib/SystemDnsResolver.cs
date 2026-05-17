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
            string addressStr = address.ToString();
            IPHostEntry entry = await Dns
                .GetHostEntryAsync(addressStr, AddressFamily.InterNetwork, cancellationToken)
                .ConfigureAwait(false);

            // If the resolver just echoed back the IP string, treat it as unresolved.
            return string.Equals(entry.HostName, addressStr, StringComparison.Ordinal)
                ? null
                : entry.HostName;
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
