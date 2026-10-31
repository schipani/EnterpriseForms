using System.Text;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Identity;

namespace EnterpriseForms.Application.Features.Identity.Profile;

/// <summary>
/// Directory profile editing (C8 M2). Only non-security fields change: display name, preferred name, job title. The verified identity
/// (issuer, subject), tenant binding, Authority membership and organization roles are never writable here and nothing in a profile
/// confers a permission. A person edits their own profile; an organization owner (the Authority's ManageOrganization decision, the
/// same authority that governs membership) edits anyone's in the same organization. The identity provider's name is kept separately
/// and sign-in never overwrites an edited display name; resetting restores it. Each change is revision-checked and audited with
/// the names of the changed fields only.
/// </summary>
public sealed partial class UserProfileHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    public const int DisplayNameMaximumLength = 200;
    public const int PreferredNameMaximumLength = 100;
    public const int JobTitleMaximumLength = 100;

    /// <summary>The name shown for the signed-in person: the directory profile name (identity provider's name unless edited), falling back to the session's name.</summary>
    public async Task<string> EffectiveDisplayNameAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var user = await scope.FindUserAsync(context.OrganizationId, context.LocalUserId, cancellationToken).ConfigureAwait(false);
        return user is { DisplayName.Length: > 0 } ? user.DisplayName : context.DisplayName;
    }

    public async Task<UserProfileResponse> GetAsync(RequestContext context, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var canEditOthers = await RequireAccessAsync(context, userId, cancellationToken).ConfigureAwait(false);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var user = await scope.FindUserAsync(context.OrganizationId, userId, cancellationToken).ConfigureAwait(false) ?? throw NotAvailable();
        return Map(user, context.LocalUserId, canEditOthers || userId == context.LocalUserId);
    }

    public async Task<UserProfileResponse> UpdateAsync(RequestContext context, Guid userId, UpdateUserProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var canEditOthers = await RequireAccessAsync(context, userId, cancellationToken).ConfigureAwait(false);
        var preferred = Clean(request.PreferredName, PreferredNameMaximumLength, "preferred name");
        var title = Clean(request.JobTitle, JobTitleMaximumLength, "job title");
        var displayName = Clean(request.DisplayName, DisplayNameMaximumLength, "display name");
        if (displayName is not null && EmailLike().IsMatch(displayName))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a display name is not an email address; email and identity are not editable here");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var user = await scope.FindUserAsync(context.OrganizationId, userId, cancellationToken).ConfigureAwait(false) ?? throw NotAvailable();
        if (user.ProfileRevision != request.ExpectedRevision)
        {
            throw new BusinessException("PROFILE_REVISION_CONFLICT", 409, "this profile was changed by someone else; reload it and try again");
        }

        var changed = new List<string>();
        var reset = request.ResetDisplayName || displayName is null;
        if (reset)
        {
            if (user.IsDisplayNameOverridden)
            {
                user.DisplayName = user.IdentityDisplayName.Length > 0 ? user.IdentityDisplayName : user.DisplayName;
                user.IsDisplayNameOverridden = false;
                changed.Add("displayName");
            }
        }
        else if (displayName != user.DisplayName)
        {
            var followsIdentity = string.Equals(displayName, user.IdentityDisplayName, StringComparison.Ordinal);
            user.DisplayName = displayName!;
            user.IsDisplayNameOverridden = !followsIdentity;
            changed.Add("displayName");
        }

        if (!string.Equals(preferred, user.PreferredName, StringComparison.Ordinal))
        {
            user.PreferredName = preferred;
            changed.Add("preferredName");
        }

        if (!string.Equals(title, user.JobTitle, StringComparison.Ordinal))
        {
            user.JobTitle = title;
            changed.Add("jobTitle");
        }

        if (changed.Count > 0)
        {
            var now = clock.GetUtcNow();
            user.ProfileRevision++;
            user.ProfileModifiedOn = now;
            user.ProfileModifiedByUserId = context.LocalUserId;
            user.ModifiedOn = now;
            AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.UserProfileUpdated, userId == context.LocalUserId ? "own profile updated" : "profile updated by an organization owner",
                targetEntityType: "user", targetEntityId: userId, metadata: new { changedFields = changed, revision = user.ProfileRevision, self = userId == context.LocalUserId });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Map(user, context.LocalUserId, canEditOthers || userId == context.LocalUserId);
    }

    /// <summary>Self always; anyone else only for an organization owner. Another organization's user is simply not found.</summary>
    private async Task<bool> RequireAccessAsync(RequestContext context, Guid userId, CancellationToken cancellationToken)
    {
        if (userId == context.LocalUserId)
        {
            return false;
        }

        var decision = await AdministrationAccess.DecideAsync(authority, context, cancellationToken).ConfigureAwait(false);
        return decision.CanManageConfiguration ? true : throw new BusinessException("FORBIDDEN", 403, "organization owner role required to edit another person's profile");
    }

    private static BusinessException NotAvailable() => new("PROFILE_NOT_AVAILABLE", 404, "this person has no profile yet (they have not signed in)");

    private static UserProfileResponse Map(User user, Guid me, bool canEdit) =>
        new(user.UserId, user.DisplayName, user.IdentityDisplayName.Length > 0 ? user.IdentityDisplayName : user.DisplayName, user.IsDisplayNameOverridden, user.PreferredName, user.JobTitle, user.ProfileRevision, user.ProfileModifiedOn,
            user.ExternalIssuerUri, user.ExternalSubjectIdentifier, user.UserId == me, canEdit);

    /// <summary>Trims, collapses whitespace, rejects control characters; empty becomes null.</summary>
    private static string? Clean(string? value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var previousSpace = false;
        foreach (var c in value.Trim())
        {
            if (char.IsControl(c))
            {
                throw new BusinessException("INVALID_REQUEST", 422, $"the {name} contains characters that are not allowed");
            }

            var space = char.IsWhiteSpace(c);
            if (!(space && previousSpace))
            {
                builder.Append(space ? ' ' : c);
            }

            previousSpace = space;
        }

        var text = builder.ToString();
        return text.Length > maximumLength ? throw new BusinessException("INVALID_REQUEST", 422, $"the {name} can have at most {maximumLength} characters") : text;
    }

    [GeneratedRegex(@"^\S+@\S+\.\S+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailLike();
}
