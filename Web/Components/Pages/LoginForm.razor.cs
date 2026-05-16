using Microsoft.AspNetCore.Components;

namespace Web.Components.Pages;

/// <summary>
///     Represents the login form component used for user authentication.
///     Handles standard credentials and displays status messages for registration and verification.
/// </summary>
public partial class LoginForm : ComponentBase
{
    /// <summary>
    ///     An optional error message provided via the query string.
    /// </summary>
    [SupplyParameterFromQuery(Name = "error")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Indicates whether the user has just successfully registered.
    ///     Automatically parsed from the "registered" query string parameter by Blazor.
    /// </summary>
    [SupplyParameterFromQuery(Name = "registered")]
    public bool IsRegistered { get; set; }

    /// <summary>
    ///     Indicates whether the user has just successfully verified their email.
    ///     Automatically parsed from the "verified" query string parameter by Blazor.
    /// </summary>
    [SupplyParameterFromQuery(Name = "verified")]
    public bool IsVerified { get; set; }


    /// <summary>
    ///     The derived success message to display to the user, if applicable.
    /// </summary>
    private string? SuccessMessage { get; set; }


    /// <summary>
    ///     Initializes the component and evaluates query parameters to set the appropriate success messages.
    /// </summary>
    protected override void OnInitialized()
    {
        if (IsRegistered)
            SuccessMessage = "Registration successful! Please log in with your new account.";
        else if (IsVerified) SuccessMessage = "Email successfully verified! You can now log in to your account.";
    }
}