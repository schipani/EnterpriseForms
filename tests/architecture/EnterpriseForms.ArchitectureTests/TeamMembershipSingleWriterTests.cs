using System.Text.RegularExpressions;
using NetArchTest.Rules;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// Phase C8 RC-26 (I-5, I-10, D8-12): the team-membership reconciler is the only writer of the roster projection and has no
/// way to submit, replay or alter an Authority command; its credential exists only in the Worker. I-10 is enforced by code
/// and these tests only — not by a database role while the API and the Worker share one runtime role (F-10, Q-11 open).
/// </summary>
public sealed partial class TeamMembershipSingleWriterTests
{
    private const string ReconcilerNamespace = "EnterpriseForms.Application.Features.Teams.MembershipReconciliation";

    [GeneratedRegex(@"AddTeamMembershipProjection\s*\(")]
    private static partial Regex RosterInsert();

    [GeneratedRegex(@"\.RemovedOn\s*=(?!=)")]
    private static partial Regex RosterRemoval();

    [GeneratedRegex(@"^\s*TeamMembershipReconciler__", RegexOptions.Multiline)]
    private static partial Regex ReconcilerCredentialVariable();

    [Fact]
    public void TheReconcilerCannotSubmitOrActForAUser()
    {
        var result = Types.InAssembly(typeof(Application.Common.Authority.IAuthorizationAuthorityClient).Assembly)
            .That().ResideInNamespace(ReconcilerNamespace)
            .ShouldNot()
            .HaveDependencyOnAny([
                "EnterpriseForms.Application.Common.Authority.IAuthorizationAuthorityClient",
                "EnterpriseForms.Application.Common.Authority.AuthorityCommandAwaiter",
                "EnterpriseForms.Application.Common.Authority.DelegatedUserContext",
                "EnterpriseForms.Application.Common.Authority.SecurityCommandRequest",
            ])
            .GetResult();
        Assert.True(result.IsSuccessful, "reconciler depends on a submit/user-assertion capability: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void OnlyTheReconcilerWritesTheTeamMembershipRoster()
    {
        var backend = RepositoryPaths.Combine("src", "backend");
        var writers = Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => RosterInsert().IsMatch(File.ReadAllText(f)) || (File.ReadAllText(f).Contains("TeamMembership", StringComparison.Ordinal) && RosterRemoval().IsMatch(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(backend, f).Replace('\\', '/'))
            .Where(f => f is not "EnterpriseForms.Infrastructure/Persistence/BusinessStore.cs" and not "EnterpriseForms.Application/Common/Persistence/IBusinessStore.cs")
            .ToList();
        Assert.Equal(["EnterpriseForms.Application/Features/Teams/MembershipReconciliation/TeamMembershipReconciliationService.cs"], writers);
    }

    [Fact]
    public void TheReconcilerCredentialIsConfiguredForTheWorkerOnly()
    {
        foreach (var file in new[] { "compose.yaml", "compose.security-test.yaml" })
        {
            var services = ParseServices(File.ReadAllText(RepositoryPaths.Combine("infrastructure", "docker", file)));
            foreach (var (service, body) in services.Where(s => s.Key != "worker"))
            {
                Assert.False(ReconcilerCredentialVariable().IsMatch(body), $"{file}: service '{service}' must not receive the team-membership reconciler credential");
            }
        }

        var sectest = ParseServices(File.ReadAllText(RepositoryPaths.Combine("infrastructure", "docker", "compose.security-test.yaml")));
        Assert.Matches(ReconcilerCredentialVariable(), sectest["worker"]);
    }

    [Fact]
    public void TheBusinessApiNeverRegistersTheReconciler()
    {
        var api = File.ReadAllText(RepositoryPaths.Combine("src", "backend", "EnterpriseForms.Api", "Program.cs"));
        Assert.DoesNotContain("AddTeamMembershipReconciler", api, StringComparison.Ordinal);
        Assert.DoesNotContain("TeamMembershipReconciliationWorker", api, StringComparison.Ordinal);
        var worker = File.ReadAllText(RepositoryPaths.Combine("src", "backend", "EnterpriseForms.Worker", "Program.cs"));
        Assert.Contains("AddTeamMembershipReconciler", worker, StringComparison.Ordinal);
    }

    /// <summary>Top-level compose services → their text (indentation-based; enough for an environment-variable check).</summary>
    private static Dictionary<string, string> ParseServices(string compose)
    {
        var services = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = compose.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inServices = false;
        string? current = null;
        var body = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("services:", StringComparison.Ordinal))
            {
                inServices = true;
                continue;
            }

            if (inServices && line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
            {
                inServices = false;
            }

            if (!inServices)
            {
                continue;
            }

            var match = Regex.Match(line, @"^  ([a-z0-9-]+):\s*$");
            if (match.Success)
            {
                if (current is not null)
                {
                    services[current] = string.Join('\n', body);
                }

                current = match.Groups[1].Value;
                body.Clear();
            }
            else if (current is not null)
            {
                body.Add(line.TrimStart());
            }
        }

        if (current is not null)
        {
            services[current] = string.Join('\n', body);
        }

        return services;
    }
}
