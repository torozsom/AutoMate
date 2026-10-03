namespace Application.Abstractions.GitHub;

/// <summary>Verifies and stores GitHub App webhooks without retaining raw payloads.</summary>
public interface IGitHubWebhookReceiver
{
    /// <summary>Accepts one authenticated delivery, deduplicating redelivery IDs.</summary>
    Task ReceiveAsync(ReadOnlyMemory<byte> body, string signature, string deliveryId,
        string eventName, CancellationToken cancellationToken);
}
