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

    // Two spaces between every column.
    private const string ColSep = "  ";
    private const int ColSepW = 2;

    // Total width of the stats section (Loss%..Jitter), not including ASN.
    // Loss%(7)+Sep(2)+Snt(5)+Sep(2)+Last(7)+Sep(2)+Avg(7)+Sep(2)+Best(7)+Sep(2)+Wrst(7)+Sep(2)+StDev(7)+Sep(2)+Jitter(7) = 66
    private const int W_Stats = W_Loss + ColSepW + W_Snt + ColSepW
                              + W_Rtt + ColSepW + W_Rtt + ColSepW
                              + W_Rtt + ColSepW + W_Rtt + ColSepW
                              + W_Rtt + ColSepW + W_Rtt;   // = 66

    // ── pre-encoded constant byte sequences ───────────────────────────────────

    // Header row bytes, encoded once at construction.
    private readonly byte[] _titleBytes;
    private readonly byte[] _keysBytes;
    private readonly byte[] _subHeaderBytes;   // kept for narrow-terminal fallback
    private readonly byte[] _headerBytes;       // kept for narrow-terminal fallback
    private readonly byte[] _hostHeaderBytes;   // "HOST" left-padded to W_Host, bold
    private readonly byte[] _statsHeaderBytes;  // "Loss%  Snt  Last  Avg  Best  Wrst  StDev  Jitter" bold
    private readonly byte[] _asnHeaderBytes;    // "ASN" bold
    private readonly bool _showAsn;

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

    // ── frame writer ──────────────────────────────────────────────────────────
    private readonly AnsiWriter _writer;

    internal MtrAnsiRenderer(TracerouteOptions options)
    {
        int maxHops = options.MaxHops;

        _snapBuffer       = new HopStats[maxHops];
        _hopHostBytes     = new byte[maxHops][];
        _hopLastAddress   = new string?[maxHops];
        _hopLastHostName  = new string?[maxHops];
        _hopLastHostWidth = new int[maxHops];
        _ttlPrefixes      = new string[maxHops];

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
        WriteTitleTimestamp();
        _writer.NewLine();

        // ── keys bar ──────────────────────────────────────────────────────────
        _writer.WriteRaw(_keysBytes);
        _writer.EraseEol();
        _writer.NewLine();

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

    // Writes the start timestamp right-justified on the current line.
    // Format matches MTR: "ddd MMM  d HH:mm:ss yyyy"  (single-digit day gets extra space).
    private void WriteTitleTimestamp()
    {
        // Format: "Wed Dec  9 03:07:34 2020"
        Span<char> buf = stackalloc char[32];
        bool ok = _startedAt.TryFormat(buf, out int written, "ddd MMM  d HH:mm:ss yyyy");
        ReadOnlySpan<char> stamp = ok ? buf[..written] : _startedAt.ToString("ddd MMM  d HH:mm:ss yyyy").AsSpan();

        // Use a cursor-position move to right-align: ESC[{col}G where col = consoleWidth - stamp.Length + 1.
        int consoleWidth = Console.WindowWidth;
        if (consoleWidth <= stamp.Length) return;   // terminal too narrow — skip

        int col = consoleWidth - stamp.Length + 1;  // 1-based column
        _writer.Grey();
        _writer.MoveCursorToColumn(col);
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

    private void WriteRttColumn(double ms, Span<char> buf)
    {
        if (double.IsNaN(ms))
        {
            // Right-align "???" in W_Rtt columns.
            _writer.WriteFixed("???".AsSpan(), W_Rtt, rightAlign: true);
            return;
        }

        if (ms.TryFormat(buf, out int written, "F1"))
            _writer.WriteFixed(buf[..written], W_Rtt, rightAlign: true);
        else
            _writer.WriteFixed(ms.ToString("F1").AsSpan(), W_Rtt, rightAlign: true);
    }

    private void WriteColoredRttColumn(double pct, Span<char> buf, bool isLoss)
    {
        if (isLoss && pct >= 10.0)
            _writer.Red();
        else if (isLoss && pct > 0.0)
            _writer.Yellow();

        if (pct.TryFormat(buf, out int written, "F1"))
            _writer.WriteFixed(buf[..written], W_Loss, rightAlign: true);
        else
            _writer.WriteFixed(pct.ToString("F1").AsSpan(), W_Loss, rightAlign: true);

        if (isLoss && pct > 0.0)
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

    // ── per-hop host label cache ──────────────────────────────────────────────

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
        pos += AppendRightAligned("  Jitter"u8, W_Rtt  + 2, buf.AsSpan(pos));

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

    // Encodes Loss%..Jitter column headers (right-aligned, bold, no reset).
    private static byte[] EncodeStatsHeaderBytes()
    {
        byte[] buf = new byte[128];
        int pos = 0;
        pos += AppendRightAligned("Loss%"u8,  W_Loss, buf.AsSpan(pos));
        pos += AppendRightAligned("  Snt"u8,  W_Snt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Last"u8, W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Avg"u8,  W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Best"u8, W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Wrst"u8, W_Rtt  + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  StDev"u8, W_Rtt + 2, buf.AsSpan(pos));
        pos += AppendRightAligned("  Jitter"u8, W_Rtt + 2, buf.AsSpan(pos));
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;          // reset
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
