using System.Net;

namespace Application.Ai;

/// <summary>Non-retryable provider denial carrying only finite classification and a validated correlation identifier.</summary>
public sealed class AnalysisProviderAccessDeniedException : HttpRequestException
{
    /// <summary>Persisted finite reason used by both result rendering and worker audit.</summary>
    public const string FailureCode = "provider_access_denied";

    /// <summary>Fixed owner guidance, independent of provider response text.</summary>
    public const string Guidance =
        "The AI provider denied access. Ask the operator to check the provider credentials, model or deployment permissions, and network access before requesting another analysis.";

    /// <summary>Creates safe metadata without retaining an error response body or its message.</summary>
    public AnalysisProviderAccessDeniedException(string provider, string? code = null, string? requestId = null)
        : base("AI provider denied access. Check provider permissions and network access.", null,
            HttpStatusCode.Forbidden)
    {
        Provider = provider is "openai" or "azure-openai" ? provider : "unknown";
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

    /// <summary>Validated request correlation identifier, without response headers or payloads.</summary>
    public string? RequestId { get; }
}