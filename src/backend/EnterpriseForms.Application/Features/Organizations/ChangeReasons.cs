using EnterpriseForms.Application.Common;

namespace EnterpriseForms.Application.Features.Organizations;

/// <summary>
/// The reason an organization-wide administrative change must carry (regional defaults, branding, logo): 3 to 500 characters after
/// trimming, recorded in the audit event written in the same transaction as the change. Personal preferences do not need one.
/// </summary>
public static class ChangeReasons
{
    public const int MinimumLength = 3;
    public const int MaximumLength = 500;

    public static string Require(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < MinimumLength or > MaximumLength)
        {
            throw new BusinessException("INVALID_REQUEST", 422, $"a reason of {MinimumLength}..{MaximumLength} characters is required");
        }

        return trimmed;
    }
}
