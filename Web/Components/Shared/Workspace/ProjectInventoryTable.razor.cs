using Application.Data.Apps;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Shared.Workspace;

/// <summary>
///     Presents paginated project inventory and delegates authorized deployment, stop and removal actions.
/// </summary>
public partial class ProjectInventoryTable
{
    /// <summary>Bounded owner-authorized page.</summary>
    [Parameter]
    public ProjectInventoryPage? Inventory { get; set; }

    /// <summary>Connected Azure account required by cloud admission.</summary>
    [Parameter]
    public bool AzureConnected { get; set; }

    /// <summary>Local hosting capability.</summary>
    [Parameter]
    public bool LocalEnabled { get; set; }

    /// <summary>Existing process-local admission state.</summary>
    [Parameter]
    public Func<Guid, bool> IsDeploying { get; set; } = _ => false;

    /// <summary>Pending stop admission and queue state.</summary>
    [Parameter]
    public Func<Guid, bool> IsStopping { get; set; } = _ => false;

    /// <summary>Stops one selected owned local deployment.</summary>
    [Parameter]
    public EventCallback<Guid> OnStop { get; set; }

    /// <summary>Prepares one selected owned project.</summary>
    [Parameter]
    public EventCallback<Guid> OnDeploy { get; set; }

    /// <summary>Requests existing project deletion.</summary>
    [Parameter]
    public EventCallback<Guid> OnRemove { get; set; }
}
