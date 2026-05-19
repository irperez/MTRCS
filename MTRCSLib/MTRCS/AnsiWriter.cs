using System.Buffers;
using System.Text;

namespace MTRCS;

/// <summary>
/// A forward-only, zero-allocation frame buffer that writes UTF-8 bytes into a pre-allocated
/// <see cref="byte"/> array and flushes it to <see cref="Console.OpenStandardOutput"/> in a
/// single <see cref="Stream.Write"/> call per frame.
///
/// All methods write directly into the internal buffer via <see cref="Encoding.UTF8"/>
/// or manual ASCII encoding — no intermediate strings are created on the hot path.
/// </summary>
internal sealed class AnsiWriter : IDisposable
{
    // ANSI/VT escape sequence prefix byte.
    private const byte Esc = 0x1B;

    private readonly byte[] _buffer;
    private readonly Stream _stdout;
    private int _pos;

    // Pre-encoded constant sequences (ASCII — single byte per char).
    private static ReadOnlySpan<byte> CursorHome       => "\x1B[H"u8;
    private static ReadOnlySpan<byte> EraseToLineEnd   => "\x1B[K"u8;
    private static ReadOnlySpan<byte> EraseToScreenEnd => "\x1B[J"u8;
    private static ReadOnlySpan<byte> ResetColor       => "\x1B[0m"u8;
    private static ReadOnlySpan<byte> BoldOn           => "\x1B[1m"u8;
    private static ReadOnlySpan<byte> ColorCyan        => "\x1B[96m"u8;
    private static ReadOnlySpan<byte> ColorGrey        => "\x1B[90m"u8;
    private static ReadOnlySpan<byte> ColorGreen        => "\x1B[92m"u8;
    private static ReadOnlySpan<byte> ColorRed         => "\x1B[91m"u8;
    private static ReadOnlySpan<byte> ColorYellow      => "\x1B[93m"u8;
    private static ReadOnlySpan<byte> ColorMagenta     => "\x1B[95m"u8;
    private static ReadOnlySpan<byte> HideCursor       => "\x1B[?25l"u8;
    private static ReadOnlySpan<byte> ShowCursor       => "\x1B[?25h"u8;
    private static ReadOnlySpan<byte> EnterAltScreen   => "\x1B[?1049h"u8;
    private static ReadOnlySpan<byte> LeaveAltScreen   => "\x1B[?1049l"u8;

    /// <param name="bufferBytes">
    /// Pre-allocated frame buffer size in bytes.
    /// 16 KB is more than enough for 30 hops × ~150 bytes/line + header/title.
    /// </param>
    internal AnsiWriter(int bufferBytes = 16 * 1024)
    {
        _buffer = new byte[bufferBytes];
        _stdout = Console.OpenStandardOutput();
    }

    // ── cursor / screen ───────────────────────────────────────────────────────

    /// <summary>Moves the cursor to the top-left of the terminal (row 1, col 1).</summary>
    internal void Home()       => WriteRaw(CursorHome);

    /// <summary>Moves the cursor to the given 1-based column on the current row. ESC[{col}G</summary>
    internal void MoveCursorToColumn(int col)
    {
        // ESC [ <col> G  — CHA (Cursor Horizontal Absolute)
        Span<char> numBuf = stackalloc char[8];
        col.TryFormat(numBuf, out int written);

        EnsureCapacity(4 + written);
        _buffer[_pos++] = Esc;
        _buffer[_pos++] = (byte)'[';
        for (int i = 0; i < written; i++)
            _buffer[_pos++] = (byte)numBuf[i];
        _buffer[_pos++] = (byte)'G';
    }

    /// <summary>Hides the cursor to prevent flicker during frame writes.</summary>
    internal void HideCaret()  => WriteRaw(HideCursor);

    /// <summary>Restores cursor visibility after the live loop exits.</summary>
    internal void ShowCaret()  => WriteRaw(ShowCursor);

    /// <summary>Switches to the terminal alternate screen buffer (saves the normal screen).</summary>
    internal void EnterAlternateScreen() => WriteRaw(EnterAltScreen);

    /// <summary>Restores the normal screen buffer (discards alternate screen content).</summary>
    internal void LeaveAlternateScreen() => WriteRaw(LeaveAltScreen);

    /// <summary>Erases from the current cursor position to end of the current line.</summary>
    internal void EraseEol()   => WriteRaw(EraseToLineEnd);

    /// <summary>Erases from the current cursor position to the end of the screen (clears all rows below).</summary>
    internal void EraseToEnd() => WriteRaw(EraseToScreenEnd);

    // ── color ─────────────────────────────────────────────────────────────────

    internal void Reset()      => WriteRaw(ResetColor);
    internal void Bold()       => WriteRaw(BoldOn);
    internal void Cyan()       => WriteRaw(ColorCyan);
    internal void Green()      => WriteRaw(ColorGreen);
    internal void Grey()       => WriteRaw(ColorGrey);
    internal void Red()        => WriteRaw(ColorRed);
    internal void Yellow()     => WriteRaw(ColorYellow);
    internal void Magenta()    => WriteRaw(ColorMagenta);

    // ── text ──────────────────────────────────────────────────────────────────

    /// <summary>Writes a UTF-8 encoded string into the frame buffer.</summary>
    internal void Write(string text)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        EnsureCapacity(maxBytes);
        _pos += Encoding.UTF8.GetBytes(text, _buffer.AsSpan(_pos));
    }

    /// <summary>
    /// Writes a <see cref="ReadOnlySpan{char}"/> padded or truncated to exactly
    /// <paramref name="width"/> display columns, right-aligned if
    /// <paramref name="rightAlign"/> is <see langword="true"/>.
    /// Uses only ASCII-safe arithmetic (no grapheme-cluster awareness needed for RTT numbers).
    /// </summary>
    internal void WriteFixed(ReadOnlySpan<char> text, int width, bool rightAlign = false)
    {
        int len = Math.Min(text.Length, width);
        int pad = width - len;

        if (rightAlign)
            WritePadding(pad);

        int maxBytes = Encoding.UTF8.GetMaxByteCount(len);
        EnsureCapacity(maxBytes);
        _pos += Encoding.UTF8.GetBytes(text[..len], _buffer.AsSpan(_pos));

        if (!rightAlign)
            WritePadding(pad);
    }

    /// <summary>Writes pre-encoded UTF-8 bytes directly — zero conversion overhead.</summary>
    internal void WriteRaw(ReadOnlySpan<byte> utf8Bytes)
    {
        EnsureCapacity(utf8Bytes.Length);
        utf8Bytes.CopyTo(_buffer.AsSpan(_pos));
        _pos += utf8Bytes.Length;
    }

    /// <summary>Writes a newline (LF only — terminals handle CR/LF).</summary>
    internal void NewLine()
    {
        EnsureCapacity(1);
        _buffer[_pos++] = (byte)'\n';
    }

    // ── frame flush ───────────────────────────────────────────────────────────

    /// <summary>
    /// Flushes the accumulated frame to stdout in a single <see cref="Stream.Write"/> call,
    /// then resets the write position.  No heap allocation occurs during flush.
    /// </summary>
    internal void Flush()
    {
        if (_pos > 0)
        {
            _stdout.Write(_buffer, 0, _pos);
            _stdout.Flush();
        }
        _pos = 0;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void WritePadding(int count)
    {
        if (count <= 0) return;
        EnsureCapacity(count);
        _buffer.AsSpan(_pos, count).Fill((byte)' ');
        _pos += count;
    }

    private void EnsureCapacity(int needed)
    {
        // Buffer is sized generously at construction; this is a safety guard.
        if (_pos + needed > _buffer.Length)
            throw new InvalidOperationException(
                $"AnsiWriter frame buffer overflow: needed {_pos + needed} bytes, capacity {_buffer.Length}.");
    }

    /// <summary>
    /// Returns a trimmed copy of the accumulated bytes — used to pre-encode constant sequences.
    /// Resets the write position after copying.
    /// </summary>
    internal byte[] ToArray()
    {
        byte[] result = _buffer[.._pos].ToArray();
        _pos = 0;
        return result;
    }

    /// <inheritdoc/>
    public void Dispose() => _stdout.Dispose();
}
