using System.Text;
using System.Text.Json;
using MTRCSLib;
using MTRCSLib.Abstractions;

namespace MTRCS;

/// <summary>
/// Exports a completed traceroute session snapshot to a file in text, CSV, or JSON format.
/// </summary>
internal sealed class ReportExporter(TracerouteOptions options)
{
    private readonly HopStats[] _snapBuffer = new HopStats[options.MaxHops];

    /// <summary>
    /// Snapshots <paramref name="session"/> and writes the report to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="session">The completed session to snapshot.</param>
    /// <param name="outputPath">Destination file path.</param>
    /// <param name="format">One of: <c>text</c>, <c>csv</c>, <c>json</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task ExportAsync(
        ITracerouteSession session,
        string outputPath,
        string format,
        CancellationToken cancellationToken = default)
    {
        int count = session.SnapshotHops(_snapBuffer);
        var hops = _snapBuffer.AsSpan(0, count);

        string content = format.ToLowerInvariant() switch
        {
            "csv"  => BuildCsv(hops, options),
            "json" => BuildJson(hops, options),
            _      => BuildText(hops, options),
        };

        await File.WriteAllTextAsync(outputPath, content, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    // ── formatters ────────────────────────────────────────────────────────────

    private static string BuildText(ReadOnlySpan<HopStats> hops, TracerouteOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"mtrcs report — {options.Host} ({options.Target})");
        sb.AppendLine($"Generated: {DateTimeOffset.UtcNow:u}");
        sb.AppendLine();

        string header = (options.EnableAsn, options.ShowPercentiles) switch
        {
            (true,  true)  => $"{"HOST",-40}  {"Loss%",7}  {"Snt",5}  {"Last",7}  {"Avg",7}  {"Best",7}  {"Wrst",7}  {"StDev",7}  {"Jitter",7}  {"P95",7}  {"P99",7}  {"ASN",-22}",
            (true,  false) => $"{"HOST",-40}  {"Loss%",7}  {"Snt",5}  {"Last",7}  {"Avg",7}  {"Best",7}  {"Wrst",7}  {"StDev",7}  {"Jitter",7}  {"ASN",-22}",
            (false, true)  => $"{"HOST",-40}  {"Loss%",7}  {"Snt",5}  {"Last",7}  {"Avg",7}  {"Best",7}  {"Wrst",7}  {"StDev",7}  {"Jitter",7}  {"P95",7}  {"P99",7}",
            _              => $"{"HOST",-40}  {"Loss%",7}  {"Snt",5}  {"Last",7}  {"Avg",7}  {"Best",7}  {"Wrst",7}  {"StDev",7}  {"Jitter",7}",
        };
        sb.AppendLine(header);
        sb.AppendLine(new string('-', header.Length));

        for (int i = 0; i < hops.Length; i++)
        {
            ref readonly HopStats h = ref hops[i];
            bool hasRtt = h.Sent > h.Lost;

            string prefix = (i + 1) < 10 ? $" {i + 1}." : $"{i + 1}.";
            string host   = FormatHost(prefix, h);
            string loss   = h.LossPercent.ToString("F1");
            string snt    = h.Sent.ToString();
            string last   = hasRtt ? h.Last.ToString("F1")   : "???";
            string avg    = hasRtt ? h.Average.ToString("F1") : "???";
            string best   = hasRtt && h.Best < double.MaxValue ? h.Best.ToString("F1")   : "???";
            string wrst   = hasRtt && h.Worst > double.MinValue ? h.Worst.ToString("F1") : "???";
            string stdev  = hasRtt ? h.StdDev.ToString("F1")  : "???";
            string jitter = !double.IsNaN(h.Jitter) ? h.Jitter.ToString("F1") : "???";
            string p95    = hasRtt && !double.IsNaN(h.P95) ? h.P95.ToString("F1") : "???";
            string p99    = hasRtt && !double.IsNaN(h.P99) ? h.P99.ToString("F1") : "???";

            string rowBase = $"{host,-40}  {loss,7}  {snt,5}  {last,7}  {avg,7}  {best,7}  {wrst,7}  {stdev,7}  {jitter,7}";
            string rowPercentiles = options.ShowPercentiles ? $"  {p95,7}  {p99,7}" : "";

            if (options.EnableAsn)
            {
                string asn = !h.AsnResolved ? "..." : h.Asn?.ToString() ?? "???";
                sb.AppendLine($"{rowBase}{rowPercentiles}  {asn,-22}");
            }
            else
            {
                sb.AppendLine($"{rowBase}{rowPercentiles}");
            }
        }

        return sb.ToString();
    }

    private static string BuildCsv(ReadOnlySpan<HopStats> hops, TracerouteOptions options)
    {
        var sb = new StringBuilder();

        string header = (options.EnableAsn, options.ShowPercentiles) switch
        {
            (true,  true)  => "Hop,Host,IP,Loss%,Snt,Last,Avg,Best,Wrst,StDev,Jitter,P95,P99,ASN,ASNDesc",
            (true,  false) => "Hop,Host,IP,Loss%,Snt,Last,Avg,Best,Wrst,StDev,Jitter,ASN,ASNDesc",
            (false, true)  => "Hop,Host,IP,Loss%,Snt,Last,Avg,Best,Wrst,StDev,Jitter,P95,P99",
            _              => "Hop,Host,IP,Loss%,Snt,Last,Avg,Best,Wrst,StDev,Jitter",
        };
        sb.AppendLine(header);

        for (int i = 0; i < hops.Length; i++)
        {
            ref readonly HopStats h = ref hops[i];
            bool hasRtt = h.Sent > h.Lost;

            string ip   = h.Address?.ToString() ?? "";
            string host = h.HostName is { Length: > 0 } hn ? hn : ip;
            string loss  = h.LossPercent.ToString("F1");
            string last  = hasRtt ? h.Last.ToString("F1")   : "";
            string avg   = hasRtt ? h.Average.ToString("F1") : "";
            string best  = hasRtt && h.Best < double.MaxValue  ? h.Best.ToString("F1")   : "";
            string wrst  = hasRtt && h.Worst > double.MinValue ? h.Worst.ToString("F1")  : "";
            string stdev = hasRtt ? h.StdDev.ToString("F1")   : "";
            string jitter = !double.IsNaN(h.Jitter) ? h.Jitter.ToString("F1") : "";
            string p95 = hasRtt && !double.IsNaN(h.P95) ? h.P95.ToString("F1") : "";
            string p99 = hasRtt && !double.IsNaN(h.P99) ? h.P99.ToString("F1") : "";

            string rowBase = $"{i + 1},{CsvEscape(host)},{CsvEscape(ip)},{loss},{h.Sent},{last},{avg},{best},{wrst},{stdev},{jitter}";
            string rowPercentiles = options.ShowPercentiles ? $",{p95},{p99}" : "";

            if (options.EnableAsn)
            {
                string asnNum  = h.Asn?.Asn ?? "";
                string asnDesc = h.Asn?.Description ?? "";
                sb.AppendLine($"{rowBase}{rowPercentiles},{CsvEscape(asnNum)},{CsvEscape(asnDesc)}");
            }
            else
            {
                sb.AppendLine($"{rowBase}{rowPercentiles}");
            }
        }

        return sb.ToString();
    }

    private static string BuildJson(ReadOnlySpan<HopStats> hops, TracerouteOptions options)
    {
        var entries = new List<HopEntryJson>(hops.Length);

        for (int i = 0; i < hops.Length; i++)
        {
            ref readonly HopStats h = ref hops[i];
            bool hasRtt = h.Sent > h.Lost;

            entries.Add(new HopEntryJson
            {
                Hop    = i + 1,
                Ip     = h.Address?.ToString(),
                Host   = h.HostName,
                Loss   = Math.Round(h.LossPercent, 1),
                Snt    = h.Sent,
                Last   = hasRtt ? Math.Round(h.Last, 1)    : null,
                Avg    = hasRtt ? Math.Round(h.Average, 1) : null,
                Best   = hasRtt && h.Best  < double.MaxValue  ? Math.Round(h.Best, 1)  : null,
                Wrst   = hasRtt && h.Worst > double.MinValue ? Math.Round(h.Worst, 1)  : null,
                StDev  = hasRtt ? Math.Round(h.StdDev, 1)  : null,
                Jitter = !double.IsNaN(h.Jitter) ? Math.Round(h.Jitter, 1) : null,
                // P95/P99 are null when --percentiles is not enabled; WhenWritingNull omits them.
                P95    = options.ShowPercentiles && hasRtt && !double.IsNaN(h.P95) ? Math.Round(h.P95, 1) : null,
                P99    = options.ShowPercentiles && hasRtt && !double.IsNaN(h.P99) ? Math.Round(h.P99, 1) : null,
                // Asn/AsnDesc are null when --asn is not enabled; WhenWritingNull omits them.
                Asn     = options.EnableAsn ? h.Asn?.Asn         : null,
                AsnDesc = options.EnableAsn ? h.Asn?.Description : null,
            });
        }

        var report = new MtrcsReportJson
        {
            Host      = options.Host,
            Target    = options.Target.ToString(),
            Generated = DateTimeOffset.UtcNow,
            Hops      = entries,
        };

        return JsonSerializer.Serialize(report, MtrcsReportJsonContext.Default.MtrcsReportJson);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string FormatHost(string prefix, in HopStats h)
    {
        string? ip = h.Address?.ToString();
        if (ip is null) return $"{prefix} ???";

        string? hn = h.HostName;
        if (hn is { Length: > 0 } && !string.Equals(hn, ip, StringComparison.Ordinal))
            return $"{prefix} {hn} ({ip})";

        return $"{prefix} {ip}";
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
