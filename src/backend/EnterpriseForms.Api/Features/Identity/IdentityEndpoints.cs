using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.Identity.EnsureProjection;
using EnterpriseForms.Application.Features.Identity.MemberAdministration;
using EnterpriseForms.Application.Features.Identity.MemberDirectory;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Identity;

public static class IdentityEndpoints
{
    public const string OwnerName = "Identity";

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/users").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapGet("/me", static async Task<Ok<CurrentUserResponse>> (HttpContext http, EnsureIdentityProjectionHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                await handler.HandleAsync(context, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new CurrentUserResponse(context.OrganizationId, context.IssuerUri, context.SubjectIdentifier, context.DisplayName));
            })
            .WithName("GetCurrentUser")
            .WithSummary("Verified principal of the current assertion (projection maintenance only; confers no permission).");

        // Phase C8 M1 (PG-050, read-only): people directory for organization owners and Security Administrators.
        var members = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization/members").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        members.MapGet("/", static async Task<Ok<OrganizationMemberDirectoryResponse>> (string? q, string? role, string? signInStatus, string? membershipState, int? pageSize, string? continuationToken, HttpContext http, GetMemberDirectoryHandler handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.GetAsync(BusinessAuthentication.RequireContext(http), new MemberDirectoryQuery(q, role, signInStatus, pageSize, continuationToken, membershipState), cancellationToken).ConfigureAwait(false));
            })
            .WithName("ListOrganizationMembers")
            .WithSummary("People directory (PG-050, C8 M2): one server page (default 15) of the organization's people, searchable (q: name, identity, team) and filterable (role: SecurityAdministrator|None; signInStatus: SignedIn|NotRecorded|NeverSignedIn), with an opaque cursor bound to the caller and filters. Rows: business directory plus the Authority's Security Administrator roster; for Security Administrators also every enabled Authority binding (including people who never signed in). Last sign-in, teams, Security Administrator badges and per-row Security Administrator action hints. Organization owners and Security Administrators only; 403 otherwise. Never an email key; never record content.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // C8 M2 (AC-7): membership and organization-role commands. Each needs an Idempotency-Key, a reason and (here) the
        // development step-up; the Authority decides every command and refuses it in staging and production until PB-08.
        members.MapPost("/{userId:guid}/roles", static async Task<Ok<MemberCommandResponse>> (Guid userId, MemberRoleChangeRequest request, HttpContext http, MemberAdministrationHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GrantDesignerAsync(BusinessAuthentication.RequireContext(http), userId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("GrantMemberRole")
            .WithSummary("AC-7.1: grant the Designer role (one organization owner, step-up, never oneself). The Organization Owner role is granted only by approving another owner's proposal.");

        members.MapPost("/{userId:guid}/roles/{roleName}/remove", static async Task<Ok<MemberCommandResponse>> (Guid userId, string roleName, MemberCommandRequest request, HttpContext http, MemberAdministrationHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.RevokeDesignerAsync(BusinessAuthentication.RequireContext(http), userId, roleName, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("RevokeMemberRole")
            .WithSummary("AC-7.1: revoke the Designer role (one organization owner). The Organization Owner role is revoked only through a proposal approved by another owner.");

        members.MapPost("/{userId:guid}/owner-proposals", static async Task<Ok<MemberCommandResponse>> (Guid userId, OwnerProposalRequest request, HttpContext http, MemberAdministrationHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ProposeAsync(BusinessAuthentication.RequireContext(http), userId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("ProposeOwnerChange")
            .WithSummary("AC-7.1/7.2: propose GrantOwner, RevokeOwner, DisableOwner or EnableOwner. Grants nothing; another owner (not the proposer, not the person) approves within 24 hours.");

        members.MapPost("/{userId:guid}/disable", static async Task<Ok<MemberCommandResponse>> (Guid userId, MemberCommandRequest request, HttpContext http, MemberAdministrationHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.SetMembershipAsync(BusinessAuthentication.RequireContext(http), userId, enable: false, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("DisableMember")
            .WithSummary("AC-7.2: disable a member who is not an owner (one owner). Sessions are revoked at once; roles, teams and shares are kept but unusable. Refused (OWNS_RECORDS, with a count) while the person directly owns active records, and for the last enabled Security Administrator.");

        members.MapPost("/{userId:guid}/enable", static async Task<Ok<MemberCommandResponse>> (Guid userId, MemberCommandRequest request, HttpContext http, MemberAdministrationHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.SetMembershipAsync(BusinessAuthentication.RequireContext(http), userId, enable: true, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("EnableMember")
            .WithSummary("AC-7.2: re-enable a disabled member who is not an owner (one owner). The kept roles, teams and shares become usable again; nothing new is granted.");

        var organization = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        organization.MapPost("/owner-proposals/{proposalId:guid}/approve", static async Task<Ok<MemberCommandResponse>> (Guid proposalId, MemberCommandRequest request, HttpContext http, MemberAdministrationHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ApproveProposalAsync(BusinessAuthentication.RequireContext(http), proposalId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("ApproveOwnerProposal")
            .WithSummary("AC-7.1/7.2: a second owner executes a pending proposal. The Authority re-checks the proposal (pending, unexpired, single use), that the proposer is still an enabled owner, and every role, membership, last-owner and record-ownership rule at this moment.");

        organization.MapPost("/membership-requests", static async Task<Ok<MemberCommandResponse>> (CreateMembershipRequestRequest request, HttpContext http, MemberAdministrationHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.RequestMembershipAsync(BusinessAuthentication.RequireContext(http), request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("RequestMembership")
            .WithSummary("AC-7.4: an owner asks to add a person of the organization's identity provider tenant (immutable subject id, never an email). Grants nothing: two operator approvals (Development/Testing only) create the membership; the request expires after 7 days. Membership is not sign-in readiness.");

        return endpoints;
    }
}
