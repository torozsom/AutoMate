using Application.Abstractions.Diagnostics;
using Application.Ai;
using Domain.Enums;

namespace Application.Abstractions.Ai;

/// <summary>
///     Bounded v2 sections; overview and recommendations use existing result fields.
/// </summary>
/// <param name="Observations">Bounded authored observations of the supplied evidence.</param>
/// <param name="MetricsAssessment">Bounded interpretation of the supplied numeric observations.</param>
/// <param name="PotentialIssues">Bounded observations of possible operational issues.</param>
/// <param name="Limitations">Bounded statements describing evidence and interpretation limits.</param>
public sealed record AssessmentSections(
    string[] Observations,
    string[] MetricsAssessment,
    string[] PotentialIssues,
    string[] Limitations);

/// <summary>
///     Owner-visible preferences and recorded channels; null deployment means unauthorized or
///     missing.
/// </summary>
/// <param name="Selection">The immutable source, container, focus and time-range options.</param>
/// <param name="Channels">Recorded log channels available to the selection controls.</param>
/// <param name="MetricContainers">The recorded metric container identities.</param>
/// <param name="Status">The recorded deployment runtime state.</param>
/// <param name="Availability">
///     A safe availability or coverage notice; null indicates no reported notice.
/// </param>
/// <param name="UnsupportedSources">Selected sources that the deployment provider cannot supply.</param>
public sealed record AssessmentPreferences(
    AssessmentSelection Selection,
    IReadOnlyList<AssessmentChannel> Channels,
    IReadOnlyList<string> MetricContainers,
    DeploymentStatus Status,
    string? Availability = null,
    AssessmentSources UnsupportedSources = AssessmentSources.None);

/// <summary>
///     Recorded log channel with typed source classification.
/// </summary>
/// <param name="Source">The selected diagnostic source flags.</param>
/// <param name="Channel">The stable terminal channel used for routing.</param>
public sealed record AssessmentChannel(AssessmentSources Source, string Channel);

/// <summary>
///     Private archive selection with frozen UTC bounds.
/// </summary>
/// <param name="Tenant">The internal owner-account telemetry scope.</param>
/// <param name="Project">The project identity used to scope the operation.</param>
/// <param name="Deployment">The exact deployment identity used to scope the operation.</param>
/// <param name="Selection">The immutable source, container, focus and time-range options.</param>
/// <param name="Window">The frozen evidence time window and truncation metadata.</param>
/// <param name="Limit">The maximum number of returned or processed records.</param>
/// <param name="CatalogOnly">
///     Whether to return recorded source metadata without diagnostic payloads.
/// </param>
public sealed record ArchiveAssessmentQuery(
    Guid Tenant,
    Guid Project,
    Guid Deployment,
    AssessmentSelection Selection,
    AssessmentWindow Window,
    int Limit = 1000,
    bool CatalogOnly = false);

/// <summary>
///     Filtered evidence and bounded metadata catalog, separate from prompt construction.
/// </summary>
/// <param name="Events">The bounded batch of redacted diagnostic envelopes.</param>
/// <param name="Channels">Recorded log channels available to the selection controls.</param>
/// <param name="MetricContainers">The recorded metric container identities.</param>
/// <param name="EarlierOmitted">
///     Whether earlier records were excluded by retention or bounded selection.
/// </param>
/// <param name="Availability">
///     A safe availability or coverage notice; null indicates no reported notice.
/// </param>
public sealed record ArchiveAssessmentPage(
    IReadOnlyList<DeploymentLogEnvelope> Events,
    IReadOnlyList<AssessmentChannel> Channels,
    IReadOnlyList<string> MetricContainers,
    bool EarlierOmitted,
    string? Availability = null);