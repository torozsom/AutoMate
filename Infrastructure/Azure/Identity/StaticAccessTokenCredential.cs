using Azure.Core;

namespace Infrastructure.Azure;

/// <summary>
///     Adapts an already-issued OAuth access token for Azure SDK calls during the current
///     request.
/// </summary>
/// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
internal sealed class StaticAccessTokenCredential(string accessToken) : TokenCredential
{
    /// <summary>
    ///     Returns the supplied memory-only access token to the Azure SDK.
    /// </summary>
    /// <param name="requestContext">
    ///     Token scope requested by the Azure SDK; the static credential returns its supplied
    ///     token.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>The supplied or acquired scoped access token and its expiry.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CreateAccessToken();
    }

    /// <summary>
    ///     Returns the supplied memory-only access token without a credential refresh request.
    /// </summary>
    /// <param name="requestContext">
    ///     Token scope requested by the Azure SDK; the static credential returns its supplied
    ///     token.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the supplied or acquired scoped access token and its expiry.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateAccessToken());
    }

    /// <summary>
    ///     Creates a short-lived SDK credential wrapper around the already issued access token.
    /// </summary>
    /// <returns>The supplied or acquired scoped access token and its expiry.</returns>
    private AccessToken CreateAccessToken()
    {
        return new AccessToken(accessToken, DateTimeOffset.UtcNow.AddMinutes(30));
    }
}
