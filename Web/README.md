# AutoMate Web Module

> The Web layer is the primary user interface and API gateway for the AutoMate platform. Built using .NET 10, Blazor
> Server, and Minimal APIs, it enforces a strict separation of concerns by delegating business logic to the Services
> layer
> and keeping the startup footprint exceptionally minimal.

---

## Architecture and Scope

Adhering strictly to **Clean Architecture** principles, the `Web` project functions exclusively as the presentation and
routing mechanism. It consumes the rigorous contracts (interfaces) provided by the `Services` and `Core` layers but
strictly avoids implementing core domain logic or direct database persistence operations.

This module is responsible for orchestrating:

- **Interactive UI Delivery** via Blazor Server.
- **RESTful API Exposure** using Minimal APIs.
- **Real-Time Telemetry** via SignalR.
- **Application Bootstrapping** through isolated configuration classes.

---

## Module Breakdown

The presentation components are logically grouped to ensure maintainability and separation of frontend concerns.

### Frontend Presentation

* **Components**: Houses the modular Blazor Server UI elements that formulate the interactive application.
    * *Pages*: Routable Razor components representing top-level screens (e.g., Dashboard, Project Details, Deployment
      Status).
    * *Layout*: Shared architectural elements that define the application shell, including navigation drawers and
      contextual headers.
    * *Shared*: Isolated, highly reusable UI primitives and interactive controls leveraged across distinct pages.

### Application Bootstrapping

* **Configs**: Architected to keep the `Program.cs` file completely pristine by offloading startup responsibilities.
    * *ServiceConfiguration*: Orchestrates all Dependency Injection container registrations. Configures scoped services,
      singleton real-time hubs, database contexts, and custom Minimal API endpoints.
    * *AppConfiguration*: Manages the HTTP request pipeline. Systematically configures middleware for authentication,
      authorization, static file serving, route interception, and endpoint mapping.

### Utility and Extensions

* **Extensions**: Houses specialized utility methods and extension classes designed to simplify presentation layer
  tasks. This includes streamlining response formats, querying claim sets from the current security principal, and
  format-shifting UI-specific data types without polluting pure domain entities.

### Real-Time Communications

* **Hubs**: Manages bi-directional, low-latency communication with connected clients utilizing SignalR.
    * *LogHub*: Mapped dynamically at `/loghub`, this component leverages the backend `ILogStreamer` service to push
      live deployment logs, container metrics, and build outputs directly to the UI, bypassing traditional HTTP polling.

### API Gateway

* **Routes**: Defines the robust Minimal API endpoints utilized by external integrations or specific background sync
  tasks. To preserve clean abstractions, raw inline routing (e.g., `app.MapGet()`) is strictly avoided outside of
  encapsulated classes.
    * *IEndpoint*: A unified contract dictating how discrete HTTP endpoints must be registered.
    * *Endpoints*: Focused implementations (such as Auth or Project routing) are housed here. These are auto-mapped via
      reflection or explicit registration mechanisms (e.g., `builder.Services.AddEndpoint<T>()`) during application
      initialization.

### Presentation State

* **Services**: Contains UI-specific transient and scoped state managers required exclusively by the front end. Distinct
  from core business services, these components handle view-specific data aggregation, UI state persistence across
  active Blazor circuits, and complex component-to-component event messaging.

---

## Core Interaction Workflows

Modules within the Web project prioritize rapid rendering and immediate user feedback:

1. **Initialization**: Application launch originates in `Program.cs`, immediately delegating execution to the `Configs`
   module to wire up DI containers and the HTTP middleware pipeline.
2. **Request Interception**: Incoming traffic is processed by `AppConfiguration`. Standard API calls are shunted to
   specifically mapped instances in the `Routes` directory, while standard HTML/UI navigation falls back to Blazor
   native routing.
3. **Execution & Rendering**: The `Components` module leverages the Blazor Server hosting model to evaluate complex UI
   logic server-side, transmitting highly optimized DOM delta updates over a continuous SignalR channel to the client
   browser.
4. **Live Data Streaming**: Upon deployment initiation, continuous stdout/stderr telemetry mapped by backend layers is
   piped into the `Hubs` module. The `LogHub` instantly broadcasts these structured payloads to subscribing frontend
   elements, achieving live observability.
