using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Abstractions.GitHub;
using Microsoft.Extensions.Options;

namespace Infrastructure.GitHub;

/// <summary>Verifies repository access and issues short-lived GitHub App installation tokens.</summary>
public sealed class GitHubAppCredentials(HttpClient httpClient, IOptions<GitHubAppOptions> options)
    : IGitHubAppCredentials
{
    /// <summary>Validated App settings.</summary>
    private readonly GitHubAppOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<(long InstallationId, long RepositoryId)> ResolveRepositoryAsync(
        string userAccessToken, string owner, string repository, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userAccessToken))
            throw new UnauthorizedAccessException("Connect GitHub before deploying.");
        var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";
        using var userRequest = CreateRequest(HttpMethod.Get, path, userAccessToken);
        using var userResponse = await httpClient.SendAsync(userRequest, cancellationToken);
        ThrowIfRateLimited(userResponse);
        if (!userResponse.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("The GitHub repository is unavailable to this user.");
        using var userPayload = await JsonDocument.ParseAsync(
            await userResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = userPayload.RootElement;
        if (!root.TryGetProperty("permissions", out var permissions) ||
            !permissions.TryGetProperty("push", out var push) || !push.GetBoolean())
            throw new UnauthorizedAccessException("GitHub repository write access is required.");
        var repositoryId = root.GetProperty("id").GetInt64();

        using var appRequest = CreateRequest(HttpMethod.Get, path + "/installation", CreateAppJwt());
        using var appResponse = await httpClient.SendAsync(appRequest, cancellationToken);
        ThrowIfRateLimited(appResponse);
        if (!appResponse.IsSuccessStatusCode)
            throw new InvalidOperationException("Install the AutoMate GitHub App on this repository before deploying.");
        using var appPayload = await JsonDocument.ParseAsync(
            await appResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return (appPayload.RootElement.GetProperty("id").GetInt64(), repositoryId);
    }

    /// <inheritdoc />
    public async Task<string> CreateInstallationTokenAsync(long installationId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post,
            $"app/installations/{installationId}/access_tokens", CreateAppJwt());
        using var response = await httpClient.SendAsync(request, cancellationToken);
        ThrowIfRateLimited(response);
        if ((int)response.StatusCode is 401 or 403 or 404)
            throw new InvalidOperationException(
                "The AutoMate GitHub App installation is unavailable. Reinstall or grant repository access.");
        response.EnsureSuccessStatusCode();
        using var payload = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return payload.RootElement.GetProperty("token").GetString()
               ?? throw new InvalidOperationException("GitHub did not issue an installation token.");
    }

    /// <summary>Builds an authenticated API request without placing credentials in a URI.</summary>
    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, "https://api.github.com/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("AutoMate/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    /// <summary>Honors Retry-After and primary reset headers before another installation request.</summary>
    internal static void ThrowIfRateLimited(HttpResponseMessage response)
    {
        if ((int)response.StatusCode != 429 && (int)response.StatusCode != 403) return;
        var remainingZero = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
                            remaining.FirstOrDefault() == "0";
        if ((int)response.StatusCode == 403 && response.Headers.RetryAfter is null && !remainingZero)
            return;
        var now = DateTimeOffset.UtcNow;
        var retryAt = response.Headers.RetryAfter?.Delta is { } delta ? now.Add(delta) :
            response.Headers.RetryAfter?.Date ?? now.AddMinutes(1);
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
            long.TryParse(resets.FirstOrDefault(), out var epoch))
            retryAt = DateTimeOffset.FromUnixTimeSeconds(epoch) > retryAt
                ? DateTimeOffset.FromUnixTimeSeconds(epoch) : retryAt;
        if (retryAt < now) retryAt = now.AddMinutes(1);
        throw new GitHubRateLimitException(retryAt);
    }

    /// <summary>Signs GitHub's short-lived App JWT with the configured private key.</summary>
    private string CreateAppJwt()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now - 60,
            exp = now + 540,
            iss = _options.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }));
        var input = header + "." + payload;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_options.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return input + "." + Encode(signature);
    }

    /// <summary>Encodes one JWT segment without padding.</summary>
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
