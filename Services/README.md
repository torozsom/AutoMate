# AutoMate Services Module

> The Services layer is the core operational hub of the AutoMate architecture. Acting as the bridge between domain entities and application interfaces, it enforces business rules, manages infrastructure integrations, and orchestrates the deployment workflows.

---

## Architecture and Scope

Adhering strictly to **Clean Architecture** principles, the `Services` project remains independent of front-end components and external web routing mechanisms. It relies exclusively on the `Core` domain while exposing well-defined contracts (interfaces) for consumption by the presentation layer (`Web`).

This module is responsible for orchestrating multiple crucial systems:
- **Relational Data Persistence** using Entity Framework Core.
- **Dynamic Template Generation** for containerized deployments.
- **Static Code Analysis** for .NET project introspection.
- **Infrastructure Management** via Docker and Docker Compose.

---

## Module Breakdown

The services are logically grouped into focused modules, ensuring single-responsibility and separation of concerns.

### Identity & Access Management
* **Auth**: Centralizes all authentication and authorization processes. Supports local user registration, credential validation, email verification workflows, and seamless OAuth integrations (e.g., synchronizing GitHub identities with local user representations).

### Infrastructure Interactions
* **Docker**: The infrastructure engine. Encapsulates interactions with the Docker daemon, providing methods to verify system prerequisites, build images from raw source directories, execute Docker Compose operations, and monitor running container lifecycles.
* **Email**: Provides a robust abstraction for transactional email delivery. Heavily utilized by the Auth module for dispatching verification tokens and critical security notifications.
* **GitHub**: The core integration point for the GitHub API. Handles secure token-based requests to fetch authenticated user repositories, facilitating seamless remote code imports.
* **Data**: Manages the application's persistence mechanism using Entity Framework Core. Defines the `AutoMateDbContext` and maps domain entities from the `Core` library to the underlying PostgreSQL schemas.
* **Migrations**: Houses the auto-generated Entity Framework Core migration scripts, tracking the temporal evolution of the database schema to ensure consistency across environments.

### Deployment Engine
* **Projects**: Manages the lifecycle and metadata of user-defined projects, distinguishing between locally uploaded archives and linked remote GitHub repositories.
* **Scanner**: Performs intelligent static analysis on source code. Parses `.csproj` files and configurations to extract vital metadata (e.g., targeted .NET versions, framework modes, and environment variables).
* **Templating**: Dynamically generates Infrastructure-as-Code artifacts. Utilizing the Scriban templating engine, it digests context from the Scanner to construct highly optimized `Dockerfile` and `docker-compose.yml` assets.
* **Orchestration**: The grand coordinator. Merges static analysis, templating, and Docker execution to transition a project from raw source code to a live container. Encompasses background hosted services (e.g., cleanup routines) to enforce resource limits and tear down expired deployments.

### Observability
* **LogStreaming**: Critical for operational visibility. Captures stdout/stderr streams from active builds and running containers in real-time. Funnels asynchronous telemetry to a centralized hub, preparing it for low-latency broadcasting via SignalR to the frontend.

---

## Core Inter-Module Workflows

Modules within this project are designed for loose coupling and high cohesion, communicating exclusively via Dependency Injection. A typical deployment workflow demonstrates this orchestration:

1. **Initialization**: The `Projects` module registers a new deployment target and verifies the source code structure.
2. **Introspection**: The `Scanner` module evaluates the codebase, mapping out runtime dependencies and framework configurations.
3. **Generation**: The `Templating` module ingests the parsed metadata to synthesize context-aware environment manifests.
4. **Execution**: The `Orchestration` service coordinates the final build, invoking the `Docker` module to provision the external container state.
5. **Telemetry & State**: Concurrently, the `LogStreaming` module pipes build feedback straight to the client, while the `Data` module persists historical deployment snapshots.
