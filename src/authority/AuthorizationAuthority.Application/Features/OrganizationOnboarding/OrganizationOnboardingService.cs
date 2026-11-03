using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.OrganizationOnboarding;

/// <summary>
/// C8 organization onboarding (C-104; owner approval of the C-103 architecture with decisions 1–11). Platform plane only.
/// <list type="bullet">
/// <item><b>Create</b> (any active operator, no evidence: it grants nothing): validates the organization name and 2–10 initial owners
/// (one approved issuer, distinct identities, never a registered Platform Operator identity), generates the organization id and every
/// owner's user id, and freezes the statement. The request is immutable afterwards.</item>
/// <item><b>Approve</b>: two distinct active operators, each with their own evidence over the identical statement. The creator may give
/// one of them. The second valid approval moves the request to <c>AwaitingKeyCustody</c>.</item>
/// <item><b>Key custody</b> happens outside the Authority (development: a dedicated create-only Vault credential). The Authority only
/// inspects the key.</item>
/// <item><b>Initialize</b> (one active operator, evidence): verifies the key, then commits the organization, the owners' bindings, the key
/// registration and revision 1 (owner roles only) in one AuthorityDb transaction. The organization is usable only when revision 1 is
/// Effective (projected and anchored): until then the organization gate stays closed.</item>
/// </list>
/// Every change is a platform journal event carrying the resulting rows (replayable after a restore); refusals of authenticated
/// operators are journaled; every decision first checks the independent archive (rollback detection) and fails closed.
/// </summary>
public sealed partial class OrganizationOnboardingService(
    IAuthorityStore store,
    PlatformOperatorPolicy platformPolicy,
    OrganizationOnboardingPolicy policy,
    PlatformOperatorEvidenceVerifier evidenceVerifier,
    PlatformJournal journal,
    IIntegrityMacProvider macProvider,
    ISpiceDbProjection spiceDb,
    ISecurityAlertSink alerts,
    TimeProvider clock)
{
    public const int StatementVersion = 1;
    public const int DefaultPageSize = 15;
    public const int MaximumPageSize = 50;
    public const string InitializeCommandKind = "InitializeOrganization";
    private static readonly byte[] CursorKey = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] KeyProbeDomain = Encoding.UTF8.GetBytes("DynamicForms/OnboardingKeyProbe/v1");

    // ---- create ----

    public async Task<OrganizationOnboardingCreateResponse> CreateAsync(VerifiedActor actor, OrganizationOnboardingCreateRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        platformPolicy.RequireOperational();
        policy.RequireOperational();
        var key = input.IdempotencyKey?.Trim() ?? string.Empty;
        if (!input.DryRun && !IdempotencyKeyPattern().IsMatch(key))
        {
            throw AuthorityException.Invalid("idempotency_key must be 8..128 characters (letters, digits, '.', '_', ':', '-')");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var head = await PlatformJournal.LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
        await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
        var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
        // The statement freezes these instants: truncated to PostgreSQL's microsecond precision so the stored row reproduces it exactly.
        var exact = clock.GetUtcNow();
        var now = new DateTimeOffset(exact.UtcTicks - (exact.UtcTicks % 10), TimeSpan.Zero);
        Draft draft;
        var warnings = new List<string>();
        try
        {
            draft = Validate(input);
            var digest = CanonicalJson.Digest(CanonicalJson.Serialize(draft));
            if (!input.DryRun && await scope.FindOnboardingRequestByIdempotencyKeyAsync(me.PlatformOperatorId, key, cancellationToken).ConfigureAwait(false) is { } previous)
            {
                if (!previous.CreateRequestDigest.AsSpan().SequenceEqual(digest))
                {
                    throw new AuthorityException(AuthorityErrorCodes.IdempotencyConflict, 409, false, "this idempotency key was already used for a different onboarding request");
                }

                var replay = await DetailAsync(scope, previous, me, now, correlationId, cancellationToken).ConfigureAwait(false);
                return new OrganizationOnboardingCreateResponse(false, true, null, null, [], replay, true, correlationId);
            }

            await RequireOwnersAreNotOperatorsAsync(scope, draft.Owners.Select(o => (o.IssuerUri, o.SubjectIdentifier)), cancellationToken).ConfigureAwait(false);
            await RequireNameFreeAsync(scope, draft.NormalizedName, null, now, cancellationToken).ConfigureAwait(false);
            if (draft.Owners.Count < OrganizationOnboardingPolicy.RecommendedOwners)
            {
                warnings.Add($"Only {draft.Owners.Count} initial owners: three are recommended, because with two owners neither can later be removed or disabled and one unavailable owner blocks every owner change.");
            }

            foreach (var owner in draft.Owners)
            {
                var elsewhere = await scope.CountOrganizationsBindingIdentityAsync(owner.IssuerUri, owner.SubjectIdentifier, cancellationToken).ConfigureAwait(false);
                if (elsewhere > 0)
                {
                    warnings.Add($"{owner.SubjectIdentifier} is already a member of {elsewhere} other organization(s); memberships stay independent.");
                }
            }

            if (input.DryRun)
            {
                return new OrganizationOnboardingCreateResponse(true, true, null, null, warnings, null, false, correlationId);
            }

            var request = new OrganizationOnboardingRequest
            {
                OrganizationOnboardingRequestId = Guid.CreateVersion7(),
                ReservedOrganizationId = Guid.CreateVersion7(),
                OrganizationDisplayName = draft.DisplayName,
                OrganizationNormalizedName = draft.NormalizedName,
                IdentityIssuerUri = draft.Owners[0].IssuerUri,
                OnboardingStateName = OrganizationOnboardingStates.PendingApproval,
                RequestReasonText = draft.Justification,
                CreatedByPlatformOperatorId = me.PlatformOperatorId,
                CreateIdempotencyKey = key,
                CreateRequestDigest = digest,
                RequestCreatedOn = now,
                RequestExpiresOn = now + policy.PendingApprovalWindow,
            };
            request.IntegrityKeyName = IntegrityKeyNaming.ProviderKeyName(request.ReservedOrganizationId, IntegrityKeyPurposes.RowIntegrityV1);
            var owners = draft.Owners.Select((o, i) => new OrganizationOnboardingOwner
            {
                OrganizationOnboardingOwnerId = Guid.CreateVersion7(),
                OrganizationOnboardingRequestId = request.OrganizationOnboardingRequestId,
                OwnerOrdinalNumber = i + 1,
                ReservedAuthorityUserId = Guid.CreateVersion7(),
                OwnerIssuerUri = o.IssuerUri,
                OwnerSubjectIdentifier = o.SubjectIdentifier,
                OwnerDisplayNameHint = o.DisplayNameHint,
                OwnerVerificationMethodName = o.VerificationMethod,
                OwnerVerificationReferenceText = o.VerificationReference,
            }).ToList();
            var (statementJson, statementDigest) = Statement(request, owners);
            request.OnboardingStatementJson = statementJson;
            request.OnboardingStatementSha256 = statementDigest;

            // Requests with this name whose approval or custody window has passed are recorded Expired, releasing the name.
            var expired = (await scope.GetOpenOnboardingRequestsByNameAsync(draft.NormalizedName, cancellationToken).ConfigureAwait(false)).Where(r => IsTimeExpired(r, now)).ToList();
            foreach (var old in expired)
            {
                Close(old, OrganizationOnboardingStates.Expired, old.OnboardingStateName == OrganizationOnboardingStates.AwaitingKeyCustody ? OrganizationOnboardingClosureReasons.KeyCustodyWindowExpired : OrganizationOnboardingClosureReasons.ApprovalWindowExpired, null, null, now);
            }

            await scope.FlushAsync(cancellationToken).ConfigureAwait(false);
            scope.AddOnboardingRequest(request);
            foreach (var owner in owners)
            {
                scope.AddOnboardingOwner(owner);
            }

            PlatformJournal.Append(scope, head, "OrganizationOnboardingRequested", new
            {
                organization_onboarding_request_id = request.OrganizationOnboardingRequestId,
                organization_id = request.ReservedOrganizationId,
                created_by_platform_operator_id = me.PlatformOperatorId,
                owner_count = owners.Count,
                statement_sha256 = Convert.ToHexStringLower(statementDigest),
                expired_onboarding_request_ids = expired.Select(r => r.OrganizationOnboardingRequestId),
                correlation_id = correlationId,
            }, PlatformStateDelta.Onboarding([.. expired, request], owners), now);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            var detail = await DetailAsync(scope, request, me, now, correlationId, cancellationToken).ConfigureAwait(false);
            await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
            return new OrganizationOnboardingCreateResponse(false, true, null, null, warnings, detail, false, correlationId);
        }
        catch (AuthorityException ex) when (input.DryRun)
        {
            return new OrganizationOnboardingCreateResponse(true, false, ex.ErrorCode, ex.Detail, warnings, null, false, correlationId);
        }
        catch (AuthorityException ex)
        {
            await PlatformOperatorRegistryService.RecordRefusalAsync(scope, head, me, PlatformDecisions.Create, PlatformOperationKinds.OrganizationOnboarding, Guid.Empty, ex, correlationId, now, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    // ---- decisions ----

    public Task<OrganizationOnboardingDecisionResponse> ApproveAsync(VerifiedActor actor, Guid requestId, OrganizationOnboardingDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, requestId, input, PlatformDecisions.Approve, grantDirection: true, correlationId, ApproveBodyAsync, cancellationToken);

    public Task<OrganizationOnboardingDecisionResponse> WithdrawAsync(VerifiedActor actor, Guid requestId, OrganizationOnboardingDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, requestId, input, PlatformDecisions.Withdraw, grantDirection: false, correlationId, WithdrawBodyAsync, cancellationToken);

    public Task<OrganizationOnboardingDecisionResponse> RejectAsync(VerifiedActor actor, Guid requestId, OrganizationOnboardingDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, requestId, input, PlatformDecisions.Reject, grantDirection: false, correlationId, CloseBodyAsync, cancellationToken);

    public Task<OrganizationOnboardingDecisionResponse> CancelAsync(VerifiedActor actor, Guid requestId, OrganizationOnboardingDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, requestId, input, PlatformDecisions.Cancel, grantDirection: false, correlationId, CloseBodyAsync, cancellationToken);

    public Task<OrganizationOnboardingDecisionResponse> InitializeAsync(VerifiedActor actor, Guid requestId, OrganizationOnboardingDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, requestId, input, PlatformDecisions.Initialize, grantDirection: true, correlationId, InitializeBodyAsync, cancellationToken);

    /// <summary>What one decision needs: the locked request, its owners, approvals, the recomputed statement and the operator.</summary>
    private sealed record DecisionContext(IAuthorityScope Scope, PlatformJournalHead Head, PlatformOperator Me, OrganizationOnboardingRequest Request, List<OrganizationOnboardingOwner> Owners,
        List<OrganizationOnboardingApproval> Approvals, string StatementJson, string StatementSha256, string Justification, OrganizationOnboardingDecisionRequest Input, string Decision, DateTimeOffset Now, Guid CorrelationId);

    private sealed record DecisionOutcome(string Outcome, string EventKind, object Detail, PlatformStateDelta Delta, Guid? SecurityCommandId, Func<Task>? AfterCommit = null);

    private async Task<OrganizationOnboardingDecisionResponse> DecideAsync(VerifiedActor actor, Guid requestId, OrganizationOnboardingDecisionRequest input, string decision, bool grantDirection, Guid correlationId,
        Func<DecisionContext, CancellationToken, Task<(AuthorityException? Refusal, DecisionOutcome? Outcome)>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        platformPolicy.RequireOperational();
        policy.RequireOperational();
        var justification = PlatformOperatorAccess.RequireJustification(input.Justification);
        if (grantDirection)
        {
            await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
        }

        OrganizationOnboardingDecisionResponse response;
        Func<Task>? afterCommit = null;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var head = await PlatformJournal.LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
            await journal.EnsureCurrentAsync(scope, head, grantDirection, correlationId, cancellationToken).ConfigureAwait(false);
            var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var request = await scope.FindOnboardingRequestAsync(requestId, lockForUpdate: true, cancellationToken).ConfigureAwait(false)
                          ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such onboarding request");
            var owners = await scope.GetOnboardingOwnersAsync(requestId, cancellationToken).ConfigureAwait(false);
            var approvals = await scope.GetOnboardingApprovalsAsync(requestId, cancellationToken).ConfigureAwait(false);
            var (statementJson, statementDigest) = Statement(request, owners);
            var statementSha256 = Convert.ToHexStringLower(statementDigest);
            var context = new DecisionContext(scope, head, me, request, owners, approvals, statementJson, statementSha256, justification, input, decision, now, correlationId);
            AuthorityException? refusal;
            DecisionOutcome? outcome;
            try
            {
                if (!statementDigest.AsSpan().SequenceEqual(request.OnboardingStatementSha256))
                {
                    // The stored rows no longer produce the statement that was frozen at creation: never decide on them.
                    alerts.Raise(new SecurityAlert("OrganizationOnboardingStatementMismatch", "Critical", PlatformJournal.ArchiveScopeId, requestId, null, "onboarding request rows do not reproduce the frozen statement", correlationId));
                    throw new AuthorityException(AuthorityErrorCodes.ApprovalStatementChanged, 409, false, "the onboarding request no longer matches its frozen statement");
                }

                // The reviewed fingerprint is checked before the decision changes anything.
                if (!input.DryRun && !string.Equals(input.ExpectedStatementSha256?.Trim(), statementSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AuthorityException(AuthorityErrorCodes.ApprovalStatementChanged, 409, false, "the request is not the one that was reviewed; review it again");
                }

                (refusal, outcome) = await body(context, cancellationToken).ConfigureAwait(false);
                if (refusal is not null && !input.DryRun)
                {
                    throw refusal;
                }

                if (input.DryRun)
                {
                    return Response(true, refusal, request, decision, statementJson, statementSha256, refusal is null ? "Eligible" : "Refused", approvals, null, null, false, now, correlationId);
                }
            }
            catch (AuthorityException ex)
            {
                await RecordRefusalAsync(context, decision, ex, cancellationToken).ConfigureAwait(false);
                throw;
            }

            if (outcome!.Outcome == "Replayed")
            {
                return Response(false, null, request, decision, statementJson, statementSha256, "Replayed", approvals, request.InitializedBySecurityCommandId, null, true, now, correlationId);
            }

            PlatformJournal.Append(scope, head, outcome.EventKind, outcome.Detail, outcome.Delta, now);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            afterCommit = outcome.AfterCommit;
            var current = await scope.GetOnboardingApprovalsAsync(requestId, cancellationToken).ConfigureAwait(false);
            response = Response(false, null, request, decision, statementJson, statementSha256, outcome.Outcome, current, outcome.SecurityCommandId, null, false, now, correlationId);
        }

        if (afterCommit is not null)
        {
            await afterCommit().ConfigureAwait(false);
        }

        var archiveState = await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
        return response with { ArchiveState = archiveState };
    }

    private async Task<(AuthorityException?, DecisionOutcome?)> ApproveBodyAsync(DecisionContext c, CancellationToken cancellationToken)
    {
        var refusal = await ApproveRefusalAsync(c.Scope, c.Request, c.Owners, c.Approvals, c.Me, c.Now, cancellationToken).ConfigureAwait(false);
        if (refusal is not null || c.Input.DryRun)
        {
            return (refusal, null);
        }

        var evidence = VerifyEvidence(c, PlatformDecisions.Approve);
        await PlatformOperatorAccess.RequireUnusedAsync(c.Scope, evidence, cancellationToken).ConfigureAwait(false);
        var stale = Stale(c.Approvals, await ActiveOperatorIdsAsync(c.Scope, cancellationToken).ConfigureAwait(false), c.Me);
        foreach (var old in stale)
        {
            CloseApproval(old, OnboardingApprovalStates.Invalidated, PlatformApprovalClosureReasons.OperatorIneligible, c.Now);
        }

        var approval = new OrganizationOnboardingApproval
        {
            OrganizationOnboardingApprovalId = Guid.CreateVersion7(),
            OrganizationOnboardingRequestId = c.Request.OrganizationOnboardingRequestId,
            PlatformOperatorId = c.Me.PlatformOperatorId,
            ApprovalStatementSha256 = Convert.FromHexString(c.StatementSha256),
            ApprovalJustificationText = c.Justification,
            ApprovalEvidenceKindName = evidence.EvidenceKind,
            ApprovalEvidenceSha256 = evidence.EvidenceSha256,
            ApprovalEvidenceIdentifier = evidence.EvidenceIdentifier,
            ApprovalRecordedOn = c.Now,
        };
        await c.Scope.FlushAsync(cancellationToken).ConfigureAwait(false);
        c.Scope.AddOnboardingApproval(approval);
        var use = PlatformOperatorAccess.EvidenceUse(evidence, c.Me.PlatformOperatorId, PlatformDecisions.Approve, c.Now);
        c.Scope.AddPlatformEvidenceUse(use);
        var other = c.Approvals.FirstOrDefault(a => a.ApprovalStateName == OnboardingApprovalStates.Valid && a.PlatformOperatorId != c.Me.PlatformOperatorId);
        var changed = new List<OrganizationOnboardingApproval>(stale) { approval };
        string outcome;
        if (other is not null)
        {
            CloseApproval(other, OnboardingApprovalStates.Consumed, null, c.Now);
            CloseApproval(approval, OnboardingApprovalStates.Consumed, null, c.Now);
            changed.Add(other);
            c.Request.OnboardingStateName = OrganizationOnboardingStates.AwaitingKeyCustody;
            c.Request.OnboardingApprovedOn = c.Now;
            c.Request.KeyCustodyExpiresOn = c.Now + policy.KeyCustodyWindow;
            outcome = "Approved";
        }
        else
        {
            outcome = "AwaitingSecondApproval";
        }

        c.Request.OnboardingRowRevision++;
        return (null, new DecisionOutcome(outcome, outcome == "Approved" ? "OrganizationOnboardingApproved" : "OrganizationOnboardingApprovalRecorded",
            DecisionDetail(c, PlatformDecisions.Approve, evidence, new { approved_with = other?.PlatformOperatorId, invalidated_approvals = stale.Select(a => a.OrganizationOnboardingApprovalId) }),
            PlatformStateDelta.Onboarding([c.Request], approvals: changed, evidenceUses: [use]), null));
    }

    private async Task<(AuthorityException?, DecisionOutcome?)> WithdrawBodyAsync(DecisionContext c, CancellationToken cancellationToken)
    {
        var mine = c.Approvals.FirstOrDefault(a => a.PlatformOperatorId == c.Me.PlatformOperatorId && a.ApprovalStateName == OnboardingApprovalStates.Valid);
        AuthorityException? refusal = c.Request.OnboardingStateName != OrganizationOnboardingStates.PendingApproval
            ? NotPending(c.Request)
            : mine is null ? new AuthorityException(AuthorityErrorCodes.NoPendingApproval, 409, false, "you have no approval of this request to withdraw") : null;
        if (refusal is not null || c.Input.DryRun)
        {
            return (refusal, null);
        }

        var evidence = VerifyEvidence(c, PlatformDecisions.Withdraw);
        await PlatformOperatorAccess.RequireUnusedAsync(c.Scope, evidence, cancellationToken).ConfigureAwait(false);
        CloseApproval(mine!, OnboardingApprovalStates.Withdrawn, PlatformApprovalClosureReasons.WithdrawnByOperator, c.Now);
        var use = PlatformOperatorAccess.EvidenceUse(evidence, c.Me.PlatformOperatorId, PlatformDecisions.Withdraw, c.Now);
        c.Scope.AddPlatformEvidenceUse(use);
        c.Request.OnboardingRowRevision++;
        return (null, new DecisionOutcome("ApprovalWithdrawn", "OrganizationOnboardingApprovalWithdrawn", DecisionDetail(c, PlatformDecisions.Withdraw, evidence, new { withdrawn_approval_id = mine!.OrganizationOnboardingApprovalId }),
            PlatformStateDelta.Onboarding([c.Request], approvals: [mine], evidenceUses: [use]), null));
    }

    /// <summary>Reject (awaiting approval) or cancel (approved, awaiting key custody): one operator, closes the request; nothing was created.</summary>
    private async Task<(AuthorityException?, DecisionOutcome?)> CloseBodyAsync(DecisionContext c, CancellationToken cancellationToken)
    {
        var decision = c.Decision;
        var expected = decision == PlatformDecisions.Reject ? OrganizationOnboardingStates.PendingApproval : OrganizationOnboardingStates.AwaitingKeyCustody;
        AuthorityException? refusal = c.Request.OnboardingStateName != expected
            ? NotPending(c.Request)
            : IsTimeExpired(c.Request, c.Now) ? Expired() : null;
        if (refusal is not null || c.Input.DryRun)
        {
            return (refusal, null);
        }

        var evidence = VerifyEvidence(c, decision);
        await PlatformOperatorAccess.RequireUnusedAsync(c.Scope, evidence, cancellationToken).ConfigureAwait(false);
        var closing = c.Approvals.Where(a => a.ApprovalStateName == OnboardingApprovalStates.Valid).ToList();
        foreach (var approval in closing)
        {
            CloseApproval(approval, OnboardingApprovalStates.Invalidated, decision == PlatformDecisions.Reject ? PlatformApprovalClosureReasons.RequestRejected : PlatformApprovalClosureReasons.RequestCancelled, c.Now);
        }

        var rejected = decision == PlatformDecisions.Reject;
        Close(c.Request, rejected ? OrganizationOnboardingStates.Rejected : OrganizationOnboardingStates.Cancelled, rejected ? OrganizationOnboardingClosureReasons.Rejected : OrganizationOnboardingClosureReasons.Cancelled, c.Me.PlatformOperatorId, c.Justification, c.Now);
        var use = PlatformOperatorAccess.EvidenceUse(evidence, c.Me.PlatformOperatorId, decision, c.Now);
        c.Scope.AddPlatformEvidenceUse(use);
        return (null, new DecisionOutcome(rejected ? "Rejected" : "Cancelled", rejected ? "OrganizationOnboardingRejected" : "OrganizationOnboardingCancelled", DecisionDetail(c, decision, evidence, new { invalidated_approvals = closing.Select(a => a.OrganizationOnboardingApprovalId) }),
            PlatformStateDelta.Onboarding([c.Request], approvals: closing, evidenceUses: [use]), null));
    }

    private async Task<(AuthorityException?, DecisionOutcome?)> InitializeBodyAsync(DecisionContext c, CancellationToken cancellationToken)
    {
        if (c.Request.OnboardingStateName == OrganizationOnboardingStates.Initialized)
        {
            return (null, new DecisionOutcome("Replayed", string.Empty, new { }, PlatformStateDelta.Onboarding(), c.Request.InitializedBySecurityCommandId));
        }

        var refusal = await InitializeRefusalAsync(c.Scope, c.Request, c.Owners, c.Approvals, c.Now, cancellationToken).ConfigureAwait(false);
        IntegrityKeyInspection? inspection = null;
        if (refusal is null)
        {
            (inspection, refusal) = await InspectKeyAsync(c.Request, cancellationToken).ConfigureAwait(false);
        }

        if (refusal is not null || c.Input.DryRun)
        {
            return (refusal, null);
        }

        var evidence = VerifyEvidence(c, PlatformDecisions.Initialize);
        await PlatformOperatorAccess.RequireUnusedAsync(c.Scope, evidence, cancellationToken).ConfigureAwait(false);
        var organizationId = c.Request.ReservedOrganizationId;
        try
        {
            await spiceDb.InitializeCursorAsync(organizationId, cancellationToken).ConfigureAwait(false);
        }
        catch (SpiceDbUnavailableException ex)
        {
            throw AuthorityException.NotReady("SpiceDB unavailable: " + ex.Message);
        }

        var now = c.Now;
        var state = new OrganizationAuthorizationState
        {
            OrganizationAuthorizationStateId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            OrganizationDisplayName = c.Request.OrganizationDisplayName,
            CheckpointEnforcementModeName = CheckpointEnforcementModes.RequiredBeforeEffective,
            StateModifiedOn = now,
            OrganizationOnboardingRequestId = c.Request.OrganizationOnboardingRequestId,
        };
        c.Scope.AddOrganization(state);
        var bindings = c.Owners.Select(o => new IdentityBinding
        {
            IdentityBindingId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AuthorityUserId = o.ReservedAuthorityUserId,
            IdentityIssuerUri = o.OwnerIssuerUri,
            IdentitySubjectIdentifier = o.OwnerSubjectIdentifier,
            IdentityBindingEnabled = true,
            IdentityCreatedOn = now,
            OrganizationOnboardingRequestId = c.Request.OrganizationOnboardingRequestId,
        }).ToList();
        foreach (var binding in bindings)
        {
            c.Scope.AddIdentityBinding(binding);
        }

        c.Scope.AddKeyRegistration(new IntegrityKeyRegistration
        {
            IntegrityKeyRegistrationId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            KeyPurposeName = IntegrityKeyPurposes.RowIntegrityV1,
            ProviderName = macProvider.ProviderName,
            ProviderKeyName = c.Request.IntegrityKeyName,
            CurrentSigningKeyVersion = inspection!.LatestVersion,
            MinimumVerificationKeyVersion = 1,
            KeyRegisteredOn = now,
            KeyModifiedOn = now,
        });
        c.Scope.AddResource(new AuthorityResource
        {
            AuthorityResourceId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            ResourceTypeName = ResourceTypeNames.Organization,
            ResourceEntityId = organizationId,
            ResourceLifecycleState = ResourceLifecycleStates.Active,
            RegisteredBySecurityCommandId = Guid.Empty,
            ResourceRegisteredOn = now,
        });

        // Revision 1: the owner role for every initial owner and nothing else. Operators are named in the payload; the command's actor
        // binding is the first owner's (a command always has an organization actor binding, as in the legacy bootstrap).
        var approvers = c.Approvals.Where(a => a.ApprovalStateName == OnboardingApprovalStates.Consumed).Select(a => new { platform_operator_id = a.PlatformOperatorId, evidence_sha256 = Convert.ToHexStringLower(a.ApprovalEvidenceSha256) }).ToList();
        var payloadJson = CanonicalJson.Serialize(new InitializePayload(c.Request.OrganizationOnboardingRequestId, c.StatementSha256, c.Owners.Select(o => new InitializeOwner(o.ReservedAuthorityUserId, o.OwnerIssuerUri, o.OwnerSubjectIdentifier)).ToList(),
            c.Approvals.Where(a => a.ApprovalStateName == OnboardingApprovalStates.Consumed).Select(a => new InitializeApproval(a.PlatformOperatorId, Convert.ToHexStringLower(a.ApprovalEvidenceSha256))).ToList(), c.Me.PlatformOperatorId, platformPolicy.Assurance));
        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            ActorIdentityBindingId = bindings[0].IdentityBindingId,
            CommandKindName = InitializeCommandKind,
            CommandIdempotencyKey = "onboarding:" + c.Request.OrganizationOnboardingRequestId.ToString("N"),
            CommandRequestDigest = Convert.FromHexString(c.StatementSha256),
            ExpectedAuthorizationRevision = 0,
            CommandPayloadJson = payloadJson,
            CommandReasonText = "organization onboarding " + c.Request.OrganizationOnboardingRequestId.ToString("D"),
            CorrelationIdentifier = c.CorrelationId,
            CommandCreatedOn = now,
            CommandModifiedOn = now,
        };
        var plan = new CommandPlan { NormalizedPayloadJson = payloadJson };
        foreach (var owner in c.Owners)
        {
            plan.AddEdges.Add((ResourceTypeNames.Organization, organizationId, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, owner.ReservedAuthorityUserId, string.Empty));
        }

        var actor = new BoundActor(new VerifiedActor(c.Me.OperatorIssuerUri, c.Me.OperatorSubjectIdentifier, "platform-console"), bindings[0]);
        var commandRequest = new SecurityCommandRequest(organizationId, SecurityCommandKind.GrantSystemRole, new ResourceRef(ResourceKind.Organization, organizationId), 0, JsonDocument.Parse(payloadJson).RootElement, command.CommandReasonText);
        SecurityCommandService.ApplyPlan(c.Scope, state, command, plan, actor, commandRequest, cancellationToken);
        AuditJournal.Append(c.Scope, state, "OrganizationInitialized", new
        {
            security_command_id = command.SecurityCommandId,
            organization_onboarding_request_id = c.Request.OrganizationOnboardingRequestId,
            initial_owner_user_ids = c.Owners.Select(o => o.ReservedAuthorityUserId),
            approved_by_platform_operator_ids = approvers.Select(a => a.platform_operator_id),
            initialized_by_platform_operator_id = c.Me.PlatformOperatorId,
            statement_sha256 = c.StatementSha256,
            approval_assurance = platformPolicy.Assurance,
            correlation_id = c.CorrelationId,
        }, clock);

        c.Request.OnboardingStateName = OrganizationOnboardingStates.Initialized;
        c.Request.OnboardingInitializedOn = now;
        c.Request.InitializedByPlatformOperatorId = c.Me.PlatformOperatorId;
        c.Request.InitializedBySecurityCommandId = command.SecurityCommandId;
        c.Request.LastInitializationFailureCode = null;
        c.Request.LastInitializationFailureOn = null;
        c.Request.OnboardingRowRevision++;
        var use = PlatformOperatorAccess.EvidenceUse(evidence, c.Me.PlatformOperatorId, PlatformDecisions.Initialize, now);
        c.Scope.AddPlatformEvidenceUse(use);
        var alert = new SecurityAlert("OrganizationOnboarded", "High", organizationId, c.Request.OrganizationOnboardingRequestId, command.SecurityCommandId,
            $"organization {organizationId} initialized from onboarding request {c.Request.OrganizationOnboardingRequestId} ({c.Owners.Count} owners; {platformPolicy.Assurance})", c.CorrelationId);
        return (null, new DecisionOutcome("Initialized", "OrganizationOnboardingInitialized",
            DecisionDetail(c, PlatformDecisions.Initialize, evidence, new { organization_id = organizationId, security_command_id = command.SecurityCommandId, integrity_key_version = inspection.LatestVersion, initial_owner_user_ids = c.Owners.Select(o => o.ReservedAuthorityUserId) }),
            PlatformStateDelta.Onboarding([c.Request], evidenceUses: [use]), command.SecurityCommandId, () =>
            {
                alerts.Raise(alert);
                return Task.CompletedTask;
            }));
    }

    // ---- reads ----

    public async Task<OrganizationOnboardingPage> SearchAsync(VerifiedActor actor, OrganizationOnboardingSearchRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        platformPolicy.RequireOperational();
        var status = string.IsNullOrWhiteSpace(input.Status) ? OrganizationOnboardingFilters.All : input.Status.Trim();
        if (!OrganizationOnboardingFilters.Values.Contains(status))
        {
            throw AuthorityException.Invalid("unknown status filter");
        }

        var text = input.Query?.Trim();
        if (text is { Length: > 200 })
        {
            throw AuthorityException.Invalid("the search text is at most 200 characters");
        }

        var limit = input.Limit ?? DefaultPageSize;
        if (limit is < 1 or > MaximumPageSize)
        {
            throw AuthorityException.Invalid($"limit must be 1..{MaximumPageSize}");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await HeadForReadAsync(scope, correlationId, cancellationToken).ConfigureAwait(false);
        var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
        var binding = Binding("onboarding", me, status, text, limit);
        var after = input.Cursor is { Length: > 0 } cursor ? ReadCursor(cursor, binding) : null;
        var now = clock.GetUtcNow();
        var page = await scope.SearchOnboardingRequestsAsync(new OnboardingRequestQuery(text, status, now, after is { } a ? (new DateTimeOffset(long.Parse(a.SortKey, CultureInfo.InvariantCulture), TimeSpan.Zero), a.Id) : null, limit + 1), cancellationToken).ConfigureAwait(false);
        var rows = page.Rows.Take(limit).ToList();
        var approvals = await scope.GetOnboardingApprovalsForRequestsAsync(rows.Select(r => r.OrganizationOnboardingRequestId).ToList(), cancellationToken).ConfigureAwait(false);
        var operators = (await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.PlatformOperatorId);
        var items = new List<OrganizationOnboardingListItem>();
        foreach (var r in rows)
        {
            var owners = await scope.GetOnboardingOwnersAsync(r.OrganizationOnboardingRequestId, cancellationToken).ConfigureAwait(false);
            var mine = approvals.Where(x => x.OrganizationOnboardingRequestId == r.OrganizationOnboardingRequestId).ToList();
            items.Add(new OrganizationOnboardingListItem(r.OrganizationOnboardingRequestId, r.ReservedOrganizationId, r.OrganizationDisplayName, owners.Count, r.OnboardingStateName,
                await DisplayStateAsync(scope, r, now, cancellationToken).ConfigureAwait(false), r.RequestCreatedOn, r.RequestExpiresOn, Recorded(mine), OrganizationOnboardingPolicy.RequiredApprovals,
                mine.Any(x => x.PlatformOperatorId == me.PlatformOperatorId && x.ApprovalStateName is OnboardingApprovalStates.Valid or OnboardingApprovalStates.Consumed),
                operators.TryGetValue(r.CreatedByPlatformOperatorId, out var creator) ? creator.OperatorDisplayName : "Unknown operator"));
        }

        var next = page.Rows.Count > limit ? WriteCursor(rows[^1].RequestCreatedOn.UtcTicks.ToString(CultureInfo.InvariantCulture), rows[^1].OrganizationOnboardingRequestId, binding) : null;
        return new OrganizationOnboardingPage(items, page.TotalCount, next, limit, status, policy.ApprovedIdentityIssuers, platformPolicy.Assurance, correlationId);
    }

    public async Task<OrganizationOnboardingDetail> GetDetailAsync(VerifiedActor actor, Guid requestId, Guid correlationId, CancellationToken cancellationToken)
    {
        platformPolicy.RequireOperational();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await HeadForReadAsync(scope, correlationId, cancellationToken).ConfigureAwait(false);
        var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
        var request = await scope.FindOnboardingRequestAsync(requestId, lockForUpdate: false, cancellationToken).ConfigureAwait(false)
                      ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such onboarding request");
        return await DetailAsync(scope, request, me, clock.GetUtcNow(), correlationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Operator plane, read-only: the one key the custody tool may create, and only while the request awaits custody.</summary>
    public async Task<OrganizationKeyCustodyStatus> GetKeyCustodyAsync(Guid requestId, Guid correlationId, CancellationToken cancellationToken)
    {
        platformPolicy.RequireOperational();
        policy.RequireOperational();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await HeadForReadAsync(scope, correlationId, cancellationToken).ConfigureAwait(false);
        var request = await scope.FindOnboardingRequestAsync(requestId, lockForUpdate: false, cancellationToken).ConfigureAwait(false)
                      ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such onboarding request");
        var now = clock.GetUtcNow();
        var awaiting = request.OnboardingStateName == OrganizationOnboardingStates.AwaitingKeyCustody && !IsTimeExpired(request, now);
        var custody = await KeyCustodyAsync(request, now, cancellationToken).ConfigureAwait(false);
        return new OrganizationKeyCustodyStatus(request.OrganizationOnboardingRequestId, request.ReservedOrganizationId, request.OnboardingStateName, awaiting && custody.Status == "Missing",
            request.IntegrityKeyName, IntegrityKeyPurposes.RowIntegrityV1, macProvider.ProviderName, custody.Status, request.KeyCustodyExpiresOn, correlationId);
    }

    // ---- shared rules ----

    private static async Task<AuthorityException?> ApproveRefusalAsync(IAuthorityScope scope, OrganizationOnboardingRequest request, List<OrganizationOnboardingOwner> owners, List<OrganizationOnboardingApproval> approvals, PlatformOperator me, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (request.OnboardingStateName != OrganizationOnboardingStates.PendingApproval)
        {
            return NotPending(request);
        }

        if (IsTimeExpired(request, now))
        {
            return Expired();
        }

        if (approvals.Any(a => a.PlatformOperatorId == me.PlatformOperatorId && a.ApprovalStateName is OnboardingApprovalStates.Valid or OnboardingApprovalStates.Consumed))
        {
            return new AuthorityException(AuthorityErrorCodes.OperatorAlreadyApproved, 409, false, "you already approved this request; a different operator must give the other approval");
        }

        if (owners.Any(o => o.OwnerIssuerUri == me.OperatorIssuerUri && o.OwnerSubjectIdentifier == me.OperatorSubjectIdentifier))
        {
            return new AuthorityException(AuthorityErrorCodes.OperatorConflictOfInterest, 403, false, "you are one of the proposed owners");
        }

        return await OwnerAndNameRefusalAsync(scope, request, owners, now, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AuthorityException?> InitializeRefusalAsync(IAuthorityScope scope, OrganizationOnboardingRequest request, List<OrganizationOnboardingOwner> owners, List<OrganizationOnboardingApproval> approvals, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (request.OnboardingStateName != OrganizationOnboardingStates.AwaitingKeyCustody)
        {
            return NotPending(request);
        }

        if (IsTimeExpired(request, now))
        {
            return Expired();
        }

        var consumed = approvals.Where(a => a.ApprovalStateName == OnboardingApprovalStates.Consumed).Select(a => a.PlatformOperatorId).Distinct().Count();
        if (consumed < OrganizationOnboardingPolicy.RequiredApprovals || owners.Count < OrganizationOnboardingPolicy.MinimumOwners)
        {
            return new AuthorityException(AuthorityErrorCodes.OrganizationOnboardingNotPending, 409, false, "the request does not carry two approvals by different operators");
        }

        if (await scope.ReadOrganizationAsync(request.ReservedOrganizationId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new AuthorityException(AuthorityErrorCodes.OrganizationIdReserved, 409, false, "an organization with the reserved id already exists");
        }

        return await OwnerAndNameRefusalAsync(scope, request, owners, now, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AuthorityException?> OwnerAndNameRefusalAsync(IAuthorityScope scope, OrganizationOnboardingRequest request, List<OrganizationOnboardingOwner> owners, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await RequireOwnersAreNotOperatorsAsync(scope, owners.Select(o => (o.OwnerIssuerUri, o.OwnerSubjectIdentifier)), cancellationToken).ConfigureAwait(false);
            await RequireNameFreeAsync(scope, request.OrganizationNormalizedName, request.OrganizationOnboardingRequestId, now, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (AuthorityException ex)
        {
            return ex;
        }
    }

    /// <summary>No initial owner may be a registered Platform Operator identity (any status, ever): an operator cannot appoint themself.</summary>
    private static async Task RequireOwnersAreNotOperatorsAsync(IAuthorityScope scope, IEnumerable<(string Issuer, string Subject)> owners, CancellationToken cancellationToken)
    {
        foreach (var (issuer, subject) in owners)
        {
            if (await scope.FindPlatformOperatorByIdentityAsync(issuer, subject, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIsOperator, 403, false, "a Platform Operator identity can never be an initial owner");
            }
        }
    }

    private static async Task RequireNameFreeAsync(IAuthorityScope scope, string normalizedName, Guid? self, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = await scope.GetOpenOnboardingRequestsByNameAsync(normalizedName, cancellationToken).ConfigureAwait(false);
        if (open.Any(r => r.OrganizationOnboardingRequestId != self && !IsTimeExpired(r, now)))
        {
            throw new AuthorityException(AuthorityErrorCodes.OrganizationNameConflict, 409, false, "another open onboarding request uses this organization name");
        }

        var organizations = await scope.GetOrganizationDirectoryRowsAsync(cancellationToken).ConfigureAwait(false);
        if (organizations.Any(o => NormalizeName(o.State.OrganizationDisplayName) == normalizedName && (self is null || o.State.OrganizationOnboardingRequestId != self)))
        {
            throw new AuthorityException(AuthorityErrorCodes.OrganizationNameConflict, 409, false, "an organization with this name already exists");
        }
    }

    private async Task<(IntegrityKeyInspection? Inspection, AuthorityException? Refusal)> InspectKeyAsync(OrganizationOnboardingRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var inspection = await macProvider.InspectKeyAsync(request.IntegrityKeyName, cancellationToken).ConfigureAwait(false);
            if (!inspection.Exists)
            {
                return (inspection, new AuthorityException(AuthorityErrorCodes.OrganizationKeyNotProvisioned, 409, false, "the organization key has not been provisioned by key custody yet"));
            }

            if (!inspection.Usable)
            {
                return (inspection, new AuthorityException(AuthorityErrorCodes.OrganizationKeyNotProvisioned, 409, false, "the organization key exists but is not usable: " + inspection.Detail));
            }

            // Prove the Authority can sign and verify with exactly this key before anything depends on it.
            var reference = new IntegrityKeyReference(request.ReservedOrganizationId, IntegrityKeyPurposes.RowIntegrityV1, request.IntegrityKeyName, inspection.LatestVersion);
            var probe = KeyProbeDomain.Concat(new byte[] { 0 }).Concat(request.ReservedOrganizationId.ToByteArray(bigEndian: true)).ToArray();
            var mac = await macProvider.SignAsync(reference, probe, cancellationToken).ConfigureAwait(false);
            if (mac.Mac.Length != 32 || !await macProvider.VerifyAsync(reference with { KeyVersion = mac.KeyVersion }, probe, mac.Mac, cancellationToken).ConfigureAwait(false))
            {
                return (inspection, new AuthorityException(AuthorityErrorCodes.OrganizationKeyNotProvisioned, 409, false, "the Authority cannot sign and verify with the organization key"));
            }

            return (inspection with { LatestVersion = mac.KeyVersion }, null);
        }
        catch (IntegrityProviderUnavailableException)
        {
            return (null, new AuthorityException(AuthorityErrorCodes.SigningUnavailable, 503, true, "the signing provider is unavailable; the key cannot be verified"));
        }
    }

    private async Task<OrganizationOnboardingKeyCustody> KeyCustodyAsync(OrganizationOnboardingRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (request.OnboardingStateName != OrganizationOnboardingStates.AwaitingKeyCustody || IsTimeExpired(request, now))
        {
            return new OrganizationOnboardingKeyCustody(request.IntegrityKeyName, "NotRequired", null);
        }

        try
        {
            var inspection = await macProvider.InspectKeyAsync(request.IntegrityKeyName, cancellationToken).ConfigureAwait(false);
            return new OrganizationOnboardingKeyCustody(request.IntegrityKeyName, !inspection.Exists ? "Missing" : inspection.Usable ? "Ready" : "Invalid", inspection.Detail);
        }
        catch (IntegrityProviderUnavailableException)
        {
            return new OrganizationOnboardingKeyCustody(request.IntegrityKeyName, "Unavailable", "the signing provider cannot be reached");
        }
    }

    private VerifiedOperatorEvidence VerifyEvidence(DecisionContext c, string decision) =>
        evidenceVerifier.Verify(c.Me, c.Input.EvidenceKind, c.Input.Evidence, new OperatorEvidenceBinding(decision, PlatformOperationKinds.OrganizationOnboarding, c.Request.OrganizationOnboardingRequestId,
            c.Request.ReservedOrganizationId, c.StatementSha256, PlatformOperatorEvidenceVerifier.JustificationSha256(c.Justification)), c.Now);

    private static async Task RecordRefusalAsync(DecisionContext c, string decision, AuthorityException refusal, CancellationToken cancellationToken)
    {
        // Nothing has been changed when a refusal is thrown; an initialization refusal also records the failure on the request.
        if (decision == PlatformDecisions.Initialize && c.Request.OnboardingStateName == OrganizationOnboardingStates.AwaitingKeyCustody)
        {
            c.Request.LastInitializationFailureCode = refusal.ErrorCode;
            c.Request.LastInitializationFailureOn = c.Now;
            c.Request.OnboardingRowRevision++;
            PlatformJournal.Append(c.Scope, c.Head, "OrganizationOnboardingInitializationRefused", new
            {
                organization_onboarding_request_id = c.Request.OrganizationOnboardingRequestId,
                platform_operator_id = c.Me.PlatformOperatorId,
                refusal_code = refusal.ErrorCode,
                correlation_id = c.CorrelationId,
            }, PlatformStateDelta.Onboarding([c.Request]), c.Now);
            await c.Scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await PlatformOperatorRegistryService.RecordRefusalAsync(c.Scope, c.Head, c.Me, decision, PlatformOperationKinds.OrganizationOnboarding, c.Request.OrganizationOnboardingRequestId, refusal, c.CorrelationId, c.Now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OrganizationOnboardingDetail> DetailAsync(IAuthorityScope scope, OrganizationOnboardingRequest request, PlatformOperator me, DateTimeOffset now, Guid correlationId, CancellationToken cancellationToken)
    {
        var owners = await scope.GetOnboardingOwnersAsync(request.OrganizationOnboardingRequestId, cancellationToken).ConfigureAwait(false);
        var approvals = await scope.GetOnboardingApprovalsAsync(request.OrganizationOnboardingRequestId, cancellationToken).ConfigureAwait(false);
        var operators = (await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.PlatformOperatorId);
        var ownerViews = new List<OrganizationOnboardingOwnerView>();
        foreach (var o in owners)
        {
            var elsewhere = await scope.CountOrganizationsBindingIdentityAsync(o.OwnerIssuerUri, o.OwnerSubjectIdentifier, cancellationToken).ConfigureAwait(false);
            var inThis = request.OnboardingStateName == OrganizationOnboardingStates.Initialized ? 1 : 0;
            ownerViews.Add(new OrganizationOnboardingOwnerView(o.ReservedAuthorityUserId, o.OwnerIssuerUri, o.OwnerSubjectIdentifier, o.OwnerDisplayNameHint, o.OwnerVerificationMethodName, o.OwnerVerificationReferenceText, elsewhere - inThis > 0));
        }

        var approveRefusal = await ApproveRefusalAsync(scope, request, owners, approvals, me, now, cancellationToken).ConfigureAwait(false);
        AuthorityException? initializeRefusal = await InitializeRefusalAsync(scope, request, owners, approvals, now, cancellationToken).ConfigureAwait(false);
        var custody = await KeyCustodyAsync(request, now, cancellationToken).ConfigureAwait(false);
        if (initializeRefusal is null && custody.Status != "Ready")
        {
            initializeRefusal = custody.Status == "Unavailable"
                ? new AuthorityException(AuthorityErrorCodes.SigningUnavailable, 503, true, "the signing provider is unavailable")
                : new AuthorityException(AuthorityErrorCodes.OrganizationKeyNotProvisioned, 409, false, "the organization key has not been provisioned (or is not usable)");
        }

        var open = !IsTimeExpired(request, now);
        var eligibility = new OrganizationOnboardingEligibility(
            approveRefusal is null,
            request.OnboardingStateName == OrganizationOnboardingStates.PendingApproval && approvals.Any(a => a.PlatformOperatorId == me.PlatformOperatorId && a.ApprovalStateName == OnboardingApprovalStates.Valid),
            request.OnboardingStateName == OrganizationOnboardingStates.PendingApproval && open,
            request.OnboardingStateName == OrganizationOnboardingStates.AwaitingKeyCustody && open,
            initializeRefusal is null,
            approveRefusal?.ErrorCode,
            approveRefusal?.Detail,
            initializeRefusal?.ErrorCode,
            initializeRefusal?.Detail);
        var activation = await ActivationAsync(scope, request, cancellationToken).ConfigureAwait(false);
        return new OrganizationOnboardingDetail(request.OrganizationOnboardingRequestId, request.ReservedOrganizationId, request.OrganizationDisplayName, request.IdentityIssuerUri, request.OnboardingStateName,
            DisplayState(request, now, activation), request.RequestReasonText, request.CreatedByPlatformOperatorId,
            operators.TryGetValue(request.CreatedByPlatformOperatorId, out var creator) ? creator.OperatorDisplayName : "Unknown operator",
            request.RequestCreatedOn, request.RequestExpiresOn, request.OnboardingApprovedOn, request.KeyCustodyExpiresOn, request.OnboardingInitializedOn, request.OnboardingClosedOn, request.OnboardingClosureReasonName,
            request.ClosureJustificationText, request.LastInitializationFailureCode, request.LastInitializationFailureOn, ownerViews,
            approvals.Select(a => new OrganizationOnboardingApprovalView(a.OrganizationOnboardingApprovalId, a.PlatformOperatorId, operators.TryGetValue(a.PlatformOperatorId, out var o) ? o.OperatorDisplayName : "Unknown operator",
                a.PlatformOperatorId == me.PlatformOperatorId, a.ApprovalJustificationText, a.ApprovalRecordedOn, a.ApprovalStateName, a.ApprovalClosureReasonName)).ToList(),
            Recorded(approvals), OrganizationOnboardingPolicy.RequiredApprovals, request.OnboardingStatementJson, Convert.ToHexStringLower(request.OnboardingStatementSha256), custody, activation, eligibility,
            request.OnboardingRowRevision, platformPolicy.Assurance, correlationId);
    }

    private static async Task<OrganizationActivation?> ActivationAsync(IAuthorityScope scope, OrganizationOnboardingRequest request, CancellationToken cancellationToken)
    {
        if (request.OnboardingStateName != OrganizationOnboardingStates.Initialized || request.InitializedBySecurityCommandId is not { } commandId)
        {
            return null;
        }

        var state = await scope.ReadOrganizationAsync(request.ReservedOrganizationId, cancellationToken).ConfigureAwait(false);
        var command = await scope.FindCommandAsync(commandId, cancellationToken).ConfigureAwait(false);
        if (state is null || command is null)
        {
            return new OrganizationActivation("Quarantined", 0, 0); // the organization rows are missing: never claim it is usable
        }

        var status = state.IntegrityQuarantineEnabled ? "Quarantined" : command.CommandStateName == nameof(CommandStatus.Effective) ? "Active" : "Activating";
        return new OrganizationActivation(status, state.AuthorizationRevision, state.ProjectionAppliedRevision);
    }

    private static async Task<string> DisplayStateAsync(IAuthorityScope scope, OrganizationOnboardingRequest request, DateTimeOffset now, CancellationToken cancellationToken) =>
        DisplayState(request, now, await ActivationAsync(scope, request, cancellationToken).ConfigureAwait(false));

    private static string DisplayState(OrganizationOnboardingRequest request, DateTimeOffset now, OrganizationActivation? activation) => request.OnboardingStateName switch
    {
        OrganizationOnboardingStates.PendingApproval => IsTimeExpired(request, now) ? OrganizationOnboardingDisplayStates.Expired : OrganizationOnboardingDisplayStates.AwaitingApproval,
        OrganizationOnboardingStates.AwaitingKeyCustody => IsTimeExpired(request, now) ? OrganizationOnboardingDisplayStates.Expired : OrganizationOnboardingDisplayStates.AwaitingKeyProvisioning,
        OrganizationOnboardingStates.Initialized => activation?.Status == "Active" ? OrganizationOnboardingDisplayStates.Active : OrganizationOnboardingDisplayStates.Activating,
        OrganizationOnboardingStates.Rejected => OrganizationOnboardingDisplayStates.Rejected,
        OrganizationOnboardingStates.Cancelled => OrganizationOnboardingDisplayStates.Cancelled,
        _ => OrganizationOnboardingDisplayStates.Expired,
    };

    private OrganizationOnboardingDecisionResponse Response(bool dryRun, AuthorityException? refusal, OrganizationOnboardingRequest request, string decision, string statementJson, string statementSha256, string outcome,
        List<OrganizationOnboardingApproval> approvals, Guid? commandId, string? archiveState, bool replayed, DateTimeOffset now, Guid correlationId) =>
        new(dryRun, refusal is null, refusal?.ErrorCode, refusal?.Detail, request.OrganizationOnboardingRequestId, request.ReservedOrganizationId, decision, statementJson, statementSha256, outcome, Recorded(approvals),
            OrganizationOnboardingPolicy.RequiredApprovals, request.OnboardingStateName, DisplayState(request, now, null), commandId, archiveState, replayed, platformPolicy.Assurance, correlationId);

    private static object DecisionDetail(DecisionContext c, string decision, VerifiedOperatorEvidence evidence, object extra) => new
    {
        organization_onboarding_request_id = c.Request.OrganizationOnboardingRequestId,
        organization_id = c.Request.ReservedOrganizationId,
        platform_operator_id = c.Me.PlatformOperatorId,
        decision,
        statement_sha256 = c.StatementSha256,
        justification = c.Justification,
        evidence_kind = evidence.EvidenceKind,
        evidence_identifier = evidence.EvidenceIdentifier,
        evidence_sha256 = Convert.ToHexStringLower(evidence.EvidenceSha256),
        evidence_key_identifier = evidence.KeyIdentifier,
        resulting_state = c.Request.OnboardingStateName,
        extra,
        correlation_id = c.CorrelationId,
    };

    private static int Recorded(IEnumerable<OrganizationOnboardingApproval> approvals) =>
        approvals.Where(a => a.ApprovalStateName is OnboardingApprovalStates.Valid or OnboardingApprovalStates.Consumed).Select(a => a.PlatformOperatorId).Distinct().Count();

    private static async Task<HashSet<Guid>> ActiveOperatorIdsAsync(IAuthorityScope scope, CancellationToken cancellationToken) =>
        (await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false)).Where(o => o.OperatorStatusName == PlatformOperatorStates.Active).Select(o => o.PlatformOperatorId).ToHashSet();

    /// <summary>Valid approvals of operators who are no longer active: they no longer count and are closed by the next decision.</summary>
    private static List<OrganizationOnboardingApproval> Stale(List<OrganizationOnboardingApproval> approvals, HashSet<Guid> active, PlatformOperator me) =>
        approvals.Where(a => a.ApprovalStateName == OnboardingApprovalStates.Valid && a.PlatformOperatorId != me.PlatformOperatorId && !active.Contains(a.PlatformOperatorId)).ToList();

    private static void CloseApproval(OrganizationOnboardingApproval approval, string state, string? reason, DateTimeOffset now)
    {
        approval.ApprovalStateName = state;
        if (state is OnboardingApprovalStates.Withdrawn or OnboardingApprovalStates.Invalidated)
        {
            approval.ApprovalClosedOn = now;
            approval.ApprovalClosureReasonName = reason;
        }
    }

    private static void Close(OrganizationOnboardingRequest request, string state, string reason, Guid? closedBy, string? justification, DateTimeOffset now)
    {
        request.OnboardingStateName = state;
        request.OnboardingClosedOn = now;
        request.OnboardingClosureReasonName = reason;
        request.ClosedByPlatformOperatorId = closedBy;
        request.ClosureJustificationText = justification;
        request.OnboardingRowRevision++;
    }

    private static bool IsTimeExpired(OrganizationOnboardingRequest request, DateTimeOffset now) => request.OnboardingStateName switch
    {
        OrganizationOnboardingStates.PendingApproval => request.RequestExpiresOn <= now,
        OrganizationOnboardingStates.AwaitingKeyCustody => request.KeyCustodyExpiresOn is not { } until || until <= now,
        _ => false,
    };

    private static AuthorityException NotPending(OrganizationOnboardingRequest request) =>
        new(AuthorityErrorCodes.OrganizationOnboardingNotPending, 409, false, $"the request is {request.OnboardingStateName}; this decision does not apply");

    private static AuthorityException Expired() =>
        new(AuthorityErrorCodes.OrganizationOnboardingExpired, 409, false, "the request's approval or key custody window has passed; create a new request");

    private static (string Json, byte[] Digest) Statement(OrganizationOnboardingRequest request, List<OrganizationOnboardingOwner> owners)
    {
        var statement = new OrganizationOnboardingStatement(StatementVersion, PlatformOperationKinds.OrganizationOnboarding, request.OrganizationOnboardingRequestId, request.ReservedOrganizationId,
            request.OrganizationDisplayName, request.OrganizationNormalizedName, request.IdentityIssuerUri,
            owners.OrderBy(o => o.OwnerOrdinalNumber).Select(o => new OrganizationOnboardingOwnerStatement(o.ReservedAuthorityUserId, o.OwnerIssuerUri, o.OwnerSubjectIdentifier, o.OwnerDisplayNameHint, o.OwnerVerificationMethodName, o.OwnerVerificationReferenceText)).ToList(),
            owners.Count, request.IntegrityKeyName, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.RequestReasonText))), request.CreatedByPlatformOperatorId,
            request.RequestCreatedOn, request.RequestExpiresOn);
        var json = CanonicalJson.Serialize(statement);
        return (json, CanonicalJson.Digest(json));
    }

    // ---- input validation ----

    private sealed record DraftOwner(string IssuerUri, string SubjectIdentifier, string DisplayNameHint, string VerificationMethod, string VerificationReference);

    private sealed record Draft(string DisplayName, string NormalizedName, IReadOnlyList<DraftOwner> Owners, string Justification);

    private Draft Validate(OrganizationOnboardingCreateRequest input)
    {
        var name = input.OrganizationDisplayName?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 200 || name.Any(char.IsControl))
        {
            throw AuthorityException.Invalid("an organization name of 2 to 200 characters is required");
        }

        var normalized = NormalizeName(name);
        if (normalized.Length < 2)
        {
            throw AuthorityException.Invalid("the organization name must contain at least two visible characters");
        }

        var justification = PlatformOperatorAccess.RequireJustification(input.Justification);
        var raw = input.InitialOwners ?? [];
        if (raw.Count < OrganizationOnboardingPolicy.MinimumOwners)
        {
            throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnersInsufficient, 400, false, $"at least {OrganizationOnboardingPolicy.MinimumOwners} initial owners are required ({OrganizationOnboardingPolicy.RecommendedOwners} recommended)");
        }

        if (raw.Count > OrganizationOnboardingPolicy.MaximumOwners)
        {
            throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, 400, false, $"at most {OrganizationOnboardingPolicy.MaximumOwners} initial owners");
        }

        var owners = new List<DraftOwner>();
        foreach (var o in raw)
        {
            var issuer = o.IssuerUri?.Trim() ?? string.Empty;
            var subject = o.SubjectIdentifier?.Trim() ?? string.Empty;
            var hint = o.DisplayNameHint?.Trim() ?? string.Empty;
            var method = o.VerificationMethod?.Trim() ?? string.Empty;
            var reference = o.VerificationReference?.Trim() ?? string.Empty;
            if (!policy.ApprovedIdentityIssuers.Contains(issuer, StringComparer.Ordinal))
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, 400, false, "every owner's issuer must be one of the approved organization identity issuers");
            }

            if (subject.Length > 512 || subject.Contains('@', StringComparison.Ordinal) || !CommandPlanner.IsValidSubject(issuer, subject))
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, 400, false, "an owner subject must be the identity provider's stable subject (Entra: the object id GUID), never an email address");
            }

            if (hint.Length is < 1 or > 200 || hint.Any(char.IsControl))
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, 400, false, "every owner needs a display name of 1 to 200 characters (display only)");
            }

            if (!OnboardingVerificationMethods.All.Contains(method, StringComparer.Ordinal) || !VerificationReferencePattern().IsMatch(reference))
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, 400, false, "every owner needs a verification method and a verification reference of 3 to 100 characters");
            }

            owners.Add(new DraftOwner(issuer, subject, hint, method, reference));
        }

        if (owners.Select(o => (o.IssuerUri, o.SubjectIdentifier)).Distinct().Count() != owners.Count)
        {
            throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, 400, false, "each initial owner must be a different identity");
        }

        if (owners.Select(o => o.IssuerUri).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIssuerMismatch, 400, false, "all initial owners must come from the same identity issuer (the organization has one issuer)");
        }

        return new Draft(name, normalized, owners, justification);
    }

    public static string NormalizeName(string name) =>
        WhitespaceRun().Replace(name.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), " ").Trim();

    // ---- cursors and journal plumbing ----

    private sealed record CursorPosition(string SortKey, Guid Id);

    internal static string Binding(string kind, PlatformOperator me, string filter, string? text, int limit) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{me.PlatformOperatorId:D}\n{filter}\n{text}\n{limit}")))[..32];

    internal static string WriteCursor(string sortKey, Guid id, string binding)
    {
        var payload = $"v1|{Convert.ToHexStringLower(Encoding.UTF8.GetBytes(sortKey))}|{id:N}|{binding}";
        var mac = Convert.ToHexStringLower(HMACSHA256.HashData(CursorKey, Encoding.UTF8.GetBytes(payload)));
        return System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload + "|" + mac));
    }

    private static CursorPosition ReadCursor(string cursor, string binding)
    {
        var (sortKey, id) = ReadCursorParts(cursor, binding);
        return new CursorPosition(sortKey, id);
    }

    internal static (string SortKey, Guid Id) ReadCursorParts(string cursor, string binding)
    {
        try
        {
            var text = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(cursor));
            var parts = text.Split('|');
            if (parts.Length == 5 && parts[0] == "v1" && parts[3] == binding)
            {
                var expected = Convert.ToHexStringLower(HMACSHA256.HashData(CursorKey, Encoding.UTF8.GetBytes(string.Join('|', parts.Take(4)))));
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[4]), Encoding.ASCII.GetBytes(expected)))
                {
                    return (Encoding.UTF8.GetString(Convert.FromHexString(parts[1])), Guid.ParseExact(parts[2], "N"));
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
        }

        throw new AuthorityException(AuthorityErrorCodes.CursorInvalid, 400, false, "the cursor is not valid for this operator and filter");
    }

    private async Task HeadForReadAsync(IAuthorityScope scope, Guid correlationId, CancellationToken cancellationToken)
    {
        var head = await scope.ReadPlatformJournalAsync(cancellationToken).ConfigureAwait(false) ?? new PlatformJournalHead();
        await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TryDeliverAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await journal.DeliverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveUnavailableException)
        {
            return ArchiveDeliveryStates.Pending;
        }
    }

    private sealed record InitializeOwner(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier);

    private sealed record InitializeApproval(Guid PlatformOperatorId, string EvidenceSha256);

    private sealed record InitializePayload(Guid OrganizationOnboardingRequestId, string StatementSha256, IReadOnlyList<InitializeOwner> Owners, IReadOnlyList<InitializeApproval> Approvals, Guid InitializedByPlatformOperatorId, string ApprovalAssurance);

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,128}$")]
    private static partial Regex IdempotencyKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._:/#-]{2,99}$")]
    private static partial Regex VerificationReferencePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
