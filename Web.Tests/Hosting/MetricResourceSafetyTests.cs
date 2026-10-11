using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Web.Observability;
using Xunit;

namespace Web.Tests;

/// <summary>Verifies resource and metric policy at real SDK and OTLP serialization boundaries.</summary>
public sealed class MetricResourceSafetyTests
{
    /// <summary>Operator labels remain useful while default resource attributes and secret-bearing labels are excluded.</summary>
    [Theory]
    [InlineData("AutoMate", "Testing", "AutoMate", "Testing")]
    [InlineData("password=private-secret", "https://private-host/path", "AutoMate", "unknown")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz1234567890", "Testing\nprivate-data", "AutoMate", "unknown")]
    public void Resources_allow_only_bounded_masked_operator_labels(string service, string environment,
        string expectedService, string expectedEnvironment)
    {
        var resource = ResourceBuilder.CreateEmpty().AddAttributes(new Dictionary<string, object>
                { ["private-extra"] = "private-token" }).Clear()
            .AddDetector(new SafeResourceDetector(new DiagnosticRedactor(), service, environment, "1.0.0", "SaaS"))
            .Build().Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(4, resource.Count);
        Assert.Equal(expectedService, resource["service.name"]);
        Assert.Equal(expectedEnvironment, resource["deployment.environment"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(resource));
    }

    /// <summary>
    ///     Real metric export retains aggregate HTTP measurements and approved operational labels without
    ///     payloads/exemplars.
    /// </summary>
    [Fact]
    public async Task Metric_wire_omits_external_tags_unknown_instruments_and_exemplars()
    {
        var received = new ConcurrentQueue<byte[]>();
        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        appBuilder.Logging.ClearProviders();
        await using var app = appBuilder.Build();
        app.MapPost("/{**path}", async (HttpContext context) =>
        {
            using var bytes = new MemoryStream();
            await context.Request.Body.CopyToAsync(bytes);
            received.Enqueue(bytes.ToArray());
            return Results.Bytes([], "application/x-protobuf");
        });
        await app.StartAsync();
        var builder = Sdk.CreateMeterProviderBuilder()
            .AddMeter("System.Net.Http", "AutoMate.Deployments", "AutoMate.Analysis")
            .SetExemplarFilter(ExemplarFilterType.AlwaysOn);
        SafeMetricPolicy.Configure(builder);
        using var provider = builder.AddOtlpExporter((exporter, reader) =>
        {
            exporter.Endpoint = new Uri(app.Urls.Single());
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 60000;
        }).Build();
        using var httpMeter = new Meter("System.Net.Http");
        var http = httpMeter.CreateHistogram<double>("http.client.request.duration", "s");
        var unknown = httpMeter.CreateCounter<long>("private-unknown-instrument");
        using var deploymentMeter = new Meter("AutoMate.Deployments");
        var jobs = deploymentMeter.CreateCounter<long>("automate.deployment.jobs.started");
        using (new Activity("fixture").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            http.Record(0.25, new KeyValuePair<string, object?>("server.address", "private-host"),
                new KeyValuePair<string, object?>("error.type", "private-exception"),
                new KeyValuePair<string, object?>("url.full", "https://private-url?token=private-token"));
            jobs.Add(2, new KeyValuePair<string, object?>("lane", "cloud"),
                new KeyValuePair<string, object?>("ProjectId", "private-project"));
            unknown.Add(5);
            using var analysisMeter = new Meter("AutoMate.Analysis");
            analysisMeter.CreateCounter<long>("automate.analysis.operations").Add(1,
                new KeyValuePair<string, object?>("analysis.operation", "provider"),
                new KeyValuePair<string, object?>("analysis.outcome", "failed"),
                new KeyValuePair<string, object?>("AnalysisId", "private-analysis"));
        }

        Assert.True(provider.ForceFlush());
        var packet = Encoding.UTF8.GetString(Assert.Single(received));
        Assert.Contains("http.client.request.duration", packet);
        Assert.Contains("automate.deployment.jobs.started", packet);
        Assert.Contains("automate.analysis.operations", packet);
        Assert.Contains("analysis.operation", packet);
        Assert.Contains("analysis.outcome", packet);
        Assert.Contains("lane", packet);
        Assert.Contains("cloud", packet);
        Assert.DoesNotContain("private", packet);
        Assert.DoesNotContain("ProjectId", packet);
    }
}