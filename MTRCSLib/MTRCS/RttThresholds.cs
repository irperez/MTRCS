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

    /// <summary>Default thresholds that match legacy MTR coloring behaviour.</summary>
    internal static readonly RttThresholds Default = new()
    {
        WarnLoss = 1.0,
        CritLoss = 10.0,
        WarnRtt  = 0.0,   // no RTT warning by default (opt-in via CLI)
        CritRtt  = 0.0,
    };
}
