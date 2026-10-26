using Application.Data.Users;
using Application.Orchestration.Cloud;
using Domain.DTO.Azure;
using Domain.DTO.Deployment;
using Microsoft.AspNetCore.Components.Authorization;
using Web.Components.Pages.Identity;

namespace Web.Services;

/// <summary>
///     Resolves cloud connections in operation scopes without exposing credentials to deployment components.
/// </summary>
/// <param name="operations">Owns the lifetime of each independent application operation.</param>
public sealed class ConnectedCloudDeploymentAdmission(ScopedApplicationOperations operations)
{
    /// <summary>
    ///     Reads Azure before GitHub, preserving each page's existing early-exit and account-switch checks.
    /// </summary>
    /// <param name="owner">The owner captured by the page before awaiting connection reads.</param>
    /// <param name="authentication">The originating circuit's authentication state.</param>
    /// <param name="enforceOwnerFence">Preserves Dashboard's early Azure rejection and resolved-owner comparison.</param>
    /// <param name="isCurrent">Checks Dashboard disposal and owner changes between asynchronous reads.</param>
    /// <returns>Connection availability plus an encapsulated admission operation; lookup failures propagate unchanged.</returns>
    public async Task<CloudDeploymentPreparation> PrepareAsync(Guid owner, AuthenticationStateProvider authentication,
        bool enforceOwnerFence, Func<bool> isCurrent)
    {
        var azure = await operations.RunAsync<IUserService, AzureCloudCredentialsDto?>(users =>
            users.GetAzureCloudCredentialsAsync(owner));

        if (!isCurrent())
            return new CloudDeploymentPreparation(CloudConnectionState.Obsolete);

        if (enforceOwnerFence && azure is null)
            return new CloudDeploymentPreparation(CloudConnectionState.MissingAzure);

        var details = await operations.RunAsync<IUserService, AuthenticatedUserDetails>(users =>
            AuthenticatedUserResolver.GetCurrentUserDetailsAsync(authentication, users));

        if (!isCurrent() || (enforceOwnerFence && details.UserId != owner))
            return new CloudDeploymentPreparation(CloudConnectionState.Obsolete);

        if (azure is null)
            return new CloudDeploymentPreparation(CloudConnectionState.MissingAzure);

        return string.IsNullOrWhiteSpace(details.AccessToken)
            ? new CloudDeploymentPreparation(CloudConnectionState.MissingGitHub)
            : new CloudDeploymentPreparation(operations, owner, details.AccessToken, azure);
    }
}

/// <summary>
///     Describes connection readiness without exposing provider credentials.
/// </summary>
public enum CloudConnectionState
{
    /// <summary>Both connections were resolved.</summary>
    Ready,

    /// <summary>The Azure connection is absent.</summary>
    MissingAzure,

    /// <summary>The GitHub credential is absent.</summary>
    MissingGitHub,

    /// <summary>The originating Dashboard operation is no longer current.</summary>
    Obsolete
}

/// <summary>
///     Encapsulates connection credentials for the short interval between preparation and admission.
/// </summary>
public sealed class CloudDeploymentPreparation
{
    /// <summary>The connected Azure credential, retained only for this admission.</summary>
    private readonly AzureCloudCredentialsDto? _azure;

    /// <summary>Memory-only connection credential; never return it to presentation or log it.</summary>
    private readonly string? _githubToken;

    /// <summary>Creates a fresh scope for the actual admission.</summary>
    private readonly ScopedApplicationOperations? _operations;

    /// <summary>The operation owner, used only by ready admissions.</summary>
    private readonly Guid _owner;

    /// <summary>
    ///     Represents a preparation that cannot be admitted.
    /// </summary>
    /// <param name="state">The existing connection rejection or stale-operation state.</param>
    internal CloudDeploymentPreparation(CloudConnectionState state)
    {
        State = state;
    }

    /// <summary>
    ///     Holds the verified connection without exposing secret-bearing public properties.
    /// </summary>
    /// <param name="operations">Owns admission scope lifetime.</param>
    /// <param name="owner">The captured AutoMate owner.</param>
    /// <param name="githubToken">The verified GitHub token.</param>
    /// <param name="azure">The verified Azure connection.</param>
    internal CloudDeploymentPreparation(ScopedApplicationOperations operations, Guid owner, string githubToken,
        AzureCloudCredentialsDto azure)
    {
        _operations = operations;
        _owner = owner;
        _githubToken = githubToken;
        _azure = azure;
        State = CloudConnectionState.Ready;
    }

    /// <summary>Connection readiness available to the originating page.</summary>
    public CloudConnectionState State { get; }

    /// <summary>
    ///     Submits the confirmed settings through the application admission boundary.
    /// </summary>
    /// <param name="config">The existing confirmed mutable settings.</param>
    /// <param name="repositoryOwner">The parsed repository owner.</param>
    /// <param name="repositoryName">The parsed repository name.</param>
    /// <param name="projectName">The existing template project name.</param>
    /// <param name="idempotencyKey">The page-owned SaaS key, or null for SelfHosted.</param>
    /// <returns>The SaaS receipt, or null after SelfHosted admission; failures propagate unchanged.</returns>
    /// <exception cref="InvalidOperationException">The connection preparation is not ready.</exception>
    public Task<CloudDeploymentReceipt?> StartAsync(DeploymentConfigDto config, string repositoryOwner,
        string repositoryName, string projectName, string? idempotencyKey)
    {
        if (State != CloudConnectionState.Ready)
            throw new InvalidOperationException("Cloud connections must be ready before admission.");

        return _operations!.RunAsync<CloudDeploymentAdmission, CloudDeploymentReceipt?>(admission =>
            admission.StartAsync(
                new CloudDeploymentAdmissionRequest(_owner, config, repositoryOwner, repositoryName, projectName,
                    _githubToken!, _azure!, idempotencyKey)));
    }
}
