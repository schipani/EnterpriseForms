using System.Text.Json;
using Xunit;

namespace EnterpriseForms.Experiments.ManagedHsmProbe.Tests;

/// <summary>Command-line behaviour: local-only default, explicit opt-ins, no client before validation, cancellation, no token output.</summary>
public sealed class ApplicationTests : IDisposable
{
    private static readonly JsonSerializerOptions EnumAsString = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly string directory = Directory.CreateTempSubdirectory("e6a-probe-tests-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Theory]
    [InlineData("")]
    [InlineData("local")]
    public async Task LocalModeIsTheDefaultPassesAndNeverCreatesAnHsmClient(string commandLine)
    {
        var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ProbeApplication.RunAsync(args, output, error, new TestSetup.TripwireFactory(), TestContext.Current.CancellationToken);
        Assert.Equal(0, exit);
        Assert.Contains("OVERALL: Pass", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Local mode makes no network call", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHsmCommandWithoutTheCommandLineFlagIsRefusedBeforeReadingAnything()
    {
        var path = WriteConfiguration(TestSetup.KnownKeys().Configuration);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ProbeApplication.RunAsync(["hsm-run", "--config", path], output, error, new TestSetup.TripwireFactory(), TestContext.Current.CancellationToken);
        Assert.Equal(ProbeApplication.UsageError, exit);
        Assert.Contains("--allow-azure-calls", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AllowAzureCalls")]
    [InlineData("Mode")]
    [InlineData("Endpoint")]
    [InlineData("PrimaryVersion")]
    public async Task AnInvalidConfigurationIsRefusedWithTheFlagAndNoClientIsCreated(string defect)
    {
        var (_, configuration) = TestSetup.KnownKeys();
        switch (defect)
        {
            case "AllowAzureCalls": configuration.AllowAzureCalls = false; break;
            case "Mode": configuration.Mode = ProbeMode.Local; break;
            case "Endpoint": configuration.HsmEndpoint = "http://probe-test.managedhsm.azure.net/"; break;
            case "PrimaryVersion": configuration.PrimaryKeyVersion = 9; break;
        }

        var path = WriteConfiguration(configuration);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ProbeApplication.RunAsync(["hsm-run", "--config", path, "--allow-azure-calls"], output, error, new TestSetup.TripwireFactory(), TestContext.Current.CancellationToken);
        Assert.Equal(ProbeApplication.UsageError, exit);
        Assert.Contains("nothing was contacted", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateConfigMakesNoCall()
    {
        var path = WriteConfiguration(TestSetup.KnownKeys().Configuration);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await ProbeApplication.RunAsync(["validate-config", "--config", path], output, error, new TestSetup.TripwireFactory(), TestContext.Current.CancellationToken));
        Assert.Contains("nothing was contacted", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFullFakeRunWritesAReportAndNeverPrintsTheAccessToken()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var path = WriteConfiguration(configuration);
        var report = Path.Combine(directory, "report.json");
        var factory = new TestSetup.FakeFactory(hsm);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ProbeApplication.RunAsync(["hsm-run", "--config", path, "--allow-azure-calls", "--report", report], output, error, factory, TestContext.Current.CancellationToken);
        Assert.Equal(0, exit);
        Assert.Equal(1, factory.Created);
        var text = File.ReadAllText(report);
        Assert.DoesNotContain(FakeCredential.TokenValue, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeCredential.TokenValue, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeCredential.TokenValue, error.ToString(), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("Pass", document.RootElement.GetProperty("overallVerdict").GetString());
        Assert.Equal("2025-07-01", document.RootElement.GetProperty("apiVersion").GetString());
        Assert.True(hsm.Requests.All(r => r.Authorization == "Bearer " + FakeCredential.TokenValue));
    }

    [Fact]
    public async Task CancellationDuringAnHsmRunStopsWithTheCancelledExitCode()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        configuration.OperationTimeoutSeconds = 30;
        hsm.Delay = TimeSpan.FromSeconds(20);
        var path = WriteConfiguration(configuration);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var started = DateTime.UtcNow;
        var exit = await ProbeApplication.RunAsync(["hsm-run", "--config", path, "--allow-azure-calls", "--report", Path.Combine(directory, "r.json")], output, error, new TestSetup.FakeFactory(hsm), cancellation.Token);
        Assert.Equal(ProbeApplication.Cancelled, exit);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(Path.Combine(directory, "r.json")));
    }

    [Fact]
    public async Task UnknownOptionsAreUsageErrors()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(ProbeApplication.UsageError, await ProbeApplication.RunAsync(["local", "--allow-azure-calls"], output, error, new TestSetup.TripwireFactory(), TestContext.Current.CancellationToken));
        Assert.Equal(ProbeApplication.UsageError, await ProbeApplication.RunAsync(["hsm-run", "--endpoint", "x"], output, error, new TestSetup.TripwireFactory(), TestContext.Current.CancellationToken));
        Assert.Equal(ProbeApplication.UsageError, await ProbeApplication.RunAsync(["sign-anything"], output, error, new TestSetup.TripwireFactory(), TestContext.Current.CancellationToken));
    }

    private string WriteConfiguration(ProbeConfiguration configuration)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        var json = JsonSerializer.Serialize(configuration, EnumAsString);
        using var document = JsonDocument.Parse(json);
        // Drop computed (get-only) properties so the strict parser accepts the file.
        var writable = document.RootElement.EnumerateObject().Where(p => p.Name is not ("EndpointUri" or "MappedVersions"))
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        File.WriteAllText(path, JsonSerializer.Serialize(writable));
        return path;
    }
}
