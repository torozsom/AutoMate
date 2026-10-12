namespace Application.Abstractions.GitHub;

/// <summary>
///     Verifies and stores GitHub App webhooks without retaining raw payloads.
/// </summary>
public interface IGitHubWebhookReceiver
{
    /// <summary>
    ///     Accepts one authenticated delivery, deduplicating redelivery IDs.
    /// </summary>
    /// <param name="body">The original webhook body used for signature validation.</param>
    /// <param name="signature">The GitHub webhook signature accompanying the original body.</param>
    /// <param name="deliveryId">The GitHub delivery identifier used for idempotent webhook processing.</param>
    /// <param name="eventName">The GitHub webhook event name used to select supported processing.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after webhook validation and supported durable processing.</returns>
    Task ReceiveAsync(ReadOnlyMemory<byte> body, string signature, string deliveryId,
        string eventName, CancellationToken cancellationToken);
}