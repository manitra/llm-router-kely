using RouterKely.Core.Authentication;
using Xunit;

namespace RouterKely.Unit.Authentication;

public sealed class ApiKeyAuthenticatorTests
{
    private readonly ApiKeyAuthenticator _authenticator = new("sk-rk-secret");

    [Theory]
    [InlineData("Bearer sk-rk-secret")]
    [InlineData("bearer sk-rk-secret")]
    public void AuthenticateAcceptsConfiguredBearerKey(string authorization) =>
        Assert.True(_authenticator.Authenticate(authorization));

    [Theory]
    [InlineData("")]
    [InlineData("sk-rk-secret")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer  sk-rk-secret")]
    public void AuthenticateRejectsInvalidAuthorization(string authorization) =>
        Assert.False(_authenticator.Authenticate(authorization));
}

