using MTRCSLib;
using MTRCSLib.Abstractions;
using Spectre.Console;

namespace MTRCS;

/// <summary>
/// Builds and refreshes a Spectre.Console <see cref="Table"/> that mimics MTR's terminal output.
///
/// MTR column layout:
///   HOST                       Loss%   Snt   Last    Avg   Best   Wrst  StDev
///
/// All rendering is zero/low-allocation:
///   <list type="bullet">
///     <item>A single <see cref="HopStats"/> stack-span is reused each refresh cycle.</item>
///     <item>Numeric formatting uses <c>TryFormat</c> into stack-allocated <see cref="Span{T}"/>.</item>
///     <item>The <see cref="Table"/> object is rebuilt each frame (Spectre requires it for live
///           updates) but the <see cref="TableTitle"/>, <see cref="TableColumn"/> objects, TTL
///           prefix strings, and per-hop label markup are all pre-computed and reused.</item>
///   </list>
/// </summary>
internal sealed class MtrRenderer
{
    // Column indices (keep in sync with BuildTable column order)
    private const int ColHost  = 0;
    private const int ColLoss  = 1;
    // Must match the Width() passed to the HOST TableColumn below.
    private const int ColHostWidth = 40;
    private const int ColSnt   = 2;
    private const int ColLast  = 3;
    private const int ColAvg   = 4;
    private const int ColBest  = 5;
    private const int ColWrst  = 6;
    private const int ColStDev = 7;
    private const int ColJitter = 8;
    private const int ColAsn   = 9;

    private readonly TracerouteOptions _options;
    // Reused snapshot buffer — avoids per-refresh heap allocation for the hop array.
    private readonly HopStats[] _snapBuffer;

    // ── per-session cached objects (computed once in constructor) ─────────────

    /// <summary>Title markup built once from the resolved target; reused every frame.</summary>
    private readonly TableTitle _tableTitle;

    /// <summary>Column descriptors built once; reused every frame.</summary>
    private TableColumn[] _columns;

    /// <summary>
    /// TTL prefix strings, e.g. _ttlPrefixes[0] == " 1." for hop 1 (1-based TTL, 0-based index).
    /// Pre-computed to avoid per-frame int.ToString() + interpolation.
    /// </summary>
    private readonly string[] _ttlPrefixes;

    // ── per-hop label cache ───────────────────────────────────────────────────

    /// <summary>
    /// Cached rendered HOST-column markup per hop slot.
    /// Invalidated when the hop's address or hostname changes.
    /// </summary>
    private readonly string?[] _hopLabelCache;

    /// <summary>Last address string seen for each hop slot (used to detect changes).</summary>
    private readonly string?[] _hopLastAddress;

    /// <summary>Last hostname seen for each hop slot (used to detect changes).</summary>
    private readonly string?[] _hopLastHostName;

    internal MtrRenderer(TracerouteOptions options)
    {
        _options = options;
        _snapBuffer = new HopStats[options.MaxHops];

        // Pre-compute table title (once per session).
        string targetIp = options.Target.ToString();
        string titleHost = options.Host.Equals(targetIp, StringComparison.Ordinal)
            ? options.Host
            : $"{options.Host} ({targetIp})";
        _tableTitle = new TableTitle($"[bold]mtrcs[/]  [cyan]{titleHost}[/]  [grey]Keys: q=quit[/]");

        // Pre-compute column descriptors (Spectre doesn't mutate them after AddColumn).
        _columns =
        [
            new TableColumn("[bold]HOST[/]").LeftAligned().Width(40),
            new TableColumn("[bold]Loss%[/]").RightAligned().Width(7),
            new TableColumn("[bold]Snt[/]").RightAligned().Width(5),
            new TableColumn("[bold]Last[/]").RightAligned().Width(7),
            new TableColumn("[bold]Avg[/]").RightAligned().Width(7),
            new TableColumn("[bold]Best[/]").RightAligned().Width(7),
            new TableColumn("[bold]Wrst[/]").RightAligned().Width(7),
            new TableColumn("[bold]StDev[/]").RightAligned().Width(7),
            new TableColumn("[bold]Jitter[/]").RightAligned().Width(7),
        ];

        // Conditionally add ASN column.
        if (options.EnableAsn)
        {
            Array.Resize(ref _columns, _columns.Length + 1);
            _columns[^1] = new TableColumn("[bold]ASN[/]").LeftAligned().Width(22);
        }

        // Pre-compute TTL prefix strings " 1." … "NN." for every possible hop slot.
        _ttlPrefixes = new string[options.MaxHops];
        for (int i = 0; i < options.MaxHops; i++)
        {
            int ttl = i + 1;
            _ttlPrefixes[i] = ttl < 10 ? $" {ttl}." : $"{ttl}.";
        }

        // Allocate per-hop label cache.
        _hopLabelCache    = new string?[options.MaxHops];
        _hopLastAddress   = new string?[options.MaxHops];
        _hopLastHostName  = new string?[options.MaxHops];
    }

    /// <summary>Returns an empty table with columns — used as the initial live target.</summary>
    internal Table BuildTable() => CreateTable(0, ReadOnlySpan<HopStats>.Empty);

    /// <summary>
    /// Reads a snapshot from <paramref name="session"/> and returns a fully-populated table.
    /// Called from the render loop at ~10 Hz; allocates only the strings Spectre needs.
    /// </summary>
    internal Table Refresh(ITracerouteSession session)
    {
        int count = session.SnapshotHops(_snapBuffer);
        return CreateTable(count, _snapBuffer.AsSpan(0, count));
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private Table CreateTable(int hopCount, ReadOnlySpan<HopStats> hops)
    {
        var table = new Table();
        table.Border(TableBorder.None);
        table.Expand();
        table.ShowHeaders();

        // Reuse the pre-computed title and column objects — avoids string interpolation
        // and Spectre markup parsing on every 10 Hz frame.
        table.Title = _tableTitle;
        foreach (TableColumn col in _columns)
            table.AddColumn(col);

        if (hopCount == 0)
        {
            // Fill the waiting row with empty cells for every column after the first.
            string[] waitingRow = new string[_columns.Length];
            waitingRow[0] = "[grey]Waiting for first probe cycle...[/]";
            for (int c = 1; c < waitingRow.Length; c++) waitingRow[c] = "";
            table.AddRow(waitingRow);
            return table;
        }

        Span<char> numBuf = stackalloc char[16];

        for (int i = 0; i < hopCount; i++)
        {
            ref readonly HopStats h = ref hops[i];

            // ── HOST column ──────────────────────────────────────────────────
            string hopLabel = GetOrBuildHopLabel(i, in h);

            // ── Loss% ────────────────────────────────────────────────────────
            string loss = FormatPercent(h.LossPercent, numBuf);
            string lossMarkup = h.LossPercent >= 10.0
                ? $"[red]{loss}[/]"
                : h.LossPercent > 0.0
                    ? $"[yellow]{loss}[/]"
                    : loss;

            // ── Snt ──────────────────────────────────────────────────────────
            string snt = FormatInt(h.Sent, numBuf);

            // ── RTT columns ──────────────────────────────────────────────────
            bool hasRtt = h.Sent > h.Lost; // at least one reply

            string last = hasRtt ? FormatMs(h.Last, numBuf) : "???";
            string avg = hasRtt ? FormatMs(h.Average, numBuf) : "???";
            string best = hasRtt && h.Best < double.MaxValue ? FormatMs(h.Best, numBuf) : "???";
            string wrst = hasRtt && h.Worst > double.MinValue ? FormatMs(h.Worst, numBuf) : "???";
            string stdev  = hasRtt ? FormatMs(h.StdDev, numBuf) : "???";
            string jitter = !double.IsNaN(h.Jitter) ? FormatMs(h.Jitter, numBuf) : "???";

            if (_options.EnableAsn)
            {
                string asnDisplay = !h.AsnResolved ? "..." : h.Asn?.ToString() ?? "???";
                table.AddRow(hopLabel, lossMarkup, snt, last, avg, best, wrst, stdev, jitter,
                    $"[grey]{EscapeMarkup(asnDisplay)}[/]");
            }
            else
            {
                table.AddRow(hopLabel, lossMarkup, snt, last, avg, best, wrst, stdev, jitter);
            }
        }

        return table;
    }

    /// <summary>
    /// Returns the HOST-column markup for hop slot <paramref name="hopIndex"/>, rebuilding it
    /// only when the hop's address or hostname has changed since the last frame.
    /// </summary>
    private string GetOrBuildHopLabel(int hopIndex, in HopStats h)
    {
        string? currentAddress  = h.Address?.ToString();
        string? currentHostName = h.HostName;

        // Return the cached label if nothing has changed.
        if (_hopLabelCache[hopIndex] is not null &&
            currentAddress  == _hopLastAddress[hopIndex] &&
            currentHostName == _hopLastHostName[hopIndex])
        {
            return _hopLabelCache[hopIndex]!;
        }

        string label = BuildHopLabel(_ttlPrefixes[hopIndex], currentAddress, currentHostName);

        _hopLabelCache[hopIndex]    = label;
        _hopLastAddress[hopIndex]   = currentAddress;
        _hopLastHostName[hopIndex]  = currentHostName;

        return label;
    }

    private static string BuildHopLabel(string prefix, string? ip, string? hostName)
    {
        if (ip is null)
            return $"{prefix} [grey]???[/]";

        // Show hostname if resolved (and different from the IP string).
        if (hostName is { Length: > 0 } hn &&
            !string.Equals(hn, ip, StringComparison.Ordinal))
        {
            // Ensure the full label fits within the HOST column width (40 chars).
            // Display layout: "{prefix} {hn} ({ip})"
            // Fixed overhead: prefix.Length + 1 (space) + 2 (" (") + ip.Length + 1 (")")
            int overhead = prefix.Length + 1 + 2 + ip.Length + 1;
            int maxHnLen = ColHostWidth - overhead;

            if (maxHnLen <= 0)
            {
                // IP alone with truncated suffix — extremely narrow edge case
                return $"{prefix} {ip}";
            }

            if (hn.Length > maxHnLen)
                hn = string.Concat(hn.AsSpan(0, maxHnLen - 1), "…");

            return $"{prefix} {EscapeMarkup(hn)} [grey]({ip})[/]";
        }

        // DNS in-flight: show IP; once resolved, the hostname replaces it.
        return $"{prefix} {ip}";
    }

    // ── formatting helpers (stack-char, no heap) ──────────────────────────────

    private static string FormatMs(double ms, Span<char> buf)
    {
        // Format as "NNN.N" (one decimal place, max 5 significant chars + unit).
        if (ms.TryFormat(buf, out int written, "F1"))
            return new string(buf[..written]);
        return ms.ToString("F1");
    }

    private static string FormatPercent(double pct, Span<char> buf)
    {
        if (pct.TryFormat(buf, out int written, "F1"))
            return new string(buf[..written]);
        return pct.ToString("F1");
    }

    private static string FormatInt(int value, Span<char> buf)
    {
        if (value.TryFormat(buf, out int written))
            return new string(buf[..written]);
        return value.ToString();
    }

    /// <summary>Escapes Spectre.Console markup characters in arbitrary strings (hostnames).</summary>
    private static string EscapeMarkup(string s) => s
        .Replace("[", "[[", StringComparison.Ordinal)
        .Replace("]", "]]", StringComparison.Ordinal);
}
