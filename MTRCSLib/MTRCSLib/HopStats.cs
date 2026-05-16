using System.Net;
using System.Runtime.CompilerServices;

namespace MTRCSLib;

/// <summary>
/// Per-hop running statistics — mirrors MTR columns: Loss%, Snt, Last, Avg, Best, Wrst, StDev.
/// Uses Welford's online algorithm for numerically stable mean/variance.
/// A fixed-size ring buffer retains the last <see cref="RingBufferSize"/> RTT samples
/// (used by the UI jitter sparkline).  All state lives on the stack / in pre-allocated arrays.
/// </summary>
public struct HopStats
{
    /// <summary>Number of recent RTT samples kept for jitter/sparkline display.</summary>
    public const int RingBufferSize = 200;

    // ── identity ──────────────────────────────────────────────────────────────
    private IPAddress? _address;

    /// <summary>IP address of the hop, or <see langword="null"/> before the first successful reply.</summary>
    public IPAddress? Address => _address;

    // ── MTR columns ───────────────────────────────────────────────────────────

    /// <summary>Total probes sent to this TTL hop.</summary>
    public int Sent { get; private set; }

    /// <summary>Number of probes that did not receive any reply (timeout/error).</summary>
    public int Lost { get; private set; }

    /// <summary>Loss percentage (0–100), rounded to one decimal.</summary>
    public double LossPercent => Sent == 0 ? 0.0 : Lost * 100.0 / Sent;

    /// <summary>RTT of the most recent successful probe in ms.</summary>
    public double Last { get; private set; }

    /// <summary>Best (minimum) RTT observed in ms.</summary>
    public double Best { get; private set; }

    /// <summary>Worst (maximum) RTT observed in ms.</summary>
    public double Worst { get; private set; }

    /// <summary>Running arithmetic mean of RTT in ms (Welford).</summary>
    public double Average { get; private set; }

    /// <summary>Running standard deviation of RTT in ms (Welford).</summary>
    public double StdDev => NetworkUtils.StdDevFromVariance(_m2 / (_successCount > 1 ? _successCount - 1 : 1));

    // ── Welford internals ─────────────────────────────────────────────────────
    private int _successCount; // probes with a valid RTT
    private double _m2;        // sum of squared deviations (for Welford)

    // ── ring buffer ───────────────────────────────────────────────────────────
    private readonly double[] _ring; // allocated once by factory
    private int _ringHead;           // next write position (wraps)
    private int _ringCount;          // number of valid samples (≤ RingBufferSize)

    /// <summary>Number of RTT samples currently in the ring buffer.</summary>
    public int RingSampleCount => _ringCount;

    // ── hostname ──────────────────────────────────────────────────────────────
    private string? _hostName;

    /// <summary>Reverse-DNS hostname for this hop, populated asynchronously.</summary>
    public string? HostName => _hostName;

    // ── internal flags ────────────────────────────────────────────────────────

    /// <summary>True once DNS resolution has been attempted (success or failure).</summary>
    public bool DnsResolved { get; private set; }

    // ── factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="HopStats"/> with its ring buffer allocated.
    /// Call this once per hop slot at session start to amortise the single allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HopStats Create() => new(new double[RingBufferSize]);

    private HopStats(double[] ring)
    {
        _ring = ring;
        _ringHead = 0;
        _ringCount = 0;
        Best = double.MaxValue;
        Worst = double.MinValue;
    }

    // ── mutation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a successful probe reply (RTT ≥ 0).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordSuccess(IPAddress address, double rttMs)
    {
        ArgumentNullException.ThrowIfNull(address);

        Sent++;
        _address ??= address;

        Last = rttMs;
        if (rttMs < Best) Best = rttMs;
        if (rttMs > Worst) Worst = rttMs;

        // Welford's online algorithm
        _successCount++;
        double delta = rttMs - Average;
        Average += delta / _successCount;
        _m2 += delta * (rttMs - Average);

        // Ring buffer
        _ring[_ringHead] = rttMs;
        _ringHead = (_ringHead + 1) % RingBufferSize;
        if (_ringCount < RingBufferSize) _ringCount++;
    }

    /// <summary>
    /// Records a probe that received no response (timeout or error).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordLoss()
    {
        Sent++;
        Lost++;
    }

    /// <summary>
    /// Sets the resolved hostname.  Safe to call from any thread once per hop.
    /// </summary>
    public void SetHostName(string? hostName)
    {
        _hostName = hostName;
        DnsResolved = true;
    }

    /// <summary>
    /// Resets all statistics to their initial state while keeping the ring buffer allocation.
    /// </summary>
    public void Reset()
    {
        _address = null;
        Sent = 0;
        Lost = 0;
        Last = 0;
        Best = double.MaxValue;
        Worst = double.MinValue;
        Average = 0;
        _m2 = 0;
        _successCount = 0;
        _ringHead = 0;
        _ringCount = 0;
        _hostName = null;
        DnsResolved = false;
    }

    /// <summary>
    /// Copies the most-recent RTT samples into <paramref name="destination"/>, oldest-first.
    /// Returns the number of values written (≤ <paramref name="destination"/>.Length).
    /// Zero-alloc: no LINQ, no intermediate arrays.
    /// </summary>
    public int CopyRingSamples(Span<double> destination)
    {
        if (_ringCount == 0) return 0;

        int count = Math.Min(_ringCount, destination.Length);

        if (_ringCount < RingBufferSize)
        {
            // Buffer not yet full — data starts at index 0.
            _ring.AsSpan(0, count).CopyTo(destination);
        }
        else
        {
            // Buffer is full — oldest entry is at _ringHead.
            int firstPartLen = Math.Min(count, RingBufferSize - _ringHead);
            _ring.AsSpan(_ringHead, firstPartLen).CopyTo(destination);
            int remaining = count - firstPartLen;
            if (remaining > 0)
                _ring.AsSpan(0, remaining).CopyTo(destination[firstPartLen..]);
        }

        return count;
    }
}
