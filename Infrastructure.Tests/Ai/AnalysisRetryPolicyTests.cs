using Application.Ai;

namespace Infrastructure.Tests.Ai;

/// <summary>Verifies retry timing bounds independently of network/database scheduling.</summary>
public sealed class AnalysisRetryPolicyTests
{
    /// <summary>Waits increase exponentially with bounded jitter; server minimums never shorten a delay.</summary>
    [Fact]
    public void Backoff_grows_and_respects_server_minimum()
    {
        var options = new AiAnalysisOptions { MaximumProviderRetries = 5 };
        Assert.InRange(AnalysisRetryPolicy.Delay(options, 0, null)!.Value.TotalSeconds, 10, 12.5);
        Assert.InRange(AnalysisRetryPolicy.Delay(options, 1, null)!.Value.TotalSeconds, 20, 22.5);
        Assert.InRange(AnalysisRetryPolicy.Delay(options, 2, 60)!.Value.TotalSeconds, 60, 62.5);
        Assert.Null(AnalysisRetryPolicy.Delay(options, 5, null));
        Assert.Null(AnalysisRetryPolicy.Delay(options, 0, 301));
        Assert.Null(AnalysisRetryPolicy.Delay(new AiAnalysisOptions { MaximumProviderRetries = 0 }, 0, null));
    }
}