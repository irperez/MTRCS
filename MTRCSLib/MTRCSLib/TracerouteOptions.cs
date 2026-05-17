using System.Net;

namespace MTRCSLib;

/// <summary>
/// Immutable configuration for a traceroute run.
/// </summary>
public readonly struct TracerouteOptions
{
    /// <summary>MTR default — maximum number of hops before giving up.</summary>
    public const int DefaultMaxHops = 30;

    /// <summary>Default probe interval in milliseconds (1 second, matching MTR).</summary>
    public const int DefaultIntervalMs = 1000;

    /// <summary>Default per-probe timeout in milliseconds.</summary>
    public const int DefaultTimeoutMs = 800;

    /// <summary>Default ICMP payload data size in bytes (matching MTR default of 28 bytes total → 20 bytes data after 8-byte header).</summary>
    public const int DefaultPayloadBytes = 28;

    /// <summary>Resolved target IPv4 address.</summary>
    public IPAddress Target { get; }

    /// <summary>Original hostname/address string supplied by the caller.</summary>
    public string Host { get; }

    /// <summary>Maximum number of TTL hops to probe (1–<see cref="DefaultMaxHops"/>).</summary>
    public int MaxHops { get; }

    /// <summary>Milliseconds between probe cycles.</summary>
    public int IntervalMs { get; }

    /// <summary>Per-probe timeout in milliseconds.</summary>
    public int TimeoutMs { get; }

    /// <summary>Number of data bytes appended after the 8-byte ICMP header.</summary>
    public int PayloadBytes { get; }

    /// <summary>When <see langword="true"/>, the session resolves ASN info for each hop via Team Cymru DNS.</summary>
    public bool EnableAsn { get; }

    private TracerouteOptions(
        IPAddress target,
        string host,
        int maxHops,
        int intervalMs,
        int timeoutMs,
        int payloadBytes,
        bool enableAsn)
    {
        Target = target;
        Host = host;
        MaxHops = maxHops;
        IntervalMs = intervalMs;
        TimeoutMs = timeoutMs;
        PayloadBytes = payloadBytes;
        EnableAsn = enableAsn;
    }

    /// <summary>
    /// Creates a <see cref="TracerouteOptions"/> from an already-resolved <see cref="IPAddress"/>.
    /// </summary>
    public static TracerouteOptions Create(
        IPAddress target,
        string host,
        int maxHops = DefaultMaxHops,
        int intervalMs = DefaultIntervalMs,
        int timeoutMs = DefaultTimeoutMs,
        int payloadBytes = DefaultPayloadBytes,
        bool enableAsn = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(host);

        if (!NetworkUtils.IsIPv4(target))
            throw new ArgumentException("Only IPv4 targets are supported.", nameof(target));
        if (maxHops is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(maxHops), "Must be between 1 and 255.");
        if (intervalMs < 1)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "Must be positive.");
        if (timeoutMs < 1)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Must be positive.");
        if (payloadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes), "Must be non-negative.");

        return new TracerouteOptions(target, host, maxHops, intervalMs, timeoutMs, payloadBytes, enableAsn);
    }

    /// <summary>
    /// Resolves <paramref name="host"/> via DNS (first IPv4 result) and returns a configured
    /// <see cref="TracerouteOptions"/>. Throws <see cref="InvalidOperationException"/> if no
    /// IPv4 address is found for the host.
    /// </summary>
    public static async ValueTask<TracerouteOptions> ResolveAsync(
        string host,
        int maxHops = DefaultMaxHops,
        int intervalMs = DefaultIntervalMs,
        int timeoutMs = DefaultTimeoutMs,
        int payloadBytes = DefaultPayloadBytes,
        bool enableAsn = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        // Try parse first to avoid a DNS round-trip for literal IPs.
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            if (!NetworkUtils.IsIPv4(parsed))
                throw new ArgumentException("Only IPv4 targets are supported.", nameof(host));
            return Create(parsed, host, maxHops, intervalMs, timeoutMs, payloadBytes, enableAsn);
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, System.Net.Sockets.AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
            throw new InvalidOperationException($"No IPv4 address found for host '{host}'.");

        return Create(addresses[0], host, maxHops, intervalMs, timeoutMs, payloadBytes, enableAsn);
    }
}
