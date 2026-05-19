using MTRCSLib;
using MTRCSLib.Abstractions;

namespace MTRCS;

/// <summary>
/// Core MTR command: resolves the target, starts a <see cref="TracerouteSession"/>,
/// and runs either live mode (continuous rendering) or report mode (fixed cycles + exit).
/// AOT-safe: no reflection, no dynamic code.
/// </summary>
internal sealed class MtrCommand
{
    /// <summary>Parsed and validated CLI settings.</summary>
    internal sealed class Settings(
        string host,
        int maxHops,
        int intervalMs,
        int timeoutMs,
        int payloadBytes,
        bool report,
        int reportCycles,
        bool showAsn,
        bool useTcp,
        bool useUdp,
        int port,
        string? outputPath,
        string outputFormat,
        double warnLoss,
        double critLoss,
        double warnRtt,
        double critRtt,
        double graphGreen,
        double graphCyan,
        double graphYellow,
        double graphRed)
    {
        internal string  Host         { get; } = host;
        internal int     MaxHops      { get; } = maxHops;
        internal int     IntervalMs   { get; } = intervalMs;
        internal int     TimeoutMs    { get; } = timeoutMs;
        internal int     PayloadBytes { get; } = payloadBytes;
        internal bool    Report       { get; } = report;
        internal int     ReportCycles { get; } = reportCycles;
        internal bool    ShowAsn      { get; } = showAsn;
        internal bool    UseTcp       { get; } = useTcp;
        internal bool    UseUdp       { get; } = useUdp;
        internal int     Port         { get; } = port;
        internal string? OutputPath   { get; } = outputPath;
        internal string  OutputFormat { get; } = outputFormat;
        internal double  WarnLoss     { get; } = warnLoss;
        internal double  CritLoss     { get; } = critLoss;
        internal double  WarnRtt      { get; } = warnRtt;
        internal double  CritRtt      { get; } = critRtt;
        internal double  GraphGreen   { get; } = graphGreen;
        internal double  GraphCyan    { get; } = graphCyan;
        internal double  GraphYellow  { get; } = graphYellow;
        internal double  GraphRed     { get; } = graphRed;

        /// <summary>Builds the <see cref="RttThresholds"/> from CLI settings.</summary>
        internal RttThresholds BuildThresholds() => new()
        {
            WarnLoss    = WarnLoss    > 0 ? WarnLoss    : RttThresholds.Default.WarnLoss,
            CritLoss    = CritLoss    > 0 ? CritLoss    : RttThresholds.Default.CritLoss,
            WarnRtt     = WarnRtt,
            CritRtt     = CritRtt,
            GraphGreen  = GraphGreen  > 0 ? GraphGreen  : RttThresholds.Default.GraphGreen,
            GraphCyan   = GraphCyan   > 0 ? GraphCyan   : RttThresholds.Default.GraphCyan,
            GraphYellow = GraphYellow > 0 ? GraphYellow : RttThresholds.Default.GraphYellow,
            GraphRed    = GraphRed    > 0 ? GraphRed    : RttThresholds.Default.GraphRed,
        };

        /// <summary>Returns <see langword="null"/> if valid; otherwise an error message.</summary>
        internal string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Host))
                return "Host must not be empty.";
            if (MaxHops is < 1 or > 255)
                return "--max-hops must be between 1 and 255.";
            if (IntervalMs < 1)
                return "--interval must be positive.";
            if (TimeoutMs < 1)
                return "--timeout must be positive.";
            if (PayloadBytes < 0)
                return "--size must be non-negative.";
            if (ReportCycles < 1)
                return "--cycles must be at least 1.";
            if (UseTcp && UseUdp)
                return "--tcp and --udp are mutually exclusive.";
            if (Port is < 0 or > 65535)
                return "--port must be between 0 and 65535.";
            if (OutputPath is not null && !Report)
                return "--output requires --report mode.";
            if (OutputFormat is not ("text" or "csv" or "json"))
                return "--format must be one of: text, csv, json.";
            if (WarnLoss < 0 || WarnLoss > 100)
                return "--warn-loss must be between 0 and 100.";
            if (CritLoss < 0 || CritLoss > 100)
                return "--crit-loss must be between 0 and 100.";
            if (WarnRtt < 0)
                return "--warn-rtt must be non-negative.";
            if (CritRtt < 0)
                return "--crit-rtt must be non-negative.";
            if (GraphGreen < 0)
                return "--graph-green must be non-negative.";
            if (GraphCyan < 0)
                return "--graph-cyan must be non-negative.";
            if (GraphYellow < 0)
                return "--graph-yellow must be non-negative.";
            if (GraphRed < 0)
                return "--graph-red must be non-negative.";
            return null;
        }
    }

    internal async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        ConsoleInit.EnableVirtualTerminal();

        TracerouteOptions options;
        try
        {
            Console.Error.Write("Resolving ");
            Console.Error.Write(settings.Host);
            Console.Error.WriteLine("...");
            ProbeMode mode = settings.UseTcp ? ProbeMode.Tcp
                           : settings.UseUdp ? ProbeMode.Udp
                           : ProbeMode.Icmp;

            options = await TracerouteOptions.ResolveAsync(
                settings.Host,
                settings.MaxHops,
                settings.IntervalMs,
                settings.TimeoutMs,
                settings.PayloadBytes,
                settings.ShowAsn,
                mode,
                settings.Port).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.Write("Error: ");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using IDisposable? factoryDisposable = options.Mode switch
        {
            ProbeMode.Tcp => new TcpPingerFactory(options.Port),
            ProbeMode.Udp => new UdpPingerFactory(options.Port),
            _ => null,
        };
        IPingerFactory pingerFactory = options.Mode switch
        {
            ProbeMode.Tcp => (TcpPingerFactory)factoryDisposable!,
            ProbeMode.Udp => (UdpPingerFactory)factoryDisposable!,
            _ => new SystemPingerFactory(settings.PayloadBytes),
        };
        IDnsResolver dnsResolver = new SystemDnsResolver();
        IAsnResolver? asnResolver = settings.ShowAsn ? new CymruAsnResolver() : null;

        await using TracerouteSession session = new(options, pingerFactory, dnsResolver, asnResolver);
        await session.StartAsync(cts.Token).ConfigureAwait(false);

        RttThresholds thresholds = settings.BuildThresholds();

        if (settings.Report)
            return await RunReportModeAsync(session, options, settings, thresholds, cts.Token).ConfigureAwait(false);

        return await RunLiveModeAsync(session, options, thresholds, cts.Token).ConfigureAwait(false);
    }

    // ── live mode ─────────────────────────────────────────────────────────────

    private static async Task<int> RunLiveModeAsync(
        ITracerouteSession session,
        TracerouteOptions options,
        RttThresholds thresholds,
        CancellationToken ct)
    {
        var renderer = new MtrAnsiRenderer(options, thresholds);
        renderer.BeginLive();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                renderer.RenderFrame(session);

                // Poll for key presses in small slices so the UI stays responsive.
                long deadline = Environment.TickCount64 + 16;
                while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                        if (key.KeyChar is 'q' or 'Q')
                        {
                            goto done;
                        }
                        else if (key.KeyChar is 'r' or 'R')
                        {
                            session.ResetStats();
                        }
                    }
                    else
                    {
                        try { await Task.Delay(10, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { goto done; }
                    }
                }
            }
            done:;

            // Final render after cancellation.
            renderer.RenderFrame(session);
        }
        finally
        {
            renderer.EndLive();
        }

        await session.StopAsync().ConfigureAwait(false);
        return 0;
    }

    // ── report mode ───────────────────────────────────────────────────────────

    private static async Task<int> RunReportModeAsync(
        ITracerouteSession session,
        TracerouteOptions options,
        Settings settings,
        RttThresholds thresholds,
        CancellationToken ct)
    {
        var renderer = new MtrRenderer(options, thresholds);

        Console.Error.Write("Running ");
        Console.Error.Write(settings.ReportCycles);
        Console.Error.WriteLine(" cycle(s) — please wait...");

        // Wait for the requested number of cycles (approximated by interval × cycles).
        long targetMs = (long)settings.ReportCycles * options.IntervalMs;
        try
        {
            await Task.Delay((int)Math.Min(targetMs, int.MaxValue), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        await session.StopAsync().ConfigureAwait(false);

        // Print the report table to stdout.
        renderer.Render(session);

        // Optionally export to file.
        if (settings.OutputPath is { Length: > 0 } outputPath)
        {
            var exporter = new ReportExporter(options);
            await exporter.ExportAsync(session, outputPath, settings.OutputFormat, ct).ConfigureAwait(false);
            Console.Error.Write("Report saved to: ");
            Console.Error.WriteLine(outputPath);
        }

        return 0;
    }
}
