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
            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Resolve host → TracerouteOptions
        TracerouteOptions options;
        try
        {
            AnsiConsole.MarkupLine($"[grey]Resolving[/] [bold]{settings.Host}[/][grey]...[/]");
            options = await TracerouteOptions.ResolveAsync(
                settings.Host,
                settings.MaxHops,
                settings.IntervalMs,
                settings.TimeoutMs,
                settings.PayloadBytes).ConfigureAwait(false);
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

        IPingerFactory pingerFactory = new SystemPingerFactory(settings.PayloadBytes);
        IDnsResolver dnsResolver = new SystemDnsResolver();

        await using TracerouteSession session = new(options, pingerFactory, dnsResolver);

        await session.StartAsync(cts.Token).ConfigureAwait(false);

        if (settings.Report)
            return await RunReportModeAsync(session, options, settings.ReportCycles, cts.Token).ConfigureAwait(false);

        return await RunLiveModeAsync(session, options, cts.Token).ConfigureAwait(false);
    }

    // ── live mode ─────────────────────────────────────────────────────────────

    private static async Task<int> RunLiveModeAsync(
        ITracerouteSession session,
        TracerouteOptions options,
        CancellationToken ct)
    {
        var renderer = new MtrRenderer(options);

        await AnsiConsole.Live(renderer.BuildTable())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Bottom)
            .StartAsync(async ctx =>
            {
                while (!ct.IsCancellationRequested)
                {
                    Table table = renderer.Refresh(session);
                    ctx.UpdateTarget(table);

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

                // Final render after cancellation
                ctx.UpdateTarget(renderer.Refresh(session));
            }).ConfigureAwait(false);

        await session.StopAsync().ConfigureAwait(false);
        return 0;
    }

    // ── report mode ───────────────────────────────────────────────────────────

    private static async Task<int> RunReportModeAsync(
        ITracerouteSession session,
        TracerouteOptions options,
        int cycles,
        CancellationToken ct)
    {
        var renderer = new MtrRenderer(options);

        AnsiConsole.MarkupLine($"[grey]Running {cycles} cycle(s) — please wait...[/]");

        // Wait for the requested number of cycles (approximated by interval × cycles).
        long targetMs = (long)cycles * options.IntervalMs;
        try
        {
            await Task.Delay((int)Math.Min(targetMs, int.MaxValue), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        await session.StopAsync().ConfigureAwait(false);

        AnsiConsole.Write(renderer.Refresh(session));
        return 0;
    }
}
