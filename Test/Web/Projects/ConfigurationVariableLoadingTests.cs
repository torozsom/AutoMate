using System.Reflection;
using Application.Abstractions.Scanning;
using Domain.DTO.Deployment;
using Web.Components.Shared.Configuration;

namespace Test.Web.Projects;

/// <summary>
///     Exercises the actual deployment form's scan/merge behavior without external project or cloud
///     access.
/// </summary>
/// <exception cref="IOException">The returned task faults when the fixture failure is selected.</exception>
public sealed class ConfigurationVariableLoadingTests
{
    /// <summary>
    ///     Both entry points use the same form, passing the actual selected file and preserving edited
    ///     values.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Selected_project_is_scanned_and_existing_entries_are_preserved()
    {
        var scanner = DispatchProxy.Create<IProjectScannerService, Scanner>();
        var probe = Assert.IsAssignableFrom<Scanner>(scanner);

        probe.Result = Task.FromResult(new Dictionary<string, string> { ["EXISTING"] = "scanned", ["NEW"] = "value" });
        var form = Form(scanner);

        await Invoke(form, "LoadVariablesFromConfigFilesAsync");
        await Invoke(form, "ConfirmDeploy");

        Assert.Equal(form.ProjectPath, probe.Path);
        Assert.Equal("edited", form.Config.CustomEnvVars["EXISTING"]);
        Assert.Equal("value", form.Config.CustomEnvVars["NEW"]);
        Assert.Contains("1 new", Message(form));
    }

    /// <summary>
    ///     Empty results and failed reads provide distinct safe feedback.
    /// </summary>
    /// <param name="failed">The recorded failed-outcome count.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_and_failed_reads_have_safe_feedback(bool failed)
    {
        var scanner = DispatchProxy.Create<IProjectScannerService, Scanner>();

        Assert.IsAssignableFrom<Scanner>(scanner).Result = failed
            ? Task.FromException<Dictionary<string, string>>(new IOException("private-path-and-values"))
            : Task.FromResult(new Dictionary<string, string>());
        var form = Form(scanner);

        await Invoke(form, "LoadVariablesFromConfigFilesAsync");

        Assert.Contains(failed ? "Unable to read" : "No environment variables", Message(form));
        Assert.DoesNotContain("private", Message(form));
    }

    /// <summary>
    ///     Repeated clicks cannot start overlapping scans.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Pending_scan_disables_duplicate_reads()
    {
        var scanner = DispatchProxy.Create<IProjectScannerService, Scanner>();
        var probe = Assert.IsAssignableFrom<Scanner>(scanner);
        var pending =
            new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);

        probe.Result = pending.Task;
        var form = Form(scanner);
        var first = Invoke(form, "LoadVariablesFromConfigFilesAsync");

        await Invoke(form, "LoadVariablesFromConfigFilesAsync");

        Assert.Equal(1, probe.Calls);

        pending.SetResult([]);
        await first;
    }

    /// <summary>
    ///     Initializes the real form with a selected project and an edited environment variable.
    /// </summary>
    /// <param name="scanner">Configured project-variable scanner fake.</param>
    /// <returns>The configured configuration form instance or response for this scenario.</returns>
    private static ConfigurationForm Form(IProjectScannerService scanner)
    {
        var form = new ConfigurationForm();

        typeof(ConfigurationForm).GetProperty("ProjectPath")!.SetValue(form, "C:/selected/Web.csproj");
        typeof(ConfigurationForm).GetProperty("Config")!.SetValue(form, new DeploymentConfigDto
            { ExposedPort = 12345, CustomEnvVars = new Dictionary<string, string> { ["EXISTING"] = "edited" } });
        typeof(ConfigurationForm).GetProperty("ProjectScanner", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(form, scanner);
        typeof(ConfigurationForm).GetMethod("OnInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, null);

        return form;
    }

    /// <summary>
    ///     Invokes the handler used by the form button.
    /// </summary>
    /// <param name="form">Deployment configuration component under test.</param>
    /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
    /// <returns>
    ///     The configured response for the selected fixture member, including null when its contract
    ///     requires no value.
    /// </returns>
    private static Task Invoke(ConfigurationForm form, string name)
    {
        return (Task)typeof(ConfigurationForm)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;
    }

    /// <summary>
    ///     Reads only owner-facing scan feedback.
    /// </summary>
    /// <param name="form">Deployment configuration component under test.</param>
    /// <returns>The synthetic text produced for this scenario.</returns>
    private static string Message(ConfigurationForm form)
    {
        return (string)typeof(ConfigurationForm)
            .GetField("_variableLoadMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    }

    /// <summary>
    ///     Records the selected path and controls scan completion.
    /// </summary>
    public class Scanner : DispatchProxy
    {
        /// <summary>Configured bounded scan reply.</summary>
        public Task<Dictionary<string, string>> Result { get; set; } =
            Task.FromResult(new Dictionary<string, string>());

        /// <summary>Actual path supplied to scanning.</summary>
        public string? Path { get; private set; }

        /// <summary>Count of admitted scans.</summary>
        public int Calls { get; private set; }

        /// <summary>
        ///     Checks the variable-discovery member, records its directory and returns the configured
        ///     discovery result.
        /// </summary>
        /// <param name="method">Reflected member or client method selected for invocation.</param>
        /// <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        /// <returns>
        ///     The configured response for the selected fixture member, including null when its contract
        ///     requires no value.
        /// </returns>
        protected override object Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IProjectScannerService.ExtractEnvironmentVariablesAsync), method?.Name);

            Calls++;
            Path = (string)args![0]!;

            return Result;
        }
    }
}
