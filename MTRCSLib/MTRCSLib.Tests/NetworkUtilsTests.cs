using System.Buffers.Binary;
using System.Net;
using Shouldly;

namespace MTRCSLib.Tests;

// ── ToUInt32BigEndian ─────────────────────────────────────────────────────────

public class NetworkUtils_ToUInt32BigEndianTests
{
    [Fact]
    public void WhenGiven_1_2_3_4_ReturnsBigEndianUInt()
    {
        // 1.2.3.4 → 0x01020304
        IPAddress address = IPAddress.Parse("1.2.3.4");
        uint result = NetworkUtils.ToUInt32BigEndian(address);
        result.ShouldBe(0x01020304u);
    }

    [Fact]
    public void WhenGivenLoopback_ReturnsCorrectValue()
    {
        // 127.0.0.1 → 0x7F000001
        uint result = NetworkUtils.ToUInt32BigEndian(IPAddress.Loopback);
        result.ShouldBe(0x7F000001u);
    }

    [Fact]
    public void WhenGivenBroadcast_ReturnsAllOnes()
    {
        // 255.255.255.255 → 0xFFFFFFFF
        uint result = NetworkUtils.ToUInt32BigEndian(IPAddress.Broadcast);
        result.ShouldBe(0xFFFFFFFFu);
    }

    [Fact]
    public void WhenGivenAllZeros_ReturnsZero()
    {
        uint result = NetworkUtils.ToUInt32BigEndian(IPAddress.Any);
        result.ShouldBe(0u);
    }

    [Theory]
    [InlineData("10.0.0.1",   0x0A000001u)]
    [InlineData("192.168.1.1", 0xC0A80101u)]
    [InlineData("8.8.8.8",    0x08080808u)]
    public void WhenGivenKnownAddress_ReturnsBigEndianEncoding(string ip, uint expected)
    {
        NetworkUtils.ToUInt32BigEndian(IPAddress.Parse(ip)).ShouldBe(expected);
    }

    [Fact]
    public void WhenRoundTrippedThroughFromUInt32_YieldsOriginalAddress()
    {
        IPAddress original = IPAddress.Parse("172.16.254.1");
        uint encoded = NetworkUtils.ToUInt32BigEndian(original);
        IPAddress decoded = NetworkUtils.FromUInt32BigEndian(encoded);
        decoded.ShouldBe(original);
    }
}

// ── FromUInt32BigEndian ───────────────────────────────────────────────────────

public class NetworkUtils_FromUInt32BigEndianTests
{
    [Fact]
    public void WhenGiven_0x01020304_Returns_1_2_3_4()
    {
        IPAddress result = NetworkUtils.FromUInt32BigEndian(0x01020304u);
        result.ShouldBe(IPAddress.Parse("1.2.3.4"));
    }

    [Fact]
    public void WhenGivenZero_Returns_0_0_0_0()
    {
        NetworkUtils.FromUInt32BigEndian(0u).ShouldBe(IPAddress.Any);
    }

    [Fact]
    public void WhenGivenAllOnes_Returns_255_255_255_255()
    {
        NetworkUtils.FromUInt32BigEndian(0xFFFFFFFFu).ShouldBe(IPAddress.Broadcast);
    }

    [Theory]
    [InlineData(0x7F000001u, "127.0.0.1")]
    [InlineData(0x08080808u, "8.8.8.8")]
    [InlineData(0xC0A80101u, "192.168.1.1")]
    public void WhenGivenKnownUInt_ReturnsExpectedAddress(uint value, string expected)
    {
        NetworkUtils.FromUInt32BigEndian(value).ShouldBe(IPAddress.Parse(expected));
    }

    [Fact]
    public void WhenRoundTrippedThroughToUInt32_YieldsOriginalValue()
    {
        uint original = 0xAC10FE01u; // 172.16.254.1
        IPAddress ip = NetworkUtils.FromUInt32BigEndian(original);
        uint encoded = NetworkUtils.ToUInt32BigEndian(ip);
        encoded.ShouldBe(original);
    }

    [Fact]
    public void ResultIsIPv4()
    {
        IPAddress result = NetworkUtils.FromUInt32BigEndian(0x01020304u);
        result.AddressFamily.ShouldBe(System.Net.Sockets.AddressFamily.InterNetwork);
    }
}

// ── IsIPv4 ────────────────────────────────────────────────────────────────────

public class NetworkUtils_IsIPv4Tests
{
    [Fact]
    public void WhenGivenLoopback_ReturnsTrue()
    {
        NetworkUtils.IsIPv4(IPAddress.Loopback).ShouldBeTrue();
    }

    [Fact]
    public void WhenGivenBroadcast_ReturnsTrue()
    {
        NetworkUtils.IsIPv4(IPAddress.Broadcast).ShouldBeTrue();
    }

    [Fact]
    public void WhenGivenAny_ReturnsTrue()
    {
        NetworkUtils.IsIPv4(IPAddress.Any).ShouldBeTrue();
    }

    [Fact]
    public void WhenGivenIPv6Loopback_ReturnsFalse()
    {
        NetworkUtils.IsIPv4(IPAddress.IPv6Loopback).ShouldBeFalse();
    }

    [Fact]
    public void WhenGivenIPv6Any_ReturnsFalse()
    {
        NetworkUtils.IsIPv4(IPAddress.IPv6Any).ShouldBeFalse();
    }

    [Fact]
    public void WhenGivenMappedIPv4InIPv6_ReturnsFalse()
    {
        // ::ffff:192.168.1.1 is AddressFamily.InterNetworkV6, not InterNetwork
        IPAddress mapped = IPAddress.Parse("::ffff:192.168.1.1");
        NetworkUtils.IsIPv4(mapped).ShouldBeFalse();
    }
}

// ── WriteIcmpEchoRequest ──────────────────────────────────────────────────────

public class NetworkUtils_WriteIcmpEchoRequestTests
{
    // Helper: allocate a fresh buffer of the given size
    private static byte[] MakeBuffer(int size) => new byte[size];

    [Fact]
    public void WritesTypeEightAtByteZero()
    {
        byte[] buf = MakeBuffer(8);
        NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 1, sequence: 1);
        buf[0].ShouldBe((byte)8);
    }

    [Fact]
    public void WritesCodeZeroAtByteOne()
    {
        byte[] buf = MakeBuffer(8);
        NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 1, sequence: 1);
        buf[1].ShouldBe((byte)0);
    }

    [Fact]
    public void WritesIdentifierBigEndianAtBytes4And5()
    {
        byte[] buf = MakeBuffer(8);
        NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 0xABCD, sequence: 0);
        buf[4].ShouldBe((byte)0xAB);
        buf[5].ShouldBe((byte)0xCD);
    }

    [Fact]
    public void WritesSequenceBigEndianAtBytes6And7()
    {
        byte[] buf = MakeBuffer(8);
        NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 0, sequence: 0x1234);
        buf[6].ShouldBe((byte)0x12);
        buf[7].ShouldBe((byte)0x34);
    }

    [Fact]
    public void ReturnsTotalLengthEqualToEightPlusDataLength()
    {
        byte[] buf = MakeBuffer(20);
        int written = NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 1, sequence: 1, dataLength: 12);
        written.ShouldBe(20);
    }

    [Fact]
    public void WhenDataLengthIsZero_ReturnsTotalLengthOfEight()
    {
        byte[] buf = MakeBuffer(8);
        int written = NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 0, sequence: 0, dataLength: 0);
        written.ShouldBe(8);
    }

    [Fact]
    public void DataBytesAreZeroFilled()
    {
        byte[] buf = MakeBuffer(16);
        // Pre-fill with 0xFF to ensure the method clears them
        Array.Fill(buf, (byte)0xFF);
        NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 0, sequence: 0, dataLength: 8);
        buf[8..16].ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void ChecksumFieldIsNonZeroForNonTrivialPacket()
    {
        // A packet with type=8, code=0, id≠0 will have a non-zero checksum.
        byte[] buf = MakeBuffer(8);
        NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 0x1234, sequence: 0x0001);
        ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2, 2));
        checksum.ShouldNotBe((ushort)0);
    }

    [Fact]
    public void ReComputingChecksumOverWrittenBufferYieldsZero()
    {
        // RFC 792: computing the checksum over the full packet (including the checksum field)
        // must yield 0xFFFF (all-ones) when the checksum was correct; or equivalently, after
        // zeroing the checksum field and recalculating it should match what was written.
        byte[] buf = MakeBuffer(16);
        int len = NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 0xBEEF, sequence: 0x0042, dataLength: 8);

        // Save checksum, zero the field, recompute and compare
        ushort written = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2, 2));
        buf[2] = 0;
        buf[3] = 0;
        ushort recomputed = NetworkUtils.ComputeIcmpChecksum(buf.AsSpan(0, len));
        recomputed.ShouldBe(written);
    }

    [Fact]
    public void WhenBufferTooSmall_ThrowsArgumentException()
    {
        byte[] buf = MakeBuffer(4); // smaller than required 8 bytes
        Should.Throw<ArgumentException>(() =>
            NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 1, sequence: 1));
    }

    [Fact]
    public void WhenBufferTooSmallForData_ThrowsArgumentException()
    {
        byte[] buf = MakeBuffer(10); // 8 header + 2 data, but requesting 8 data = 16 total
        Should.Throw<ArgumentException>(() =>
            NetworkUtils.WriteIcmpEchoRequest(buf, identifier: 1, sequence: 1, dataLength: 8));
    }

    [Theory]
    [InlineData(0x0000, 0x0000)]
    [InlineData(0x1234, 0x5678)]
    [InlineData(0xFFFF, 0xFFFF)]
    public void IdentifierAndSequenceRoundTripCorrectly(int id, int seq)
    {
        byte[] buf = MakeBuffer(8);
        NetworkUtils.WriteIcmpEchoRequest(buf, (ushort)id, (ushort)seq);

        ushort gotId  = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4, 2));
        ushort gotSeq = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6, 2));

        gotId.ShouldBe((ushort)id);
        gotSeq.ShouldBe((ushort)seq);
    }
}

// ── ComputeIcmpChecksum ───────────────────────────────────────────────────────

public class NetworkUtils_ComputeIcmpChecksumTests
{
    [Fact]
    public void WhenAllBytesAreZero_ReturnsAllOnesComplement()
    {
        // One's complement of zero-sum = 0xFFFF
        Span<byte> data = stackalloc byte[8];
        NetworkUtils.ComputeIcmpChecksum(data).ShouldBe((ushort)0xFFFF);
    }

    [Fact]
    public void WhenSingleZeroByte_ReturnsCorrectOddByteResult()
    {
        // Single 0x00 byte is left-shifted to 0x0000 → complement = 0xFFFF
        ReadOnlySpan<byte> data = [0x00];
        NetworkUtils.ComputeIcmpChecksum(data).ShouldBe((ushort)0xFFFF);
    }

    [Fact]
    public void WhenAllBytesAreFF_ReturnsZero()
    {
        // 0xFFFF + 0xFFFF = 0x1FFFE → fold → 0xFFFF → complement = 0x0000
        Span<byte> data = stackalloc byte[4];
        data.Fill(0xFF);
        NetworkUtils.ComputeIcmpChecksum(data).ShouldBe((ushort)0x0000);
    }

    [Fact]
    public void KnownIcmpEchoRequest_ReturnsKnownChecksum()
    {
        // ICMP echo request: type=8, code=0, cksum=0, id=0x0001, seq=0x0001
        // Words: 0x0800 + 0x0000 + 0x0001 + 0x0001 = 0x0802 → ~0x0802 = 0xF7FD
        ReadOnlySpan<byte> header = [0x08, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01];
        ushort checksum = NetworkUtils.ComputeIcmpChecksum(header);
        checksum.ShouldBe((ushort)0xF7FD);
    }

    [Fact]
    public void ComputingChecksumTwiceOverPacketWithChecksumYieldsZeroOrAllOnes()
    {
        // After embedding the checksum, recomputing over the full packet yields 0x0000
        // (because the sum of a correctly-checksummed packet is 0xFFFF → ~0xFFFF = 0x0000).
        byte[] packet = [0x08, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01];
        ushort checksum = NetworkUtils.ComputeIcmpChecksum(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), checksum);
        NetworkUtils.ComputeIcmpChecksum(packet).ShouldBe((ushort)0x0000);
    }

    [Fact]
    public void OddLengthInput_PadsLastByteCorrectly()
    {
        // 3 bytes: 0x08, 0x00, 0xAB
        // Words: 0x0800, then odd byte 0xAB padded to 0xAB00
        // Sum: 0x0800 + 0xAB00 = 0xB300 → ~0xB300 = 0x4CFF
        ReadOnlySpan<byte> data = [0x08, 0x00, 0xAB];
        NetworkUtils.ComputeIcmpChecksum(data).ShouldBe((ushort)0x4CFF);
    }

    [Fact]
    public void EmptyInput_ReturnsAllOnesComplement()
    {
        // Sum of nothing is 0 → ~0 = 0xFFFF
        NetworkUtils.ComputeIcmpChecksum(ReadOnlySpan<byte>.Empty).ShouldBe((ushort)0xFFFF);
    }

    [Fact]
    public void LargeInput_FoldsCarryBitsCorrectly()
    {
        // 512 bytes of 0x01 → 256 words of 0x0101 → sum = 256 * 0x0101 = 0x10100
        // Fold: 0x0100 + 0x0001 = 0x0101 → ~0x0101 = 0xFEFE
        byte[] data = new byte[512];
        Array.Fill(data, (byte)0x01);
        NetworkUtils.ComputeIcmpChecksum(data).ShouldBe((ushort)0xFEFE);
    }
}

// ── StdDevFromVariance ────────────────────────────────────────────────────────

public class NetworkUtils_StdDevFromVarianceTests
{
    [Fact]
    public void WhenVarianceIsZero_ReturnsZero()
    {
        NetworkUtils.StdDevFromVariance(0.0).ShouldBe(0.0);
    }

    [Fact]
    public void WhenVarianceIsNegative_ReturnsZero()
    {
        NetworkUtils.StdDevFromVariance(-1.0).ShouldBe(0.0);
    }

    [Fact]
    public void WhenVarianceIsOne_ReturnsOne()
    {
        NetworkUtils.StdDevFromVariance(1.0).ShouldBe(1.0);
    }

    [Fact]
    public void WhenVarianceIsFour_ReturnsTwo()
    {
        NetworkUtils.StdDevFromVariance(4.0).ShouldBe(2.0);
    }

    [Fact]
    public void WhenVarianceIsFifty_ReturnsSquareRootOfFifty()
    {
        NetworkUtils.StdDevFromVariance(50.0).ShouldBe(Math.Sqrt(50.0));
    }

    [Theory]
    [InlineData(9.0,   3.0)]
    [InlineData(25.0,  5.0)]
    [InlineData(100.0, 10.0)]
    public void WhenGivenPerfectSquareVariance_ReturnsExactRoot(double variance, double expected)
    {
        NetworkUtils.StdDevFromVariance(variance).ShouldBe(expected);
    }

    [Fact]
    public void WhenVarianceIsVerySmall_ReturnsPositiveResult()
    {
        double result = NetworkUtils.StdDevFromVariance(1e-10);
        result.ShouldBeGreaterThan(0.0);
    }

    [Fact]
    public void ResultIsAlwaysNonNegative()
    {
        foreach (double variance in new[] { -100.0, -0.001, 0.0, 0.001, 100.0 })
            NetworkUtils.StdDevFromVariance(variance).ShouldBeGreaterThanOrEqualTo(0.0);
    }
}

// ── IsIPv6 ────────────────────────────────────────────────────────────────────

public class NetworkUtils_IsIPv6Tests
{
    [Fact]
    public void WhenGivenIPv6Loopback_ReturnsTrue()
    {
        NetworkUtils.IsIPv6(IPAddress.IPv6Loopback).ShouldBeTrue();
    }

    [Fact]
    public void WhenGivenIPv6Any_ReturnsTrue()
    {
        NetworkUtils.IsIPv6(IPAddress.IPv6Any).ShouldBeTrue();
    }

    [Fact]
    public void WhenGivenGoogleIPv6Address_ReturnsTrue()
    {
        NetworkUtils.IsIPv6(IPAddress.Parse("2001:4860:4860::8888")).ShouldBeTrue();
    }

    [Fact]
    public void WhenGivenMappedIPv4InIPv6_ReturnsTrue()
    {
        // ::ffff:192.168.1.1 is AddressFamily.InterNetworkV6
        NetworkUtils.IsIPv6(IPAddress.Parse("::ffff:192.168.1.1")).ShouldBeTrue();
    }

    [Fact]
    public void WhenGivenIPv4Loopback_ReturnsFalse()
    {
        NetworkUtils.IsIPv6(IPAddress.Loopback).ShouldBeFalse();
    }

    [Fact]
    public void WhenGivenIPv4Broadcast_ReturnsFalse()
    {
        NetworkUtils.IsIPv6(IPAddress.Broadcast).ShouldBeFalse();
    }

    [Fact]
    public void WhenGivenIPv4Any_ReturnsFalse()
    {
        NetworkUtils.IsIPv6(IPAddress.Any).ShouldBeFalse();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    public void WhenGivenIPv4Address_ReturnsFalse(string ip)
    {
        NetworkUtils.IsIPv6(IPAddress.Parse(ip)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("2606:4700:4700::1111")]
    public void WhenGivenVariousIPv6Addresses_ReturnsTrue(string ip)
    {
        NetworkUtils.IsIPv6(IPAddress.Parse(ip)).ShouldBeTrue();
    }

    // Symmetry: IsIPv4 and IsIPv6 are mutually exclusive for all standard addresses.
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    public void IsIPv4AndIsIPv6_AreMutuallyExclusive(string ip)
    {
        var address = IPAddress.Parse(ip);
        (NetworkUtils.IsIPv4(address) && NetworkUtils.IsIPv6(address)).ShouldBeFalse();
        (NetworkUtils.IsIPv4(address) || NetworkUtils.IsIPv6(address)).ShouldBeTrue();
    }
}
