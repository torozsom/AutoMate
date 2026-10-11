using System.Reflection;
using Application.Abstractions.Ai;
using Application.Ai;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Exercises the shared context picker and real event callbacks without provider calls.</summary>
public sealed class AssessmentContextControlsTests
{
    /// <summary>Verifies independent log/metric choices and suppression of frozen events.</summary>
    [Fact]
    public async Task Individual_container_and_source_choices_are_independent_and_frozen_controls_ignore_events()
    {
        AssessmentSelection? changed = null;
        var component = new AssessmentContextControls();
        Set(component, nameof(component.Selection), new AssessmentSelection());
        Set(component, nameof(component.Channels),
            new AssessmentChannel[] { new(AssessmentSources.Web, "web"), new(AssessmentSources.Database, "db") });
        Set(component, nameof(component.MetricContainers), new[] { "web", "db" });
        Set(component, nameof(component.OnChange),
            EventCallback.Factory.Create<AssessmentSelection>(new object(), selection => changed = selection));
        await Invoke(component, "LogContainerChanged", "db", new ChangeEventArgs { Value = false });
        Assert.Equal(["web"], changed!.LogContainers!);
        Assert.Null(changed.MetricContainers);
        Assert.True(changed.IncludeMetrics);
        Set(component, nameof(component.Selection), changed);
        await Invoke(component, "MetricsChanged", new ChangeEventArgs { Value = false });
        Assert.False(changed!.IncludeMetrics);
        Assert.Equal(["web"], changed.LogContainers!);
        Set(component, nameof(component.Disabled), true);
        var frozen = changed;
        await Invoke(component, "SourceChanged", AssessmentSources.Web, new ChangeEventArgs { Value = false });
        Assert.Same(frozen, changed);
    }

    /// <summary>Checks UTC inputs, finite focus options, unavailable source labels and encoded output.</summary>
    [Fact]
    public async Task Custom_picker_and_v2_sections_render_accessible_bounded_content()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AssessmentContextControls>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Selection"] = new AssessmentSelection(Range: AssessmentRange.Custom),
                    ["Channels"] = new AssessmentChannel[]
                        { new(AssessmentSources.Web, "web"), new(AssessmentSources.Database, "db") },
                    ["MetricContainers"] = new[] { "web", "db" }, ["UnsupportedSources"] = AssessmentSources.Azure,
                    ["Disabled"] = true
                }))).ToHtmlString());
        Assert.Contains("Startup assessment", html);
        Assert.Contains("Failure diagnosis", html);
        Assert.Contains("Runtime overview", html);
        Assert.Contains("Historical review", html);
        Assert.Contains("From (UTC)", html);
        Assert.Contains("Until (UTC)", html);
        Assert.Contains("Unsupported for this recorded deployment", html);
        Assert.Contains("fieldset", html);
        Assert.Contains("disabled", html);
        Assert.Contains("Azure application console output", html);
        var section = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AssessmentResultSection>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Title"] = "Observations", ["Items"] = new[] { "<script>unsafe</script>" },
                    ["EmptyMessage"] = "No observations."
                }))).ToHtmlString());
        Assert.DoesNotContain("<script>", section);
        Assert.Contains("&lt;script&gt;", section);
    }

    /// <summary>Dispatches the real picker callback without browser or provider dependencies.</summary>
    private static Task Invoke(object component, string name, params object[] args)
    {
        return (Task)component.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(
            component, args)!;
    }

    /// <summary>Initializes isolated callback state without pretending that an unrendered component has a render handle.</summary>
    private static void Set(object component, string name, object value)
    {
        component.GetType().GetProperty(name)!.SetValue(component, value);
    }
}