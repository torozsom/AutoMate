namespace Application.Ai;

/// <summary>Finite owner-visible skip reasons; provider errors and configuration text never become result guidance.</summary>
public enum AnalysisSkipReason
{
    /// <summary>Missing/disabled/unapproved provider configuration or revoked egress policy.</summary>
    Unavailable,

    /// <summary>Project allowance exhausted before work admission.</summary>
    ProjectQuotaExceeded,

    /// <summary>No supported bounded diagnostic context is available.</summary>
    UnsupportedData
}

/// <summary>Fixed code/message mapping shared by persistence and owner readback, including legacy unavailable results.</summary>
public static class AnalysisSkipPolicy
{
    /// <summary>Returns only bounded authored codes.</summary>
    public static string Code(AnalysisSkipReason reason)
    {
        return reason switch
        {
            AnalysisSkipReason.ProjectQuotaExceeded => "quota_exceeded",
            AnalysisSkipReason.UnsupportedData => "unsupported_data",
            _ => "unavailable"
        };
    }

    /// <summary>Ignores any persisted/provider summary and uses authored explanations only.</summary>
    public static string Message(string? code)
    {
        return code switch
        {
            "quota_exceeded" =>
                "AI analysis was skipped because this project's daily analysis allowance has been reached.",
            "unsupported_data" => "AI analysis was skipped because no supported diagnostic data was available.",
            _ => "AI analysis was skipped because provider configuration or diagnostic egress approval is unavailable."
        };
    }
}