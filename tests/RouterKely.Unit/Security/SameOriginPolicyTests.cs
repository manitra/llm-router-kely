using RouterKely.Core.Security;
using Xunit;

namespace RouterKely.Unit.Security;

public sealed class SameOriginPolicyTests
{
    private const string Scheme = "http";
    private const string Host = "127.0.0.1:8080";

    [Fact]
    public void AcceptsMatchingOrigin() =>
        Assert.True(SameOriginPolicy.IsSameOrigin(Scheme, Host, "http://127.0.0.1:8080", null));

    [Theory]
    [InlineData("https://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:9090")]
    [InlineData("http://evil.example")]
    [InlineData("http://127.0.0.1:8080.evil.example")]
    public void RejectsMismatchedOrigin(string origin) =>
        Assert.False(SameOriginPolicy.IsSameOrigin(Scheme, Host, origin, null));

    [Fact]
    public void RejectsOriginMismatchEvenWithValidReferer() =>
        Assert.False(SameOriginPolicy.IsSameOrigin(Scheme, Host, "http://evil.example", "http://127.0.0.1:8080/ui/login"));

    [Fact]
    public void AcceptsOpaqueOriginWithSameOriginReferer() =>
        Assert.True(SameOriginPolicy.IsSameOrigin(Scheme, Host, "null", "http://127.0.0.1:8080/ui/login"));

    [Fact]
    public void AcceptsMissingOriginWithSameOriginReferer() =>
        Assert.True(SameOriginPolicy.IsSameOrigin(Scheme, Host, null, "http://127.0.0.1:8080/ui/login"));

    [Fact]
    public void RejectsOpaqueOriginWithoutReferer() =>
        Assert.False(SameOriginPolicy.IsSameOrigin(Scheme, Host, "null", null));

    [Fact]
    public void RejectsMissingOriginAndReferer() =>
        Assert.False(SameOriginPolicy.IsSameOrigin(Scheme, Host, null, null));

    [Theory]
    [InlineData("http://127.0.0.1:8080.evil.example/ui/login")]
    [InlineData("http://127.0.0.1:80800/ui/login")]
    [InlineData("https://127.0.0.1:8080/ui/login")]
    public void RejectsCrossOriginRefererForOpaqueOrigin(string referer) =>
        Assert.False(SameOriginPolicy.IsSameOrigin(Scheme, Host, "null", referer));

    [Fact]
    public void AcceptsOriginWhenHostContainsNoPort() =>
        Assert.True(SameOriginPolicy.IsSameOrigin("https", "ui.example.com", "https://ui.example.com", null));
}
