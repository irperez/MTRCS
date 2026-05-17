namespace MTRCSLib;

/// <summary>
/// ASN information for a single hop, returned by <see cref="Abstractions.IAsnResolver"/>.
/// </summary>
/// <param name="Asn">Autonomous System Number (e.g. "AS15169").</param>
/// <param name="Description">Short network/org description (e.g. "GOOGLE").</param>
public sealed record AsnInfo(string Asn, string Description)
{
    /// <summary>Compact display string, e.g. "AS15169 GOOGLE".</summary>
    public override string ToString() => $"{Asn} {Description}";
}
