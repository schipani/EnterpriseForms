using AuthorizationAuthority.Application.Common;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// C10 (independent review D-1, partial; WP-0 Section 3.6): the attachment version number is the evidence a document share's boundary
/// is compared with, so on every content write path except protection migration (which keeps the row's version) an attachment leaf of
/// a candidate must carry a positive version number that is not below the accepted one. A version number can therefore never be
/// lowered into an earlier share's boundary. Enforced at Prepare and re-derived at Commit from the verified staged rows and the accepted
/// leaves, so it is not a Prepare-only rule (docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §3.3).
/// <para>
/// Not covered (open, WP-0 Section 10.3): new content that keeps the accepted number. Every carried row is re-staged with a new
/// operation binding, so Commit cannot tell an unchanged attachment from new bytes under the same number without accepted content
/// evidence, which the Authority does not hold; a Prepare-only comparison would need a content acceptance rule version increment.
/// </para>
/// </summary>
internal static class AttachmentVersionRules
{
    public const string RefusalCode = "ATTACHMENT_VERSION_NOT_INCREASING";

    /// <param name="staged">Each attachment leaf of the candidate with its staged version number.</param>
    /// <param name="accepted">The accepted version evidence of each attachment of the current head (absent or null when it has none).</param>
    public static void Require(IEnumerable<(Guid ItemId, int? Version)> staged, IReadOnlyDictionary<Guid, int?> accepted)
    {
        foreach (var (itemId, version) in staged)
        {
            if (version is not { } number || number <= 0)
            {
                throw Refused("an attachment version needs a positive version number");
            }

            if (accepted.GetValueOrDefault(itemId) is { } current && number < current)
            {
                throw Refused("an attachment's version number can never decrease");
            }
        }
    }

    private static AuthorityException Refused(string detail) => AuthorityException.Invalid(RefusalCode + ": " + detail);
}
