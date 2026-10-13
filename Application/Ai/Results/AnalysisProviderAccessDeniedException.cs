using System.Net;

namespace Application.Ai;

/// <summary>
///     Non-retryable provider denial carrying only finite classification and a validated
///     correlation identifier.
/// </summary>
public sealed class AnalysisProviderAccessDeniedException : HttpRequestException
{
    /// <summary>Persisted finite reason used by both result rendering and worker audit.</summary>
    public const string FailureCode = "provider_access_denied";

    /// <summary>Fixed owner guidance, independent of provider response text.</summary>
    public const string Guidance =
        "The AI provider denied access. Ask the operator to verify the endpoint and credential belong to the same resource, the model deployment is available, and authentication is enabled. For Azure, verify network access: the AutoMate host's outbound address must be allowed by the resource firewall or reach the approved endpoint through correct DNS and routing. Request another analysis after access is corrected.";

    /// <summary>
    ///     Creates safe metadata without retaining an error response body or its message.
    /// </summary>
    /// <param name="provider">The provider identity; not a credential or endpoint.</param>
    /// <param name="code">The recognized provider denial code; unrecognized values become unknown.</param>
    /// <param name="requestId">
    ///     The provider request correlation identifier; malformed values are discarded.
    /// </param>
    /// <param name="reason">Finite operator classification; arbitrary provider text is discarded.</param>
    public AnalysisProviderAccessDeniedException(string provider, string? code = null, string? requestId = null,
        string? reason = null)
        : base("AI provider denied access. Check provider permissions and network access.", null,
            HttpStatusCode.Forbidden)
    {
        Provider = provider is "openai" or "azure-openai" ? provider : "unknown";
        Reason = reason is "network_access_denied" or "resource_blocked" ? reason : "unknown";
        Code = code is "403" or "PermissionDenied" or "Forbidden" or "AccessDenied" or "AuthorizationFailed" or
            "permission_denied" or "insufficient_permissions" or "unsupported_country_region_territory" or
            "access_program_not_enabled" or "misalignment_policy_violation"
            ? code
            : "unknown";
        RequestId = Guid.TryParse(requestId, out var id) ? id.ToString("D") :
            requestId is { Length: >= 20 and <= 68 } && requestId.StartsWith("req_", StringComparison.Ordinal) &&
            requestId.AsSpan(4).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 ? requestId : null;
    }

    /// <summary>Registered provider name, never an arbitrary endpoint.</summary>
    public string Provider { get; }

    /// <summary>Recognized provider code; arbitrary error text is omitted.</summary>
    public string Code { get; }

    /// <summary>Fixed diagnostic category derived from recognized denial messages, never their text.</summary>
    public string Reason { get; }

    /// <summary>Validated request correlation identifier, without response headers or payloads.</summary>
    public string? RequestId { get; }
}
