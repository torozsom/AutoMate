using Microsoft.AspNetCore.Components;

namespace Web.Components.Pages;

/// <summary>
/// Represents a login form component used for user authentication.
/// </summary>
/// <remarks>
/// The component can display messages related to user registration, email verification,
/// and error handling by extracting parameters from the query string.
/// </remarks>
public partial class LoginForm : ComponentBase
{
    [SupplyParameterFromQuery(Name = "error")]
    public string? ErrorMessage { get; set; }

    [SupplyParameterFromQuery(Name = "registered")]
    public string? RegisteredMessage { get; set; }

    [SupplyParameterFromQuery(Name = "verified")]
    public string? VerifiedMessage { get; set; }


    /// <summary>
    ///     Checks the query parameters for registration and verification messages
    ///     and sets the appropriate success messages to be displayed on the login page.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();

        if (RegisteredMessage == "true")
            RegisteredMessage = "Registration successful! Please log in with your new account.";

        if (VerifiedMessage == "true")
            RegisteredMessage = "Email successfully verified! You can now log in to your account.";
    }
}
