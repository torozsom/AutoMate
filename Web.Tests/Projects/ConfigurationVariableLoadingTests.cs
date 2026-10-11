using System.Reflection;
using Application.Abstractions.Scanning;
using Domain.DTO;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Exercises the actual deployment form's scan/merge behavior without external project or cloud access.</summary>
public sealed class ConfigurationVariableLoadingTests
{
    /// <summary>Both entry points use the same form, passing the actual selected file and preserving edited values.</summary>
    [Fact]
    public async Task Selected_project_is_scanned_and_existing_entries_are_preserved()
    {
        var scanner = DispatchProxy.Create<IProjectScannerService, Scanner>();
        var probe = (Scanner)scanner;
        probe.Result = Task.FromResult(new Dictionary<string, string> { ["EXISTING"] = "scanned", ["NEW"] = "value" });
        var form = Form(scanner);
        await Invoke(form, "LoadVariablesFromConfigFilesAsync");
        await Invoke(form, "ConfirmDeploy");
        Assert.Equal(form.ProjectPath, probe.Path);
        Assert.Equal("edited", form.Config.CustomEnvVars!["EXISTING"]);
        Assert.Equal("value", form.Config.CustomEnvVars["NEW"]);
        Assert.Contains("1 new", Message(form));
    }

    /// <summary>Empty results and failed reads provide distinct safe feedback.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_and_failed_reads_have_safe_feedback(bool failed)
    {
        var scanner = DispatchProxy.Create<IProjectScannerService, Scanner>();
        ((Scanner)scanner).Result = failed
            ? Task.FromException<Dictionary<string, string>>(new IOException("private-path-and-values"))
            : Task.FromResult(new Dictionary<string, string>());
        var form = Form(scanner);
        await Invoke(form, "LoadVariablesFromConfigFilesAsync");
        Assert.Contains(failed ? "Unable to read" : "No environment variables", Message(form));
        Assert.DoesNotContain("private", Message(form));
    }

    /// <summary>Repeated clicks cannot start overlapping scans.</summary>
    [Fact]
    public async Task Pending_scan_disables_duplicate_reads()
    {
        var scanner = DispatchProxy.Create<IProjectScannerService, Scanner>();
        var probe = (Scanner)scanner;
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

    /// <summary>Initializes the real form with a selected project and an edited environment variable.</summary>
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

    /// <summary>Invokes the handler used by the form button.</summary>
    private static Task Invoke(ConfigurationForm form, string name)
    {
        return (Task)typeof(ConfigurationForm)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;
    }

    /// <summary>Reads only owner-facing scan feedback.</summary>
    private static string Message(ConfigurationForm form)
    {
        return (string)typeof(ConfigurationForm)
            .GetField("_variableLoadMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    }

    /// <summary>Records the selected path and controls scan completion.</summary>
    public class Scanner : DispatchProxy
    {
        /// <summary>Configured bounded scan reply.</summary>
        public Task<Dictionary<string, string>> Result { get; set; } =
            Task.FromResult(new Dictionary<string, string>());

        /// <summary>Actual path supplied to scanning.</summary>
        public string? Path { get; private set; }

        /// <summary>Count of admitted scans.</summary>
        public int Calls { get; private set; }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IProjectScannerService.ExtractEnvironmentVariablesAsync), method?.Name);
            Calls++;
            Path = (string)args![0]!;
            return Result;
        }
    }
}