using Services.Data;
using Web.Routes;

namespace Web.Configs;


/// <summary>
///     Provides a centralized location for configuring the HTTP request pipeline of the ASP.NET Core web application.
/// </summary>
public static class AppConfiguration
{
    /// <summary>
    ///     Configures the HTTP request pipeline for the ASP.NET Core web application.
    ///     This method sets up middleware components for error handling, security, authentication, and routing.
    /// </summary>
    /// <param name="app"></param>
    public static void Configure(WebApplication app)
    {
        // Perform database connectivity check
        CheckDatabaseConnectivity(app);

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
        var endpoints = app.Services.GetServices<IEndpoint>();
        foreach(var endpoint in endpoints)
            endpoint.Map(app);

    }


    /// <summary>
    ///     Checks the database connectivity at application startup by attempting to connect to the database using the
    ///     configured DbContext.
    /// </summary>
    /// <param name="app"></param>
    private static void CheckDatabaseConnectivity(WebApplication app)
    {
        // Testing database connectivity at startup.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        try
        {
            var canConnect = db.Database.CanConnectAsync();
            Console.WriteLine(canConnect.Result
                ? "Successfully connected to the database"
                : "Failed to connect to the database");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Database connectivity error: " + ex.Message);
        }
    }
}