using Application.Abstractions.GitHub;
using Application.Abstractions.Hosting;

namespace Web.Routes.Endpoints;

/// <summary>Public HTTPS ingress for signed GitHub App workflow notifications.</summary>
public sealed class GitHubAppWebhookEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        if (app.ServiceProvider.GetRequiredService<IDeploymentCapabilities>().LocalDeploymentsEnabled)
            return;
        app.MapPost("/api/github/app/webhook", async (HttpContext context,
            IGitHubWebhookReceiver receiver, CancellationToken cancellationToken) =>
        {
            if (context.Request.ContentLength is > 1_048_576) return Results.StatusCode(413);
            using var body = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (body.Length + read > 1_048_576) return Results.StatusCode(413);
                await body.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }
            try
            {
                await receiver.ReceiveAsync(body.ToArray(),
                    context.Request.Headers["X-Hub-Signature-256"].ToString(),
                    context.Request.Headers["X-GitHub-Delivery"].ToString(),
                    context.Request.Headers["X-GitHub-Event"].ToString(), cancellationToken);
                return Results.Accepted();
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
            catch (KeyNotFoundException) { return Results.BadRequest(); }
        }).AllowAnonymous();
    }
}
