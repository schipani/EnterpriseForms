using System.Globalization;
using EnterpriseForms.Application.Common;
using Microsoft.Extensions.Primitives;

namespace EnterpriseForms.Api.Features.Sharing;

internal static class ShareRevisionPrecondition
{
    private const int RevisionStart = 1 + 36 + 1; // Opening quote, GUID in D format, and separator.

    public static string Format(Guid recordShareId, long shareRevision)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shareRevision);
        return string.Create(CultureInfo.InvariantCulture, $"\"{recordShareId:D}:{shareRevision}\"");
    }

    public static long Require(StringValues ifMatch, Guid recordShareId)
    {
        if (ifMatch.Count == 0)
        {
            throw new BusinessException("PRECONDITION_REQUIRED", StatusCodes.Status428PreconditionRequired,
                "If-Match with the current share revision ETag is required");
        }

        var tag = ifMatch.Count == 1 ? ifMatch[0]?.Trim(' ', '\t') : null;
        if (tag is null || tag.Length <= RevisionStart + 1 || tag[^1] != '"' ||
            !long.TryParse(tag.AsSpan(RevisionStart, tag.Length - RevisionStart - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision <= 0 || !string.Equals(tag, Format(recordShareId, revision), StringComparison.Ordinal))
        {
            throw new BusinessException("PRECONDITION_FAILED", StatusCodes.Status412PreconditionFailed,
                "If-Match must carry a single strong revision ETag for this share");
        }

        // The Authority compares the revision atomically with the scope update.
        return revision;
    }
}
