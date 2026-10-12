namespace Infrastructure.Azure;

/// <summary>
///     Runtime metrics rendered in the deployment log stream for an Azure Container App.
/// </summary>
/// <param name="Cpu">The latest average CPU usage display value.</param>
/// <param name="Memory">The latest average memory usage display value.</param>
/// <param name="CpuCores">Observed CPU core usage, if supported.</param>
/// <param name="MemoryBytes">Observed memory usage in bytes, if supported.</param>
/// <param name="Observations">Typed Azure numeric observations, when supported.</param>
internal sealed record AzureContainerAppMetrics(
    string Cpu,
    string Memory,
    double? CpuCores = null,
    double? MemoryBytes = null,
    IReadOnlyList<AzureMetricObservation>? Observations = null);