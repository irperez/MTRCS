using System.Net;

namespace MTRCSLib;

/// <summary>
/// Immutable configuration for a traceroute run.
/// </summary>
public readonly struct TracerouteOptions
{
    /// <summary>MTR default â€” maximum number of hops before giving up.</summary>
    public const int DefaultMaxHops = 30;

    /// <summary>Default probe interval in milliseconds (1 second, matching MTR).</summary>
    public const int DefaultIntervalMs = 1000;

    /// <summary>Default per-probe timeout in milliseconds.</summary>
    public const int DefaultTimeoutMs = 800;

    /// <summary>Default ICMP payload data size in bytes (matching MTR default of 28 bytes total â†’ 20 bytes data after 8-byte header).</summary>
    public const int DefaultPayloadBytes = 28;

    /// <summary>Resolved target IPv4 address.</summary>
    public IPAddress Target { get; }

    /// <summary>Original hostname/address string supplied by the caller.</summary>
    public string Host { get; }

    /// <summary>Maximum number of TTL hops to probe (1â€“<see cref="DefaultMaxHops"/>).</summary>
    public int MaxHops { get; }

    /// <summary>Milliseconds between probe cycles.</summary>
    public int IntervalMs { get; }

    /// <summary>Per-probe timeout in milliseconds.</summary>
    public int TimeoutMs { get; }

    /// <summary>Number of data bytes appended after the 8-byte ICMP header.</summary>
    public int PayloadBytes { get; }

    /// <summary>When <see langword="true"/>, the session resolves ASN info for each hop via Team Cymru DNS.</summary>
    public bool EnableAsn { get; }

    /// <summary>
    /// When <see langword="true"/>, P95 and P99 percentile columns are displayed in the live view and reports.
    /// </summary>
    public bool ShowPercentiles { get; }

    /// <summary>
    /// DSCP value (0–63) written into the IP header of every probe packet, or 0 for the OS default.
    /// Allows IT pros to verify that QoS policies honour differentiated-services markings hop-by-hop.
    /// </summary>
    public int DscpValue { get; }

    /// <summary>
    /// When <see langword="true"/> (the default), one silent warmup probe cycle is fired
    /// before statistics are recorded.  The first real-world ping is almost always inflated
    /// due to cold ARP/routing/socket caches; discarding it keeps charts and averages clean.
    /// Set to <see langword="false"/> to include the first ping in all charts and statistics.
    /// </summary>
    public bool WarmupPing { get; }

    /// <summary>Probe protocol: ICMP (default), TCP SYN (-T), or UDP (-u).</summary>
    public ProbeMode Mode { get; }

    /// <summary>
    /// Destination port used for TCP/UDP probes.
    /// Default: 80 for TCP, 33434 for UDP, ignored for ICMP.
    /// </summary>
    public int Port { get; }

    private TracerouteOptions(
        IPAddress target,
        string host,
        int maxHops,
        int intervalMs,
        int timeoutMs,
        int payloadBytes,
        bool enableAsn,
        ProbeMode mode,
        int port,
        bool warmupPing,
        bool showPercentiles,
        int dscpValue)
    {
        Target = target;
        Host = host;
        MaxHops = maxHops;
        IntervalMs = intervalMs;
        TimeoutMs = timeoutMs;
        PayloadBytes = payloadBytes;
        EnableAsn = enableAsn;
        Mode = mode;
        Port = port;
        WarmupPing = warmupPing;
        ShowPercentiles = showPercentiles;
        DscpValue = dscpValue;
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
        bool enableAsn = false,
        ProbeMode mode = ProbeMode.Icmp,
        int port = 0,
        bool warmupPing = true,
        bool showPercentiles = false,
        int dscpValue = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(host);

        if (!NetworkUtils.IsIPv4(target) && !NetworkUtils.IsIPv6(target))
            throw new ArgumentException("Target must be an IPv4 or IPv6 address.", nameof(target));
        if (maxHops is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(maxHops), "Must be between 1 and 255.");
        if (intervalMs < 1)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "Must be positive.");
        if (timeoutMs < 1)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Must be positive.");
        if (payloadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes), "Must be non-negative.");
        if (port < 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Must be between 0 and 65535.");
        if (dscpValue is < 0 or > 63)
            throw new ArgumentOutOfRangeException(nameof(dscpValue), "DSCP value must be between 0 and 63.");

        // Resolve default port per mode when caller passes 0.
        int resolvedPort = port != 0 ? port : mode switch
        {
            ProbeMode.Tcp => 80,
            ProbeMode.Udp => UdpPinger.DefaultUdpPort,
            _ => 0,
        };

        return new TracerouteOptions(target, host, maxHops, intervalMs, timeoutMs, payloadBytes, enableAsn, mode, resolvedPort, warmupPing, showPercentiles, dscpValue);
    }

    /// <summary>
    /// Resolves <paramref name="host"/> via DNS and returns a configured <see cref="TracerouteOptions"/>.
    /// When <paramref name="preferIPv6"/> is <see langword="true"/> an IPv6 address is preferred;
    /// otherwise IPv4 is preferred with IPv6 as fallback when no IPv4 result is found.
    /// Throws <see cref="InvalidOperationException"/> if no address is found.
    /// </summary>
    public static async ValueTask<TracerouteOptions> ResolveAsync(
        string host,
        int maxHops = DefaultMaxHops,
        int intervalMs = DefaultIntervalMs,
        int timeoutMs = DefaultTimeoutMs,
        int payloadBytes = DefaultPayloadBytes,
        bool enableAsn = false,
        ProbeMode mode = ProbeMode.Icmp,
        int port = 0,
        bool warmupPing = true,
        bool preferIPv6 = false,
        bool showPercentiles = false,
        int dscpValue = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        // Try parse first to avoid a DNS round-trip for literal IPs.
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            if (!NetworkUtils.IsIPv4(parsed) && !NetworkUtils.IsIPv6(parsed))
                throw new ArgumentException("Host must be a valid IPv4 or IPv6 address.", nameof(host));
            return Create(parsed, host, maxHops, intervalMs, timeoutMs, payloadBytes, enableAsn, mode, port, warmupPing, showPercentiles, dscpValue);
        }

        IPAddress[] all = await TryResolveAsync(host, System.Net.Sockets.AddressFamily.Unspecified, cancellationToken).ConfigureAwait(false);

        if (all.Length == 0)
            throw new InvalidOperationException($"No IPv4 or IPv6 address found for host '{host}'.");

        var preferredFamily = preferIPv6
            ? System.Net.Sockets.AddressFamily.InterNetworkV6
            : System.Net.Sockets.AddressFamily.InterNetwork;

        IPAddress? preferred = Array.Find(all, a => a.AddressFamily == preferredFamily) ?? all[0];

        return Create(preferred, host, maxHops, intervalMs, timeoutMs, payloadBytes, enableAsn, mode, port, warmupPing, showPercentiles, dscpValue);
    }

    /// <summary>
    /// Calls <see cref="Dns.GetHostAddressesAsync"/> and returns an empty array instead of
    /// throwing when no records of the requested type exist.
    /// On Windows the DNS stack throws <see cref="System.Net.Sockets.SocketException"/> with
    /// <see cref="System.Net.Sockets.SocketError.NoData"/> or
    /// <see cref="System.Net.Sockets.SocketError.HostNotFound"/> rather than returning an empty
    /// result â€” this wrapper normalises that platform behaviour so the fallback logic runs.
    /// </summary>
    private static async ValueTask<IPAddress[]> TryResolveAsync(
        string host,
        System.Net.Sockets.AddressFamily family,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, family, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException ex)
            when (ex.SocketErrorCode is System.Net.Sockets.SocketError.NoData
                                     or System.Net.Sockets.SocketError.HostNotFound
                                     or System.Net.Sockets.SocketError.TryAgain)
        {
            return [];
        }
    }
}
