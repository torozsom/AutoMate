using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Services.Data;


// This is the main entry point for the ASP.NET Core web application.
var builder = WebApplication.CreateBuilder(args);

// Add the database context to the services container, using PostgreSQL as the database provider.
builder.Services.AddDbContext<AutoMateDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Add authentication services to the container, configuring cookie authentication
// as the default scheme and GitHub as an external authentication provider.
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = "GitHub";
    })
    .AddCookie()
    .AddGitHub(options =>
    {
        options.ClientId = builder.Configuration["GitHub:ClientId"] ??
                           throw new InvalidOperationException("GitHub:ClientId is required");
        options.ClientSecret = builder.Configuration["GitHub:ClientSecret"] ??
                               throw new InvalidOperationException("GitHub:ClientSecret is required");
        options.CallbackPath = new PathString("/signin-github");
        options.Scope.Add("user:email");
    });

// Add a cascading authentication state provider to the services container, which allows
// components to access the current authentication state and react to changes in authentication status.
builder.Services.AddCascadingAuthenticationState();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add services for Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Build the application.
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
else
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Use status code pages that re-execute the request pipeline for a specific path ("/not-found") when a 404 status code is encountered.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Redirect HTTP requests to HTTPS.
app.UseHttpsRedirection();

// Use authentication middleware allowing users to log in and access protected resources.
app.UseAuthentication();

// Use authorization middleware to ensure that users are authorized to access certain resources.
app.UseAuthorization();

// Use antiforgery middleware to protect against cross-site request forgery (CSRF) attacks.
app.UseAntiforgery();

// Configure endpoints
Web.Routes.Configure(app);

// Run the application, starting the web server and listening for incoming HTTP requests.
app.Run();