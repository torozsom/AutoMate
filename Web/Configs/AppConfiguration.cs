using Services.Data;
using Web.Hubs;
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
    /// <param name="app">The WebApplication instance to configure.</param>
    /// <returns>The original WebApplication for chaining.</returns>
    public static async Task<WebApplication> UseApplicationPipelineAsync(this WebApplication app)
    {
        // Use forwarded headers middleware to correctly handle proxy headers
        // (e.g., X-Forwarded-For) for client IP and protocol information.
        app.UseForwardedHeaders();

        // Perform database connectivity check asynchronously
        await CheckDatabaseConnectivityAsync(app);

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", true);
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }
        else
        {
            // Use Swagger/OpenAPI middleware for development environment to provide API documentation and testing capabilities.
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Use status code pages that re-execute the request pipeline for a specific path ("/not-found") when a 404 status code is encountered.
        app.UseStatusCodePagesWithReExecute("/not-found");

        // Redirect HTTP requests to HTTPS.
        app.UseHttpsRedirection();

        // Use rate limiting middleware to limit the number of requests per IP address.
        app.UseRateLimiter();

        // Use authentication middleware allowing users to log in and access protected resources.
        app.UseAuthentication();

        // Use authorization middleware to ensure that users are authorized to access certain resources.
        app.UseAuthorization();

        // Use antiforgery middleware to protect against cross-site request forgery (CSRF) attacks.
        app.UseAntiforgery();

        // Map Blazor Hub for real-time communication.
        app.MapHub<LogHub>("/loghub");

        // Configure endpoints dynamically
        var endpoints = app.Services.GetServices<IEndpoint>();
        foreach (var endpoint in endpoints)
            endpoint.Map(app);

        return app;
    }


    /// <summary>
    ///     Asynchronously checks the database connectivity at application startup by attempting to connect to the database
    ///     using the configured DbContext.
    /// </summary>
    /// <param name="app">The WebApplication instance used to create a scope for retrieving the DbContext.</param>
    private static async Task CheckDatabaseConnectivityAsync(WebApplication app)
    {
        // Testing database connectivity at startup.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

        try
        {
            // Use CanConnectAsync to check if the application can connect to the database without throwing an exception.
            var canConnect = await db.Database.CanConnectAsync();

            if (canConnect)
            {
                logger.LogInformation("[Startup] Successfully connected to the database.");
            }
            else
            {
                logger.LogWarning("[Startup] Failed to connect to the database. Ensure PostgreSQL is running.");
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "[Startup] Critical database connectivity error.");
        }
    }
}