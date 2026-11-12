using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C8 M4 (AC-8.1, C-110) through the real command service over the in-memory store: Security Auditor appointment and removal
/// (rules A1–A9), the owner-approved reason codes, the undisclosed note, the D8-62a self-change refusal and the evidence a
/// refused command may leave. SpiceDB permission (view_audit grants nothing else), the business API and the People page run
/// on Docker in the Stage B suite (SecurityAuditorTests there).
/// </summary>
public sealed class SecurityAuditorTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Administrator = Guid.CreateVersion7();
    private static readonly Guid SecondAdministrator = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Designer = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Auditor = Guid.CreateVersion7();
    private const string Issuer = "https://idp.test.invalid";
    private const string Note = "confidential context: ticket SEC-4471 about a named colleague";
    private static readonly FieldSearchApprovalGate StepUp = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");

    private readonly InMemoryAuthorityStore store = new();

    public SecurityAuditorTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, OrganizationDisplayName = "Audit Org", AuthorizationRevision = 4, ProjectionAppliedRevision = 4 });
        foreach (var user in new[] { Administrator, SecondAdministrator, Owner, Designer, Member, Auditor })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = "subject-" + user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.Organization, ResourceEntityId = Org, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(Edge(OrganizationRoleNames.SystemAdministrator, Administrator));
        store.Edges.Add(Edge(OrganizationRoleNames.SystemAdministrator, SecondAdministrator));
        store.Edges.Add(Edge(OrganizationRoleNames.OrganizationOwner, Owner));
        store.Edges.Add(Edge(OrganizationRoleNames.Designer, Designer));
        store.Edges.Add(Edge(OrganizationRoleNames.SecurityAuditor, Auditor));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthorityRelationshipEdge Edge(string relation, Guid user) => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.Organization, ResourceEntityId = Org, RelationshipName = relation,
        SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = user, SubjectRelationName = string.Empty, RelationshipCreatedOn = DateTimeOffset.UtcNow,
    };

    private sealed class NoFaults : IFaultInjector
    {
        public void CrashIfArmed(string faultName)
        {
        }
    }

    private long Revision => store.Organizations.Single().AuthorizationRevision;

    private bool Holds(Guid user, string role) => store.Edges.Any(e => e.RemovedBySecurityCommandId is null && e.ResourceEntityId == Org && e.RelationshipName == role && e.SubjectEntityId == user);

    private Task<OperationStatus> SubmitAsync(Guid actor, SecurityCommandKind kind, object payload, string? changeReason = null, FieldSearchApprovalGate? gate = null, long? expectedRevision = null, string? idempotencyKey = null) =>
        new SecurityCommandService(store, new NoFaults(), TimeProvider.System, gate ?? StepUp).SubmitAsync(new VerifiedActor(Issuer, "subject-" + actor.ToString("N"), "test"),
            new SecurityCommandRequest(Org, kind, new ResourceRef(ResourceKind.Organization, Org), expectedRevision ?? Revision, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options), changeReason),
            idempotencyKey ?? "auditor-" + Guid.NewGuid().ToString("N"), Guid.CreateVersion7(), Ct);

    private Task<OperationStatus> AppointAsync(Guid actor, Guid target, string code = SecurityAuditorReasonCodes.AuditCoverageAssignment, string? note = null, string? stepUp = FieldSearchApprovalGate.SyntheticStepUpEvidence, FieldSearchApprovalGate? gate = null) =>
        SubmitAsync(actor, SecurityCommandKind.GrantSystemRole, new SecurityAuditorPayload(target, OrganizationRoleNames.SecurityAuditor, code, note, stepUp), changeReason: note, gate: gate);

    private Task<OperationStatus> RemoveAsync(Guid actor, Guid target, string code = SecurityAuditorReasonCodes.AssignmentEnded, string? note = null) =>
        SubmitAsync(actor, SecurityCommandKind.RevokeSystemRole, new SecurityAuditorPayload(target, OrganizationRoleNames.SecurityAuditor, code, note, FieldSearchApprovalGate.SyntheticStepUpEvidence), changeReason: note);

    private static async Task<string> RefusedAsync(Func<Task> action) => (await Assert.ThrowsAsync<AuthorityException>(action)).ErrorCode;

    private SecurityCommand LastCommand => store.Commands[^1];

    private List<SecurityJournalEntry> AuditStream(string kind) => store.Journal.Where(j => j.JournalStreamName == JournalStreamNames.Audit && j.JournalEventKind == kind).ToList();

    [Fact]
    public async Task ASecurityAdministratorAppointsAnAuditorWithAReasonCodeAndOnlyTheAuditorRelationChanges()
    {
        var before = Revision;
        var status = await SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole,
            new SecurityAuditorPayload(Member, OrganizationRoleNames.SecurityAuditor, SecurityAuditorReasonCodes.InvestigationSupport, null, FieldSearchApprovalGate.SyntheticStepUpEvidence), changeReason: "free-text change reason " + Note);
        Assert.Equal(before + 1, status.AuthorizationRevision);
        Assert.True(Holds(Member, OrganizationRoleNames.SecurityAuditor));
        foreach (var other in new[] { OrganizationRoleNames.SystemAdministrator, OrganizationRoleNames.OrganizationOwner, OrganizationRoleNames.Designer })
        {
            Assert.False(Holds(Member, other));
        }

        // Explicit journal evidence: kind, role, target and the structured code; never the caller's change_reason text.
        var command = LastCommand;
        Assert.Equal("GrantSystemRole", command.CommandKindName);
        Assert.Equal(SecurityAuditorReasonCodes.InvestigationSupport, command.CommandReasonText); // never the caller's change_reason
        using var payload = JsonDocument.Parse(command.CommandPayloadJson);
        Assert.Equal(OrganizationRoleNames.SecurityAuditor, payload.RootElement.GetProperty("role_name").GetString());
        Assert.Equal(Member, payload.RootElement.GetProperty("authority_user_id").GetGuid());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("reason_note").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("step_up_evidence").ValueKind);
        var journal = store.Journal.Single(j => j.JournalStreamName == JournalStreamNames.Authorization && j.SecurityCommandId == command.SecurityCommandId);
        Assert.Contains("\"reason_code\":\"InvestigationSupport\"", journal.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain(FieldSearchApprovalGate.SyntheticStepUpEvidence, journal.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SEC-4471", journal.JournalEventBodyJson + command.CommandPayloadJson, StringComparison.Ordinal);
    }

    /// <summary>D8-72 (kept permanently by M1, C-117): a plaintext note is refused whatever its content and leaves nothing behind.</summary>
    [Fact]
    public async Task APlaintextReasonNoteIsRefusedPermanentlyAndNothingOfItIsPersisted()
    {
        var attempts = new (SecurityCommandKind Kind, object Payload)[]
        {
            (SecurityCommandKind.GrantSystemRole, new { authority_user_id = Member, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.ComplianceReview, reason_note = Note, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }),
            (SecurityCommandKind.GrantSystemRole, new { authority_user_id = Member, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.ComplianceReview, reason_note = "   ", step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }),
            (SecurityCommandKind.GrantSystemRole, new { authority_user_id = Member, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.ComplianceReview, reason_note = Note, reason_note_envelope = Envelope(), step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }),
            (SecurityCommandKind.RevokeSystemRole, new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note = Note, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }),
        };
        var revision = Revision;
        foreach (var (kind, payload) in attempts)
        {
            Assert.Equal(AuthorityErrorCodes.ReasonNoteNotAccepted, await RefusedAsync(() => SubmitAsync(Administrator, kind, payload, changeReason: Note)));
        }

        Assert.Equal(revision, Revision); // nothing committed
        Assert.False(Holds(Member, OrganizationRoleNames.SecurityAuditor));
        Assert.True(Holds(Auditor, OrganizationRoleNames.SecurityAuditor));
        var rejectedRows = store.Commands.Where(c => c.CommandStateName == SecurityCommandStates.Rejected).ToList();
        Assert.Equal(attempts.Length, rejectedRows.Count);
        var evidence = AuditStream("SecurityAuditorCommandRejected");
        Assert.Equal(attempts.Length, evidence.Count);
        var everything = string.Join(" ", rejectedRows.Select(c => c.CommandPayloadJson + " " + c.CommandReasonText).Concat(evidence.Select(e => e.JournalEventBodyJson)));
        foreach (var secret in new[] { "SEC-4471", "reason_note", "ciphertext_b64", "wrapped_data_key_b64", FieldSearchApprovalGate.SyntheticStepUpEvidence })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }

        Assert.All(evidence, e => Assert.Contains(AuthorityErrorCodes.ReasonNoteNotAccepted, e.JournalEventBodyJson, StringComparison.Ordinal));

        // Commands without a note (absent or explicitly null) keep working.
        await AppointAsync(Administrator, Member, SecurityAuditorReasonCodes.ComplianceReview);
        // A null envelope and a null plaintext note are both simply no note (V3 as amended in C-117 and approved in C-118).
        await SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole, new { authority_user_id = Designer, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.ComplianceReview, reason_note_envelope = (object?)null, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence });
        Assert.True(Holds(Designer, OrganizationRoleNames.SecurityAuditor));
        await SubmitAsync(Administrator, SecurityCommandKind.RevokeSystemRole, new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note = (string?)null, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence });
        Assert.True(Holds(Member, OrganizationRoleNames.SecurityAuditor));
        Assert.False(Holds(Auditor, OrganizationRoleNames.SecurityAuditor));
    }

    /// <summary>A structurally valid sealed note (random bytes: the Authority cannot and does not decrypt; V4–V9 are structural).</summary>
    private static object Envelope(Guid? noteId = null, int ciphertextBytes = 2048, string format = "DynamicForms/AuditorReasonNote", string keyReference = "local-dev-kek-v1") => new
    {
        envelope_format = format,
        envelope_version = 1,
        algorithm_name = "AES-256-GCM",
        key_reference = keyReference,
        note_id = (noteId ?? Guid.CreateVersion7()).ToString("D"),
        nonce_b64 = Convert.ToBase64String(new byte[12]),
        ciphertext_b64 = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(ciphertextBytes)),
        tag_b64 = Convert.ToBase64String(new byte[16]),
        wrapped_data_key_b64 = Convert.ToBase64String(new byte[60]),
    };

    /// <summary>
    /// C8 M4 M1 (C-117, A-Q2): a sealed note is accepted, kept only as ciphertext in the normalized payload, the authorization
    /// journal and (through it) the archive; plaintext notes stay refused permanently (V2); every structural rule V3–V11 refuses.
    /// </summary>
    [Fact]
    public async Task ASealedNoteIsAcceptedAsCiphertextOnlyAndEveryEnvelopeRuleIsEnforced()
    {
        var noteId = Guid.CreateVersion7();
        var envelope = Envelope(noteId);
        await SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole, new { authority_user_id = Member, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.ComplianceReview, reason_note_envelope = envelope, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence });
        Assert.True(Holds(Member, OrganizationRoleNames.SecurityAuditor));
        using (var payload = JsonDocument.Parse(LastCommand.CommandPayloadJson))
        {
            Assert.Equal(noteId.ToString("D"), payload.RootElement.GetProperty("reason_note_envelope").GetProperty("note_id").GetString());
            Assert.Equal(2048, Convert.FromBase64String(payload.RootElement.GetProperty("reason_note_envelope").GetProperty("ciphertext_b64").GetString()!).Length);
        }

        var journal = store.Journal.Single(j => j.JournalStreamName == JournalStreamNames.Authorization && j.SecurityCommandId == LastCommand.SecurityCommandId);
        Assert.Contains("\"note_id\":\"" + noteId.ToString("D") + "\"", journal.JournalEventBodyJson, StringComparison.Ordinal);

        // V10: the same note id cannot be attached to another command.
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => SubmitAsync(Administrator, SecurityCommandKind.RevokeSystemRole, new { authority_user_id = Member, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = Envelope(noteId), step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence })));

        var refused = new (string Rule, object Payload, string Code)[]
        {
            ("V2 plaintext", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note = Note, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.ReasonNoteNotAccepted),
            ("V3 not an object", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = "sealed note as a string", step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.InvalidCommand),
            ("V4 format", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = Envelope(format: "Other"), step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.InvalidCommand),
            ("V5 key reference", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = Envelope(keyReference: "bad key ref"), step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.InvalidCommand),
            ("V6 note id", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = Envelope(Guid.NewGuid()), step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.InvalidCommand),
            ("V8 size", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = Envelope(ciphertextBytes: 2000), step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.InvalidCommand),
        };
        foreach (var (rule, payload, code) in refused)
        {
            Assert.True(code == await RefusedAsync(() => SubmitAsync(Administrator, SecurityCommandKind.RevokeSystemRole, payload)), rule);
        }

        // Rejected rows and audit-stream evidence never keep envelope bytes or the plaintext.
        var everything = string.Join(" ", store.Commands.Where(c => c.CommandStateName == SecurityCommandStates.Rejected).Select(c => c.CommandPayloadJson + " " + c.CommandReasonText).Concat(AuditStream("SecurityAuditorCommandRejected").Select(e => e.JournalEventBodyJson)));
        foreach (var secret in new[] { "SEC-4471", "ciphertext_b64", "wrapped_data_key_b64", noteId.ToString("D") })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// V3 as approved by the owner (C-118): an omitted envelope and an explicit JSON null are the same "no note". Both commit, and the
    /// normalized payload and the authorization journal body are identical apart from the target. Every non-null value that is not a
    /// valid envelope is refused, and a plaintext note is refused even when the envelope is null.
    /// </summary>
    [Fact]
    public async Task AnOmittedAndANullEnvelopeAreEquivalentAndEveryOtherNonEnvelopeValueIsRefused()
    {
        string Normalized(Guid target)
        {
            var command = store.Commands.Last(c => c.CommandStateName != SecurityCommandStates.Rejected);
            var journal = store.Journal.Single(j => j.JournalStreamName == JournalStreamNames.Authorization && j.SecurityCommandId == command.SecurityCommandId);
            using var payload = JsonDocument.Parse(command.CommandPayloadJson);
            Assert.Equal(target, payload.RootElement.GetProperty("authority_user_id").GetGuid());
            // Only per-command values differ between the two journal bodies: ids, sequence numbers, digests and times.
            var body = journal.JournalEventBodyJson.Replace(target.ToString("D"), "<target>", StringComparison.Ordinal);
            body = System.Text.RegularExpressions.Regex.Replace(body, "\"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\"", "\"<id>\"");
            body = System.Text.RegularExpressions.Regex.Replace(body, "\"([a-z_]*revision)\":[0-9]+", "\"$1\":<n>");
            body = System.Text.RegularExpressions.Regex.Replace(body, "\"([a-z_]*digest[a-z_]*)\":(null|\"[^\"]*\")", "\"$1\":<v>"); // the first entry of a stream has no previous digest
            body = System.Text.RegularExpressions.Regex.Replace(body, "\"[0-9a-fA-F]{64}\"|\"[0-9]{4}-[0-9]{2}-[0-9]{2}T[^\"]*\"", "\"<v>\"");
            return command.CommandPayloadJson.Replace(target.ToString("D"), "<target>", StringComparison.Ordinal) + "\n" + journal.JournalEventKind + "\n" + body;
        }

        await SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole, new { authority_user_id = Member, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.ComplianceReview, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence });
        var omitted = Normalized(Member);
        await SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole, new { authority_user_id = Designer, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.ComplianceReview, reason_note = (string?)null, reason_note_envelope = (object?)null, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence });
        var explicitNull = Normalized(Designer);
        Assert.True(Holds(Member, OrganizationRoleNames.SecurityAuditor) && Holds(Designer, OrganizationRoleNames.SecurityAuditor));
        Assert.Equal(omitted, explicitNull);
        Assert.DoesNotContain("ciphertext_b64", omitted, StringComparison.Ordinal);

        var revision = Revision;
        var refused = new (string Case, object Payload, string Code)[]
        {
            ("number", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = 0, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.InvalidCommand),
            ("boolean", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = false, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.InvalidCommand),
            ("empty string", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = string.Empty, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.InvalidCommand),
            ("array", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = Array.Empty<object>(), step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.InvalidCommand),
            ("empty object", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note_envelope = new { }, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.InvalidCommand),
            ("plaintext with a null envelope", new { authority_user_id = Auditor, role_name = OrganizationRoleNames.SecurityAuditor, reason_code = SecurityAuditorReasonCodes.AssignmentEnded, reason_note = Note, reason_note_envelope = (object?)null, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }, AuthorityErrorCodes.ReasonNoteNotAccepted),
        };
        foreach (var (name, payload, code) in refused)
        {
            Assert.True(code == await RefusedAsync(() => SubmitAsync(Administrator, SecurityCommandKind.RevokeSystemRole, payload)), name);
        }

        Assert.Equal(revision, Revision);
        Assert.True(Holds(Auditor, OrganizationRoleNames.SecurityAuditor));
    }

    [Fact]
    public async Task OnlyAnEnabledSecurityAdministratorMayAppointOrRemoveAnAuditor()
    {
        foreach (var actor in new[] { Owner, Designer, Member, Auditor })
        {
            Assert.Equal(AuthorityErrorCodes.Forbidden, await RefusedAsync(() => AppointAsync(actor, Designer)));
            Assert.Equal(AuthorityErrorCodes.Forbidden, await RefusedAsync(() => AppointAsync(actor, actor)));
            Assert.Equal(AuthorityErrorCodes.Forbidden, await RefusedAsync(() => RemoveAsync(actor, Auditor)));
        }

        Assert.True(Holds(Auditor, OrganizationRoleNames.SecurityAuditor));
        Assert.False(Holds(Designer, OrganizationRoleNames.SecurityAuditor));
    }

    [Fact]
    public async Task NobodyAppointsOrRemovesThemselfForEitherRoleIncludingADualHolder()
    {
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => AppointAsync(Administrator, Administrator)));
        await AppointAsync(SecondAdministrator, Administrator); // another administrator may make them a dual holder
        Assert.True(Holds(Administrator, OrganizationRoleNames.SecurityAuditor) && Holds(Administrator, OrganizationRoleNames.SystemAdministrator));
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => RemoveAsync(Administrator, Administrator)));
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => SubmitAsync(Administrator, SecurityCommandKind.RevokeSystemRole,
            new SecurityAdministratorPayload(Administrator, OrganizationRoleNames.SystemAdministrator, "stepping down", FieldSearchApprovalGate.SyntheticStepUpEvidence))));
        Assert.True(Holds(Administrator, OrganizationRoleNames.SecurityAuditor) && Holds(Administrator, OrganizationRoleNames.SystemAdministrator));
        await RemoveAsync(SecondAdministrator, Administrator);
        Assert.False(Holds(Administrator, OrganizationRoleNames.SecurityAuditor));
    }

    [Fact]
    public async Task ReasonCodesAreExactCaseSensitiveAndDirectionSpecific()
    {
        foreach (var code in new[] { "auditCoverageAssignment", "AUDIT_COVERAGE_ASSIGNMENT", "AssignmentEnded", "Other", " AuditCoverageAssignment", "" })
        {
            Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => AppointAsync(Administrator, Member, code)));
        }

        foreach (var code in new[] { "assignmentEnded", "AuditCoverageAssignment", "Other" })
        {
            Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => RemoveAsync(Administrator, Auditor, code)));
        }

        foreach (var code in SecurityAuditorReasonCodes.Appointment)
        {
            var target = Guid.CreateVersion7();
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = target, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = "subject-" + target.ToString("N"), IdentityBindingEnabled = true });
            await AppointAsync(Administrator, target, code);
            await RemoveAsync(Administrator, target, SecurityAuditorReasonCodes.Removal[SecurityAuditorReasonCodes.Appointment.ToList().IndexOf(code)]);
        }

        Assert.Equal(["AuditCoverageAssignment", "InvestigationSupport", "ReplacementOfDepartingAuditor", "ComplianceReview"], SecurityAuditorReasonCodes.Appointment);
        Assert.Equal(["AssignmentEnded", "RoleChange", "MembershipEnding", "ConflictOfInterest", "SecurityConcern"], SecurityAuditorReasonCodes.Removal);
    }

    [Fact]
    public async Task ThePayloadShapesOfTheTwoRolesCannotBeMixed()
    {
        // A free-text reason for an auditor, or a reason code for an administrator, is rejected by strict binding.
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole,
            new { authority_user_id = Member, role_name = OrganizationRoleNames.SecurityAuditor, reason = "free text", reason_code = SecurityAuditorReasonCodes.ComplianceReview, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence })));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole,
            new { authority_user_id = Member, role_name = OrganizationRoleNames.SystemAdministrator, reason = "governance", reason_code = SecurityAuditorReasonCodes.ComplianceReview, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence })));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole,
            new { authority_user_id = Member, role_name = OrganizationRoleNames.SecurityAuditor, step_up_evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence }))); // code missing
        Assert.False(Holds(Member, OrganizationRoleNames.SystemAdministrator));
        Assert.False(Holds(Member, OrganizationRoleNames.SecurityAuditor));
    }

    [Fact]
    public async Task TargetMustBeAnEnabledBindingOfThisOrganizationAndRemovalHasNoLastHolderRule()
    {
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => AppointAsync(Administrator, Guid.CreateVersion7()))); // another organization's user, or nobody
        store.Bindings.Single(b => b.AuthorityUserId == Designer).IdentityBindingEnabled = false;
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => AppointAsync(Administrator, Designer)));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => AppointAsync(Administrator, Auditor))); // already one
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => RemoveAsync(Administrator, Member))); // not one

        // A disabled auditor can be removed, and the organization may end with no auditor (A3, A5).
        store.Bindings.Single(b => b.AuthorityUserId == Auditor).IdentityBindingEnabled = false;
        await RemoveAsync(Administrator, Auditor, SecurityAuditorReasonCodes.MembershipEnding);
        Assert.DoesNotContain(store.Edges, e => e.RemovedBySecurityCommandId is null && e.RelationshipName == OrganizationRoleNames.SecurityAuditor);
    }

    [Fact]
    public async Task StepUpIsRequiredAndUnavailableInStagingAndProduction()
    {
        Assert.Equal("STEP_UP_REQUIRED", await RefusedAsync(() => AppointAsync(Administrator, Member, stepUp: null)));
        Assert.Equal("STEP_UP_REQUIRED", await RefusedAsync(() => AppointAsync(Administrator, Member, stepUp: "passkey-ok")));
        Assert.Equal("STEP_UP_REQUIRED", await RefusedAsync(() => AppointAsync(Administrator, Member, gate: FieldSearchApprovalGate.Unavailable)));
        Assert.False(Holds(Member, OrganizationRoleNames.SecurityAuditor));
    }

    [Fact]
    public async Task StaleAuthorizationStateAndIdempotencyReplayWithADifferentPayloadAreRefused()
    {
        var stale = Revision - 1;
        Assert.Equal(AuthorityErrorCodes.StaleAuthorizationRevision, await RefusedAsync(() => SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole,
            new SecurityAuditorPayload(Member, OrganizationRoleNames.SecurityAuditor, SecurityAuditorReasonCodes.ComplianceReview, null, FieldSearchApprovalGate.SyntheticStepUpEvidence), expectedRevision: stale)));
        Assert.False(Holds(Member, OrganizationRoleNames.SecurityAuditor));

        const string key = "auditor-replay";
        await SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole, new SecurityAuditorPayload(Member, OrganizationRoleNames.SecurityAuditor, SecurityAuditorReasonCodes.ComplianceReview, null, FieldSearchApprovalGate.SyntheticStepUpEvidence), idempotencyKey: key);
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, await RefusedAsync(() => SubmitAsync(Administrator, SecurityCommandKind.GrantSystemRole,
            new SecurityAuditorPayload(Designer, OrganizationRoleNames.SecurityAuditor, SecurityAuditorReasonCodes.ComplianceReview, null, FieldSearchApprovalGate.SyntheticStepUpEvidence), idempotencyKey: key)));
        Assert.False(Holds(Designer, OrganizationRoleNames.SecurityAuditor));
    }

    /// <summary>C-110: a refused auditor command leaves only the approved fields — on its row and in its audit-stream entry.</summary>
    [Fact]
    public async Task RejectionEvidenceHoldsOnlyApprovedFieldsAndNeverTheNoteStepUpOrAnInvalidValue()
    {
        const string invalidCode = "MyOwnReasonWithSecret-SEC-4471";
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => AppointAsync(Administrator, Member, invalidCode)));
        var invalid = LastCommand;
        Assert.Equal(SecurityCommandStates.Rejected, invalid.CommandStateName);
        Assert.Null(invalid.CommandReasonText);
        var rejection = Assert.Single(AuditStream("SecurityAuditorCommandRejected"));
        foreach (var text in new[] { invalid.CommandPayloadJson, rejection.JournalEventBodyJson })
        {
            Assert.DoesNotContain(invalidCode, text, StringComparison.Ordinal);
            Assert.DoesNotContain("SEC-4471", text, StringComparison.Ordinal);
            Assert.DoesNotContain(FieldSearchApprovalGate.SyntheticStepUpEvidence, text, StringComparison.Ordinal);
            Assert.DoesNotContain("reason_note", text, StringComparison.Ordinal);
        }

        using (var body = JsonDocument.Parse(rejection.JournalEventBodyJson))
        {
            var root = body.RootElement.GetProperty("body");
            Assert.Equal(["security_command_id", "command_kind", "actor_identity_binding_id", "target_authority_user_id", "role_name", "rejection_reason", "reason_code", "reason_code_valid", "correlation_id"],
                root.EnumerateObject().Select(p => p.Name).ToList());
            Assert.False(root.GetProperty("reason_code_valid").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("reason_code").ValueKind);
            Assert.Equal(Member, root.GetProperty("target_authority_user_id").GetGuid());
            Assert.Equal(AuthorityErrorCodes.InvalidCommand, root.GetProperty("rejection_reason").GetString());
        }

        // A valid code refused for another reason (self-change) is recorded.
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => AppointAsync(Administrator, Administrator, SecurityAuditorReasonCodes.ComplianceReview)));
        var self = AuditStream("SecurityAuditorCommandRejected")[^1];
        Assert.Contains("\"reason_code\":\"ComplianceReview\"", self.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.Contains("\"reason_code_valid\":true", self.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.Contains(AuthorityErrorCodes.SelfChangeForbidden, self.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SEC-4471", self.JournalEventBodyJson + LastCommand.CommandPayloadJson + LastCommand.CommandReasonText, StringComparison.Ordinal);
        Assert.Equal(SecurityAuditorReasonCodes.ComplianceReview, LastCommand.CommandReasonText);
        Assert.Empty(AuditStream("SecurityAdministratorCommandRejected"));
    }

    [Fact]
    public async Task RefusedAdministratorCommandsKeepTheirEvidenceShapeButNoLongerStoreTheRawBody()
    {
        await RefusedAsync(() => SubmitAsync(Owner, SecurityCommandKind.GrantSystemRole,
            new SecurityAdministratorPayload(Member, OrganizationRoleNames.SystemAdministrator, "private justification text", FieldSearchApprovalGate.SyntheticStepUpEvidence), changeReason: "private justification text"));
        var rejected = LastCommand;
        Assert.DoesNotContain("private justification text", rejected.CommandPayloadJson + rejected.CommandReasonText, StringComparison.Ordinal);
        Assert.DoesNotContain(FieldSearchApprovalGate.SyntheticStepUpEvidence, rejected.CommandPayloadJson, StringComparison.Ordinal);
        var entry = Assert.Single(AuditStream("SecurityAdministratorCommandRejected"));
        using var body = JsonDocument.Parse(entry.JournalEventBodyJson);
        Assert.Equal(["security_command_id", "command_kind", "actor_identity_binding_id", "rejection_reason", "correlation_id"], body.RootElement.GetProperty("body").EnumerateObject().Select(p => p.Name).ToList());
        Assert.Empty(AuditStream("SecurityAuditorCommandRejected"));
    }
}
