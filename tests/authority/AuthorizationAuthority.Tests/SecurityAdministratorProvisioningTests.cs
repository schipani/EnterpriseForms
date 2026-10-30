using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// DR-C7-3 at unit level: dual-controlled operator approvals (<see cref="OperatorApprovalVerifier"/>) and the first-appointment
/// and recovery operations (<see cref="SecurityAdministratorProvisioningService"/>) over the in-memory Authority store. The
/// real path (journal, SpiceDB projection, archive, business API) runs on Docker in the Stage B suite.
/// </summary>
public sealed class SecurityAdministratorProvisioningTests : IDisposable
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OtherOrg = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Second = Guid.CreateVersion7();

    private readonly ECDsa first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa forged = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly InMemoryAuthorityStore store = new();
    private readonly RecordingAlerts alerts = new();

    public SecurityAdministratorProvisioningTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, OrganizationDisplayName = "Org", AuthorizationRevision = 4, ProjectionAppliedRevision = 4 });
        foreach (var user in new[] { Owner, Member, Second })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = Subject(user), IdentityBindingEnabled = true });
        }

        store.Edges.Add(Edge(OrganizationRoleNames.OrganizationOwner, Owner));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Subject(Guid user) => "subject-" + user.ToString("N")[..12];

    private static AuthorityRelationshipEdge Edge(string relation, Guid user) => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
        OrganizationId = Org,
        ResourceTypeName = ResourceTypeNames.Organization,
        ResourceEntityId = Org,
        RelationshipName = relation,
        SubjectTypeName = ResourceTypeNames.User,
        SubjectEntityId = user,
        SubjectRelationName = string.Empty,
        RelationshipCreatedOn = DateTimeOffset.UtcNow,
    };

    private static OperatorApprovalOptions Options(params (string Id, ECDsa Key)[] approvers) => new()
    {
        Mode = OperatorApprovalVerifier.SyntheticMode,
        Approvers = approvers.Select(a => new OperatorApproverOptions { ApproverId = a.Id, PublicKeyBase64 = Convert.ToBase64String(a.Key.ExportSubjectPublicKeyInfo()) }).ToList(),
    };

    private SecurityAdministratorProvisioningService Service(OperatorApprovalVerifier? verifier = null) =>
        new(store, verifier ?? OperatorApprovalVerifier.Create(Options(("approver-1", first), ("approver-2", second)), "Testing"), alerts, TimeProvider.System);

    private static FirstSecurityAdministratorRequest First(Guid target, bool dryRun, IReadOnlyList<string>? approvals = null, Guid? operation = null, Guid? confirm = null, string? subject = null) =>
        new(operation ?? Operation, confirm ?? Org, target, "https://idp", subject ?? Subject(target), "Organization has no Security Administrator (ticket OPS-1)", dryRun, approvals);

    private static readonly Guid Operation = Guid.CreateVersion7();

    /// <summary>An approval as an approver's tooling would produce it: ES256 over the statement digest returned by the dry run.</summary>
    private static string Sign(ECDsa key, string approverId, SecurityAdministratorProvisioningResponse preview, Action<Dictionary<string, object>>? tamper = null, DateTimeOffset? issuedAt = null, TimeSpan? lifetime = null, string alg = "ES256")
    {
        var iat = issuedAt ?? DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["iss"] = OperatorApprovalClaims.Issuer,
            ["aud"] = OperatorApprovalClaims.Audience,
            ["sub"] = approverId,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iat"] = iat.ToUnixTimeSeconds(),
            ["nbf"] = iat.AddSeconds(-5).ToUnixTimeSeconds(),
            ["exp"] = iat.Add(lifetime ?? TimeSpan.FromMinutes(10)).ToUnixTimeSeconds(),
            [OperatorApprovalClaims.StatementDigest] = preview.ApprovalStatementSha256,
            [OperatorApprovalClaims.OperationId] = preview.OperationId.ToString("D"),
            [OperatorApprovalClaims.OperationKind] = preview.OperationKind,
            [OperatorApprovalClaims.OrganizationId] = preview.OrganizationId.ToString("D"),
            [OperatorApprovalClaims.TargetAuthorityUserId] = preview.Target.AuthorityUserId.ToString("D"),
        };
        tamper?.Invoke(claims);
        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { alg, typ = "JWT", kid = approverId }));
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256);
        return header + "." + payload + "." + Base64Url.EncodeToString(signature);
    }

    private async Task<SecurityAdministratorProvisioningResponse> PreviewAsync(Guid target, Guid? operation = null) =>
        await Service().FirstAppointmentAsync(Org, First(target, dryRun: true, operation: operation), "ops-harness", Guid.NewGuid(), Ct);

    private static async Task<string> RejectedAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<AuthorityException>(action);
        return ex.ErrorCode;
    }

    private int AdministratorEdges() => store.Edges.Count(e => e.RelationshipName == OrganizationRoleNames.SystemAdministrator && e.RemovedBySecurityCommandId is null);

    private List<string> AuditEvents() => store.Journal.Where(j => j.JournalStreamName == "audit").Select(j => j.JournalEventKind).ToList();

    [Fact]
    public void SyntheticApprovalsCannotStartOutsideDevelopmentOrTestingAndNeedTwoDistinctP256Keys()
    {
        Assert.False(OperatorApprovalVerifier.Create(null, "Production").Operational);
        Assert.False(OperatorApprovalVerifier.Create(new OperatorApprovalOptions { Mode = "Unavailable" }, "Staging").Operational);
        foreach (var environment in new[] { "Production", "Staging", "Preproduction" })
        {
            Assert.Throws<InvalidOperationException>(() => OperatorApprovalVerifier.Create(Options(("a", first), ("b", second)), environment));
        }

        Assert.Throws<InvalidOperationException>(() => OperatorApprovalVerifier.Create(new OperatorApprovalOptions { Mode = "EntraPrivilegedApprovals" }, "Production")); // not implemented: refused
        Assert.Throws<InvalidOperationException>(() => OperatorApprovalVerifier.Create(Options(("a", first)), "Testing"));
        Assert.Throws<InvalidOperationException>(() => OperatorApprovalVerifier.Create(Options(("a", first), ("b", first)), "Testing")); // same key twice
        Assert.Throws<InvalidOperationException>(() => OperatorApprovalVerifier.Create(Options(("a", first), ("a", second)), "Testing"));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<InvalidOperationException>(() => OperatorApprovalVerifier.Create(Options(("a", first), ("b", p384)), "Testing"));
        var verifier = OperatorApprovalVerifier.Create(Options(("a", first), ("b", second)), "Development");
        Assert.True(verifier.Operational);
        Assert.Contains("not two-human", verifier.Assurance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADryRunShowsTheExactChangeAndChangesNothing()
    {
        var preview = await PreviewAsync(Member);
        Assert.True(preview.DryRun);
        Assert.True(preview.Eligible);
        Assert.Equal(Member, preview.Target.AuthorityUserId);
        Assert.Equal(Subject(Member), preview.Target.SubjectIdentifier);
        Assert.Equal(OrganizationRoleNames.SystemAdministrator, preview.RoleName);
        Assert.Empty(preview.CurrentAdministrators);
        Assert.Contains("\"organization_id\":\"" + Org.ToString("D") + "\"", preview.ApprovalStatementJson, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preview.ApprovalStatementJson))), preview.ApprovalStatementSha256);
        Assert.Contains(preview.Consequences, c => c.Contains("No Form Designer rights", StringComparison.Ordinal));
        Assert.Equal(0, AdministratorEdges());
        Assert.Empty(store.Commands);
        Assert.Empty(store.Journal);
    }

    [Fact]
    public async Task TwoIndependentApprovalsAppointTheFirstAdministratorOnceWithEvidenceAndAnAlert()
    {
        var preview = await PreviewAsync(Member);
        var approvals = new[] { Sign(first, "approver-1", preview), Sign(second, "approver-2", preview) };
        var outcome = await Service().FirstAppointmentAsync(Org, First(Member, dryRun: false, approvals), "ops-harness", Guid.NewGuid(), Ct);
        Assert.False(outcome.DryRun);
        Assert.False(outcome.Replayed);
        Assert.NotNull(outcome.SecurityCommandId);
        Assert.Equal(1, AdministratorEdges());
        var command = Assert.Single(store.Commands);
        Assert.Equal("ProvisionFirstSecurityAdministrator", command.CommandKindName);
        Assert.Equal(5, command.CommittedAuthorizationRevision);
        Assert.Contains("\"approver_id\":\"approver-1\"", command.CommandPayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"approver_id\":\"approver-2\"", command.CommandPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(approvals[0], command.CommandPayloadJson, StringComparison.Ordinal); // digests only, never the approvals
        Assert.Equal(["SecurityAdministratorFirstAppointed", "SecurityAlertRaised"], AuditEvents());
        Assert.Contains("not two-human", store.Journal.First(j => j.JournalEventKind == "SecurityAdministratorFirstAppointed").JournalEventBodyJson, StringComparison.Ordinal);
        Assert.Equal("Warning", Assert.Single(alerts.Raised).Severity);

        // Replay: the same operation and statement return the recorded outcome; nothing new happens, even with stale approvals.
        var replay = await Service().FirstAppointmentAsync(Org, First(Member, dryRun: false, approvals), "ops-harness", Guid.NewGuid(), Ct);
        Assert.True(replay.Replayed);
        Assert.Equal(outcome.SecurityCommandId, replay.SecurityCommandId);
        Assert.Single(store.Commands);
        Assert.Equal(1, AdministratorEdges());

        // The same operation id for a different member is a conflict, not a second appointment.
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, await RejectedAsync(() => Service().FirstAppointmentAsync(Org, First(Second, dryRun: false, approvals), "ops-harness", Guid.NewGuid(), Ct)));

        // A new operation while an administrator exists is refused (and journaled).
        var another = Guid.CreateVersion7();
        var secondPreview = await PreviewAsync(Second, another);
        Assert.False(secondPreview.Eligible);
        Assert.Equal(AuthorityErrorCodes.SecurityAdministratorExists, secondPreview.RefusalCode);
        Assert.Equal(AuthorityErrorCodes.SecurityAdministratorExists, await RejectedAsync(() => Service().FirstAppointmentAsync(Org, First(Second, false, [Sign(first, "approver-1", secondPreview), Sign(second, "approver-2", secondPreview)], another), "ops-harness", Guid.NewGuid(), Ct)));
        Assert.Equal(1, AdministratorEdges());
        Assert.Contains("SecurityAdministratorProvisioningRejected", AuditEvents());
    }

    [Fact]
    public async Task ADisabledAdministratorIsNotUsableSoAFirstAppointmentIsAllowed()
    {
        store.Edges.Add(Edge(OrganizationRoleNames.SystemAdministrator, Second));
        store.Bindings.Single(b => b.AuthorityUserId == Second).IdentityBindingEnabled = false;
        Assert.True((await PreviewAsync(Member)).Eligible);
    }

    [Fact]
    public async Task EveryApprovalDefectIsRefusedAndNothingChanges()
    {
        var preview = await PreviewAsync(Member);
        var good1 = Sign(first, "approver-1", preview);
        var good2 = Sign(second, "approver-2", preview);
        async Task<string> Attempt(params string[] approvals) => await RejectedAsync(() => Service().FirstAppointmentAsync(Org, First(Member, false, approvals), "ops-harness", Guid.NewGuid(), Ct));

        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt());
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1));
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalsNotIndependent, await Attempt(good1, Sign(first, "approver-1", preview))); // same approver twice
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, Sign(forged, "approver-2", preview))); // forged with approver-2's id
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, Sign(forged, "approver-3", preview))); // unregistered approver
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, Sign(second, "approver-2", preview, issuedAt: DateTimeOffset.UtcNow.AddMinutes(-20), lifetime: TimeSpan.FromMinutes(5)))); // expired
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, Sign(second, "approver-2", preview, lifetime: TimeSpan.FromHours(2)))); // too long-lived
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, Sign(second, "approver-2", preview, c => c[OperatorApprovalClaims.OrganizationId] = OtherOrg.ToString("D"))));
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, Sign(second, "approver-2", preview, c => c[OperatorApprovalClaims.TargetAuthorityUserId] = Second.ToString("D"))));
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, Sign(second, "approver-2", preview, c => c[OperatorApprovalClaims.OperationKind] = SecurityAdministratorProvisioningKinds.Recovery)));
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, Sign(second, "approver-2", preview, c => c[OperatorApprovalClaims.StatementDigest] = new string('0', 64))));
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, Sign(second, "approver-2", preview, alg: "ES384")));
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, good2[..^4] + "AAAA")); // tampered signature
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, "not.a.token"));

        // An approval for another organization's statement cannot be reused here either (different digest).
        var elsewhere = preview with { OrganizationId = OtherOrg, ApprovalStatementSha256 = new string('a', 64) };
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await Attempt(good1, Sign(second, "approver-2", elsewhere)));

        // Where dual control is unavailable (staging/production), even valid approvals are refused.
        Assert.Equal(AuthorityErrorCodes.OperatorDualControlUnavailable, await RejectedAsync(() => Service(OperatorApprovalVerifier.Unavailable).FirstAppointmentAsync(Org, First(Member, false, [good1, good2]), "ops-harness", Guid.NewGuid(), Ct)));

        Assert.Equal(0, AdministratorEdges());
        Assert.Empty(store.Commands);
        Assert.All(AuditEvents(), e => Assert.Equal("SecurityAdministratorProvisioningRejected", e));
        Assert.Empty(alerts.Raised); // first-appointment refusals are journaled; alerts are for executions and refused recoveries
    }

    [Fact]
    public async Task TheTargetMustBeAnEnabledVerifiedMemberOfThisOrganizationWithThatIdentity()
    {
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Service().FirstAppointmentAsync(Org, First(Guid.CreateVersion7(), true), "ops", Guid.NewGuid(), Ct)).RefusalCode); // not a member
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Service().FirstAppointmentAsync(Org, First(Member, true, subject: "someone-else"), "ops", Guid.NewGuid(), Ct)).RefusalCode);
        store.Bindings.Single(b => b.AuthorityUserId == Second).IdentityBindingEnabled = false;
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await PreviewAsync(Second)).RefusalCode);
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RejectedAsync(() => Service().FirstAppointmentAsync(Org, First(Member, true, confirm: OtherOrg), "ops", Guid.NewGuid(), Ct)));
        Assert.Equal(AuthorityErrorCodes.NotFound, await RejectedAsync(() => Service().FirstAppointmentAsync(OtherOrg, First(Member, true, confirm: OtherOrg), "ops", Guid.NewGuid(), Ct)));
    }

    [Fact]
    public async Task RecoveryIsSeparateNeedsAnIncidentHasAnExplicitScopeAndRaisesACriticalAlert()
    {
        var operation = Guid.CreateVersion7();
        SecurityAdministratorRecoveryRequest Recovery(bool dryRun, string incident = "INC-2026-0042", IReadOnlyList<Guid>? removed = null, IReadOnlyList<string>? approvals = null) =>
            new(operation, Org, Member, "https://idp", Subject(Member), "Both administrators left; passkeys lost (incident)", incident, removed ?? [Second], dryRun, approvals);

        // No administrator at all: recovery is not the operation to use.
        Assert.Equal(AuthorityErrorCodes.RecoveryNotApplicable, (await Service().RecoverAsync(Org, Recovery(true, removed: []), "ops", Guid.NewGuid(), Ct)).RefusalCode);
        store.Edges.Add(Edge(OrganizationRoleNames.SystemAdministrator, Second));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RejectedAsync(() => Service().RecoverAsync(Org, Recovery(true, incident: ""), "ops", Guid.NewGuid(), Ct)));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RejectedAsync(() => Service().RecoverAsync(Org, Recovery(true, removed: [Member]), "ops", Guid.NewGuid(), Ct))); // never the appointee
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Service().RecoverAsync(Org, Recovery(true, removed: [Owner]), "ops", Guid.NewGuid(), Ct)).RefusalCode); // not an administrator

        // A first appointment is refused here (a usable administrator exists), so recovery is the separate path.
        Assert.Equal(AuthorityErrorCodes.SecurityAdministratorExists, (await PreviewAsync(Member)).RefusalCode);

        var preview = await Service().RecoverAsync(Org, Recovery(true), "ops", Guid.NewGuid(), Ct);
        Assert.True(preview.Eligible);
        Assert.Contains("\"incident_reference\":\"INC-2026-0042\"", preview.ApprovalStatementJson, StringComparison.Ordinal);
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await RejectedAsync(() => Service().RecoverAsync(Org, Recovery(false, approvals: [Sign(first, "approver-1", preview), Sign(second, "approver-2", preview, c => c[OperatorApprovalClaims.OperationKind] = SecurityAdministratorProvisioningKinds.FirstAppointment)]), "ops", Guid.NewGuid(), Ct)));
        Assert.Equal("Warning", Assert.Single(alerts.Raised).Severity); // refused recovery alerts too

        var outcome = await Service().RecoverAsync(Org, Recovery(false, approvals: [Sign(first, "approver-1", preview), Sign(second, "approver-2", preview)]), "ops", Guid.NewGuid(), Ct);
        Assert.NotNull(outcome.SecurityCommandId);
        Assert.Equal([Member], outcome.CurrentAdministrators.Select(a => a.AuthorityUserId));
        Assert.Equal("RecoverSecurityAdministrator", store.Commands.Single(c => c.CommandStateName != SecurityCommandStates.Rejected).CommandKindName);
        Assert.Contains("SecurityAdministratorRecovered", AuditEvents());
        Assert.Equal("Critical", alerts.Raised[^1].Severity);
        Assert.Contains("INC-2026-0042", store.Journal.First(j => j.JournalEventKind == "SecurityAdministratorRecovered").JournalEventBodyJson, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        first.Dispose();
        second.Dispose();
        forged.Dispose();
    }

    private sealed class RecordingAlerts : ISecurityAlertSink
    {
        public List<SecurityAlert> Raised { get; } = [];

        public void Raise(SecurityAlert alert) => Raised.Add(alert);
    }
}
