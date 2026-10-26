# Deployment capabilities

Application-visible local/cloud capability flags selected by the hosting profile.

## Contents

| File                                                     | Types                     | Responsibility                                                                                                                                            |
|----------------------------------------------------------|---------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------|
| [DeploymentCapabilities.cs](DeploymentCapabilities.cs)   | `DeploymentCapabilities`  | Immutable capability set selected by the host's deployment profile.                                                                                       |
| [IDeploymentCapabilities.cs](IDeploymentCapabilities.cs) | `IDeploymentCapabilities` | Describes deployment operations enabled for the running AutoMate instance. Capabilities are enforced by the worker and orchestrators, not only by the UI. |

## Usage and integration

Inject IDeploymentCapabilities into workflows that need to gate local or cloud operations. DeploymentCapabilities is the
immutable record implementation. Web HostingProfileOptions converts SelfHosted to local/cloud enabled and SaaS to
cloud-only; ServiceConfiguration registers the selected capabilities.

## Contracts and lifecycle

Capability flags describe this host, not a user's project authorization, provider availability or credentials. UI
visibility and server execution both consume them; hiding a control is insufficient enforcement.
CloudDeploymentAdmission currently chooses durable SaaS versus in-memory self-hosted routing from
LocalDeploymentsEnabled.

## Related documentation

- [Parent module](../README.md)
- [Profile mapping](../../../Web/Configs/Hosting/HostingProfileOptions.cs)
- [Registration](../../../Web/Configs/Hosting/ServiceConfiguration.cs)
- [Execution gates](../../Orchestration/Jobs/DeploymentJobWorker.cs)
- [Admission routing](../../Orchestration/Cloud/README.md)
- [Profile tests](../../../Test/Web/Hosting/HostingProfileRegistrationTests.cs)
