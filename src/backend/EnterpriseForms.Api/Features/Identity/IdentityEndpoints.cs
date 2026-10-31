using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.Identity.EnsureProjection;
using EnterpriseForms.Application.Features.Identity.MemberAdministration;
using EnterpriseForms.Application.Features.Identity.MemberDirectory;
using EnterpriseForms.Application.Features.Identity.Presence;
using EnterpriseForms.Application.Features.Identity.Profile;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Identity;

public static class IdentityEndpoints
{
    public const string OwnerName = "Identity";

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/users").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapGet("/me", static async Task<Ok<CurrentUserResponse>> (HttpContext http, EnsureIdentityProjectionHandler handler, UserProfileHandlers profiles, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                await handler.HandleAsync(context, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new CurrentUserResponse(context.OrganizationId, context.IssuerUri, context.SubjectIdentifier, await profiles.EffectiveDisplayNameAsync(context, cancellationToken).ConfigureAwait(false)));
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

        // C8 M2: directory profile (non-security fields). Own profile for everyone; anyone's profile for an organization owner.
        group.MapGet("/me/profile", static async Task<Ok<UserProfileResponse>> (HttpContext http, UserProfileHandlers handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.GetAsync(context, context.LocalUserId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetMyProfile")
            .WithSummary("My profile: display name, preferred name and job title with the revision token; verified identity shown read-only.");

        group.MapPut("/me/profile", static async Task<Ok<UserProfileResponse>> (UpdateUserProfileRequest request, HttpContext http, UserProfileHandlers handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.UpdateAsync(context, context.LocalUserId, request, cancellationToken).ConfigureAwait(false));
            })
            .WithName("UpdateMyProfile")
            .WithSummary("Edit my display name, preferred name and job title (revision-checked, audited). Identity, membership and roles are never writable here.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Shared presence (informational only; never an authorization input).
        var presence = endpoints.MapGroup(ApiVersions.V1Prefix + "/presence").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        presence.MapPost("/heartbeat", static async Task<NoContent> (PresenceHeartbeatRequest request, HttpContext http, PresenceHandlers handler, CancellationToken cancellationToken) =>
            {
                await handler.HeartbeatAsync(BusinessAuthentication.RequireContext(http), request, cancellationToken).ConfigureAwait(false);
                return TypedResults.NoContent();
            })
            .WithName("PresenceHeartbeat")
            .WithSummary("Authenticated heartbeat for the signed-in user only (the identity comes from the session, never from the request). interactionAgeSeconds is how long ago the person last interacted.");

        presence.MapGet("/", static async Task<Ok<PresenceQueryResponse>> (string? userIds, HttpContext http, PresenceHandlers handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                var ids = new List<Guid>();
                foreach (var part in (userIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    ids.Add(Guid.TryParse(part, out var id) ? id : throw new BusinessException("INVALID_REQUEST", 422, "userIds must be a comma-separated list of ids"));
                }

                return TypedResults.Ok(await handler.QueryAsync(BusinessAuthentication.RequireContext(http), ids, cancellationToken).ConfigureAwait(false));
            })
            .WithName("QueryPresence")
            .WithSummary("Presence (Available, Away, Offline, Unknown) of up to 100 people of the caller's own organization. Informational only.");

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

        members.MapGet("/{userId:guid}/profile", static async Task<Ok<UserProfileResponse>> (Guid userId, HttpContext http, UserProfileHandlers handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.GetAsync(BusinessAuthentication.RequireContext(http), userId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetMemberProfile")
            .WithSummary("A person's directory profile (the person themselves or an organization owner of the same organization).");

        members.MapPut("/{userId:guid}/profile", static async Task<Ok<UserProfileResponse>> (Guid userId, UpdateUserProfileRequest request, HttpContext http, UserProfileHandlers handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.UpdateAsync(BusinessAuthentication.RequireContext(http), userId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("UpdateMemberProfile")
            .WithSummary("Edit a person's display name, preferred name and job title (organization owner or the person; revision-checked, audited). Verified identity, membership and roles cannot be changed.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        var organization = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        organization.MapGet("/owner-proposals", static async Task<Ok<OwnerProposalPageResponse>> (string? q, string? status, int? pageSize, string? continuationToken, HttpContext http, GetApprovalsHandler handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.GetProposalsAsync(BusinessAuthentication.RequireContext(http), new ApprovalQuery(q, status, pageSize, continuationToken), cancellationToken).ConfigureAwait(false));
            })
            .WithName("ListOwnerProposals")
            .WithSummary("One server page (default 5, newest first) of owner proposals, searchable (q) and filterable by status (Pending, Approved, Rejected, Expired); opaque cursor. Owners and Security Administrators only.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        organization.MapGet("/membership-requests", static async Task<Ok<MembershipRequestPageResponse>> (string? q, string? status, int? pageSize, string? continuationToken, HttpContext http, GetApprovalsHandler handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.GetRequestsAsync(BusinessAuthentication.RequireContext(http), new ApprovalQuery(q, status, pageSize, continuationToken), cancellationToken).ConfigureAwait(false));
            })
            .WithName("ListMembershipRequests")
            .WithSummary("One server page (default 5, newest first) of membership requests, searchable (q) and filterable by status; opaque cursor. Owners and Security Administrators only.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

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
