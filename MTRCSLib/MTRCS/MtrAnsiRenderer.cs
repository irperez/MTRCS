using System.Buffers;
using System.Text;
using MTRCSLib;
using MTRCSLib.Abstractions;

namespace MTRCS;

/// <summary>
/// Zero-allocation live renderer for the MTR-style terminal display.
///
/// Design goals:
/// <list type="bullet">
///   <item>No per-frame heap allocations on the hot path — all string work happens
///         at construction or when hop identity (address/hostname) changes.</item>
///   <item>A single <see cref="AnsiWriter"/> frame buffer is flushed to stdout once per frame.</item>
///   <item>Title and header lines are pre-encoded to UTF-8 bytes at construction and
///         written via <see cref="AnsiWriter.WriteRaw"/> — zero conversion per frame.</item>
///   <item>Per-hop host labels are cached as pre-encoded UTF-8 bytes and rebuilt only
///         when the hop's address or hostname changes.</item>
///   <item>All numeric columns use <c>TryFormat</c> into a stack-allocated <see cref="Span{char}"/>.</item>
/// </list>
///
/// Column layout (matches MTR output):
/// <code>
///   HOST                                     Loss%    Snt   Last    Avg   Best   Wrst  StDev
///   1234567890123456789012345678901234567890  1234567  12345 1234567 1234567 1234567 1234567 1234567
/// </code>
/// </summary>
internal sealed class MtrAnsiRenderer
{
    // ── column widths (chars) ─────────────────────────────────────────────────
    private const int W_Host  = 40;
    private const int W_Loss  =  7;
    private const int W_Snt   =  5;
    private const int W_Rtt   =  7;   // Last / Avg / Best / Wrst / StDev / Jitter all use this width
    private const int W_Asn   = 20;   // ASN column (e.g. "AS15169 GOOGLE")
    private const int W_Spark =  8;   // sparkline column (8 Unicode block chars)

    // Two spaces between every column.
    private const string ColSep = "  ";
    private const int ColSepW = 2;

    // Total width of the stats section (Loss%..Jitter+Sparkline), not including ASN.
    // Loss%(7)+Sep(2)+Snt(5)+Sep(2)+Last(7)+Sep(2)+Avg(7)+Sep(2)+Best(7)+Sep(2)+Wrst(7)+Sep(2)+StDev(7)+Sep(2)+Jitter(7)+Sep(2)+Sparkline(8) = 76
    private const int W_Stats = W_Loss + ColSepW + W_Snt + ColSepW
                              + W_Rtt + ColSepW + W_Rtt + ColSepW
                              + W_Rtt + ColSepW + W_Rtt + ColSepW
                              + W_Rtt + ColSepW + W_Rtt + ColSepW + W_Spark;   // = 76

    // ── pre-encoded constant byte sequences ───────────────────────────────────

    // Header row bytes, encoded once at construction.
    private readonly byte[] _titleBytes;
    private readonly byte[] _keysBytes;
    private readonly byte[] _subHeaderBytes;   // kept for narrow-terminal fallback
    private readonly byte[] _headerBytes;       // kept for narrow-terminal fallback
    private readonly byte[] _hostHeaderBytes;   // "HOST" left-padded to W_Host, bold
    private readonly byte[] _statsHeaderBytes;  // "Loss%  Snt  Last  Avg  Best  Wrst  StDev  Jitter" bold
    private readonly byte[] _asnHeaderBytes;    // "ASN" bold
    private readonly byte[] _sparklineHeaderBytes; // "Graph" bold
    private readonly bool _showAsn;

    // ── thresholds ─────────────────────────────────────────────────────────────
    private readonly RttThresholds _thresholds;

    // ── start timestamp ───────────────────────────────────────────────────────
    private readonly DateTime _startedAt;

    // ── per-hop caches ────────────────────────────────────────────────────────

    // Pre-encoded UTF-8 bytes for the HOST column of each hop.
    // Rebuilt only when address, hostname, or available column width changes.
    private readonly byte[]?[] _hopHostBytes;
    private readonly string?[] _hopLastAddress;
    private readonly string?[] _hopLastHostName;
    private readonly int[]     _hopLastHostWidth;

    // Pre-computed TTL prefix strings " 1." … "NN.".
    private readonly string[] _ttlPrefixes;

    // ── snapshot buffer ───────────────────────────────────────────────────────
    private readonly HopStats[] _snapBuffer;

    // ── sparkline ring sample buffer (per-hop sparkline reuse scratch) ─────────
    private readonly double[] _ringBuf;

    // ── destination latency history (scrolling graph) ─────────────────────────
    // Stores one RTT sample per ping cycle for the destination (last responding hop).
    // Large enough to cover the widest realistic terminal (~500 cols).
    private const int LatencyHistorySize = 512;
    private readonly double[] _latencyHistory = new double[LatencyHistorySize];
    private int _latencyHead;   // next write index (wraps)
    private int _latencyCount;  // samples filled so far (≤ LatencyHistorySize)
    private int _latencyLastSent; // destination hop Sent count at last sample — gates one-per-ping

    // ── frame writer ──────────────────────────────────────────────────────────
    private readonly AnsiWriter _writer;

    // ── spinner ───────────────────────────────────────────────────────────────
    private static ReadOnlySpan<char> SpinnerFrames => "⠉⠘⠰⢠⣀⡄⠆⠃";
    private int _spinnerIndex;

    internal MtrAnsiRenderer(TracerouteOptions options, RttThresholds thresholds = default)
    {
        int maxHops = options.MaxHops;

        _snapBuffer       = new HopStats[maxHops];
        _ringBuf          = new double[HopStats.RingBufferSize];
        _hopHostBytes     = new byte[maxHops][];
        _hopLastAddress   = new string?[maxHops];
        _hopLastHostName  = new string?[maxHops];
        _hopLastHostWidth = new int[maxHops];
        _ttlPrefixes      = new string[maxHops];
        _thresholds       = thresholds;

        for (int i = 0; i < maxHops; i++)
        {
            int ttl = i + 1;
            _ttlPrefixes[i] = ttl < 10 ? $" {ttl}." : $"{ttl}.";
        }

        // Pre-encode the title line.
        string targetIp = options.Target.ToString();
        string titleHost = options.Host.Equals(targetIp, StringComparison.Ordinal)
            ? options.Host
            : $"{options.Host} ({targetIp})";
        _titleBytes       = EncodeTitleLine(titleHost);
        _keysBytes        = EncodeKeysLine();
        _subHeaderBytes   = EncodeSubHeaderLine(options.EnableAsn);
        _headerBytes      = EncodeHeaderLine(options.EnableAsn);
        _hostHeaderBytes  = EncodeHostHeaderBytes();
        _statsHeaderBytes = EncodeStatsHeaderBytes();
        _asnHeaderBytes   = EncodeAsnHeaderBytes();
        _sparklineHeaderBytes = EncodeSparklineHeaderBytes();
        _showAsn          = options.EnableAsn;
        _startedAt        = DateTime.Now;

        // 16 KB frame buffer — ample for 30 hops × ~150 bytes + title/header overhead.
        _writer = new AnsiWriter(16 * 1024);
    }

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>Switches to the alternate screen buffer and hides the cursor before the render loop starts.</summary>
    internal void BeginLive()
    {
        _writer.EnterAlternateScreen();
        _writer.HideCaret();
        _writer.Flush();
    }

    /// <summary>Restores the cursor and returns to the normal screen buffer after the render loop exits.</summary>
    internal void EndLive()
    {
        _writer.ShowCaret();
        _writer.LeaveAlternateScreen();
        _writer.Flush();
    }

    /// <summary>
    /// Snapshots hop stats from <paramref name="session"/> and writes a full frame to stdout.
    /// Zero heap allocations on the hot path once hop identity has been resolved.
    /// </summary>
    internal void RenderFrame(ITracerouteSession session)
    {
        int count = session.SnapshotHops(_snapBuffer);

        _writer.Home();

        // ── title + start timestamp ────────────────────────────────────────────
        _writer.WriteRaw(_titleBytes);
        _writer.EraseEol();         // clear stale chars before jumping to timestamp position
        WriteTitleTimestamp(count > 0);
        _writer.NewLine();

        // ── keys bar ──────────────────────────────────────────────────────────
        _writer.WriteRaw(_keysBytes);
        _writer.EraseEol();
        _writer.NewLine();

        // ── destination latency bar graph ─────────────────────────────────────
        WriteLatencyGraph(count);

        // ── Packets / Pings sub-header ────────────────────────────────────────
        _writer.EraseEol();
        WriteSubHeader();
        _writer.EraseEol();
        _writer.NewLine();

        // ── column header ─────────────────────────────────────────────────────
        WriteHeader();
        _writer.EraseEol();
        _writer.NewLine();

        if (count == 0)
        {
            _writer.Grey();
            _writer.Write("Waiting for first probe cycle...");
            _writer.Reset();
            _writer.EraseEol();
            _writer.NewLine();
            _writer.Flush();
            return;
        }

        // Stack-allocate number formatting buffer (reused across all hops).
        Span<char> numBuf = stackalloc char[16];

        for (int i = 0; i < count; i++)
        {
            ref readonly HopStats h = ref _snapBuffer[i];
            WriteHopLine(i, in h, numBuf, out int hostWidth);
            WriteEcmpLines(in h, hostWidth);
        }

        // Clear any rows left over from a previous frame that had more hops.
        _writer.EraseToEnd();
        _writer.Flush();
    }

    // ── 8-level Unicode bar blocks (▁ ▂ ▃ ▄ ▅ ▆ ▇ █) ─────────────────────────
    private static ReadOnlySpan<char> BarBlocks => "\u2581\u2582\u2583\u2584\u2585\u2586\u2587\u2588";

    // Writes the full-width scrolling latency history chart.
    // One column is appended per ping cycle for the destination (last responding hop).
    // The chart scrolls left as new samples arrive, identical in behaviour to the per-hop sparklines
    // but filling the full terminal width and using color-coded columns.
    // A compact legend sits to the right on the same line, reflecting the active tier thresholds.
    private void WriteLatencyGraph(int hopCount)
    {
        // Legend format: " ■<G  ■<C  ■<Y  ■<R  ■>R ms" where G/C/Y/R are the tier values.
        // Each tier entry is "■<NNN" (max 7 chars) + 2-space separator.
        // We build the legend string to measure its actual width.
        double g = _thresholds.GraphGreen;
        double c = _thresholds.GraphCyan;
        double y = _thresholds.GraphYellow;
        double r = _thresholds.GraphRed;

        // Build legend text (reuse stack buffers — no heap alloc for typical values).
        Span<char> legendBuf = stackalloc char[64];
        int legendPos = 0;

        legendBuf[legendPos++] = ' ';
        legendPos += AppendLegendTier(legendBuf[legendPos..], '\u25a0', '<', g);
        legendBuf[legendPos++] = ' '; legendBuf[legendPos++] = ' ';
        legendPos += AppendLegendTier(legendBuf[legendPos..], '\u25a0', '<', c);
        legendBuf[legendPos++] = ' '; legendBuf[legendPos++] = ' ';
        legendPos += AppendLegendTier(legendBuf[legendPos..], '\u25a0', '<', y);
        legendBuf[legendPos++] = ' '; legendBuf[legendPos++] = ' ';
        legendPos += AppendLegendTier(legendBuf[legendPos..], '\u25a0', '<', r);
        legendBuf[legendPos++] = ' '; legendBuf[legendPos++] = ' ';
        // ">R ms" part
        legendBuf[legendPos++] = '\u25a0';
        legendBuf[legendPos++] = '>';
        legendPos += FormatThresholdValue(legendBuf[legendPos..], r);
        legendBuf[legendPos++] = 'm'; legendBuf[legendPos++] = 's';

        int legendWidth = legendPos; // display width == char count (all ASCII/narrow)

        int consoleWidth = Console.WindowWidth;
        int chartWidth   = consoleWidth - legendWidth;
        if (chartWidth < 4)
        {
            _writer.EraseEol();
            _writer.NewLine();
            return;
        }

        // ── find destination hop (last responding) ─────────────────────────────
        double destLast = double.NaN;
        int    destSent = 0;
        for (int i = hopCount - 1; i >= 0; i--)
        {
            ref readonly HopStats h = ref _snapBuffer[i];
            if (h.Sent > h.Lost && !double.IsNaN(h.Last) && h.Last > 0)
            {
                destLast = h.Last;
                destSent = h.Sent;
                break;
            }
        }

        // ── append one sample per new ping ────────────────────────────────────
        if (!double.IsNaN(destLast) && destSent != _latencyLastSent)
        {
            _latencyHistory[_latencyHead] = destLast;
            _latencyHead = (_latencyHead + 1) % LatencyHistorySize;
            if (_latencyCount < LatencyHistorySize) _latencyCount++;
            _latencyLastSent = destSent;
        }

        // ── build the visible window (oldest → newest, left → right) ──────────
        int visible = Math.Min(chartWidth, _latencyCount);

        // ── adaptive vertical scale over visible window ────────────────────────
        double winMax = 0.0;
        for (int i = 0; i < visible; i++)
        {
            int idx = (_latencyHead - visible + i + LatencyHistorySize) % LatencyHistorySize;
            if (_latencyHistory[idx] > winMax) winMax = _latencyHistory[idx];
        }
        double scaleMax = Math.Max(10.0, winMax * 1.20);

        // ── draw empty (future) columns on the left ────────────────────────────
        int emptyLeft = chartWidth - visible;
        if (emptyLeft > 0)
        {
            _writer.Grey();
            Span<char> pad = stackalloc char[Math.Min(emptyLeft, 512)];
            pad[..Math.Min(emptyLeft, 512)].Fill(' ');
            _writer.WriteFixed(pad[..Math.Min(emptyLeft, 512)], emptyLeft, rightAlign: false);
            _writer.Reset();
        }

        // ── draw each sample column ────────────────────────────────────────────
        Span<char> colBuf = stackalloc char[1];
        for (int i = 0; i < visible; i++)
        {
            int idx = (_latencyHead - visible + i + LatencyHistorySize) % LatencyHistorySize;
            double rtt = _latencyHistory[idx];

            int level = (int)Math.Round(rtt / scaleMax * 7.0);
            level     = Math.Clamp(level, 0, 7);
            colBuf[0] = BarBlocks[level];

            if      (rtt <  g) _writer.Green();
            else if (rtt <  c) _writer.Cyan();
            else if (rtt <  y) _writer.Yellow();
            else if (rtt <  r) _writer.Red();
            else               _writer.Magenta();

            _writer.WriteFixed(colBuf, 1, rightAlign: false);
        }

        _writer.Reset();

        // ── legend ─────────────────────────────────────────────────────────────
        ReadOnlySpan<char> legendSpan = legendBuf[..legendPos];
        // Write " ■<G" in green, "  ■<C" in cyan, etc. — re-render with colors.
        _writer.Write(" ");
        _writer.Green();   WriteThresholdLabel('\u25a0', '<', g);
        _writer.Reset();   _writer.Write("  ");
        _writer.Cyan();    WriteThresholdLabel('\u25a0', '<', c);
        _writer.Reset();   _writer.Write("  ");
        _writer.Yellow();  WriteThresholdLabel('\u25a0', '<', y);
        _writer.Reset();   _writer.Write("  ");
        _writer.Red();     WriteThresholdLabel('\u25a0', '<', r);
        _writer.Reset();   _writer.Write("  ");
        _writer.Magenta(); WriteThresholdLabel('\u25a0', '>', r); _writer.Write("ms");
        _writer.Reset();

        _ = legendSpan; // suppress unused warning

        _writer.EraseEol();
        _writer.NewLine();
    }

    // Appends "■<NNN" (or "■>NNN") into dest and returns chars written.
    private static int AppendLegendTier(Span<char> dest, char symbol, char op, double value)
    {
        int pos = 0;
        dest[pos++] = symbol;
        dest[pos++] = op;
        pos += FormatThresholdValue(dest[pos..], value);
        return pos;
    }

    // Formats a threshold value: integer if whole, else one decimal place.
    private static int FormatThresholdValue(Span<char> dest, double value)
    {
        if (value == Math.Floor(value))
        {
            int iv = (int)value;
            iv.TryFormat(dest, out int w);
            return w;
        }
        value.TryFormat(dest, out int written, "F1");
        return written;
    }

    // Writes a colored threshold label (e.g. "■<5") directly to the frame buffer.
    private void WriteThresholdLabel(char symbol, char op, double value)
    {
        Span<char> buf = stackalloc char[16];
        int pos = 0;
        buf[pos++] = symbol;
        buf[pos++] = op;
        pos += FormatThresholdValue(buf[pos..], value);
        _writer.WriteFixed(buf[..pos], pos, rightAlign: false);
    }

    // Format matches MTR: "ddd MMM  d HH:mm:ss yyyy"  (single-digit day gets extra space).
    // When isActive is true, a spinning ASCII animation is drawn to the left of the timestamp.
    private void WriteTitleTimestamp(bool isActive)
    {
        // Format: "Wed Dec  9 03:07:34 2020"
        Span<char> buf = stackalloc char[32];
        bool ok = _startedAt.TryFormat(buf, out int written, "ddd MMM  d HH:mm:ss yyyy");
        ReadOnlySpan<char> stamp = ok ? buf[..written] : _startedAt.ToString("ddd MMM  d HH:mm:ss yyyy").AsSpan();

        // spinner is 1 char + 1 space gap before the timestamp
        const int spinnerWidth = 2; // char + space
        int totalWidth = stamp.Length + spinnerWidth;

        // Use a cursor-position move to right-align: ESC[{col}G where col = consoleWidth - totalWidth + 1.
        int consoleWidth = Console.WindowWidth;
        if (consoleWidth <= totalWidth) return;   // terminal too narrow — skip

        int col = consoleWidth - totalWidth + 1;  // 1-based column of spinner
        _writer.MoveCursorToColumn(col);

        if (isActive)
        {
            _writer.Cyan();
            Span<char> spinBuf = stackalloc char[1];
            spinBuf[0] = SpinnerFrames[_spinnerIndex];
            _spinnerIndex = (_spinnerIndex + 1) % SpinnerFrames.Length;
            _writer.WriteFixed(spinBuf, 1, rightAlign: false);
            _writer.Reset();
        }
        else
        {
            // Hide spinner with a space when not pinging.
            _writer.WriteFixed(" ".AsSpan(), 1, rightAlign: false);
        }

        _writer.Write(" ");
        _writer.Grey();
        _writer.WriteFixed(stamp, stamp.Length, rightAlign: false);
        _writer.Reset();
    }

    // ── private: frame composition ────────────────────────────────────────────

    // Moves the cursor so the stats section (Loss%..Jitter[+ASN]) ends at the right terminal edge.
    private void MoveToStatsColumn()
    {
        int statsWidth = W_Stats + (_showAsn ? ColSepW + W_Asn : 0);
        int consoleWidth = Console.WindowWidth;
        // Leave at least W_Host chars for the host column; if terminal is too narrow, don't jump.
        int col = consoleWidth - statsWidth;
        if (col <= W_Host) return;
        _writer.MoveCursorToColumn(col);
    }

    // Writes sub-header ("Packets" / "Pings") right-aligned to match MoveToStatsColumn.
    private void WriteSubHeader()
    {
        int statsWidth = W_Stats + (_showAsn ? ColSepW + W_Asn : 0);
        int consoleWidth = Console.WindowWidth;
        int statsStart = consoleWidth - statsWidth;   // leave 1-col margin at right edge
        if (statsStart <= W_Host) return;

        // "Packets" centred over Loss%+ColSep+Snt = 7+2+5 = 14 chars.
        int packetsWidth = W_Loss + ColSepW + W_Snt;               // 14
        int packetsCenter = statsStart + packetsWidth / 2 - 1;     // 1-based mid column

        // "Pings" centred over Last..Jitter = ColSep(2)+6×(W_Rtt+ColSep) − trailing ColSep = 2+6×9−2 = 54 chars.
        int pingsStart = statsStart + packetsWidth + ColSepW;
        int pingsWidth = W_Rtt + ColSepW + W_Rtt + ColSepW + W_Rtt + ColSepW + W_Rtt + ColSepW + W_Rtt + ColSepW + W_Rtt; // 6×7 + 5×2 = 52
        int pingsCenter = pingsStart + pingsWidth / 2 - 1;

        _writer.Bold();
        // Write "Packets" centred: move to (packetsCenter - "Packets".Length/2 + 1).
        int packetsCol = packetsCenter - 3;  // "Packets"(7) half = 3
        if (packetsCol >= 1) _writer.MoveCursorToColumn(packetsCol);
        _writer.Write("Packets");

        // Write "Pings" centred.
        int pingsCol = pingsCenter - 2;      // "Pings"(5) half = 2
        if (pingsCol > packetsCol + 7) _writer.MoveCursorToColumn(pingsCol);
        _writer.Write("Pings");
        _writer.Reset();
    }

    // Writes the column header row right-aligned to match MoveToStatsColumn.
    private void WriteHeader()
    {
        int statsWidth = W_Stats + (_showAsn ? ColSepW + W_Asn : 0);
        int consoleWidth = Console.WindowWidth;
        int statsStart = consoleWidth - statsWidth;   // leave 1-col margin at right edge
        if (statsStart <= W_Host)
        {
            // Terminal too narrow — fall back to pre-encoded fixed header.
            _writer.WriteRaw(_headerBytes);
            return;
        }

        // HOST label left-aligned at column 1, then jump to stats start.
        _writer.WriteRaw(_hostHeaderBytes);
        _writer.EraseEol();
        _writer.MoveCursorToColumn(statsStart);

        _writer.WriteRaw(_statsHeaderBytes);

        if (_showAsn)
        {
            _writer.Write(ColSep);
            _writer.WriteRaw(_asnHeaderBytes);
        }

        _writer.Reset();
    }

    // Returns the 1-based terminal column where the stats section starts, given the current console width.
    private int ComputeStatsStartColumn(int consoleWidth)
    {
        int statsWidth = W_Stats + (_showAsn ? ColSepW + W_Asn : 0);
        int col = consoleWidth - statsWidth;
        return col > W_Host ? col : 0;  // 0 = terminal too narrow, use fallback
    }

    private void WriteHopLine(int hopIndex, in HopStats h, Span<char> numBuf, out int hostWidth)
    {
        bool hasRtt = h.Sent > h.Lost;

        // Compute how wide the host column actually is this frame.
        int consoleWidth = Console.WindowWidth;
        int statsCol     = ComputeStatsStartColumn(consoleWidth);   // 0 = too narrow
        hostWidth        = statsCol > 0 ? statsCol - 1 : W_Host;    // -1: leave 1-col gap before cursor jump

        // ── HOST column (pre-encoded, cached) ─────────────────────────────────
        byte[] hostBytes = GetOrBuildHostBytes(hopIndex, in h, hostWidth);
        _writer.WriteRaw(hostBytes);

        // Erase remaining host area, then jump so stats right-align to the terminal edge.
        _writer.EraseEol();
        if (statsCol > 0)
            _writer.MoveCursorToColumn(statsCol);
        else
            MoveToStatsColumn();

        // ── Loss% ─────────────────────────────────────────────────────────────
        WriteColoredRttColumn(h.LossPercent, numBuf, isLoss: true);
        _writer.Write(ColSep);

        // ── Snt ───────────────────────────────────────────────────────────────
        WriteIntColumn(h.Sent, W_Snt, numBuf);
        _writer.Write(ColSep);

        // ── RTT columns ───────────────────────────────────────────────────────
        WriteRttColumn(hasRtt ? h.Last                                        : double.NaN, numBuf);
        _writer.Write(ColSep);
        WriteRttColumn(hasRtt ? h.Average                                     : double.NaN, numBuf);
        _writer.Write(ColSep);
        WriteRttColumn(hasRtt && h.Best  < double.MaxValue ? h.Best  : double.NaN, numBuf);
        _writer.Write(ColSep);
        WriteRttColumn(hasRtt && h.Worst > double.MinValue ? h.Worst : double.NaN, numBuf);
        _writer.Write(ColSep);
        WriteRttColumn(hasRtt ? h.StdDev                                      : double.NaN, numBuf);
        _writer.Write(ColSep);
        WriteRttColumn(!double.IsNaN(h.Jitter) ? h.Jitter : double.NaN, numBuf);

        // ── Graph (sparkline) column ──────────────────────────────────────────
        _writer.Write(ColSep);
        WriteSparklineColumn(in h);

        // ── ASN column ────────────────────────────────────────────────────────
        if (_showAsn)
        {
            _writer.Write(ColSep);
            WriteAsnColumn(in h);
        }

        _writer.EraseEol();
        _writer.NewLine();
    }

    // Writes additional indented lines for each ECMP alternate address at this hop.
    private void WriteEcmpLines(in HopStats h, int hostWidth)
    {
        int altCount = h.AltAddressCount;
        if (altCount == 0) return;

        const string indent = "    "; // 4 spaces = TTL prefix width (" N." + space)

        for (int a = 0; a < altCount; a++)
        {
            string altIp = h.GetAltAddress(a).ToString();
            string? altHn = h.GetAltHostName(a);

            _writer.Grey();
            _writer.Write(indent);
            _writer.Reset();

            int available   = hostWidth - indent.Length;
            int ipSuffixLen = altIp.Length + 3; // " (" + ip + ")"

            if (altHn is { Length: > 0 } && !string.Equals(altHn, altIp, StringComparison.Ordinal))
            {
                if (available >= altHn.Length + ipSuffixLen)
                {
                    // Phase 3: full "hn (ip)"
                    string label = $"{altHn} ({altIp})";
                    _writer.WriteFixed(label.AsSpan(), available);
                }
                else if (available >= 2 + ipSuffixLen)
                {
                    // Phase 2: truncated hostname + "…" + " (ip)"
                    int maxHnLen = available - 1 - ipSuffixLen;
                    string label = $"{altHn[..maxHnLen]}\u2026 ({altIp})";
                    _writer.WriteFixed(label.AsSpan(), available);
                }
                else
                {
                    // Phase 1: hostname only, truncated if needed
                    string hn = altHn.Length > available
                        ? $"{altHn[..Math.Max(0, available - 1)]}\u2026"
                        : altHn;
                    _writer.WriteFixed(hn.AsSpan(), available);
                }
            }
            else
            {
                _writer.WriteFixed(altIp.AsSpan(), available);
            }

            _writer.EraseEol();
            _writer.NewLine();
        }
    }

    // Also used for individual RTT columns; caller passes avg RTT only for the coloring check.
    private void WriteRttColumn(double ms, Span<char> buf)
    {
        if (double.IsNaN(ms))
        {
            // Right-align "???" in W_Rtt columns.
            _writer.WriteFixed("???".AsSpan(), W_Rtt, rightAlign: true);
            return;
        }

        if (_thresholds.CritRtt > 0 && ms >= _thresholds.CritRtt)
            _writer.Red();
        else if (_thresholds.WarnRtt > 0 && ms >= _thresholds.WarnRtt)
            _writer.Yellow();

        if (ms.TryFormat(buf, out int written, "F1"))
            _writer.WriteFixed(buf[..written], W_Rtt, rightAlign: true);
        else
            _writer.WriteFixed(ms.ToString("F1").AsSpan(), W_Rtt, rightAlign: true);

        if ((_thresholds.CritRtt > 0 && ms >= _thresholds.CritRtt) ||
            (_thresholds.WarnRtt > 0 && ms >= _thresholds.WarnRtt))
            _writer.Reset();
    }

    private void WriteColoredRttColumn(double pct, Span<char> buf, bool isLoss)
    {
        if (isLoss)
        {
            if (pct >= _thresholds.CritLoss)
                _writer.Red();
            else if (pct >= _thresholds.WarnLoss)
                _writer.Yellow();
        }
        else
        {
            // RTT coloring — only applied when thresholds are configured (> 0).
            if (_thresholds.CritRtt > 0 && pct >= _thresholds.CritRtt)
                _writer.Red();
            else if (_thresholds.WarnRtt > 0 && pct >= _thresholds.WarnRtt)
                _writer.Yellow();
        }

        if (pct.TryFormat(buf, out int written, "F1"))
            _writer.WriteFixed(buf[..written], isLoss ? W_Loss : W_Rtt, rightAlign: true);
        else
            _writer.WriteFixed(pct.ToString("F1").AsSpan(), isLoss ? W_Loss : W_Rtt, rightAlign: true);

        if (isLoss && pct >= _thresholds.WarnLoss)
            _writer.Reset();
        else if (!isLoss && _thresholds.CritRtt > 0 && pct >= _thresholds.WarnRtt)
            _writer.Reset();
    }

    private void WriteIntColumn(int value, int width, Span<char> buf)
    {
        if (value.TryFormat(buf, out int written))
            _writer.WriteFixed(buf[..written], width, rightAlign: true);
        else
            _writer.WriteFixed(value.ToString().AsSpan(), width, rightAlign: true);
    }

    private void WriteAsnColumn(in HopStats h)
    {
        if (!h.AsnResolved)
        {
            // Resolution in-flight — show a grey placeholder.
            _writer.Grey();
            _writer.WriteFixed("...".AsSpan(), W_Asn, rightAlign: false);
            _writer.Reset();
            return;
        }

        string display = h.Asn is { } asn ? asn.ToString() : "???";
        ReadOnlySpan<char> span = display.Length > W_Asn
            ? display.AsSpan(0, W_Asn)
            : display.AsSpan();
        _writer.Grey();
        _writer.WriteFixed(span, W_Asn, rightAlign: false);
        _writer.Reset();
    }

    // Sparkline block characters ordered lowest-to-highest (8 levels).
    private static ReadOnlySpan<char> SparkBlocks => "\u2581\u2582\u2583\u2584\u2585\u2586\u2587\u2588";

    private void WriteSparklineColumn(in HopStats h)
    {
        // Request all available samples so we always get the newest W_Spark entries.
        // Requesting only W_Spark would give the oldest W_Spark when the ring is full.
        int count = h.CopyRingSamples(_ringBuf.AsSpan(0, HopStats.RingBufferSize));
        if (count == 0)
        {
            _writer.Grey();
            _writer.WriteFixed("-".AsSpan(), W_Spark);
            _writer.Reset();
            return;
        }

        // Take the newest W_Spark samples (oldest-first ordering from CopyRingSamples).
        int filled = Math.Min(count, W_Spark);
        int offset = count - filled;   // skip the oldest entries, keep the newest W_Spark

        // Find min/max over the visible window for scaling.
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < filled; i++)
        {
            if (_ringBuf[offset + i] < min) min = _ringBuf[offset + i];
            if (_ringBuf[offset + i] > max) max = _ringBuf[offset + i];
        }

        double range = max - min;

        // Build sparkline right-aligned: spaces on the left, bars on the right.
        // This way the first bar appears on the right and scrolls left as new bars arrive.
        Span<char> sparkBuf = stackalloc char[W_Spark];
        int padLeft = W_Spark - filled;
        for (int i = 0; i < padLeft; i++)
            sparkBuf[i] = ' ';

        for (int i = 0; i < filled; i++)
        {
            double v = _ringBuf[offset + i];
            int level = range < 0.01
                ? 4                     // flat line — use mid-level block
                : (int)((v - min) / range * 7.0);
            level = Math.Clamp(level, 0, 7);
            sparkBuf[padLeft + i] = SparkBlocks[level];
        }

        // Color the sparkline using the Avg RTT thresholds.
        double avg = h.Average;
        if (_thresholds.CritRtt > 0 && avg >= _thresholds.CritRtt)
            _writer.Red();
        else if (_thresholds.WarnRtt > 0 && avg >= _thresholds.WarnRtt)
            _writer.Yellow();
        else
            _writer.Cyan();

        _writer.WriteFixed(sparkBuf[..W_Spark], W_Spark);
        _writer.Reset();
    }



    private byte[] GetOrBuildHostBytes(int hopIndex, in HopStats h, int hostWidth)
    {
        string? currentAddress  = h.Address?.ToString();
        string? currentHostName = h.HostName;

        if (_hopHostBytes[hopIndex] is { } cached &&
            currentAddress  == _hopLastAddress[hopIndex] &&
            currentHostName == _hopLastHostName[hopIndex] &&
            hostWidth        == _hopLastHostWidth[hopIndex])
        {
            return cached;
        }

        byte[] encoded = BuildAndEncodeHostLabel(hopIndex, currentAddress, currentHostName, hostWidth);

        _hopHostBytes[hopIndex]    = encoded;
        _hopLastAddress[hopIndex]  = currentAddress;
        _hopLastHostName[hopIndex] = currentHostName;
        _hopLastHostWidth[hopIndex] = hostWidth;

        return encoded;
    }

    private byte[] BuildAndEncodeHostLabel(int hopIndex, string? ip, string? hostName, int hostWidth)
    {
        string prefix = _ttlPrefixes[hopIndex];

        // Build the visible text portion first into a char buffer, then encode to UTF-8.
        // This is called only when hop identity or available width changes — not on every frame.
        using var sb = new ValueStringBuilder(stackalloc char[64]);

        if (ip is null)
        {
            sb.Append(prefix);
            sb.Append(' ');
            sb.Append("???");
            return EncodeWithColor(sb.AsSpan(), ColorKind.Grey, hostWidth);
        }

        if (hostName is { Length: > 0 } hn &&
            !string.Equals(hn, ip, StringComparison.Ordinal))
        {
            // available = chars left in hostWidth after "prefix " (TTL prefix + one space).
            int available   = hostWidth - prefix.Length - 1;
            int ipSuffixLen = ip.Length + 3; // " (" + ip + ")" = 3 + ip.Length

            if (available >= hn.Length + ipSuffixLen)
            {
                // Phase 3: enough room for full "hn (ip)" — no ellipsis.
                sb.Append(prefix);
                sb.Append(' ');
                sb.Append(hn);
                return EncodeLabelWithGreyIp(sb.AsSpan(), ip, hostWidth);
            }

            if (available >= 2 + ipSuffixLen)
            {
                // Phase 2: show as many hostname chars as fit, then "…", then " (ip)".
                int maxHnLen = available - 1 - ipSuffixLen; // -1 for "…"
                sb.Append(prefix);
                sb.Append(' ');
                sb.Append(hn.AsSpan(0, maxHnLen));
                sb.Append('\u2026'); // "…"
                return EncodeLabelWithGreyIp(sb.AsSpan(), ip, hostWidth);
            }

            // Phase 1: not enough space for the IP at all — show hostname only, truncated if needed.
            sb.Append(prefix);
            sb.Append(' ');
            if (hn.Length > available)
            {
                sb.Append(hn.AsSpan(0, Math.Max(0, available - 1)));
                sb.Append('\u2026'); // "…"
            }
            else
            {
                sb.Append(hn);
            }
            return EncodePadded(sb.AsSpan(), hostWidth);
        }

        // Fallback: just "prefix ip".
        sb.Append(prefix);
        sb.Append(' ');
        sb.Append(ip);
        return EncodePadded(sb.AsSpan(), hostWidth);
    }

    // ── static encoding helpers ───────────────────────────────────────────────

    private static byte[] EncodeWithColor(ReadOnlySpan<char> text, ColorKind color, int padToWidth)
    {
        // Estimate: ANSI prefix + content + ANSI reset + padding.
        int maxLen = 20 + Encoding.UTF8.GetMaxByteCount(text.Length) + padToWidth + 4;
        byte[] buf = new byte[maxLen];
        int pos = 0;

        ReadOnlySpan<byte> colorSeq = color switch
        {
            ColorKind.Grey => "\x1B[90m"u8,
            ColorKind.Cyan => "\x1B[96m"u8,
            _              => "\x1B[0m"u8,
        };

        colorSeq.CopyTo(buf.AsSpan(pos)); pos += colorSeq.Length;
        pos += Encoding.UTF8.GetBytes(text, buf.AsSpan(pos));
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;

        // Pad to width with spaces.
        int textLen = text.Length; // ASCII-safe for hostnames/IPs
        int pad = padToWidth - textLen;
        if (pad > 0) { buf.AsSpan(pos, pad).Fill((byte)' '); pos += pad; }

        return buf[..pos];
    }

    private static byte[] EncodeLabelWithGreyIp(ReadOnlySpan<char> label, string ip, int padToWidth)
    {
        // "label (ip)" where "(ip)" is grey.
        string greyIp = $" ({ip})";
        int visibleLen = label.Length + greyIp.Length;
        int pad = Math.Max(0, padToWidth - visibleLen);

        int maxLen = Encoding.UTF8.GetMaxByteCount(label.Length)
                   + 10                                              // grey escape
                   + Encoding.UTF8.GetMaxByteCount(greyIp.Length)
                   + 4                                               // reset
                   + pad;
        byte[] buf = new byte[maxLen];
        int pos = 0;

        pos += Encoding.UTF8.GetBytes(label, buf.AsSpan(pos));
        "\x1B[90m"u8.CopyTo(buf.AsSpan(pos)); pos += 5;
        pos += Encoding.UTF8.GetBytes(greyIp, buf.AsSpan(pos));
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;

        if (pad > 0) { buf.AsSpan(pos, pad).Fill((byte)' '); pos += pad; }

        return buf[..pos];
    }

    private static byte[] EncodePadded(ReadOnlySpan<char> text, int padToWidth)
    {
        int pad = Math.Max(0, padToWidth - text.Length);
        byte[] buf = new byte[Encoding.UTF8.GetMaxByteCount(text.Length) + pad];
        int pos = Encoding.UTF8.GetBytes(text, buf.AsSpan());
        buf.AsSpan(pos, pad).Fill((byte)' ');
        return buf[..(pos + pad)];
    }

    // ── pre-encoding for title and header ─────────────────────────────────────

    private static byte[] EncodeTitleLine(string titleHost)
    {
        // "mtrcs  {titleHost}"
        // Bold "mtrcs", cyan host.
        int maxBytes = 64 + Encoding.UTF8.GetMaxByteCount(titleHost.Length);
        byte[] buf = new byte[maxBytes];
        int pos = 0;

        "\x1B[1m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;          // bold
        "mtrcs"u8.CopyTo(buf.AsSpan(pos)); pos += 5;
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;          // reset
        "  "u8.CopyTo(buf.AsSpan(pos)); pos += 2;
        "\x1B[96m"u8.CopyTo(buf.AsSpan(pos)); pos += 5;         // cyan
        pos += Encoding.UTF8.GetBytes(titleHost, buf.AsSpan(pos));
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;          // reset

        return buf[..pos];
    }

    private static byte[] EncodeKeysLine()
    {
        // "Keys:  d=Display mode  r=Restart statistics  q=quit"
        // Bold key letters, normal text.
        byte[] buf = new byte[128];
        int pos = 0;

        "\x1B[1m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;           // bold
        "Keys:"u8.CopyTo(buf.AsSpan(pos)); pos += 5;
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;           // reset
        "  "u8.CopyTo(buf.AsSpan(pos)); pos += 2;
        "\x1B[1m"u8.CopyTo(buf.AsSpan(pos)); pos += 4; buf[pos++] = (byte)'r'; "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;
        "=Restart statistics"u8.CopyTo(buf.AsSpan(pos)); pos += 19;
        "  "u8.CopyTo(buf.AsSpan(pos)); pos += 2;
        "\x1B[1m"u8.CopyTo(buf.AsSpan(pos)); pos += 4; buf[pos++] = (byte)'q'; "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;
        "=quit"u8.CopyTo(buf.AsSpan(pos)); pos += 5;

        return buf[..pos];
    }

    private static byte[] EncodeSubHeaderLine(bool showAsn)
    {
        // "                                              Packets           Pings"
        // "Packets" centred over Loss%+Snt, "Pings" centred over Last+Avg+Best+Wrst+StDev+Jitter.
        // W_Host(40) + ColSep(2) then columns.
        // Packets section spans Loss%(7)+ColSep(2)+Snt(5) = 14 chars visually, centre label at position.
        // Pings section spans Last+Avg+Best+Wrst+StDev+Jitter = 6*(7+2) = 54 chars.
        byte[] buf = new byte[256];
        int pos = 0;

        "\x1B[1m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;           // bold

        // Blank space for HOST column + ColSep + right-align Loss% = W_Host + 2 + (W_Loss - "Loss%".len padding).
        // We want "Packets" to appear above Loss%..Snt.  The Loss% column starts at offset W_Host+2=42.
        // Loss%(7) + Sep(2) + Snt(5) = 14 chars wide.  Centre "Packets"(7) => 3 leading spaces + 4 trailing.
        // Indent = W_Host + 2 + 3 = 45 spaces total.
        const int PacketsIndent = W_Host + 2 + 3;           // 45
        buf.AsSpan(pos, PacketsIndent).Fill((byte)' '); pos += PacketsIndent;
        "Packets"u8.CopyTo(buf.AsSpan(pos)); pos += 7;

        // Gap between Packets label and Pings label.
        // After Snt: 4 trailing spaces of Packets label + ColSep(2) = positions already accounted for.
        // Snt trailing = 14-3-7=4 chars to the right. Then ColSep(2) before Last(7) column.
        // We have 4 trailing + 2 ColSep + floor((6*9-5)/2)=floor(49/2)=26 indent for "Pings".
        // = 4 + 2 + 26 = 32 spaces.
        const int PingsIndent = 4 + 2 + 26;                 // 32
        buf.AsSpan(pos, PingsIndent).Fill((byte)' '); pos += PingsIndent;
        "Pings"u8.CopyTo(buf.AsSpan(pos)); pos += 5;

        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;           // reset

        return buf[..pos];
    }

    private static byte[] EncodeHeaderLine(bool showAsn = false)
    {
        // "HOST                                     Loss%    Snt   Last    Avg   Best   Wrst  StDev  Jitter  [ASN]"
        // All bold.
        byte[] buf = new byte[320];
        int pos = 0;

        "\x1B[1m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;   // bold on

        // HOST — left-aligned, padded to W_Host.
        ReadOnlySpan<byte> hostHdr = "HOST"u8;
        hostHdr.CopyTo(buf.AsSpan(pos)); pos += hostHdr.Length;
        buf.AsSpan(pos, W_Host - hostHdr.Length).Fill((byte)' '); pos += W_Host - hostHdr.Length;

        // Right-aligned column headers.
        pos += AppendRightAligned("  Loss%"u8,  W_Loss + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Snt"u8,    W_Snt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Last"u8,   W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Avg"u8,    W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Best"u8,   W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Wrst"u8,   W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  StDev"u8,  W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Jitter"u8, W_Rtt   + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Graph"u8,  W_Spark + 2, buf.AsSpan(pos));

        if (showAsn)
            pos += AppendRightAligned("  ASN"u8, W_Asn + 2, buf.AsSpan(pos));

        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;   // bold off

        return buf[..pos];
    }

    private static int AppendRightAligned(ReadOnlySpan<byte> text, int width, Span<byte> dest)
    {
        int pad = Math.Max(0, width - text.Length);
        dest[..pad].Fill((byte)' ');
        text.CopyTo(dest[pad..]);
        return pad + text.Length;
    }

    // Encodes just the "HOST" label left-padded to W_Host, bold — no stats.
    private static byte[] EncodeHostHeaderBytes()
    {
        byte[] buf = new byte[64];
        int pos = 0;
        "\x1B[1m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;          // bold
        ReadOnlySpan<byte> hostHdr = "HOST"u8;
        hostHdr.CopyTo(buf.AsSpan(pos)); pos += hostHdr.Length;
        buf.AsSpan(pos, W_Host - hostHdr.Length).Fill((byte)' '); pos += W_Host - hostHdr.Length;
        // no reset — caller will continue with EraseEol + MoveCursor
        return buf[..pos];
    }

    // Encodes Loss%..Sparkline column headers (right-aligned, bold, no reset).
    private static byte[] EncodeStatsHeaderBytes()
    {
        byte[] buf = new byte[160];
        int pos = 0;
        pos += AppendRightAligned("Loss%"u8,    W_Loss, buf.AsSpan(pos));
        pos += AppendRightAligned("  Snt"u8,    W_Snt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Last"u8,   W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Avg"u8,    W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Best"u8,   W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Wrst"u8,   W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  StDev"u8,  W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Jitter"u8, W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Graph"u8,  W_Spark + 2, buf.AsSpan(pos));
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;          // reset
        return buf[..pos];
    }

    // Encodes "Graph" sparkline column header (left-aligned, bold, reset).
    private static byte[] EncodeSparklineHeaderBytes()
    {
        byte[] buf = new byte[32];
        int pos = 0;
        "\x1B[1m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;
        pos += AppendRightAligned("Graph"u8, W_Spark, buf.AsSpan(pos));
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;
        return buf[..pos];
    }

    // Encodes "ASN" column header (right-aligned, bold, reset).
    private static byte[] EncodeAsnHeaderBytes()
    {
        byte[] buf = new byte[32];
        int pos = 0;
        "\x1B[1m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;
        pos += AppendRightAligned("ASN"u8, W_Asn, buf.AsSpan(pos));
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;
        return buf[..pos];
    }

    private enum ColorKind { Grey, Cyan }
}

/// <summary>
/// Minimal stack-first string builder to avoid heap allocation for short strings
/// when building hop label text before UTF-8 encoding.
/// </summary>
file struct ValueStringBuilder : IDisposable
{
    private char[] _buf;
    private int _pos;

    internal ValueStringBuilder(Span<char> initial)
    {
        // Cannot store Span<char> as a field; allocate a managed array of the same size.
        _buf = ArrayPool<char>.Shared.Rent(initial.Length > 0 ? initial.Length : 64);
        _pos = 0;
    }

    internal void Append(ReadOnlySpan<char> value)
    {
        if (_pos + value.Length > _buf.Length)
            Grow(value.Length);
        value.CopyTo(_buf.AsSpan(_pos));
        _pos += value.Length;
    }

    internal void Append(string value) => Append(value.AsSpan());
    internal void Append(char value)
    {
        if (_pos + 1 > _buf.Length) Grow(1);
        _buf[_pos++] = value;
    }

    internal ReadOnlySpan<char> AsSpan() => _buf.AsSpan(0, _pos);

    private void Grow(int needed)
    {
        int newSize = Math.Max(_buf.Length * 2, _pos + needed);
        char[] next = ArrayPool<char>.Shared.Rent(newSize);
        _buf.AsSpan(0, _pos).CopyTo(next);
        ArrayPool<char>.Shared.Return(_buf);
        _buf = next;
    }

    public void Dispose()
    {
        ArrayPool<char>.Shared.Return(_buf);
        _buf = [];
    }
}
