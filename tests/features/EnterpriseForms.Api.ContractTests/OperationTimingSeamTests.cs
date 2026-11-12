using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>C-137 (R-37): the record-creation timing seam is a no-op unless configured, and refuses to start outside Development and Testing.</summary>
public sealed class OperationTimingSeamTests
{
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Configuration(bool enabled) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [TestingOperationTiming.ConfigurationKey] = enabled ? "true" : "false" }).Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void TheDiagnosticRefusesToStartOutsideDevelopmentAndTesting(string environment) =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddOperationTiming(Configuration(true), new Environment(environment)));

    [Theory]
    [InlineData("Testing", true, true)]
    [InlineData("Development", true, true)]
    [InlineData("Testing", false, false)]
    public void TheLoggingSinkIsRegisteredOnlyWhenConfigured(string environment, bool enabled, bool registered) =>
        Assert.Equal(registered, new ServiceCollection().AddOperationTiming(Configuration(enabled), new Environment(environment)).Any(d => d.ServiceType == typeof(IOperationTimingSink)));

    private sealed class Recording(bool enabled) : IOperationTimingSink
    {
        public List<OperationTimingRecord> Records { get; } = [];

        public bool Enabled => enabled;

        public void Record(OperationTimingRecord record) => Records.Add(record);
    }

    [Fact]
    public void ThePhaseTimerRecordsConsecutivePhasesAndPollsOnlyWhenTheSinkIsEnabled()
    {
        var enabled = new Recording(true);
        var timer = new OperationPhaseTimer(enabled);
        timer.Mark("first");
        timer.Poll();
        timer.Poll();
        timer.Mark("second");
        var organization = Guid.CreateVersion7();
        timer.Complete("CreateRecord", organization, "Created");
        var record = Assert.Single(enabled.Records);
        Assert.Equal(["first", "second"], record.Phases.Select(p => p.Key));
        Assert.Equal((2, "Created", organization), (record.StatusPolls, record.Outcome, record.OrganizationId));
        Assert.True(record.TotalMilliseconds >= record.Phases.Sum(p => p.Value) - 0.001);

        var disabled = new Recording(false);
        var silent = new OperationPhaseTimer(disabled);
        silent.Mark("first");
        silent.Complete("CreateRecord", organization, "Created");
        Assert.Empty(disabled.Records);
        Assert.Same(NoOperationTiming.Instance, NoOperationTiming.Instance);
        Assert.False(NoOperationTiming.Instance.Enabled);
    }
}
