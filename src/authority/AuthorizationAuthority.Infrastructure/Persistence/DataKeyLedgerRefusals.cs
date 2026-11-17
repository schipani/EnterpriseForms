using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Content;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// E3-M2e (docs/security/E3_M2E_CONTRACT_CLOSURE.md §3.2): database refusals of a data-key ledger write.
/// <list type="bullet">
/// <item>L-3: SQLSTATE 23505 on <c>uq_content_data_key_binding_identifier</c> (a concurrent binding of the same identifier, typically
/// from another organization, which no organization lock serializes) is reported as the approved refusal
/// <c>422 ENVELOPE_INVALID: DATA_KEY_IDENTIFIER_REUSED</c>. The whole transaction (operation, candidate leaves, bindings) rolls back,
/// so no signature computed for it ever leaves the Authority.</item>
/// <item>L-12: SQLSTATE 23514 raised by the P-1 binding guard or the P-2 immutability triggers is an invariant failure: an alert and
/// <c>503 DATA_KEY_LEDGER_INVARIANT_VIOLATED</c>, never success and never <c>OPERATION_RULES_OUTDATED</c>.</item>
/// </list>
/// Any other database error is left untouched.
/// </summary>
public static class DataKeyLedgerRefusals
{
    private const string UniqueViolation = "23505";
    private const string CheckViolation = "23514";

    /// <summary>Null when <paramref name="exception"/> is not a ledger refusal (the caller rethrows it unchanged).</summary>
    public static AuthorityException? Translate(DbUpdateException exception, ISecurityAlertSink? alerts)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is not PostgresException pg)
            {
                continue;
            }

            if (pg.SqlState == UniqueViolation && pg.ConstraintName == ContentDataKeyLedgerSql.UniqueIdentifierIndex)
            {
                return EnvelopeRules.Refused(EnvelopeV2Rules.Reasons.DataKeyIdentifierReused);
            }

            if (pg.SqlState == CheckViolation && pg.ConstraintName is ContentDataKeyLedgerSql.GuardTrigger or ContentDataKeyLedgerSql.PermanentTrigger)
            {
                var binding = exception.Entries.Select(x => x.Entity).OfType<ContentDataKeyBinding>().FirstOrDefault();
                alerts?.Raise(new SecurityAlert(DataKeyLedgerFailures.InvariantAlertKind, "Critical", binding?.OrganizationId ?? Guid.Empty, binding?.BindingContentChangeOperationId ?? Guid.Empty, null,
                    $"the database refused a data-key ledger write ({pg.ConstraintName}); this build applies content acceptance rule version {ContentAcceptanceRules.CurrentVersion}", Guid.Empty));
                return DataKeyLedgerFailures.InvariantViolated();
            }
        }

        return null;
    }
}
