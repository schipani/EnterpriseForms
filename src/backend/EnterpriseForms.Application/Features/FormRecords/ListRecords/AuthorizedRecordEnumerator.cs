using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;

namespace EnterpriseForms.Application.Features.FormRecords.ListRecords;

/// <summary>The caller's complete discoverable record set, all pages read at one authorization revision.</summary>
public sealed record AuthorizedRecordSet(IReadOnlyList<Guid> FormRecordIds, long AuthorizationRevision);

/// <summary>
/// Reads every page of the authority's discover lookup (C-29). The pages are combined only when they were all issued at
/// the same authorization revision; when access changes mid-enumeration (the authority invalidates the continuation, or
/// a page reports a newer revision) the enumeration restarts, and after <see cref="MaximumAttempts"/> attempts it fails
/// closed with a retryable error. Above <see cref="MaximumRecords"/> the listing is refused rather than truncated. Any
/// authority failure propagates: an incomplete set is never returned.
/// </summary>
public static class AuthorizedRecordEnumerator
{
    public const int MaximumRecords = 10_000;

    public const int MaximumAttempts = 3;

    public const int AuthorityPageSize = 250;

    public static async Task<AuthorizedRecordSet> EnumerateAsync(IAuthorizationAuthorityClient authority, DelegatedUserContext user, Guid organizationId, CancellationToken cancellationToken, int maximumRecords = MaximumRecords)
    {
        ArgumentNullException.ThrowIfNull(authority);
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var ids = new List<Guid>();
            var seen = new HashSet<Guid>();
            long? revision = null;
            string? token = null;
            var restart = false;
            do
            {
                RecordDiscoveryPage page;
                try
                {
                    page = await authority.DiscoverAuthorizedRecordsAsync(user, organizationId, AuthorityPageSize, token, cancellationToken).ConfigureAwait(false);
                }
                catch (AuthorityClientException ex) when (token is not null && ex.ErrorCode == "CONTINUATION_INVALIDATED")
                {
                    restart = true;
                    break;
                }

                if (revision is not null && page.AuthorizationRevision != revision)
                {
                    restart = true;
                    break;
                }

                revision = page.AuthorizationRevision;
                foreach (var id in page.FormRecordIds)
                {
                    if (seen.Add(id))
                    {
                        ids.Add(id);
                    }
                }

                if (ids.Count > maximumRecords)
                {
                    throw new BusinessException("RECORD_LIST_TOO_LARGE", 422, $"you can access more than {maximumRecords} records, which is more than this list can show completely; nothing partial is shown");
                }

                if (page.ContinuationToken is not null && page.FormRecordIds.Count == 0)
                {
                    throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true, "the authority returned an empty page with a continuation");
                }

                token = page.ContinuationToken;
            }
            while (token is not null);

            if (!restart)
            {
                return new AuthorizedRecordSet(ids, revision ?? 0);
            }
        }

        throw new AuthorityClientException("AUTHORIZATION_STATE_CHANGED", 503, true, "access changed while your records were being listed; try again");
    }
}
