using Azure.Core;

namespace Services.Azure;

/// <summary>
///     Adapts an already-issued OAuth access token for Azure SDK calls during the current request.
/// </summary>
internal sealed class StaticAccessTokenCredential(string accessToken) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CreateAccessToken();
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateAccessToken());
    }

    private AccessToken CreateAccessToken()
    {
        return new AccessToken(accessToken, DateTimeOffset.UtcNow.AddMinutes(30));
    }
}