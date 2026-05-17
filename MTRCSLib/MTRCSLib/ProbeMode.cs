namespace MTRCSLib;

/// <summary>
/// Probe protocol used for traceroute hops.
/// </summary>
public enum ProbeMode : byte
{
    /// <summary>ICMP Echo Request (default, like classic traceroute/ping).</summary>
    Icmp = 0,

    /// <summary>TCP SYN probe (-T). Reaches hosts that block ICMP but accept TCP.</summary>
    Tcp = 1,

    /// <summary>UDP probe (-u). Reaches hosts that block ICMP but pass UDP.</summary>
    Udp = 2,
}
