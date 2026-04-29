using Microsoft.AspNetCore.Components;
using Services.Auth;

namespace Web.Components.Pages;

/// <summary>
///     This component handles the email verification process for users.
///     When a user clicks on the verification link sent to their email,
///     they are directed to this component with a token in the query string.
///     The component validates the token against the database, checks for
///     expiration, and updates the user's email verification status accordingly.
///     If the token is invalid or expired, it displays an appropriate error message to the user.
/// </summary>
public partial class VerifyEmail : ComponentBase
{
    private string _pageTitle = "Email Confirmation";

    [Inject] private IAuthService AuthService { get; set; } = null!;

    [Inject] private NavigationManager NavigationManager { get; set; } = null!;


    [SupplyParameterFromQuery(Name = "token")]
    public string? Token { get; set; }

    [SupplyParameterFromQuery(Name = "checkemail")]

    public bool CheckEmail { get; set; }

    private string? ErrorMessage { get; set; }


    /// <summary>
    ///     Handles the email verification process when the component is initialized.
    ///     It checks for the presence of a token, validates it against the database,
    ///     and updates the user's email verification status accordingly. If the token is
    ///     invalid or expired, it sets an appropriate error message to inform the user.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        if (CheckEmail)
        {
            _pageTitle = "Check Your Email";
            return;
        }

        await ProcessEmailVerification();
    }

    /// <summary>
    ///     Executes the actual verification logic using the token provided in the URL.
    /// </summary>
    private async Task ProcessEmailVerification()
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            ErrorMessage = "Invalid or missing token. Please check the link and try again.";
            return;
        }

        try
        {
            var success = await AuthService.VerifyEmailAsync(Token);

            if (success)
                NavigationManager.NavigateTo("/login?verified=true");
            else
                ErrorMessage = "Invalid token or token has expired. Please check the link and try again.";
        }
        catch (Exception ex)
        {
            ErrorMessage = "A technical error occurred. Please try again later.\n" +
                           "Error details: " + ex.Message;
        }
    }
}