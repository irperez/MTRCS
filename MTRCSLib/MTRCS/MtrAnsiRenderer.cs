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

    // ── pre-encoded constant byte sequences ───────────────────────────────────

    // Header row bytes, encoded once at construction.
    private readonly byte[] _titleBytes;
    private readonly byte[] _headerBytes;
    private readonly bool _showAsn;

    // ── per-hop caches ────────────────────────────────────────────────────────

    // Pre-encoded UTF-8 bytes for the HOST column of each hop.
    // Rebuilt only when address or hostname changes.
    private readonly byte[]?[] _hopHostBytes;
    private readonly string?[] _hopLastAddress;
    private readonly string?[] _hopLastHostName;

    // Pre-computed TTL prefix strings " 1." … "NN.".
    private readonly string[] _ttlPrefixes;

    // ── snapshot buffer ───────────────────────────────────────────────────────
    private readonly HopStats[] _snapBuffer;

    // ── frame writer ──────────────────────────────────────────────────────────
    private readonly AnsiWriter _writer;

    internal MtrAnsiRenderer(TracerouteOptions options)
    {
        int maxHops = options.MaxHops;

        _snapBuffer      = new HopStats[maxHops];
        _hopHostBytes    = new byte[maxHops][];
        _hopLastAddress  = new string?[maxHops];
        _hopLastHostName = new string?[maxHops];
        _ttlPrefixes     = new string[maxHops];

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
        _titleBytes  = EncodeTitleLine(titleHost);
        _headerBytes = EncodeHeaderLine(options.EnableAsn);
        _showAsn     = options.EnableAsn;

        // 16 KB frame buffer — ample for 30 hops × ~150 bytes + title/header overhead.
        _writer = new AnsiWriter(16 * 1024);
    }

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>Hides the cursor once before the render loop starts.</summary>
    internal void BeginLive()
    {
        _writer.HideCaret();
        _writer.Flush();
    }

    /// <summary>Restores the cursor after the render loop exits.</summary>
    internal void EndLive()
    {
        _writer.ShowCaret();
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

        // ── title ─────────────────────────────────────────────────────────────
        _writer.WriteRaw(_titleBytes);
        _writer.EraseEol();
        _writer.NewLine();

        // ── header ────────────────────────────────────────────────────────────
        _writer.WriteRaw(_headerBytes);
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
            WriteHopLine(i, in h, numBuf);
        }

        _writer.Flush();
    }

    // ── private: frame composition ────────────────────────────────────────────

    private void WriteHopLine(int hopIndex, in HopStats h, Span<char> numBuf)
    {
        bool hasRtt = h.Sent > h.Lost;

        // ── HOST column (pre-encoded, cached) ─────────────────────────────────
        byte[] hostBytes = GetOrBuildHostBytes(hopIndex, in h);
        _writer.WriteRaw(hostBytes);
        _writer.Write(ColSep);

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

    private byte[] GetOrBuildHostBytes(int hopIndex, in HopStats h)
    {
        string? currentAddress  = h.Address?.ToString();
        string? currentHostName = h.HostName;

        if (_hopHostBytes[hopIndex] is { } cached &&
            currentAddress  == _hopLastAddress[hopIndex] &&
            currentHostName == _hopLastHostName[hopIndex])
        {
            return cached;
        }

        byte[] encoded = BuildAndEncodeHostLabel(hopIndex, currentAddress, currentHostName);

        _hopHostBytes[hopIndex]    = encoded;
        _hopLastAddress[hopIndex]  = currentAddress;
        _hopLastHostName[hopIndex] = currentHostName;

        return encoded;
    }

    private byte[] BuildAndEncodeHostLabel(int hopIndex, string? ip, string? hostName)
    {
        string prefix = _ttlPrefixes[hopIndex];

        // Build the visible text portion first into a char buffer, then encode to UTF-8.
        // This is called only when hop identity changes — not on every frame.
        using var sb = new ValueStringBuilder(stackalloc char[64]);

        if (ip is null)
        {
            // No address yet — write "  1. ???" in grey.
            // We encode the ANSI grey sequence around the label.
            sb.Append(prefix);
            sb.Append(' ');
            sb.Append("???");
            byte[] greyBytes = EncodeWithColor(sb.AsSpan(), ColorKind.Grey, W_Host);
            return greyBytes;
        }

        if (hostName is { Length: > 0 } hn &&
            !string.Equals(hn, ip, StringComparison.Ordinal))
        {
            // "{prefix} {hn} ({ip})" — truncate hostname to fit.
            int overhead = prefix.Length + 1 + 2 + ip.Length + 1; // prefix + " " + " (" + ip + ")"
            int maxHnLen = W_Host - overhead;

            if (maxHnLen > 0)
            {
                if (hn.Length > maxHnLen)
                    hn = string.Concat(hn.AsSpan(0, maxHnLen - 1), "\u2026"); // "…"

                sb.Append(prefix);
                sb.Append(' ');
                sb.Append(hn);

                // Encode "prefix hn" in default color, then " (ip)" in grey.
                return EncodeLabelWithGreyIp(sb.AsSpan(), ip, W_Host);
            }
        }

        // Fallback: just "prefix ip".
        sb.Append(prefix);
        sb.Append(' ');
        sb.Append(ip);
        return EncodePadded(sb.AsSpan(), W_Host);
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
        // "mtrcs  {titleHost}  Keys: q=quit"
        // Bold "mtrcs", cyan host, grey keys hint.
        using var sb = new ValueStringBuilder(stackalloc char[128]);
        sb.Append("mtrcs");

        // We'll compose the byte sequence manually to embed color codes.
        int maxBytes = 128 + Encoding.UTF8.GetMaxByteCount(titleHost.Length);
        byte[] buf = new byte[maxBytes];
        int pos = 0;

        "\x1B[1m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;          // bold
        "mtrcs"u8.CopyTo(buf.AsSpan(pos)); pos += 5;
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;          // reset
        "  "u8.CopyTo(buf.AsSpan(pos)); pos += 2;
        "\x1B[96m"u8.CopyTo(buf.AsSpan(pos)); pos += 5;         // cyan
        pos += Encoding.UTF8.GetBytes(titleHost, buf.AsSpan(pos));
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;          // reset
        "  "u8.CopyTo(buf.AsSpan(pos)); pos += 2;
        "\x1B[90m"u8.CopyTo(buf.AsSpan(pos)); pos += 5;         // grey
        "Keys: q=quit"u8.CopyTo(buf.AsSpan(pos)); pos += 12;
        "\x1B[0m"u8.CopyTo(buf.AsSpan(pos)); pos += 4;          // reset

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
