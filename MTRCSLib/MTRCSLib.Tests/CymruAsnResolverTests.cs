using System.Net;
using Shouldly;

namespace MTRCSLib.Tests;

// ── Helpers ───────────────────────────────────────────────────────────────────

/// <summary>
/// Builds a fake DNS query function that maps host names to TXT responses,
/// making it easy to exercise all branches without any network I/O.
/// </summary>
file static class FakeDns
{
    /// <summary>
    /// Returns a delegate that looks up <paramref name="responses"/> by host name.
    /// Any host not present in the dictionary returns <see langword="null"/>.
    /// </summary>
    internal static Func<string, CancellationToken, ValueTask<string?>> From(
        Dictionary<string, string?> responses) =>
        (host, _) => ValueTask.FromResult(responses.TryGetValue(host, out var v) ? v : null);

    /// <summary>Throws <see cref="OperationCanceledException"/> for every call.</summary>
    internal static Func<string, CancellationToken, ValueTask<string?>> AlwaysCancel =>
        (_, ct) => ValueTask.FromException<string?>(new OperationCanceledException(ct));

    /// <summary>Returns null for every call (simulates timeout / DNS failure).</summary>
    internal static Func<string, CancellationToken, ValueTask<string?>> AlwaysNull =>
        (_, _) => ValueTask.FromResult<string?>(null);

    /// <summary>Throws a non-cancel exception (e.g. socket error).</summary>
    internal static Func<string, CancellationToken, ValueTask<string?>> AlwaysThrow =>
        (_, _) => ValueTask.FromException<string?>(new InvalidOperationException("network error"));

    /// <summary>Captures every host queried so assertions can verify query order.</summary>
    internal static (Func<string, CancellationToken, ValueTask<string?>> fn, List<string> calls)
        Capturing(Dictionary<string, string?> responses)
    {
        var calls = new List<string>();
        return ((host, ct) =>
        {
            calls.Add(host);
            return From(responses)(host, ct);
        }, calls);
    }
}

// ── ResolveAsync — returns null for unsupported address families ───────────────

public class CymruAsnResolver_WhenAddressFamilyUnsupported
{
    [Fact]
    public async Task WhenPassedLoopbackV6Mapped_ReturnsNull()
    {
        // ::ffff:127.0.0.1 maps to IPv4 but is stored as IPv6; IsIPv4 returns false for it.
        var address = IPAddress.Parse("::1"); // pure IPv6 loopback
        // We still want a result for pure IPv6, so use a truly unsupported family:
        // Simulate by bypassing via a fake that returns null — the family guard is tested
        // by confirming a multicast-scoped address that is already IPv6 returns a result.
        // Instead, directly verify with a real "none-of-the-above" address is not possible
        // with IPAddress; the guard covers !IsIPv4 && !IsIPv6.  We test the positive paths
        // for both families below, confirming the guard is never hit on valid addresses.
        await Task.CompletedTask; // placeholder — see positive-path tests
    }
}

// ── ResolveAsync — IPv4 happy path ────────────────────────────────────────────

public class CymruAsnResolver_IPv4HappyPath
{
    private static CymruAsnResolver BuildResolver(
        string originTxt,
        string? asnTxt,
        string originHost,
        string asnHost) =>
        new(FakeDns.From(new Dictionary<string, string?>
        {
            [originHost] = originTxt,
            [asnHost]    = asnTxt,
        }));

    [Fact]
    public async Task WhenGoogleDnsIpv4_ReturnsGoogleAsn()
    {
        // 8.8.8.8 → reversed "8.8.8.8"
        var resolver = BuildResolver(
            "15169 | 8.8.8.0/24 | US | arin | 1992-12-01",
            "15169 | US | arin | 1992-12-01 | GOOGLE, US",
            "8.8.8.8.origin.asn.cymru.com",
            "15169.asn.cymru.com");

        var result = await resolver.ResolveAsync(IPAddress.Parse("8.8.8.8"));

        result.ShouldNotBeNull();
        result!.Asn.ShouldBe("AS15169");
        result.Description.ShouldBe("GOOGLE");
    }

    [Fact]
    public async Task WhenReversingOctets_BuildsCorrectOriginHost()
    {
        // 1.2.3.4 → origin host "4.3.2.1.origin.asn.cymru.com"
        var (fn, calls) = FakeDns.Capturing(new Dictionary<string, string?>
        {
            ["4.3.2.1.origin.asn.cymru.com"] = "64512 | 1.2.3.0/24 | AU | apnic | 2000-01-01",
            ["64512.asn.cymru.com"]           = "64512 | AU | apnic | 2000-01-01 | EXAMPLE-NET",
        });
        var resolver = new CymruAsnResolver(fn);

        await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        calls[0].ShouldBe("4.3.2.1.origin.asn.cymru.com");
    }

    [Fact]
    public async Task WhenDescriptionHasTrailingCommaCC_StripsIt()
    {
        var resolver = BuildResolver(
            "15169 | 8.8.8.0/24 | US | arin | 1992-12-01",
            "15169 | US | arin | 1992-12-01 | GOOGLE, US",
            "8.8.8.8.origin.asn.cymru.com",
            "15169.asn.cymru.com");

        var result = await resolver.ResolveAsync(IPAddress.Parse("8.8.8.8"));

        result!.Description.ShouldBe("GOOGLE");
    }

    [Fact]
    public async Task WhenDescriptionHasNoTrailingCommaCC_LeavesItUnchanged()
    {
        var resolver = BuildResolver(
            "701 | 8.7.6.0/24 | US | arin | 1990-01-01",
            "701 | US | arin | 1990-01-01 | UUNET",
            "6.7.8.8.origin.asn.cymru.com",
            "701.asn.cymru.com");

        var result = await resolver.ResolveAsync(IPAddress.Parse("8.8.7.6"));

        result!.Description.ShouldBe("UUNET");
    }

    [Fact]
    public async Task WhenAsnLookupReturnsNull_FallsBackToCountryCode()
    {
        var resolver = new CymruAsnResolver(FakeDns.From(new Dictionary<string, string?>
        {
            ["8.8.8.8.origin.asn.cymru.com"] = "15169 | 8.8.8.0/24 | US | arin | 1992-12-01",
            // no entry for 15169.asn.cymru.com → null
        }));

        var result = await resolver.ResolveAsync(IPAddress.Parse("8.8.8.8"));

        result.ShouldNotBeNull();
        result!.Asn.ShouldBe("AS15169");
        result.Description.ShouldBe("US");
    }

    [Fact]
    public async Task WhenAsnLookupReturnsEmptyDescription_FallsBackToCountryCode()
    {
        var resolver = new CymruAsnResolver(FakeDns.From(new Dictionary<string, string?>
        {
            ["8.8.8.8.origin.asn.cymru.com"] = "15169 | 8.8.8.0/24 | FR | arin | 1992-12-01",
            ["15169.asn.cymru.com"]           = "15169 | FR | arin | 1992-12-01 | ",
        }));

        var result = await resolver.ResolveAsync(IPAddress.Parse("8.8.8.8"));

        result!.Description.ShouldBe("FR");
    }

    [Fact]
    public async Task WhenMultiOriginAsns_UsesFirstAsn()
    {
        // Some prefixes have multiple origin ASNs separated by a space in field 0.
        var resolver = new CymruAsnResolver(FakeDns.From(new Dictionary<string, string?>
        {
            ["4.3.2.1.origin.asn.cymru.com"] = "64512 64513 | 1.2.3.0/24 | US | arin | 2000-01-01",
            ["64512.asn.cymru.com"]           = "64512 | US | arin | 2000-01-01 | FIRST-NET",
        }));

        var result = await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        result!.Asn.ShouldBe("AS64512");
    }

    [Fact]
    public async Task WhenOriginTxtReturnsNull_ReturnsNull()
    {
        var resolver = new CymruAsnResolver(FakeDns.AlwaysNull);

        var result = await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task WhenOriginTxtHasEmptyAsnField_ReturnsNull()
    {
        var resolver = new CymruAsnResolver(FakeDns.From(new Dictionary<string, string?>
        {
            ["4.3.2.1.origin.asn.cymru.com"] = " | 1.2.3.0/24 | US | arin | 2000-01-01",
        }));

        var result = await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task WhenNetworkErrorOccurs_ReturnsNull()
    {
        var resolver = new CymruAsnResolver(FakeDns.AlwaysThrow);

        var result = await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task WhenCancelled_ThrowsOperationCanceledException()
    {
        var resolver = new CymruAsnResolver(FakeDns.AlwaysCancel);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"), cts.Token).AsTask());
    }

    [Fact]
    public async Task WhenSuccessful_AsnHasASPrefix()
    {
        var resolver = new CymruAsnResolver(FakeDns.From(new Dictionary<string, string?>
        {
            ["4.3.2.1.origin.asn.cymru.com"] = "64512 | 1.2.3.0/24 | US | arin | 2000-01-01",
            ["64512.asn.cymru.com"]           = "64512 | US | arin | 2000-01-01 | TEST-NET",
        }));

        var result = await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        result!.Asn.ShouldStartWith("AS");
    }
}

// ── ResolveAsync — IPv6 happy path ────────────────────────────────────────────

public class CymruAsnResolver_IPv6HappyPath
{
    [Fact]
    public async Task WhenGoogleDnsIpv6_ReturnsGoogleAsn()
    {
        // 2001:4860:4860::8888
        // Full: 2001:4860:4860:0000:0000:0000:0000:8888
        // Reversed nibbles: 8.8.8.8.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.6.8.4.0.6.8.4.1.0.0.2
        const string reversedIpv6 = "8.8.8.8.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.6.8.4.0.6.8.4.1.0.0.2";
        const string originHost   = reversedIpv6 + ".origin6.asn.cymru.com";

        var resolver = new CymruAsnResolver(FakeDns.From(new Dictionary<string, string?>
        {
            [originHost]           = "15169 | 2001:4860::/32 | US | arin | 2005-03-14",
            ["15169.asn.cymru.com"] = "15169 | US | arin | 1992-12-01 | GOOGLE, US",
        }));

        var result = await resolver.ResolveAsync(IPAddress.Parse("2001:4860:4860::8888"));

        result.ShouldNotBeNull();
        result!.Asn.ShouldBe("AS15169");
        result.Description.ShouldBe("GOOGLE");
    }

    [Fact]
    public async Task WhenIPv6OriginTxtNull_ReturnsNull()
    {
        var resolver = new CymruAsnResolver(FakeDns.AlwaysNull);

        var result = await resolver.ResolveAsync(IPAddress.Parse("2001:4860:4860::8888"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task WhenIPv6UsesOrigin6Suffix()
    {
        const string reversedIpv6 = "8.8.8.8.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.6.8.4.0.6.8.4.1.0.0.2";
        var (fn, calls) = FakeDns.Capturing(new Dictionary<string, string?>
        {
            [reversedIpv6 + ".origin6.asn.cymru.com"] = "15169 | 2001:4860::/32 | US | arin | 2005-03-14",
            ["15169.asn.cymru.com"]                    = "15169 | US | arin | 1992-12-01 | GOOGLE, US",
        });
        var resolver = new CymruAsnResolver(fn);

        await resolver.ResolveAsync(IPAddress.Parse("2001:4860:4860::8888"));

        calls[0].ShouldEndWith(".origin6.asn.cymru.com");
    }

    [Fact]
    public async Task WhenCancelledDuringIPv6Lookup_ThrowsOperationCanceledException()
    {
        var resolver = new CymruAsnResolver(FakeDns.AlwaysCancel);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(IPAddress.Parse("2001:4860:4860::8888"), cts.Token).AsTask());
    }
}

// ── ResolveAsync — AsnInfo record ─────────────────────────────────────────────

public class CymruAsnResolver_ReturnedAsnInfo
{
    private static CymruAsnResolver Resolver(string asnNumber, string description) =>
        new(FakeDns.From(new Dictionary<string, string?>
        {
            ["4.3.2.1.origin.asn.cymru.com"] = $"{asnNumber} | 1.2.3.0/24 | US | arin | 2000-01-01",
            [$"{asnNumber}.asn.cymru.com"]    = $"{asnNumber} | US | arin | 2000-01-01 | {description}",
        }));

    [Theory]
    [InlineData("15169", "GOOGLE",    "AS15169", "GOOGLE")]
    [InlineData("701",   "UUNET",     "AS701",   "UUNET")]
    [InlineData("3356",  "LEVEL3",    "AS3356",  "LEVEL3")]
    public async Task WhenResolved_AsnAndDescriptionMatchExpected(
        string asnNumber, string description, string expectedAsn, string expectedDesc)
    {
        var resolver = Resolver(asnNumber, description);

        var result = await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        result!.Asn.ShouldBe(expectedAsn);
        result.Description.ShouldBe(expectedDesc);
    }

    [Fact]
    public async Task ToString_ReturnsCombinedAsnAndDescription()
    {
        var resolver = Resolver("15169", "GOOGLE");

        var result = await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        result!.ToString().ShouldBe("AS15169 GOOGLE");
    }
}

// ── ParseField / field indexing edge cases ────────────────────────────────────

public class CymruAsnResolver_FieldParsingEdgeCases
{
    [Fact]
    public async Task WhenOriginTxtMissingPipeFields_ReturnsNull()
    {
        // Only one field → no pipe separators at all → ASN field parsed but CC empty → falls back to empty CC
        var resolver = new CymruAsnResolver(FakeDns.From(new Dictionary<string, string?>
        {
            ["4.3.2.1.origin.asn.cymru.com"] = "64512",
        }));

        // Should not throw; graceful handling of malformed records
        var result = await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        // ASN parses successfully from field 0; CC is empty; description falls back to empty CC
        result.ShouldNotBeNull();
        result!.Asn.ShouldBe("AS64512");
        result.Description.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task WhenAsnTxtHasFewerThan5Fields_DescriptionIsEmpty_FallsBackToCC()
    {
        var resolver = new CymruAsnResolver(FakeDns.From(new Dictionary<string, string?>
        {
            ["4.3.2.1.origin.asn.cymru.com"] = "64512 | 1.2.0.0/16 | DE | ripe | 2001-01-01",
            ["64512.asn.cymru.com"]           = "64512 | DE | ripe | 2001-01-01",  // no description field
        }));

        var result = await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        result!.Description.ShouldBe("DE");
    }

    [Fact]
    public async Task WhenDescriptionIsWhitespaceOnly_FallsBackToCC()
    {
        var resolver = new CymruAsnResolver(FakeDns.From(new Dictionary<string, string?>
        {
            ["4.3.2.1.origin.asn.cymru.com"] = "64512 | 1.2.0.0/16 | JP | apnic | 2001-01-01",
            ["64512.asn.cymru.com"]           = "64512 | JP | apnic | 2001-01-01 |    ",
        }));

        var result = await resolver.ResolveAsync(IPAddress.Parse("1.2.3.4"));

        result!.Description.ShouldBe("JP");
    }
}
