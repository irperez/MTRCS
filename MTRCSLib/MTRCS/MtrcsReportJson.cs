namespace MTRCS;

/// <summary>
/// Root JSON shape for a mtrcs report export.
/// Uses concrete named types so the AOT source generator can emit
/// the required serialization metadata without reflection.
/// </summary>
internal sealed class MtrcsReportJson
{
    public string Host      { get; init; } = string.Empty;
    public string Target    { get; init; } = string.Empty;
    public DateTimeOffset Generated { get; init; }
    public List<HopEntryJson> Hops  { get; init; } = [];
}

/// <summary>Per-hop entry in the JSON report.</summary>
internal sealed class HopEntryJson
{
    public int     Hop    { get; init; }
    public string? Ip     { get; init; }
    public string? Host   { get; init; }
    public double  Loss   { get; init; }
    public int     Snt    { get; init; }
    public double? Last   { get; init; }
    public double? Avg    { get; init; }
    public double? Best   { get; init; }
    public double? Wrst   { get; init; }
    public double? StDev  { get; init; }
    public double? Jitter { get; init; }
    // Populated only when --percentiles is enabled; omitted from output when null.
    public double? P95    { get; init; }
    public double? P99    { get; init; }
    // Populated only when --asn is enabled; omitted from output when null.
    public string? Asn    { get; init; }
    public string? AsnDesc { get; init; }
}
