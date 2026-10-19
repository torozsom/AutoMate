# Deployment history

One launch's recorded state, stable configuration/resource evidence and durable archive deletion work.

## Contents

| File                                                                     | Types and responsibility                                                                                                                                  |
|--------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------|
| [Deployment.cs](Deployment.cs)                                           | Deployment: launch history, runtime state, completion outcome, resolved resources and collector ownership.                                                |
| [DeploymentConfigurationSnapshot.cs](DeploymentConfigurationSnapshot.cs) | DeploymentConfigurationSnapshot: immutable non-secret launch/artifact metadata; DeploymentDatabaseSnapshot: database provider, name and component suffix. |
| [DeploymentArchiveCleanup.cs](DeploymentArchiveCleanup.cs)               | DeploymentArchiveCleanup: archive deletion outbox retaining former tenant/project identities.                                                             |

## Usage and integration

Lifecycle services update Deployment
while [DeploymentSnapshotCapture](../../../Infrastructure/ApplicationServices/Orchestration/Lifecycle/DeploymentSnapshotCapture.cs)
creates non-secret
snapshots. [DeploymentDetailsService](../../../Infrastructure/ApplicationServices/Data/Apps/DeploymentDetailsService.cs)
reads authorized
details; [DeploymentArchiveCleanupWorker](../../../Infrastructure/Diagnostics/Archive/DeploymentArchiveCleanupWorker.cs)
processes deletion work.

## Contracts and lifecycle

Status describes recorded runtime state; Outcome and FinishedAt retain completion evidence after later stops. Running
alone does not establish application health. ResolvedHostPort and resource/revision/image identities belong to that
deployment rather than later project configuration edits.

ConfigurationSnapshotJson may be null for legacy deployments. DeploymentConfigurationSnapshot and
DeploymentDatabaseSnapshot omit credentials, environment secrets and database connection strings. Snapshot capture
selects safe values.

Deployment belongs to a CsProject and links diagnostic checkpoints and AI results. RuntimeCollectorLeaseOwner/Until
support recovery/fencing through consumers. DeploymentArchiveCleanup has no cascading owner/project foreign keys, so
archive deletion work survives deletion of its original owners.

## Related documentation

- [Entities index](../README.md)
- [State semantics](../../Enums/Deployments/README.md)
- [Diagnostic relationships](../Diagnostics/README.md)
- [Persistence constraints](../../../Infrastructure/Data/AutoMateModelConfiguration.cs)
- [Archive tests](../../../Test/Infra/Diagnostics/Archive/DiskDeploymentArchiveTests.cs)
- [History privacy tests](../../../Test/Web/Projects/DeploymentFailurePrivacyTests.cs)
