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
///           updates) but column and row objects reuse string literals where possible.</item>
///   </list>
/// </summary>
internal sealed class MtrRenderer
{
    // Column indices (keep in sync with BuildTable column order)
    private const int ColHost = 0;
    private const int ColLoss = 1;
    // Must match the Width() passed to the HOST TableColumn below.
    private const int ColHostWidth = 40;
    private const int ColSnt = 2;
    private const int ColLast = 3;
    private const int ColAvg = 4;
    private const int ColBest = 5;
    private const int ColWrst = 6;
    private const int ColStDev = 7;

    private readonly TracerouteOptions _options;
    // Reused snapshot buffer — avoids per-refresh heap allocation for the hop array.
    private readonly HopStats[] _snapBuffer;

    internal MtrRenderer(TracerouteOptions options)
    {
        _options = options;
        _snapBuffer = new HopStats[options.MaxHops];
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

        // Header: show target name + IP similar to MTR's banner.
        string title = _options.Host.Equals(_options.Target.ToString(), StringComparison.Ordinal)
            ? _options.Host
            : $"{_options.Host} ({_options.Target})";

        table.Title = new TableTitle($"[bold]mtrcs[/]  [cyan]{title}[/]  [grey]Keys: q=quit[/]");

        // Columns
        table.AddColumn(new TableColumn("[bold]HOST[/]").LeftAligned().Width(40));
        table.AddColumn(new TableColumn("[bold]Loss%[/]").RightAligned().Width(7));
        table.AddColumn(new TableColumn("[bold]Snt[/]").RightAligned().Width(5));
        table.AddColumn(new TableColumn("[bold]Last[/]").RightAligned().Width(7));
        table.AddColumn(new TableColumn("[bold]Avg[/]").RightAligned().Width(7));
        table.AddColumn(new TableColumn("[bold]Best[/]").RightAligned().Width(7));
        table.AddColumn(new TableColumn("[bold]Wrst[/]").RightAligned().Width(7));
        table.AddColumn(new TableColumn("[bold]StDev[/]").RightAligned().Width(7));

        if (hopCount == 0)
        {
            table.AddRow("[grey]Waiting for first probe cycle...[/]", "", "", "", "", "", "", "");
            return table;
        }

        Span<char> numBuf = stackalloc char[16];

        for (int i = 0; i < hopCount; i++)
        {
            ref readonly HopStats h = ref hops[i];

            // ── HOST column ──────────────────────────────────────────────────
            string hopLabel = BuildHopLabel(i + 1, in h);

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
            string stdev = hasRtt ? FormatMs(h.StdDev, numBuf) : "???";

            table.AddRow(hopLabel, lossMarkup, snt, last, avg, best, wrst, stdev);
        }

        return table;
    }

    private static string BuildHopLabel(int ttl, in HopStats h)
    {
        // TTL number is 1-padded to 3 chars like MTR: " 1. host"
        string number = ttl.ToString();
        string prefix = ttl < 10 ? $" {number}." : $"{number}.";

        if (h.Address is null)
            return $"{prefix} [grey]???[/]";

        string ip = h.Address.ToString();

        // Show hostname if resolved (and different from the IP string).
        if (h.HostName is { Length: > 0 } hn &&
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
