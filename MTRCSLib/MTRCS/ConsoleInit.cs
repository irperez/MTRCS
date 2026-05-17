using System.Runtime.InteropServices;
using System.Text;

namespace MTRCS;

/// <summary>
/// One-time terminal initialisation:
/// <list type="bullet">
///   <item>Enables ANSI/VT escape-sequence processing on Windows legacy console hosts.</item>
///   <item>Switches <see cref="Console.OutputEncoding"/> to UTF-8 so that Unicode symbols
///         (e.g. the ellipsis "…" in truncated hostnames) are emitted correctly.</item>
/// </list>
/// Safe to call on all platforms — the P/Invoke path is guarded by <see cref="OperatingSystem.IsWindows"/>.
/// </summary>
internal static partial class ConsoleInit
{
    // Windows console mode flag: enables VT100/ANSI sequences in ConHost.
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    // STD_OUTPUT_HANDLE
    private const int StdOutputHandle = -11;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int nStdHandle);

    /// <summary>
    /// Enables VT escape-sequence processing and sets UTF-8 output encoding.
    /// Must be called before any ANSI output is written.
    /// </summary>
    internal static void EnableVirtualTerminal()
    {
        // UTF-8 output on all platforms.
        Console.OutputEncoding = Encoding.UTF8;

        if (!OperatingSystem.IsWindows()) return;

        nint handle = GetStdHandle(StdOutputHandle);
        if (handle == -1) return;

        if (!GetConsoleMode(handle, out uint mode)) return;

        // No-op if already set (e.g. Windows Terminal, VS integrated terminal).
        if ((mode & EnableVirtualTerminalProcessing) != 0) return;

        SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }
}
