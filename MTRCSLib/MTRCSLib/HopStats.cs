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
    /// <summary>Number of recent RTT samples kept for jitter/sparkline display and percentile calculations.</summary>
    public const int RingBufferSize = 128;

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

    // ── Percentiles ───────────────────────────────────────────────────────────
    // Sorted scratch copy rebuilt on demand; null until first percentile access.
    private double[]? _sortedScratch;
    private bool _sortedDirty = true; // true whenever new samples have been added

    /// <summary>
    /// 95th-percentile RTT in ms, computed over the ring-buffer samples.
    /// Returns <see cref="double.NaN"/> until at least two replies have been received.
    /// </summary>
    public double P95 => ComputePercentile(0.95);

    /// <summary>
    /// 99th-percentile RTT in ms, computed over the ring-buffer samples.
    /// Returns <see cref="double.NaN"/> until at least two replies have been received.
    /// </summary>
    public double P99 => ComputePercentile(0.99);

    // ── Jitter ────────────────────────────────────────────────────────────────
    private double _prevRtt;   // previous successful RTT (for jitter delta)
    private bool _hasPrevRtt;

    /// <summary>
    /// Absolute difference between the last two successful RTT samples (|last − prev|).
    /// Returns <see cref="double.NaN"/> until at least two replies have been received.
    /// </summary>
    public double Jitter { get; private set; } = double.NaN;

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

    // ── ECMP / load-balanced alternate addresses ──────────────────────────────
    // Populated when probes at this TTL return from more than one distinct IP
    // (Equal-Cost Multi-Path routing).  The primary address is tracked in _address;
    // additional ones live here.  Cap at 8 to keep memory bounded.
    private const int MaxAltAddresses = 8;
    private List<System.Net.IPAddress>? _altAddresses;
    private List<string?>? _altHostNames;       // parallel to _altAddresses; null = not yet resolved
    private List<bool>?    _altDnsResolved;      // parallel; true once PTR lookup attempted

    /// <summary>Number of additional (ECMP) addresses seen at this hop.</summary>
    public int AltAddressCount => _altAddresses?.Count ?? 0;

    /// <summary>
    /// Registers <paramref name="address"/> as an alternate ECMP address if it is new.
    /// Returns <see langword="true"/> when the address was added and DNS should be scheduled.
    /// Must be called under the session stats lock.
    /// </summary>
    public bool TryRegisterAltAddress(System.Net.IPAddress address)
    {
        // Primary address is not an "alt".
        if (_address is not null && _address.Equals(address)) return false;

        _altAddresses ??= new List<System.Net.IPAddress>(2);
        foreach (var a in _altAddresses)
            if (a.Equals(address)) return false;

        if (_altAddresses.Count >= MaxAltAddresses) return false;

        _altAddresses.Add(address);
        (_altHostNames  ??= new List<string?>(2)).Add(null);
        (_altDnsResolved ??= new List<bool>(2)).Add(false);
        return true;
    }

    /// <summary>Returns the IP address at <paramref name="altIndex"/> (0-based).</summary>
    public System.Net.IPAddress GetAltAddress(int altIndex) => _altAddresses![altIndex];

    /// <summary>Returns the resolved hostname for the alt address at <paramref name="altIndex"/>, or <see langword="null"/>.</summary>
    public string? GetAltHostName(int altIndex) => _altHostNames?[altIndex];

    /// <summary>Returns <see langword="true"/> if DNS has been attempted for the alt address at <paramref name="altIndex"/>.</summary>
    public bool IsAltDnsResolved(int altIndex) => _altDnsResolved?[altIndex] ?? false;

    /// <summary>Sets the resolved hostname for the alt address at <paramref name="altIndex"/>.</summary>
    public void SetAltHostName(int altIndex, string? hostName)
    {
        if (_altHostNames is null || altIndex >= _altHostNames.Count) return;
        _altHostNames[altIndex] = hostName;
        if (_altDnsResolved is not null && altIndex < _altDnsResolved.Count)
            _altDnsResolved[altIndex] = true;
    }

    /// <summary>Marks the alt DNS slot as resolved (used to avoid duplicate lookups).</summary>
    public void MarkAltDnsScheduled(int altIndex)
    {
        if (_altDnsResolved is not null && altIndex < _altDnsResolved.Count)
            _altDnsResolved[altIndex] = true;
    }

    // ── ASN ───────────────────────────────────────────────────────────────────
    private AsnInfo? _asn;

    /// <summary>ASN information for this hop, populated asynchronously when <c>--asn</c> is enabled.</summary>
    public AsnInfo? Asn => _asn;

    /// <summary>True once ASN resolution has been attempted (success or failure).</summary>
    public bool AsnResolved { get; private set; }

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

        // Jitter: |last - prev|
        if (_hasPrevRtt)
            Jitter = Math.Abs(rttMs - _prevRtt);
        _prevRtt = rttMs;
        _hasPrevRtt = true;

        // Welford's online algorithm
        _successCount++;
        double delta = rttMs - Average;
        Average += delta / _successCount;
        _m2 += delta * (rttMs - Average);

        // Ring buffer
        _ring[_ringHead] = rttMs;
        _ringHead = (_ringHead + 1) % RingBufferSize;
        if (_ringCount < RingBufferSize) _ringCount++;
        _sortedDirty = true;
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
    /// Sets the resolved ASN info.  Safe to call from any thread once per hop.
    /// </summary>
    public void SetAsn(AsnInfo? asn)
    {
        _asn = asn;
        AsnResolved = true;
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
        Jitter = double.NaN;
        _prevRtt = 0;
        _hasPrevRtt = false;
        _asn = null;
        AsnResolved = false;
        _altAddresses?.Clear();
        _altHostNames?.Clear();
        _altDnsResolved?.Clear();
        _sortedDirty = true;
    }

    /// <summary>
    /// Computes the requested percentile (0.0–1.0) over current ring-buffer samples using
    /// nearest-rank interpolation.  Returns <see cref="double.NaN"/> when fewer than 2 samples
    /// are available.
    /// </summary>
    private double ComputePercentile(double fraction)
    {
        if (_ringCount < 2) return double.NaN;

        // Rebuild sorted scratch only when new samples have arrived.
        if (_sortedDirty || _sortedScratch is null || _sortedScratch.Length < _ringCount)
        {
            _sortedScratch = new double[_ringCount];
            CopyRingSamples(_sortedScratch.AsSpan(0, _ringCount));
            Array.Sort(_sortedScratch, 0, _ringCount);
            _sortedDirty = false;
        }

        // Nearest-rank: index = ceil(fraction * n) - 1, clamped.
        int idx = (int)Math.Ceiling(fraction * _ringCount) - 1;
        idx = Math.Clamp(idx, 0, _ringCount - 1);
        return _sortedScratch[idx];
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
