using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Content;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// E3-M2b-1 CARV (§19.4): a write refused by the database fence — SQLSTATE 23514 from the commit CHECK, a floor CHECK or the
/// state guard — is translated into <c>409 OPERATION_RULES_OUTDATED</c> and raises an operational alert. The scope's
/// transaction rolls back everything it held (operation transition, manifest, leaves, identities, head, index, journal,
/// checkpoint). In a pure CARV fleet this happens only when a newer floor overtakes a running older CARV build.
/// Any other database error is left untouched.
/// </summary>
public static class ContentAcceptanceRuleRefusals
{
    public const string AlertKind = "CONTENT_ACCEPTANCE_RULE_REFUSED_BY_DATABASE";

    private const string CheckViolation = "23514";

    /// <summary>Returns the fence object that refused the write, or null when <paramref name="exception"/> is not a CARV refusal.</summary>
    public static string? RefusingObject(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: CheckViolation } pg && ContentAcceptanceRules.DatabaseObjects.IsFenceObject(pg.ConstraintName))
            {
                return pg.ConstraintName;
            }
        }

        return null;
    }

    /// <summary>Translates a CARV refusal (and alerts); null for any other exception, which the caller rethrows unchanged.</summary>
    public static AuthorityException? Translate(DbUpdateException exception, ISecurityAlertSink? alerts)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var refusing = RefusingObject(exception);
        if (refusing is null)
        {
            return null;
        }

        var operation = exception.Entries.Select(e => e.Entity).OfType<ContentChangeOperation>().FirstOrDefault();
        alerts?.Raise(new SecurityAlert(AlertKind, "Critical", operation?.OrganizationId ?? Guid.Empty, operation?.ContentChangeOperationId ?? Guid.Empty, null,
            $"the database content acceptance rule fence ({refusing}) refused a content operation write; this build applies rule version {ContentAcceptanceRules.CurrentVersion} (mixed-version fleet or a newer floor)",
            operation?.CorrelationIdentifier ?? Guid.Empty));
        return new AuthorityException(AuthorityErrorCodes.OperationRulesOutdated, 409, false, "the operation crosses content acceptance rule versions and was refused; start a fresh operation");
    }
}
