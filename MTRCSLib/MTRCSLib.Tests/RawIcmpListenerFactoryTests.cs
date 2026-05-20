using System.Net.Sockets;
using MTRCSLib.Abstractions;
using Shouldly;

namespace MTRCSLib.Tests;

// ── Unsupported address family — no sockets opened, safe to run anywhere ──────

public class RawIcmpListenerFactory_UnsupportedFamilyTests
{
    [Fact]
    public void WhenGivenUnixFamily_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() =>
            RawIcmpListenerFactory.Create(AddressFamily.Unix));
    }

    [Fact]
    public void WhenGivenUnspecifiedFamily_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() =>
            RawIcmpListenerFactory.Create(AddressFamily.Unspecified));
    }

    [Theory]
    [InlineData(AddressFamily.Unix)]
    [InlineData(AddressFamily.Unspecified)]
    [InlineData(AddressFamily.AppleTalk)]
    public void WhenGivenUnsupportedFamily_ExceptionNamesAddressFamilyParameter(AddressFamily family)
    {
        ArgumentException ex = Should.Throw<ArgumentException>(() =>
            RawIcmpListenerFactory.Create(family));
        ex.ParamName.ShouldBe("addressFamily");
    }

    [Theory]
    [InlineData(AddressFamily.Unix)]
    [InlineData(AddressFamily.Unspecified)]
    public void WhenGivenUnsupportedFamily_ExceptionMessageContainsFamilyName(AddressFamily family)
    {
        ArgumentException ex = Should.Throw<ArgumentException>(() =>
            RawIcmpListenerFactory.Create(family));
        ex.Message.ShouldContain(family.ToString());
    }
}

// ── Raw socket creation — requires elevated privileges ────────────────────────
// These tests verify that the factory produces a live, disposable listener.
// They are skipped in CI / non-elevated environments; remove Skip to verify manually.

public class RawIcmpListenerFactory_IntegrationTests
{
    private const string SkipReason =
        "Integration test — requires elevated privileges (raw sockets). Remove Skip to verify manually.";

    [Fact(Skip = SkipReason)]
    public void WhenGivenInterNetwork_ReturnsNonNullListener()
    {
        using IRawIcmpListener listener = RawIcmpListenerFactory.Create(AddressFamily.InterNetwork);
        listener.ShouldNotBeNull();
    }

    [Fact(Skip = SkipReason)]
    public void WhenGivenInterNetworkV6_ReturnsNonNullListener()
    {
        using IRawIcmpListener listener = RawIcmpListenerFactory.Create(AddressFamily.InterNetworkV6);
        listener.ShouldNotBeNull();
    }

    [Fact(Skip = SkipReason)]
    public void WhenGivenInterNetwork_DisposesWithoutThrowing()
    {
        IRawIcmpListener listener = RawIcmpListenerFactory.Create(AddressFamily.InterNetwork);
        Should.NotThrow(() => listener.Dispose());
    }

    [Fact(Skip = SkipReason)]
    public void WhenGivenInterNetworkV6_DisposesWithoutThrowing()
    {
        IRawIcmpListener listener = RawIcmpListenerFactory.Create(AddressFamily.InterNetworkV6);
        Should.NotThrow(() => listener.Dispose());
    }
}
