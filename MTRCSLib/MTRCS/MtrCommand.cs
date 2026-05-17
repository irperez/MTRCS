using System.ComponentModel;
using System.Net;
using MTRCSLib;
using MTRCSLib.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MTRCS;

/// <summary>
/// Spectre.Console.Cli async command that runs a continuous MTR-style traceroute
/// and renders live statistics to the terminal.
/// </summary>
internal sealed class MtrCommand : AsyncCommand<MtrCommand.Settings>
{
    /// <summary>CLI settings / arguments for the mtr command.</summary>
    internal sealed class Settings : CommandSettings
    {
        [Description("Hostname or IPv4 address to trace.")]
        [CommandArgument(0, "<host>")]
        public string Host { get; init; } = string.Empty;

        [Description("Maximum number of TTL hops (1–30). Default: 30")]
        [CommandOption("-m|--max-hops")]
        [DefaultValue(TracerouteOptions.DefaultMaxHops)]
        public int MaxHops { get; init; } = TracerouteOptions.DefaultMaxHops;

        [Description("Probe cycle interval in milliseconds. Default: 1000")]
        [CommandOption("-i|--interval")]
        [DefaultValue(TracerouteOptions.DefaultIntervalMs)]
        public int IntervalMs { get; init; } = TracerouteOptions.DefaultIntervalMs;

        [Description("Per-probe timeout in milliseconds. Default: 800")]
        [CommandOption("-t|--timeout")]
        [DefaultValue(TracerouteOptions.DefaultTimeoutMs)]
        public int TimeoutMs { get; init; } = TracerouteOptions.DefaultTimeoutMs;

        [Description("ICMP payload size in bytes. Default: 28")]
        [CommandOption("-s|--size")]
        [DefaultValue(TracerouteOptions.DefaultPayloadBytes)]
        public int PayloadBytes { get; init; } = TracerouteOptions.DefaultPayloadBytes;

        [Description("Report mode: run one cycle, print results, and exit.")]
        [CommandOption("-r|--report")]
        [DefaultValue(false)]
        public bool Report { get; init; }

        [Description("Number of cycles to run in report mode. Default: 10")]
        [CommandOption("-c|--cycles")]
        [DefaultValue(10)]
        public int ReportCycles { get; init; } = 10;

        [Description("Show ASN (Autonomous System Number) column via Team Cymru DNS lookup.")]
        [CommandOption("-a|--asn")]
        [DefaultValue(false)]
        public bool ShowAsn { get; init; }

        [Description("Use TCP SYN probes instead of ICMP (bypasses ICMP firewalls).")]
        [CommandOption("-T|--tcp")]
        [DefaultValue(false)]
        public bool UseTcp { get; init; }

        [Description("Use UDP probes instead of ICMP (bypasses ICMP firewalls).")]
        [CommandOption("-u|--udp")]
        [DefaultValue(false)]
        public bool UseUdp { get; init; }

        [Description("Destination port for TCP/UDP probes. Default: 80 for TCP, 33434 for UDP.")]
        [CommandOption("-P|--port")]
        [DefaultValue(0)]
        public int Port { get; init; }

        [Description("Output file path for report export (requires --report).")]
        [CommandOption("-o|--output")]
        public string? OutputPath { get; init; }

        [Description("Export format: text, csv, json. Default: text")]
        [CommandOption("-f|--format")]
        [DefaultValue("text")]
        public string OutputFormat { get; init; } = "text";

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Host))
                return ValidationResult.Error("Host must not be empty.");
            if (MaxHops is < 1 or > 255)
                return ValidationResult.Error("--max-hops must be between 1 and 255.");
            if (IntervalMs < 1)
                return ValidationResult.Error("--interval must be positive.");
            if (TimeoutMs < 1)
                return ValidationResult.Error("--timeout must be positive.");
            if (PayloadBytes < 0)
                return ValidationResult.Error("--size must be non-negative.");
            if (ReportCycles < 1)
                return ValidationResult.Error("--cycles must be at least 1.");
            if (UseTcp && UseUdp)
                return ValidationResult.Error("--tcp and --udp are mutually exclusive.");
            if (Port is < 0 or > 65535)
                return ValidationResult.Error("--port must be between 0 and 65535.");
            if (OutputPath is not null && !Report)
                return ValidationResult.Error("--output requires --report mode.");
            if (OutputFormat is not ("text" or "csv" or "json"))
                return ValidationResult.Error("--format must be one of: text, csv, json.");
            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ConsoleInit.EnableVirtualTerminal();

        // Resolve host → TracerouteOptions
        TracerouteOptions options;
        try
        {
            AnsiConsole.MarkupLine($"[grey]Resolving[/] [bold]{settings.Host}[/][grey]...[/]");
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
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }

        // Wire up Ctrl+C → cancellation (also linked to the CLI-provided token)
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

        if (settings.Report)
            return await RunReportModeAsync(session, options, settings, cts.Token).ConfigureAwait(false);

        return await RunLiveModeAsync(session, options, cts.Token).ConfigureAwait(false);
    }

    // ── live mode ─────────────────────────────────────────────────────────────

    private static async Task<int> RunLiveModeAsync(
        ITracerouteSession session,
        TracerouteOptions options,
        CancellationToken ct)
    {
        var renderer = new MtrAnsiRenderer(options);
        renderer.BeginLive();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                renderer.RenderFrame(session);

                try
                {
                    // Refresh at ~10 Hz so the UI stays snappy without burning CPU.
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

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
        CancellationToken ct)
    {
        var renderer = new MtrRenderer(options);

        AnsiConsole.MarkupLine($"[grey]Running {settings.ReportCycles} cycle(s) — please wait...[/]");

        // Wait for the requested number of cycles (approximated by interval × cycles).
        long targetMs = (long)settings.ReportCycles * options.IntervalMs;
        try
        {
            await Task.Delay((int)Math.Min(targetMs, int.MaxValue), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        await session.StopAsync().ConfigureAwait(false);

        // Always print the table to the terminal.
        AnsiConsole.Write(renderer.Refresh(session));

        // Optionally export to file.
        if (settings.OutputPath is { Length: > 0 } outputPath)
        {
            var exporter = new ReportExporter(options);
            await exporter.ExportAsync(session, outputPath, settings.OutputFormat, ct).ConfigureAwait(false);
            AnsiConsole.MarkupLine($"[grey]Report saved to:[/] [bold]{outputPath}[/]");
        }

        return 0;
    }
}
