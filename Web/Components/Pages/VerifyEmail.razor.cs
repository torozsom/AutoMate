using Microsoft.AspNetCore.Components;
using Services.Auth;

namespace Web.Components.Pages;

/// <summary>
///     This component handles the email verification process for users.
///     It either prompts the user to check their email or validates a provided token.
/// </summary>
public partial class VerifyEmail : ComponentBase
{
    /// A page title that can be dynamically set based on the context.
    private string _pageTitle = "Email Confirmation";


    /// The authentication service used to verify the email token.
    [Inject]
    private IAuthService AuthService { get; set; } = null!;

    /// The navigation manager used to redirect users after successful verification.
    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    /// The logger instance for logging verification-related events.
    [Inject]
    private ILogger<VerifyEmail> Logger { get; set; } = null!;


    /// <summary>
    ///     The verification token provided in the URL query string.
    /// </summary>
    [SupplyParameterFromQuery(Name = "token")]
    public string? Token { get; set; }

    /// <summary>
    ///     A flag indicating whether the component should simply prompt the user to check their email.
    /// </summary>
    [SupplyParameterFromQuery(Name = "checkemail")]
    public bool CheckEmail { get; set; }


    /// An error message to display if verification fails.
    private string? ErrorMessage { get; set; }


    /// <summary>
    ///     Handles the email verification process when the component is initialized.
    ///     It checks for the presence of a token, validates it against the database,
    ///     and updates the user's email verification status accordingly.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        if (CheckEmail)
        {
            _pageTitle = "Check Your Email";
            return;
        }

        await ProcessEmailVerificationAsync();
    }


    /// <summary>
    ///     Executes the actual verification logic using the token provided in the URL.
    /// </summary>
    private async Task ProcessEmailVerificationAsync()
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
            Logger.LogError(ex, "An error occurred during email verification.");
            ErrorMessage = "A technical error occurred while verifying your email. Please try again later.";
        }
    }
}