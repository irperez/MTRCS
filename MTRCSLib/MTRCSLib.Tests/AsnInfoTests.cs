using Shouldly;

namespace MTRCSLib.Tests;

public class AsnInfoTests
{
    [Fact]
    public void ToString_ReturnsAsnAndDescription()
    {
        var info = new AsnInfo("AS15169", "GOOGLE");
        info.ToString().ShouldBe("AS15169 GOOGLE");
    }

    [Fact]
    public void ToString_WhenDescriptionEmpty_ReturnsAsnWithTrailingSpace()
    {
        var info = new AsnInfo("AS701", string.Empty);
        info.ToString().ShouldBe("AS701 ");
    }

    // When the name lookup fails the CC country code from the origin record is used instead.
    [Fact]
    public void ToString_WhenDescriptionIsCountryCode_ShowsCountryCode()
    {
        var info = new AsnInfo("AS26101", "US");
        info.ToString().ShouldBe("AS26101 US");
    }

    [Theory]
    [InlineData("AS15169", "GOOGLE")]
    [InlineData("AS701",   "UUNET")]
    [InlineData("AS26101", "US")]
    public void ToString_MatchesExpectedFormat(string asn, string description)
    {
        var info = new AsnInfo(asn, description);
        info.ToString().ShouldBe($"{asn} {description}");
    }
}
