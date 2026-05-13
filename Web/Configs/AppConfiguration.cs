using Services.Data;
using Web.Hubs;
using Web.Routes;

namespace Web.Configs;

/// <summary>
///     Provides a centralized location for configuring the HTTP request pipeline and initializing infrastructure.
/// </summary>
public static class AppConfiguration
{
    /// <summary>
    ///     Provides a method to configure the HTTP request pipeline and initialize application infrastructure
    ///     for the ASP.NET Core web application.
    /// </summary>
    /// <param name="app">The WebApplication instance to configure.</param>
    extension(WebApplication app)
    {
        /// <summary>
        ///     Configures the HTTP request pipeline for the ASP.NET Core web application.
        ///     Defines the strict order of middleware components for routing, security, and endpoints.
        /// </summary>
        /// <returns>The original WebApplication for chaining.</returns>
        public WebApplication UseApplicationPipeline()
        {
            // Diagnostics & Forwarded Headers
            app.UseForwardedHeaders();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // HSTS adds a Strict-Transport-Security header to force clients to use HTTPS.
                app.UseHsts();
            }

            // Use status code pages that re-execute the request pipeline when a 404 status code is encountered.
            app.UseStatusCodePagesWithReExecute("/not-found");

            // If AutoMate is hosted behind a reverse proxy (like Nginx/Traefik)
            // that handles SSL termination, remove UseHttpsRedirection() to avoid infinite loops.
            app.UseHttpsRedirection();

            // Routing (Explicitly added to ensure correct middleware execution order)
            app.UseRouting();

            // Security & Rate Limiting
            app.UseRateLimiter();

            // Use authentication middleware to authenticate users.
            app.UseAuthentication();

            // Use authorization middleware to ensure that users are authorized to access certain resources.
            app.UseAuthorization();

            // Use antiforgery middleware to protect against cross-site request forgery (CSRF) attacks.
            app.UseAntiforgery();

            // Endpoints & SignalR Hubs
            app.MapHub<LogHub>("/loghub");

            // Map Health Checks (Industry standard for readiness/liveness probes)
            app.MapHealthChecks("/health");

            // Dynamically map all custom endpoints
            var endpoints = app.Services.GetServices<IEndpoint>();
            foreach (var endpoint in endpoints)
                endpoint.Map(app);

            return app;
        }


        /// <summary>
        ///     Asynchronously initializes application infrastructure.
        /// </summary>
        public async Task InitializeInfrastructureAsync()
        {
            // Testing database connectivity at startup.
            using var scope = app.Services.CreateScope();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AppStartup");
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();

            logger.LogInformation("[Startup] Initializing infrastructure and checking dependencies...");

            try
            {
                // Modern EF Core connectivity check
                var canConnect = await db.Database.CanConnectAsync();
                if (canConnect)
                {
                    logger.LogInformation("[Startup] Successfully connected to the database.");
                    //await db.Database.MigrateAsync();
                }
                else
                {
                    logger.LogWarning("[Startup] Failed to connect to the database. " +
                                      "Ensure the database is running and credentials are valid.");
                }
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "[Startup] Critical database connectivity error during initialization.");
            }
        }
    }
}