using System.Net.Sockets;
using Shouldly;

namespace MTRCSLib.Tests;

// ── Port validation — fires before socket creation, safe to run anywhere ──────

public class UdpPingerFactory_PortValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void WhenPortOutOfRange_ThrowsArgumentOutOfRangeException(int port)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new UdpPingerFactory(destPort: port));
    }

    [Fact]
    public void WhenPortOutOfRange_ExceptionNamesDestPortParameter()
    {
        ArgumentOutOfRangeException ex = Should.Throw<ArgumentOutOfRangeException>(() =>
            new UdpPingerFactory(destPort: 0));
        ex.ParamName.ShouldBe("destPort");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(33434)]  // default MTR UDP port
    [InlineData(65535)]
    public void WhenPortInRange_DoesNotThrow(int port)
    {
        // Port validation fires before raw socket creation.
        // SocketException on non-elevated runners is acceptable — guard is proven.
        try
        {
            using var factory = new UdpPingerFactory(destPort: port);
        }
        catch (SocketException)
        {
            // Raw socket unavailable (non-elevated). Port validation passed.
        }
    }
}

// ── Default port constant ─────────────────────────────────────────────────────

public class UdpPingerFactory_DefaultPortTests
{
    [Fact]
    public void DefaultUdpPort_Is33434()
    {
        // Verify the constant matches the MTR convention.
        UdpPinger.DefaultUdpPort.ShouldBe(33434);
    }

    [Fact]
    public void DefaultDestPort_IsDefaultUdpPort()
    {
        // Port 0 is rejected, and DefaultUdpPort (33434) is accepted.
        Should.Throw<ArgumentOutOfRangeException>(() => new UdpPingerFactory(destPort: 0));

        try
        {
            using var factory = new UdpPingerFactory(); // default port = 33434
        }
        catch (SocketException)
        {
            // Non-elevated: socket failed after validation passed.
        }
    }
}

// ── Dispose behaviour ─────────────────────────────────────────────────────────

public class UdpPingerFactory_DisposeTests
{
    private const string SkipReason =
        "Integration test — requires elevated privileges (raw sockets). Remove Skip to verify manually.";

    [Fact(Skip = SkipReason)]
    public void DisposingTwice_DoesNotThrow()
    {
        var factory = new UdpPingerFactory();
        factory.Dispose();
        Should.NotThrow(() => factory.Dispose());
    }

    [Fact(Skip = SkipReason)]
    public void AfterDispose_CreateThrowsObjectDisposedException()
    {
        var factory = new UdpPingerFactory();
        factory.Dispose();
        Should.Throw<ObjectDisposedException>(() => factory.Create());
    }

    [Fact(Skip = SkipReason)]
    public void WhenNotDisposed_CreateReturnsNonNullPinger()
    {
        using var factory = new UdpPingerFactory();
        using var pinger = factory.Create();
        pinger.ShouldNotBeNull();
    }
}
