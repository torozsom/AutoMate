using Core.Entities;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Services.Data;

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
    [Inject]
    private AutoMateDbContext DbContext { get; set; } = null!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

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
            return;

        if (string.IsNullOrEmpty(Token))
        {
            ErrorMessage = "Invalid or missing token. Please check the link and try again.";
            return;
        }

        var user = await DbContext.Users.OfType<LocalUser>()
            .FirstOrDefaultAsync(u => u.EmailVerificationToken == Token);

        if (user == null)
        {
            ErrorMessage = "Invalid token. No user found for the provided token.";
            return;
        }

        if (user.VerificationTokenExpiry < DateTimeOffset.UtcNow)
        {
            ErrorMessage = "Confirmation token has expired. Please request a new verification email.";
            return;
        }

        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.VerificationTokenExpiry = null;

        await DbContext.SaveChangesAsync();
        NavigationManager.NavigateTo("/login?verified=true");
    }
}
