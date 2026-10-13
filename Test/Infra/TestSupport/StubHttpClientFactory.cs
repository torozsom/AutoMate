using System.Net;
using System.Text;

namespace Test.Infra.TestSupport;

/// <summary>
///     Creates HTTP clients sharing a caller-owned synthetic message handler.
/// </summary>
/// <param name="handler">Configured HTTP handler or reflected port callback.</param>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    /// <summary>
    ///     Creates a client without transferring ownership of the shared message handler.
    /// </summary>
    /// <param name="name">Fixture member, option or metric name selected by the caller; ignored by this fixture.</param>
    /// <returns>A client sharing the supplied handler; disposing the client does not dispose that handler.</returns>
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(handler, false);
    }
}

/// <summary>
///     Returns HTTP responses from a configured synchronous fixture callback.
/// </summary>
/// <param name="responder">Callback supplying a controlled synthetic HTTP response.</param>
internal sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    /// <summary>
    ///     Supplies the configured synthetic HTTP response and propagates fixture callback failures.
    /// </summary>
    /// <param name="request">Synthetic HTTP request observed by the handler.</param>
    /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
    /// <returns>A task returning the configured HTTP response message instance or response for this scenario.</returns>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(responder(request));
    }

    /// <summary>
    ///     Creates JSON data with the supplied identities and synthetic scenario values.
    /// </summary>
    /// <param name="content">Provider response or source text to read within bounds.</param>
    /// <returns>The configured HTTP response message instance or response for this scenario.</returns>
    public static HttpResponseMessage Json(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }
}