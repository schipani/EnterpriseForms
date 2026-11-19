using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Domain.Features.Journal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 G14-R2 (A-4 §24.9): the Development/Testing recovery command line. Strict parsing (its arguments are removed from the host's), a
/// refusal outside Development/Testing, "not composed" without the recovery service, and an end-to-end status on a composed rig.
/// </summary>
public sealed class JournalRecoveryCommandTests
{
    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void TheCommandParsesItsVerbAndOptionsAndLeavesTheHostArguments()
    {
        var org = Guid.CreateVersion7().ToString("D");
        var recovery = Guid.CreateVersion7().ToString("D");
        string[] args = ["--urls", "http://127.0.0.1:1", JournalRecoveryCommand.Flag, "execute", "--organization", org, "--recovery-id", recovery, "--executor", "operator-x"];
        Assert.True(JournalRecoveryCommand.Requested(args, out var parsed, out var host));
        Assert.Equal(["--urls", "http://127.0.0.1:1"], host);
        Assert.Equal("execute", parsed!.Verb);
        Assert.Equal((org, recovery, "operator-x"), (parsed.Values["--organization"], parsed.Values["--recovery-id"], parsed.Values["--executor"]));

        Assert.False(JournalRecoveryCommand.Requested(["--urls", "x"], out _, out _));
        Assert.True(JournalRecoveryCommand.Requested([JournalRecoveryCommand.Flag, "approve", "--organization", org], out var unknownVerb, out _)); // no "approve" verb: the tool never signs
        Assert.Null(unknownVerb);
    }

    [Fact]
    public void TheCommandIsRefusedOutsideDevelopmentAndTestingAndWithoutComposition()
    {
        using var empty = new ServiceCollection().BuildServiceProvider();
        var arguments = new JournalRecoveryCommand.Arguments("status", new Dictionary<string, string> { ["--organization"] = Guid.CreateVersion7().ToString("D"), ["--recovery-id"] = Guid.CreateVersion7().ToString("D") });
        foreach (var environment in new[] { "Production", "Staging" })
        {
            Assert.Equal(2, JournalRecoveryCommand.Run(empty, new TestHostEnvironment(environment), arguments));
        }

        Assert.Equal(2, JournalRecoveryCommand.Run(empty, new TestHostEnvironment("Development"), null));
        Assert.Equal(3, JournalRecoveryCommand.Run(empty, new TestHostEnvironment("Testing"), arguments));
    }

    [Fact]
    public void StatusOnAComposedRigReportsUnknownRecoveriesAndExecuteRefusesWithoutAuthorization()
    {
        var world = new CapabilityRefusalEvidenceWorld();
        var org = world.AddOrganization();
        var archive = new Infrastructure.Archive.InMemorySecurityEventArchive();
        using var approvers = new SyntheticRecoveryApprovers("approver-a", "approver-b");
        world.DPlus.Approvals = approvers.Verifier;
        var services = new ServiceCollection()
            .AddSingleton(new DPlusStreamRecoveryService(world.Store, archive, new Infrastructure.Archive.InMemoryJournalVerificationFindingStore(), world.DPlus.Durability, world.Clock))
            .BuildServiceProvider();
        using (services)
        {
            var values = new Dictionary<string, string> { ["--organization"] = org.ToString("D"), ["--recovery-id"] = Guid.CreateVersion7().ToString("D"), ["--executor"] = "operator-x" };
            Assert.Equal(4, JournalRecoveryCommand.Run(services, new TestHostEnvironment("Testing"), new JournalRecoveryCommand.Arguments("status", values)));
            Assert.Equal(4, JournalRecoveryCommand.Run(services, new TestHostEnvironment("Testing"), new JournalRecoveryCommand.Arguments("execute", values)));
            Assert.Equal(2, JournalRecoveryCommand.Run(services, new TestHostEnvironment("Testing"), new JournalRecoveryCommand.Arguments("decide", values))); // no --token-file
        }

        Assert.Equal(JournalStreamNames.Audit, AuditStream);
    }

    private const string AuditStream = JournalStreamNames.Audit;
}
