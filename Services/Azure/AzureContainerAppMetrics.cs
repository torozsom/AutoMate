namespace Services.Azure;

/// <summary>
///     Runtime metrics rendered in the deployment log stream for an Azure Container App.
/// </summary>
/// <param name="Cpu">The latest average CPU usage display value.</param>
/// <param name="Memory">The latest average memory usage display value.</param>
internal sealed record AzureContainerAppMetrics(string Cpu, string Memory);