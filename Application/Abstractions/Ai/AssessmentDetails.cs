using Application.Abstractions.Diagnostics;
using Application.Ai;
using Domain.Enums;

namespace Application.Abstractions.Ai;

/// <summary>Bounded v2 sections; overview and recommendations use existing result fields.</summary>
public sealed record AssessmentSections(
    string[] Observations,
    string[] MetricsAssessment,
    string[] PotentialIssues,
    string[] Limitations);

/// <summary>Owner-visible preferences and recorded channels; null deployment means unauthorized or missing.</summary>
public sealed record AssessmentPreferences(
    AssessmentSelection Selection,
    IReadOnlyList<AssessmentChannel> Channels,
    IReadOnlyList<string> MetricContainers,
    DeploymentStatus Status,
    string? Availability = null,
    AssessmentSources UnsupportedSources = AssessmentSources.None);

/// <summary>Recorded log channel with typed source classification.</summary>
public sealed record AssessmentChannel(AssessmentSources Source, string Channel);

/// <summary>Private archive selection with frozen UTC bounds.</summary>
public sealed record ArchiveAssessmentQuery(
    Guid Tenant,
    Guid Project,
    Guid Deployment,
    AssessmentSelection Selection,
    AssessmentWindow Window,
    int Limit = 1000,
    bool CatalogOnly = false);

/// <summary>Filtered evidence and bounded metadata catalog, separate from prompt construction.</summary>
public sealed record ArchiveAssessmentPage(
    IReadOnlyList<DeploymentLogEnvelope> Events,
    IReadOnlyList<AssessmentChannel> Channels,
    IReadOnlyList<string> MetricContainers,
    bool EarlierOmitted,
    string? Availability = null);