using MTRCSLib;

namespace MTRCS;

/// <summary>
/// Zero-allocation, reflection-free command-line parser.
/// Parses <c>string[] args</c> into <see cref="MtrCommand.Settings"/> with full validation.
/// AOT-safe: no attributes are read at runtime, no dynamic code.
/// </summary>
internal static class CliParser
{
    private const string AppName = "mtrcs";
    private const string AppVersion = "1.0.0";

    private static readonly string HelpText = $"""
        {AppName} {AppVersion} — MTR-style traceroute

        USAGE
          {AppName} <host> [options]

        ARGUMENTS
          <host>              Hostname or IPv4 address to trace.

        OPTIONS
          -m, --max-hops      Maximum TTL hops (1–255). Default: {TracerouteOptions.DefaultMaxHops}
          -i, --interval      Probe cycle interval in ms. Default: {TracerouteOptions.DefaultIntervalMs}
          -t, --timeout       Per-probe timeout in ms. Default: {TracerouteOptions.DefaultTimeoutMs}
          -s, --size          ICMP payload size in bytes. Default: {TracerouteOptions.DefaultPayloadBytes}
          -r, --report        Report mode: run cycles, print results, and exit.
          -c, --cycles        Cycles to run in report mode. Default: 10
          -a, --asn           Show ASN column via Team Cymru DNS lookup.
          -T, --tcp           Use TCP SYN probes instead of ICMP.
          -u, --udp           Use UDP probes instead of ICMP.
          -P, --port          Destination port for TCP/UDP probes. Default: 80 (TCP) / 33434 (UDP)
          -o, --output        File path for report export (requires --report).
          -f, --format        Export format: text, csv, json. Default: text
              --warn-loss     Loss% threshold for yellow highlight. Default: 1
              --crit-loss     Loss% threshold for red highlight. Default: 10
              --warn-rtt      Avg RTT (ms) threshold for yellow highlight. Default: 0 (off)
              --crit-rtt      Avg RTT (ms) threshold for red highlight. Default: 0 (off)
          -h, --help          Show this help.
              --version       Show version.

        EXAMPLES
          {AppName} example.com
          {AppName} 8.8.8.8 --max-hops 20 --interval 500
          {AppName} example.com --report --cycles 10 --output report.txt
          {AppName} example.com --warn-rtt 50 --crit-rtt 150 --crit-loss 5
        """;

    /// <summary>
    /// Parses <paramref name="args"/> into a <see cref="ParseResult"/>.
    /// On parse error, <see cref="ParseResult.Error"/> is set and the caller should print it and exit 1.
    /// On <c>--help</c> or <c>--version</c>, <see cref="ParseResult.ShouldExit"/> is true with exit code 0.
    /// </summary>
    internal static ParseResult Parse(string[] args)
    {
        if (args.Length == 0)
            return ParseResult.Fail($"Missing required argument <host>.{Environment.NewLine}{HelpText}");

        // Mutable locals — filled as we scan args.
        string? host = null;
        int maxHops       = TracerouteOptions.DefaultMaxHops;
        int intervalMs    = TracerouteOptions.DefaultIntervalMs;
        int timeoutMs     = TracerouteOptions.DefaultTimeoutMs;
        int payloadBytes  = TracerouteOptions.DefaultPayloadBytes;
        bool report       = false;
        int reportCycles  = 10;
        bool showAsn      = false;
        bool useTcp       = false;
        bool useUdp       = false;
        int port          = 0;
        string? outputPath   = null;
        string outputFormat  = "text";
        double warnLoss = 0.0;
        double critLoss = 0.0;
        double warnRtt  = 0.0;
        double critRtt  = 0.0;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h":
                case "--help":
                    return ParseResult.Exit(HelpText, 0);

                case "--version":
                    return ParseResult.Exit($"{AppName} {AppVersion}", 0);

                case "-r":
                case "--report":
                    report = true;
                    break;

                case "-a":
                case "--asn":
                    showAsn = true;
                    break;

                case "-T":
                case "--tcp":
                    useTcp = true;
                    break;

                case "-u":
                case "--udp":
                    useUdp = true;
                    break;

                case "-m":
                case "--max-hops":
                    if (!TryNextInt(args, ref i, arg, out maxHops, out string? err1)) return ParseResult.Fail(err1!);
                    break;

                case "-i":
                case "--interval":
                    if (!TryNextInt(args, ref i, arg, out intervalMs, out string? err2)) return ParseResult.Fail(err2!);
                    break;

                case "-t":
                case "--timeout":
                    if (!TryNextInt(args, ref i, arg, out timeoutMs, out string? err3)) return ParseResult.Fail(err3!);
                    break;

                case "-s":
                case "--size":
                    if (!TryNextInt(args, ref i, arg, out payloadBytes, out string? err4)) return ParseResult.Fail(err4!);
                    break;

                case "-c":
                case "--cycles":
                    if (!TryNextInt(args, ref i, arg, out reportCycles, out string? err5)) return ParseResult.Fail(err5!);
                    break;

                case "-P":
                case "--port":
                    if (!TryNextInt(args, ref i, arg, out port, out string? err6)) return ParseResult.Fail(err6!);
                    break;

                case "-o":
                case "--output":
                    if (!TryNextString(args, ref i, arg, out outputPath, out string? err7)) return ParseResult.Fail(err7!);
                    break;

                case "-f":
                case "--format":
                    if (!TryNextString(args, ref i, arg, out string? fmt, out string? err8)) return ParseResult.Fail(err8!);
                    outputFormat = fmt!;
                    break;

                case "--warn-loss":
                    if (!TryNextDouble(args, ref i, arg, out warnLoss, out string? errWL)) return ParseResult.Fail(errWL!);
                    break;

                case "--crit-loss":
                    if (!TryNextDouble(args, ref i, arg, out critLoss, out string? errCL)) return ParseResult.Fail(errCL!);
                    break;

                case "--warn-rtt":
                    if (!TryNextDouble(args, ref i, arg, out warnRtt, out string? errWR)) return ParseResult.Fail(errWR!);
                    break;

                case "--crit-rtt":
                    if (!TryNextDouble(args, ref i, arg, out critRtt, out string? errCR)) return ParseResult.Fail(errCR!);
                    break;

                default:
                    if (arg.StartsWith('-'))
                        return ParseResult.Fail($"Unknown option: {arg}");
                    // First non-option arg is the positional <host>.
                    if (host is null)
                        host = arg;
                    else
                        return ParseResult.Fail($"Unexpected argument: {arg}");
                    break;
            }
        }

        if (host is null)
            return ParseResult.Fail("Missing required argument <host>.");

        var settings = new MtrCommand.Settings(
            host, maxHops, intervalMs, timeoutMs, payloadBytes,
            report, reportCycles, showAsn, useTcp, useUdp, port,
            outputPath, outputFormat,
            warnLoss, critLoss, warnRtt, critRtt);

        string? validationError = settings.Validate();
        return validationError is null
            ? ParseResult.Ok(settings)
            : ParseResult.Fail(validationError);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static bool TryNextInt(string[] args, ref int i, string flag, out int value, out string? error)
    {
        if (i + 1 >= args.Length)
        {
            value = 0;
            error = $"{flag} requires a value.";
            return false;
        }
        if (!int.TryParse(args[++i], out value))
        {
            error = $"{flag} requires an integer value, got '{args[i]}'.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryNextString(string[] args, ref int i, string flag, out string? value, out string? error)
    {
        if (i + 1 >= args.Length)
        {
            value = null;
            error = $"{flag} requires a value.";
            return false;
        }
        value = args[++i];
        error = null;
        return true;
    }

    private static bool TryNextDouble(string[] args, ref int i, string flag, out double value, out string? error)
    {
        if (i + 1 >= args.Length)
        {
            value = 0;
            error = $"{flag} requires a value.";
            return false;
        }
        if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            error = $"{flag} requires a numeric value, got '{args[i]}'.";
            return false;
        }
        error = null;
        return true;
    }
}

/// <summary>Result of <see cref="CliParser.Parse"/>.</summary>
internal readonly struct ParseResult
{
    internal MtrCommand.Settings? Settings  { get; private init; }
    internal string?              Message   { get; private init; }
    internal bool                 ShouldExit { get; private init; }
    internal int                  ExitCode  { get; private init; }
    internal bool                 IsError   => ShouldExit && ExitCode != 0;

    internal static ParseResult Ok(MtrCommand.Settings settings)             => new() { Settings = settings };
    internal static ParseResult Exit(string message, int code)               => new() { Message = message, ShouldExit = true, ExitCode = code };
    internal static ParseResult Fail(string error)                           => new() { Message = error,   ShouldExit = true, ExitCode = 1 };
}
