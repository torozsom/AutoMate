# AutoMate Core Module

> The Core layer sits at the absolute center of the AutoMate Clean Architecture pattern. It encapsulates the pure domain logic, enterprise entities, and static primitives required to represent the state of the application. It remains completely agnostic to infrastructure, external integrations, or presentation layer concerns.

---

## Architecture and Scope

Adhering strictly to **Clean Architecture** principles, the `Core` project fundamentally dictates the shape of the system. It contains no implementations of business rules or external database persistence mapping; instead, it defines *what* data exists and *how* it should be transported across boundaries. 

This project must not depend on any other project within the solution (`Web` or `Services`). It provides the universal contracts that the outer layers must implement and utilize.

---

## Module Breakdown

The definitions within this module are categorized by their role in the data flow of the system.

### Domain Entities
* **Entities**: The authoritative, state-bearing business objects mapped by the persistent layer. Entities derive strictly from `BaseEntity` (which provides foundational identifiers and audit tracking). 
  * Examples include representations of deployment state (`Deployment`), repository abstractions (`Project`, `CsProject`, `LocalProjectConfig`), and distinct identity structures utilizing Table-Per-Hierarchy persistence patterns (`User`, `LocalUser`, `GitHubUser`).

### Data Transport
* **DTO**: Data Transfer Objects (DTOs) constructed expressly to shuffle formatted data over HTTP or function boundaries without exposing the raw underlying database schema. 
  * Examples include payload structures for repository interactions (`GitHubRepositoryDto`), system analysis metadata (`ProjectMetadataDto`), and configuration shapes (`DeploymentConfigDto`, `DatabaseConfigDto`).

### Primitives & Types
* **Enums**: Immutable, strictly-typed enumerations that restrict application state to predefined valid domains.
  * *AppType*: Defines the runtime capability expected of a project.
  * *DeploymentStatus*: Tracks the exact chronological state of a Docker orchestration workflow.
  * *SourceType*: Differentiates between local code architectures and remote GitHub definitions.

---

## Core Principles & Guidelines

When making modifications or extensions to the Core module, these rigid principles must be observed:

1. **No External Dependencies**: The Core module must not integrate NuGet packages related to Entity Framework, ASP.NET Core, Docker, or external API consumption. Only rudimentary standard library logic belongs here.
2. **Behavior Over State**: While primarily data-holding structures, entities should enforce their own internal invariant logic if applicable, rather than exposing anemic objects to the Services layer.
3. **Immutability and Encapsulation**: Limit public setters where logical. Changes to critical entity states should be facilitated via domain operations, keeping the true state encapsulated from arbitrary manipulation.
