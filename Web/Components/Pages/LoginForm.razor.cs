using Microsoft.AspNetCore.Components;

namespace Web.Components.Pages;

/// <summary>
///     Represents a login form component used for user authentication.
/// </summary>
/// <remarks>
///     The component can display messages related to user registration, email verification,
///     and error handling by extracting parameters from the query string.
/// </remarks>
public partial class LoginForm : ComponentBase
{
    [SupplyParameterFromQuery(Name = "error")]
    public string? ErrorMessage { get; set; }

    [SupplyParameterFromQuery(Name = "registered")]
    public string? RegisteredQueryParam { get; set; }

    [SupplyParameterFromQuery(Name = "verified")]
    public string? VerifiedQueryParam { get; set; }

    public string? SuccessMessage { get; private set; }


    /// <summary>
    ///     Checks the query parameters for registration and verification messages
    ///     and sets the appropriate success messages to be displayed on the login page.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        DetermineSuccessMessage();
    }


    /// <summary>
    ///     Evaluates the query parameters and sets the appropriate success message
    ///     for the user interface.
    /// </summary>
    private void DetermineSuccessMessage()
    {
        if (string.Equals(RegisteredQueryParam, "true", StringComparison.OrdinalIgnoreCase))
            SuccessMessage = "Registration successful! Please log in with your new account.";
        else if (string.Equals(VerifiedQueryParam, "true", StringComparison.OrdinalIgnoreCase))
            SuccessMessage = "Email successfully verified! You can now log in to your account.";
    }
}