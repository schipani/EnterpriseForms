using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Membership;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Infrastructure.Faults;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.OperatorPlane;

/// <summary>Operator plane: requires the operator credential (never the business workload credential).</summary>
public static class OperatorEndpoints
{
    public static IEndpointRouteBuilder MapOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/operator").WithTags("Operator").RequireAuthorization(InternalAuthentication.OperatorPolicy);

        group.MapPost("/organizations", static async Task<Ok<BootstrapOrganizationResponse>> (BootstrapOrganizationRequest request, HttpContext context, OrganizationBootstrapService service, CancellationToken cancellationToken) =>
            {
                var operatorName = context.User.FindFirst("workload_name")?.Value ?? "operator";
                return TypedResults.Ok(await service.BootstrapAsync(request, operatorName, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("BootstrapOrganization")
            .WithSummary("Provisions organization state, identity bindings, organization roles and the organization integrity key (Stage A stand-in for the PIM/dual-control workflow).");

        group.MapGet("/organizations/{organizationId:guid}/gate", static async Task<Ok<OrganizationGateStatus>> (Guid organizationId, OrganizationBootstrapService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.GetGateStatusAsync(organizationId, cancellationToken).ConfigureAwait(false)))
            .WithName("GetOrganizationGate");

        // DR-C7-3: first and recovery Security Administrator (operator credential + two independent approvals; never the business API).
        group.MapGet("/organizations/{organizationId:guid}/security-administrators", static async Task<Ok<OperatorSecurityAdministratorStatus>> (Guid organizationId, HttpContext context, SecurityAdministratorProvisioningService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.GetStatusAsync(organizationId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetOperatorSecurityAdministrators")
            .WithSummary("Operator view: the organization's verified members and Security Administrators (identities only), and whether dual-controlled approval is available here.");

        group.MapPost("/organizations/{organizationId:guid}/security-administrators/first-appointment", static async Task<Ok<SecurityAdministratorProvisioningResponse>> (Guid organizationId, FirstSecurityAdministratorRequest request, HttpContext context, SecurityAdministratorProvisioningService service, CancellationToken cancellationToken) =>
            {
                var operatorName = context.User.FindFirst("workload_name")?.Value ?? "operator";
                return TypedResults.Ok(await service.FirstAppointmentAsync(organizationId, request, operatorName, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("AppointFirstSecurityAdministrator")
            .WithSummary("DR-C7-3: first Security Administrator of an organization with none usable. dry_run previews the exact change and the statement to approve; execution needs two ES256 approvals from distinct registered approvers (OPERATOR_DUAL_CONTROL_UNAVAILABLE where approvals are not available, e.g. staging/production). Single-use operation id; journaled and alerted.");

        group.MapPost("/organizations/{organizationId:guid}/security-administrators/recovery", static async Task<Ok<SecurityAdministratorProvisioningResponse>> (Guid organizationId, SecurityAdministratorRecoveryRequest request, HttpContext context, SecurityAdministratorProvisioningService service, CancellationToken cancellationToken) =>
            {
                var operatorName = context.User.FindFirst("workload_name")?.Value ?? "operator";
                return TypedResults.Ok(await service.RecoverAsync(organizationId, request, operatorName, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("RecoverSecurityAdministrator")
            .WithSummary("DR-C7-3: recovery when the organization's Security Administrators are unavailable. Incident reference required; explicit scope (one appointee, named administrators removed); two independent approvals; critical security alert; journaled.");

        // C8 M2 (AC-7.4): owner membership requests, decided only here (approval: two independent approvals; rejection: one operator).
        group.MapGet("/organizations/{organizationId:guid}/membership-requests", static async Task<Ok<OperatorMembershipRequests>> (Guid organizationId, HttpContext context, MembershipProvisioningService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ListAsync(organizationId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("ListOrganizationMembershipRequests");

        group.MapPost("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}/approve", static async Task<Ok<MembershipApprovalResponse>> (Guid organizationId, Guid requestId, MembershipApprovalRequest request, HttpContext context, MembershipProvisioningService service, CancellationToken cancellationToken) =>
            {
                var operatorName = context.User.FindFirst("workload_name")?.Value ?? "operator";
                return TypedResults.Ok(await service.ApproveAsync(organizationId, requestId, request, operatorName, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("ApproveOrganizationMembership")
            .WithSummary("AC-7.4: dry run, then two ES256 approvals from distinct registered approvers over the exact statement (Development/Testing only until PIM). Re-checks request state, expiry, the requesting owner, the identity and its issuer inside the organization lock; creates the identity binding (and optional designer role) as one journaled command.");

        group.MapPost("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}/reject", static async Task<Ok<OrganizationMembershipRequestEntry>> (Guid organizationId, Guid requestId, MembershipRejectionRequest request, HttpContext context, MembershipProvisioningService service, CancellationToken cancellationToken) =>
            {
                var operatorName = context.User.FindFirst("workload_name")?.Value ?? "operator";
                return TypedResults.Ok(await service.RejectAsync(organizationId, requestId, request, operatorName, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("RejectOrganizationMembership");

        // C8 M2 Stage 1: Platform Operator status, the recovery ceremony (two custodian approvals, only below two active operators)
        // and platform journal replay after a database restore. Never reachable with the Platform Console or business credentials.
        group.MapGet("/platform-operators", static async Task<Ok<PlatformOperatorPlaneStatus>> (HttpContext context, PlatformOperatorRegistryService registry, CancellationToken cancellationToken) =>
                TypedResults.Ok(await registry.GetPlaneStatusAsync(InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetPlatformOperatorPlaneStatus");

        group.MapPost("/platform-operators/bootstrap", static async Task<Ok<PlatformOperatorBootstrapResponse>> (PlatformOperatorBootstrapRequest request, HttpContext context, PlatformOperatorRegistryService registry, CancellationToken cancellationToken) =>
            {
                var operatorName = context.User.FindFirst("workload_name")?.Value ?? "operator";
                return TypedResults.Ok(await registry.BootstrapAsync(request, operatorName, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("BootstrapPlatformOperators")
            .WithSummary("Recovery ceremony: registers new Platform Operator identities only while fewer than two are active. Dry run first; execution needs two ES256 custodian approvals (DR-C7-3 keys) over the statement. Critical alert; platform journal.");

        group.MapPost("/platform-journal/replay", static async Task<Ok<PlatformJournalReplayResponse>> (HttpContext context, PlatformJournal journal, CancellationToken cancellationToken) =>
            {
                var operatorName = context.User.FindFirst("workload_name")?.Value ?? "operator";
                return TypedResults.Ok(await journal.ReplayAsync(operatorName, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("ReplayPlatformJournal")
            .WithSummary("After a database restore: re-applies archived platform events beyond the database head, each verified against the chain; quarantines on mismatch. Never chooses or edits state.");

        group.MapPost("/fault-injection", static Results<Ok<FaultInjectionRequest>, ProblemHttpResult> (FaultInjectionRequest request, IServiceProvider services, IHostEnvironment environment) =>
            {
                var injector = services.GetService<ProcessCrashFaultInjector>();
                if (injector is null || !environment.IsEnvironment("Testing"))
                {
                    return TypedResults.Problem(title: "Fault injection disabled", detail: "FaultInjection:Enabled is honoured only in the Testing environment.", statusCode: StatusCodes.Status403Forbidden);
                }

                if (request.FaultName is not (FaultNames.None or FaultNames.CrashAfterJournalCommit or FaultNames.CrashAfterProjectionWrite or FaultNames.CrashAfterContentHeadCommit or FaultNames.CrashAfterPrepareCommit or FaultNames.PauseBeforeContentCommit))
                {
                    throw AuthorityException.Invalid("unknown fault name");
                }

                injector.Arm(request.FaultName == FaultNames.None ? string.Empty : request.FaultName);
                return TypedResults.Ok(new FaultInjectionRequest(injector.Armed.Length == 0 ? FaultNames.None : injector.Armed));
            })
            .WithName("ArmFaultInjection")
            .WithSummary("Testing only: arms a one-shot process crash at a named protocol transition (SEC-P09).");

        group.MapGet("/fault-injection", static Results<Ok<FaultInjectionStatus>, ProblemHttpResult> (IServiceProvider services, IHostEnvironment environment) =>
            {
                var injector = services.GetService<ProcessCrashFaultInjector>();
                if (injector is null || !environment.IsEnvironment("Testing"))
                {
                    return TypedResults.Problem(title: "Fault injection disabled", detail: "FaultInjection:Enabled is honoured only in the Testing environment.", statusCode: StatusCodes.Status403Forbidden);
                }

                return TypedResults.Ok(new FaultInjectionStatus(injector.Armed.Length == 0 ? FaultNames.None : injector.Armed, injector.Paused));
            })
            .WithName("GetFaultInjection")
            .WithSummary("Testing only: armed fault and number of operations held at a pause point.");

        group.MapPost("/testing/contributor-writes", static Results<Ok<ContributorWriteSwitchState>, ProblemHttpResult> (ContributorWriteOverrideRequest request, ShareContributorWriteSwitch contributorWrites, IHostEnvironment environment) =>
            {
                if (!environment.IsEnvironment("Testing") || !contributorWrites.TestingOverrideAllowed)
                {
                    return TypedResults.Problem(title: "Override disabled", detail: "The contributor-write override exists only in the Testing environment.", statusCode: StatusCodes.Status403Forbidden);
                }

                contributorWrites.SetTestingOverride(request.Enabled);
                return TypedResults.Ok(new ContributorWriteSwitchState(contributorWrites.Enabled, contributorWrites.ConfiguredEnabled));
            })
            .WithName("OverrideContributorWrites")
            .WithSummary("Testing only: turns field-scoped contributor writes on or off for the disposable integration stack (Phase C5, D10); null restores the deployment configuration.");

        return endpoints;
    }
}
