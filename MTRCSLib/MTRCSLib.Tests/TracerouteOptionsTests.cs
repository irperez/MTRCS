using System.Net;
using Shouldly;

namespace MTRCSLib.Tests;

// ── Null guards ───────────────────────────────────────────────────────────────

public class TracerouteOptions_NullGuardTests
{
    [Fact]
    public void WhenTargetIsNull_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() =>
            TracerouteOptions.Create(null!, "host"));
    }

    [Fact]
    public void WhenHostIsNull_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() =>
            TracerouteOptions.Create(IPAddress.Loopback, null!));
    }
}

// ── Address family guard ──────────────────────────────────────────────────────

public class TracerouteOptions_AddressFamilyTests
{
    [Fact]
    public void WhenTargetIsIPv4_DoesNotThrow()
    {
        Should.NotThrow(() => TracerouteOptions.Create(IPAddress.Loopback, "localhost"));
    }

    [Fact]
    public void WhenTargetIsIPv6_DoesNotThrow()
    {
        Should.NotThrow(() => TracerouteOptions.Create(IPAddress.IPv6Loopback, "localhost"));
    }
}

// ── maxHops validation ────────────────────────────────────────────────────────

public class TracerouteOptions_MaxHopsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(int.MaxValue)]
    public void WhenMaxHopsOutOfRange_ThrowsArgumentOutOfRangeException(int maxHops)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", maxHops: maxHops));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(255)]
    public void WhenMaxHopsInRange_DoesNotThrow(int maxHops)
    {
        Should.NotThrow(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", maxHops: maxHops));
    }

    [Fact]
    public void WhenCreated_MaxHopsIsStored()
    {
        var opts = TracerouteOptions.Create(IPAddress.Loopback, "host", maxHops: 15);
        opts.MaxHops.ShouldBe(15);
    }
}

// ── intervalMs validation ─────────────────────────────────────────────────────

public class TracerouteOptions_IntervalMsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void WhenIntervalMsNotPositive_ThrowsArgumentOutOfRangeException(int intervalMs)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", intervalMs: intervalMs));
    }

    [Fact]
    public void WhenIntervalMsIsOne_DoesNotThrow()
    {
        Should.NotThrow(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", intervalMs: 1));
    }

    [Fact]
    public void WhenCreated_IntervalMsIsStored()
    {
        var opts = TracerouteOptions.Create(IPAddress.Loopback, "host", intervalMs: 500);
        opts.IntervalMs.ShouldBe(500);
    }
}

// ── timeoutMs validation ──────────────────────────────────────────────────────

public class TracerouteOptions_TimeoutMsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WhenTimeoutMsNotPositive_ThrowsArgumentOutOfRangeException(int timeoutMs)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", timeoutMs: timeoutMs));
    }

    [Fact]
    public void WhenTimeoutMsIsOne_DoesNotThrow()
    {
        Should.NotThrow(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", timeoutMs: 1));
    }
}

// ── payloadBytes validation ───────────────────────────────────────────────────

public class TracerouteOptions_PayloadBytesTests
{
    [Fact]
    public void WhenPayloadBytesIsNegative_ThrowsArgumentOutOfRangeException()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", payloadBytes: -1));
    }

    [Fact]
    public void WhenPayloadBytesIsZero_DoesNotThrow()
    {
        Should.NotThrow(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", payloadBytes: 0));
    }
}

// ── port validation ───────────────────────────────────────────────────────────

public class TracerouteOptions_PortValidationTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void WhenPortOutOfRange_ThrowsArgumentOutOfRangeException(int port)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", port: port));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(65535)]
    public void WhenPortInRange_DoesNotThrow(int port)
    {
        Should.NotThrow(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", port: port));
    }
}

// ── DSCP validation ───────────────────────────────────────────────────────────

public class TracerouteOptions_DscpValidationTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(100)]
    public void WhenDscpOutOfRange_ThrowsArgumentOutOfRangeException(int dscp)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", dscpValue: dscp));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(46)]  // EF (Expedited Forwarding)
    [InlineData(63)]
    public void WhenDscpInRange_DoesNotThrow(int dscp)
    {
        Should.NotThrow(() =>
            TracerouteOptions.Create(IPAddress.Loopback, "host", dscpValue: dscp));
    }

    [Fact]
    public void WhenCreated_DscpValueIsStored()
    {
        var opts = TracerouteOptions.Create(IPAddress.Loopback, "host", dscpValue: 46);
        opts.DscpValue.ShouldBe(46);
    }
}

// ── Default port resolution per mode ─────────────────────────────────────────

public class TracerouteOptions_DefaultPortResolutionTests
{
    [Fact]
    public void WhenIcmpModeAndPortZero_PortRemainsZero()
    {
        var opts = TracerouteOptions.Create(IPAddress.Loopback, "host",
            mode: ProbeMode.Icmp, port: 0);
        opts.Port.ShouldBe(0);
    }

    [Fact]
    public void WhenTcpModeAndPortZero_DefaultsToPort80()
    {
        var opts = TracerouteOptions.Create(IPAddress.Loopback, "host",
            mode: ProbeMode.Tcp, port: 0);
        opts.Port.ShouldBe(80);
    }

    [Fact]
    public void WhenUdpModeAndPortZero_DefaultsToPort33434()
    {
        var opts = TracerouteOptions.Create(IPAddress.Loopback, "host",
            mode: ProbeMode.Udp, port: 0);
        opts.Port.ShouldBe(33434);
    }

    [Fact]
    public void WhenTcpModeAndPortExplicit_UsesSuppliedPort()
    {
        var opts = TracerouteOptions.Create(IPAddress.Loopback, "host",
            mode: ProbeMode.Tcp, port: 443);
        opts.Port.ShouldBe(443);
    }

    [Fact]
    public void WhenUdpModeAndPortExplicit_UsesSuppliedPort()
    {
        var opts = TracerouteOptions.Create(IPAddress.Loopback, "host",
            mode: ProbeMode.Udp, port: 5000);
        opts.Port.ShouldBe(5000);
    }

    [Fact]
    public void WhenIcmpModeAndPortExplicit_UsesSuppliedPort()
    {
        var opts = TracerouteOptions.Create(IPAddress.Loopback, "host",
            mode: ProbeMode.Icmp, port: 8080);
        opts.Port.ShouldBe(8080);
    }
}

// ── Property storage ──────────────────────────────────────────────────────────

public class TracerouteOptions_PropertyStorageTests
{
    private static readonly IPAddress Target = IPAddress.Parse("1.2.3.4");

    [Fact]
    public void WhenCreated_TargetIsStored()
    {
        var opts = TracerouteOptions.Create(Target, "host");
        opts.Target.ShouldBe(Target);
    }

    [Fact]
    public void WhenCreated_HostIsStored()
    {
        var opts = TracerouteOptions.Create(Target, "example.com");
        opts.Host.ShouldBe("example.com");
    }

    [Fact]
    public void WhenCreated_EnableAsnIsStored()
    {
        var opts = TracerouteOptions.Create(Target, "host", enableAsn: true);
        opts.EnableAsn.ShouldBeTrue();
    }

    [Fact]
    public void WhenCreated_WarmupPingIsStored()
    {
        var opts = TracerouteOptions.Create(Target, "host", warmupPing: false);
        opts.WarmupPing.ShouldBeFalse();
    }

    [Fact]
    public void WhenCreated_ShowPercentilesIsStored()
    {
        var opts = TracerouteOptions.Create(Target, "host", showPercentiles: true);
        opts.ShowPercentiles.ShouldBeTrue();
    }

    [Fact]
    public void WhenCreated_ModeIsStored()
    {
        var opts = TracerouteOptions.Create(Target, "host", mode: ProbeMode.Tcp);
        opts.Mode.ShouldBe(ProbeMode.Tcp);
    }

    [Fact]
    public void DefaultConstants_HaveExpectedValues()
    {
        TracerouteOptions.DefaultMaxHops.ShouldBe(30);
        TracerouteOptions.DefaultIntervalMs.ShouldBe(1000);
        TracerouteOptions.DefaultTimeoutMs.ShouldBe(800);
        TracerouteOptions.DefaultPayloadBytes.ShouldBe(28);
    }

    [Fact]
    public void WhenCreatedWithDefaults_PropertiesMatchDefaultConstants()
    {
        var opts = TracerouteOptions.Create(Target, "host");
        opts.MaxHops.ShouldBe(TracerouteOptions.DefaultMaxHops);
        opts.IntervalMs.ShouldBe(TracerouteOptions.DefaultIntervalMs);
        opts.TimeoutMs.ShouldBe(TracerouteOptions.DefaultTimeoutMs);
        opts.PayloadBytes.ShouldBe(TracerouteOptions.DefaultPayloadBytes);
    }
}
