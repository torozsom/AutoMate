using Application.Auth;
using Microsoft.AspNetCore.Components;
using Web.Services;

namespace Web.Components.Pages;

/// <summary>
///     This component handles the email verification process for users. It either prompts the user to
///     check their email or validates a provided token.
/// </summary>
public partial class VerifyEmail : ComponentBase
{
    /// <summary>Current verification-page heading.</summary>
    private string _pageTitle = "Email Confirmation";


    /// <summary>Isolates each authentication operation from the interactive circuit context.</summary>
    [Inject]
    private ScopedApplicationOperations Operations { get; set; } = null!;

    /// <summary>Application navigation and absolute-link generation.</summary>
    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    /// <summary>Reviewed operational logger for the component.</summary>
    [Inject]
    private ILogger<VerifyEmail> Logger { get; set; } = null!;


    /// <summary>The verification token provided in the URL query string.</summary>
    [SupplyParameterFromQuery(Name = "token")]
    public string? Token { get; set; }

    /// <summary>A flag indicating whether the component should simply prompt the user to check their email.</summary>
    [SupplyParameterFromQuery(Name = "checkemail")]
    public bool CheckEmail { get; set; }


    /// <summary>Current safe form or verification error.</summary>
    private string? ErrorMessage { get; set; }


    /// <summary>
    ///     Handles the email verification process when the component is initialized. It checks for the
    ///     presence of a token, validates it against the database, and updates the user's email
    ///     verification status accordingly.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
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
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task ProcessEmailVerificationAsync()
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            ErrorMessage = "Invalid or missing token. Please check the link and try again.";
            return;
        }

        try
        {
            var success = await Operations.RunAsync<IAuthService, bool>(auth => auth.VerifyEmailAsync(Token));

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
