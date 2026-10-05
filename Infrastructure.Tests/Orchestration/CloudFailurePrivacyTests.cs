using System.Reflection;
using Infrastructure.ApplicationServices.Orchestration;

namespace Infrastructure.Tests.Orchestration;

/// <summary>Checks the customer-visible failure policy against untrusted exception suffixes.</summary>
public sealed class CloudFailurePrivacyTests
{
    /// <summary>Recognized provider failures keep actionable guidance without copying their payload.</summary>
    [Theory]
    [InlineData("Reconnect Azure", "Reconnect Azure")]
    [InlineData("Azure authorization expired", "Reconnect Azure")]
    [InlineData("The Azure Container Registry name", "Registry name")]
    [InlineData("The AutoMate GitHub App installation", "GitHub App installation")]
    [InlineData("Unable to verify the prior AutoMate commit", "prior AutoMate commit")]
    [InlineData("Unexpected provider failure", "Cloud deployment preparation failed")]
    public void Failure_guidance_never_copies_provider_suffix(string prefix, string guidance)
    {
        var policy = typeof(CloudRunProcessor).GetMethod("SafeFailureReason",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var reason = (string)policy.Invoke(null,
            [new InvalidOperationException(prefix + ": private-payload-token")])!;
        Assert.Contains(guidance, reason);
        Assert.DoesNotContain("private", reason);
    }
}