using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class CredentialProtectorTests
{
    [Fact]
    public void Protect_ThenUnprotect_RoundTripsPlaintext()
    {
        var protectedValue = CredentialProtector.Protect("s3cret-p@ss");
        var result = CredentialProtector.Unprotect(protectedValue);

        Assert.Equal("s3cret-p@ss", result);
    }

    [Fact]
    public void Protect_DoesNotReturnThePlaintext()
    {
        var protectedValue = CredentialProtector.Protect("s3cret-p@ss");

        Assert.DoesNotContain("s3cret-p@ss", protectedValue);
    }

    [Fact]
    public void Protect_HandlesEmptyString()
    {
        var protectedValue = CredentialProtector.Protect("");
        var result = CredentialProtector.Unprotect(protectedValue);

        Assert.Equal("", result);
    }
}
