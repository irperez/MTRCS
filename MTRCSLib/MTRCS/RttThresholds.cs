namespace MTRCS;

/// <summary>
/// RTT and loss thresholds used to color-code columns in both live and report renderers.
/// Values of 0 mean "not set" and the threshold is ignored.
/// </summary>
internal readonly struct RttThresholds
{
    /// <summary>Loss % at or above which the loss column turns yellow. Default: 1%.</summary>
    internal double WarnLoss { get; init; }

    /// <summary>Loss % at or above which the loss column turns red. Default: 10%.</summary>
    internal double CritLoss { get; init; }

    /// <summary>Average RTT in ms at or above which RTT columns turn yellow.</summary>
    internal double WarnRtt { get; init; }

    /// <summary>Average RTT in ms at or above which RTT columns turn red.</summary>
    internal double CritRtt { get; init; }

    // ── Latency bar-graph color tier thresholds ───────────────────────────────
    // Columns below GraphGreen are green; green–cyan boundary is GraphCyan; etc.

    /// <summary>RTT (ms) below which latency graph columns are green. Default: 5 ms.</summary>
    internal double GraphGreen { get; init; }

    /// <summary>RTT (ms) below which latency graph columns are cyan. Default: 15 ms.</summary>
    internal double GraphCyan { get; init; }

    /// <summary>RTT (ms) below which latency graph columns are yellow. Default: 30 ms.</summary>
    internal double GraphYellow { get; init; }

    /// <summary>RTT (ms) below which latency graph columns are red; at or above this they are magenta. Default: 50 ms.</summary>
    internal double GraphRed { get; init; }

    /// <summary>Default thresholds that match legacy MTR coloring behaviour.</summary>
    internal static readonly RttThresholds Default = new()
    {
        WarnLoss    = 1.0,
        CritLoss    = 10.0,
        WarnRtt     = 0.0,   // no RTT warning by default (opt-in via CLI)
        CritRtt     = 0.0,
        GraphGreen  = 5.0,
        GraphCyan   = 15.0,
        GraphYellow = 30.0,
        GraphRed    = 50.0,
    };
}
