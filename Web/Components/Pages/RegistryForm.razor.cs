using System.ComponentModel.DataAnnotations;
using Core.Entities;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Services.Data;
using Services.Email;

namespace Web.Components.Pages;


/// <summary>
///     The RegistryForm component is responsible for handling the registration process
///     for new users. It displays a form for user input, validates the input, and handles
///     the registration process.
/// </summary>
public partial class RegistryForm : ComponentBase
{
    [Inject]
    private AutoMateDbContext DbContext { get; set; } = null!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    [Inject]
    private IEmailSender EmailSender { get; set; } = null!;


    /// <summary>
    ///     Model for user registration, containing validation attributes to ensure proper input.
    /// </summary>
    public class RegisterModel
    {
        [Required(ErrorMessage = "Email address required.")]
        [EmailAddress(ErrorMessage = "Invalid email format.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Username required.")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "Username must be between 3-50 characters.")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password required.")]
        [MinLength(6, ErrorMessage = "Password must be at least 6 characters.")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password confirmation required.")]
        [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }


    [SupplyParameterFromForm]
    public RegisterModel? Model { get; set; }

    public string? ErrorMessage { get; set; }


    /// <summary>
    ///     Initializes the component and ensures that the Model is instantiated
    ///     to avoid null reference issues during form binding.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        Model ??= new RegisterModel();
    }


    /// <summary>
    ///     Handles the registration process when the form is submitted.
    ///     It checks for existing email, creates a new user, hashes the password,
    ///     saves the user to the database, and sends a verification email with a unique token.
    ///     Finally, it redirects the user to a page prompting them to check their email for verification.
    /// </summary>
    private async Task HandleRegistration()
    {
        if (Model is null) return;

        ErrorMessage = null;

        var emailExists = await DbContext.Users.AnyAsync(u => u.Email == Model.Email);
        if (emailExists)
        {
            ErrorMessage = "This email address is already in use.";
            return;
        }

        var newUser = new LocalUser
        {
            Email = Model.Email,
            Username = Model.Username,
            IsEmailVerified = false,
            EmailVerificationToken = Guid.NewGuid().ToString(),
            VerificationTokenExpiry = DateTimeOffset.UtcNow.AddHours(24)
        };

        var hasher = new PasswordHasher<LocalUser>();
        newUser.PasswordHash = hasher.HashPassword(newUser, Model.Password);

        DbContext.Users.Add(newUser);
        await DbContext.SaveChangesAsync();

        var verificationLink = NavigationManager.GetUriWithQueryParameters(
            NavigationManager.ToAbsoluteUri("/verify-email").ToString(),
            new Dictionary<string, object?> { { "token", newUser.EmailVerificationToken } });


        await EmailSender.SendEmailAsync(
            newUser.Email,
            "Confirm your registration to AutoMate!",
            "Welcome to AutoMate!\n\nPlease follow this link for verification:\n" + verificationLink);

        NavigationManager.NavigateTo("/verify-email?checkemail=true");
    }
}