using MTRCSLib;
using MTRCSLib.Abstractions;

namespace MTRCS;

/// <summary>
/// Zero-allocation report renderer for MTR-style terminal output.
/// Used only in <c>--report</c> mode; writes directly to stdout via <see cref="AnsiWriter"/>.
///
/// Column layout:
///   HOST                                     Loss%    Snt   Last    Avg   Best   Wrst  StDev  Jitter
/// </summary>
internal sealed class MtrRenderer
{
    // ── column widths (chars) — must match MtrAnsiRenderer ───────────────────
    private const int W_Host   = 40;
    private const int W_Loss   =  7;
    private const int W_Snt    =  5;
    private const int W_Rtt    =  7;
    private const int W_Asn    = 20;
    private const string ColSep = "  ";

    private readonly TracerouteOptions _options;
    private readonly HopStats[] _snapBuffer;

    // ── pre-encoded constant sequences ───────────────────────────────────────
    private readonly byte[] _titleBytes;
    private readonly byte[] _headerBytes;

    // ── per-hop caches ────────────────────────────────────────────────────────
    private readonly string[] _ttlPrefixes;

    internal MtrRenderer(TracerouteOptions options)
    {
        _options    = options;
        _snapBuffer = new HopStats[options.MaxHops];

        _ttlPrefixes = new string[options.MaxHops];
        for (int i = 0; i < options.MaxHops; i++)
        {
            int ttl = i + 1;
            _ttlPrefixes[i] = ttl < 10 ? $" {ttl}." : $"{ttl}.";
        }

        string targetIp = options.Target.ToString();
        string titleHost = options.Host.Equals(targetIp, StringComparison.Ordinal)
            ? options.Host
            : $"{options.Host} ({targetIp})";

        _titleBytes  = EncodeTitleLine(titleHost);
        _headerBytes = EncodeHeaderLine(options.EnableAsn);
    }

    /// <summary>
    /// Snapshots <paramref name="session"/> and writes the full report table to stdout.
    /// </summary>
    internal void Render(ITracerouteSession session)
    {
        int count = session.SnapshotHops(_snapBuffer);
        var writer = new AnsiWriter(8 * 1024);

        writer.WriteRaw(_titleBytes);
        writer.NewLine();
        writer.WriteRaw(_headerBytes);
        writer.NewLine();

        if (count == 0)
        {
            writer.Grey();
            writer.Write("No hops recorded.");
            writer.Reset();
            writer.NewLine();
            writer.Flush();
            return;
        }

        Span<char> numBuf = stackalloc char[16];

        for (int i = 0; i < count; i++)
        {
            ref readonly HopStats h = ref _snapBuffer[i];
            WriteHopLine(writer, i, in h, numBuf);
        }

        writer.Flush();
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private void WriteHopLine(AnsiWriter w, int hopIndex, in HopStats h, Span<char> numBuf)
    {
        bool hasRtt = h.Sent > h.Lost;

        // ── HOST column ───────────────────────────────────────────────────────
        WriteHostColumn(w, hopIndex, in h);
        w.Write(ColSep);

        // ── Loss% ─────────────────────────────────────────────────────────────
        double loss = h.LossPercent;
        FormatF1(loss, numBuf, out int lossLen);
        if (loss >= 10.0) w.Red();
        else if (loss > 0.0) w.Yellow();
        w.WriteFixed(numBuf[..lossLen], W_Loss, rightAlign: true);
        if (loss > 0.0) w.Reset();
        w.Write(ColSep);

        // ── Snt ───────────────────────────────────────────────────────────────
        FormatInt(h.Sent, numBuf, out int sntLen);
        w.WriteFixed(numBuf[..sntLen], W_Snt, rightAlign: true);
        w.Write(ColSep);

        // ── RTT columns ───────────────────────────────────────────────────────
        WriteRttColumn(w, hasRtt ? h.Last                                        : double.NaN, numBuf);
        w.Write(ColSep);
        WriteRttColumn(w, hasRtt ? h.Average                                     : double.NaN, numBuf);
        w.Write(ColSep);
        WriteRttColumn(w, hasRtt && h.Best  < double.MaxValue ? h.Best  : double.NaN, numBuf);
        w.Write(ColSep);
        WriteRttColumn(w, hasRtt && h.Worst > double.MinValue ? h.Worst : double.NaN, numBuf);
        w.Write(ColSep);
        WriteRttColumn(w, hasRtt ? h.StdDev                                      : double.NaN, numBuf);
        w.Write(ColSep);
        WriteRttColumn(w, !double.IsNaN(h.Jitter) ? h.Jitter : double.NaN, numBuf);

        // ── ASN column ────────────────────────────────────────────────────────
        if (_options.EnableAsn)
        {
            w.Write(ColSep);
            string asnDisplay = !h.AsnResolved ? "..." : h.Asn?.ToString() ?? "???";
            w.Grey();
            w.WriteFixed(asnDisplay.AsSpan(), W_Asn);
            w.Reset();
        }

        w.NewLine();
    }

    private void WriteHostColumn(AnsiWriter w, int hopIndex, in HopStats h)
    {
        string prefix  = _ttlPrefixes[hopIndex];
        string? ip     = h.Address?.ToString();
        string? hn     = h.HostName;

        if (ip is null)
        {
            w.Grey();
            w.WriteFixed($"{prefix} ???".AsSpan(), W_Host);
            w.Reset();
            return;
        }

        // Show "prefix hostname (ip)" if hostname differs from IP.
        if (hn is { Length: > 0 } && !string.Equals(hn, ip, StringComparison.Ordinal))
        {
            int overhead  = prefix.Length + 1 + 2 + ip.Length + 1; // " prefix hn (ip)"
            int maxHnLen  = W_Host - overhead;
            ReadOnlySpan<char> hnSpan = maxHnLen > 0 && hn.Length > maxHnLen
                ? hn.AsSpan(0, maxHnLen)
                : hn.AsSpan();

            // Write prefix + " " + hostname, then " (" + ip + ")" in grey — padded to W_Host total.
            string full = maxHnLen > 0 && hn.Length > maxHnLen
                ? $"{prefix} {hn[..maxHnLen]}({ip})"
                : $"{prefix} {hn} ({ip})";
            w.WriteFixed(full.AsSpan(), W_Host);
        }
        else
        {
            w.WriteFixed($"{prefix} {ip}".AsSpan(), W_Host);
        }
    }

    private static void WriteRttColumn(AnsiWriter w, double ms, Span<char> buf)
    {
        if (double.IsNaN(ms))
        {
            w.Grey();
            w.WriteFixed("???".AsSpan(), W_Rtt, rightAlign: true);
            w.Reset();
        }
        else
        {
            FormatF1(ms, buf, out int len);
            w.WriteFixed(buf[..len], W_Rtt, rightAlign: true);
        }
    }

    private static void FormatF1(double value, Span<char> buf, out int len)
    {
        if (!value.TryFormat(buf, out len, "F1"))
            len = 0;
    }

    private static void FormatInt(int value, Span<char> buf, out int len)
    {
        if (!value.TryFormat(buf, out len))
            len = 0;
    }

    // ── pre-encode title + header ─────────────────────────────────────────────

    private static byte[] EncodeTitleLine(string titleHost)
    {
        using var w = new AnsiWriter(512);
        w.Bold();
        w.Write("mtrcs");
        w.Reset();
        w.Write("  ");
        w.Cyan();
        w.Write(titleHost);
        w.Reset();
        return w.ToArray();
    }

    private static byte[] EncodeHeaderLine(bool showAsn)
    {
        using var w = new AnsiWriter(256);
        w.Bold();
        w.WriteFixed("HOST".AsSpan(),   W_Host);
        w.Write(ColSep);
        w.WriteFixed("Loss%".AsSpan(),  W_Loss,  rightAlign: true);
        w.Write(ColSep);
        w.WriteFixed("Snt".AsSpan(),    W_Snt,   rightAlign: true);
        w.Write(ColSep);
        w.WriteFixed("Last".AsSpan(),   W_Rtt,   rightAlign: true);
        w.Write(ColSep);
        w.WriteFixed("Avg".AsSpan(),    W_Rtt,   rightAlign: true);
        w.Write(ColSep);
        w.WriteFixed("Best".AsSpan(),   W_Rtt,   rightAlign: true);
        w.Write(ColSep);
        w.WriteFixed("Wrst".AsSpan(),   W_Rtt,   rightAlign: true);
        w.Write(ColSep);
        w.WriteFixed("StDev".AsSpan(),  W_Rtt,   rightAlign: true);
        w.Write(ColSep);
        w.WriteFixed("Jitter".AsSpan(), W_Rtt,   rightAlign: true);
        if (showAsn)
        {
            w.Write(ColSep);
            w.WriteFixed("ASN".AsSpan(), W_Asn);
        }
        w.Reset();
        return w.ToArray();
    }
}