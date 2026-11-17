using AuthorizationAuthority.Application.Common;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>E3-M2e (docs/security/E3_M2E_CONTRACT_CLOSURE.md §3): what callers and alerts see when the data-key ledger is not enforced or is contradicted.</summary>
public static class DataKeyLedgerFailures
{
    public const string AlertKind = "CONTENT_DATA_KEY_LEDGER_NOT_ENFORCED";

    public const string InvariantAlertKind = "CONTENT_DATA_KEY_LEDGER_INVARIANT_VIOLATED";

    /// <summary>The operation abort reason and the journaled incident reason of a DK-6 or P-4 mismatch.</summary>
    public const string BindingMismatchReason = "DATA_KEY_BINDING_MISMATCH";

    /// <summary>L-13 refusal: no database internals and no reason code reach the caller.</summary>
    public static AuthorityException NotEnforced() =>
        new(AuthorityErrorCodes.ContentDataKeyLedgerNotEnforced, 503, false, "content writes that use the data-key ledger are refused: the ledger is not verified; operator recovery is required");

    /// <summary>L-12 refusal: the database refused a ledger write (binding guard or immutability). Never success, never an outdated-rules conflict.</summary>
    public static AuthorityException InvariantViolated() =>
        new(AuthorityErrorCodes.DataKeyLedgerInvariantViolated, 503, false, "the data-key ledger refused the write; the operation was not accepted");

    /// <summary>DK-6 / P-4 refusal after the abort and incident are committed.</summary>
    public static AuthorityException BindingMismatch() =>
        new(AuthorityErrorCodes.DataKeyBindingMismatch, 409, false, "the operation's Envelope v2 data keys disagree with the data-key ledger; the operation was aborted");
}
