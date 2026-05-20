using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MTRCSLib;

/// <summary>
/// Zero-allocation helpers for IPv4 address manipulation and ICMP payload building.
/// </summary>
internal static class NetworkUtils
{
    /// <summary>
    /// Converts an IPv4 address to its big-endian uint representation without heap allocation.
    /// </summary>
    public static uint ToUInt32BigEndian(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out int written) || written != 4)
            throw new ArgumentException("Address must be IPv4.", nameof(address));
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>
    /// Creates an IPv4 address from a big-endian uint without heap allocation beyond the IPAddress ctor.
    /// </summary>
    public static IPAddress FromUInt32BigEndian(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        // IPAddress(ReadOnlySpan<byte>) avoids intermediate array on .NET 6+.
        return new IPAddress(bytes);
    }

    /// <summary>
    /// Returns true if <paramref name="address"/> is an IPv4 address (not mapped).
    /// </summary>
    public static bool IsIPv4(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>
    /// Returns true if <paramref name="address"/> is an IPv6 address.
    /// </summary>
    public static bool IsIPv6(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>
    /// Writes a minimal ICMP Echo Request payload (header + optional data) into <paramref name="buffer"/>.
    /// Caller must ensure buffer is at least 8 bytes (header only).
    /// Returns the number of bytes written.
    /// </summary>
    /// <param name="buffer">Destination span — must be ≥ 8 bytes.</param>
    /// <param name="identifier">ICMP identifier field (process/session id).</param>
    /// <param name="sequence">ICMP sequence number.</param>
    /// <param name="dataLength">Number of zero-fill data bytes after the header.</param>
    public static int WriteIcmpEchoRequest(Span<byte> buffer, ushort identifier, ushort sequence, int dataLength = 0)
    {
        int totalLength = 8 + dataLength;
        if (buffer.Length < totalLength)
            throw new ArgumentException($"Buffer must be at least {totalLength} bytes.", nameof(buffer));

        // Type=8 (echo request), Code=0
        buffer[0] = 8;
        buffer[1] = 0;

        // Checksum placeholder
        buffer[2] = 0;
        buffer[3] = 0;

        // Identifier (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(buffer[4..], identifier);

        // Sequence (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(buffer[6..], sequence);

        // Zero-fill data
        if (dataLength > 0)
            buffer.Slice(8, dataLength).Clear();

        // Compute checksum over the header+data
        ushort checksum = ComputeIcmpChecksum(buffer[..totalLength]);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], checksum);

        return totalLength;
    }

    /// <summary>
    /// Computes the one's-complement checksum used by ICMP.
    /// </summary>
    public static ushort ComputeIcmpChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        int length = data.Length;

        while (i + 1 < length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            i += 2;
        }

        if (i < length)
            sum += (uint)(data[i] << 8); // pad odd byte

        // Fold 32-bit sum into 16-bit
        while (sum >> 16 != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);

        return (ushort)~sum;
    }

    /// <summary>
    /// Computes the standard deviation from pre-computed variance (Welford's).
    /// Returns 0 when fewer than 2 samples.
    /// </summary>
    public static double StdDevFromVariance(double variance) =>
        variance <= 0.0 ? 0.0 : Math.Sqrt(variance);
}
