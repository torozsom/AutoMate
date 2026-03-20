namespace Core.Enums;

/// <summary>
///     Defines the types of applications that can be created and deployed,
///     including Web API, Blazor, and MVC applications.
/// </summary>
public enum AppType
{
    /// <summary>
    /// Represents a Web API application.
    /// </summary>
    WebApi,

    /// <summary>
    /// Represents a Blazor application.
    /// </summary>
    Blazor,

    /// <summary>
    /// Represents an ASP.NET MVC application.
    /// </summary>
    Mvc
}