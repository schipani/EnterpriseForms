namespace EnterpriseForms.Domain.Features.FormRecords;

/// <summary>
/// F-C7-01: the idempotency fingerprint of one record-creation request (one row per idempotency key, i.e. per derived
/// record id). It lets a retry of the same request complete the creation and makes a different request with the same
/// key a conflict. The fingerprint is HMAC-SHA-256 over the canonical request under a random per-attempt key; that key is
/// stored only as an application-encryption envelope (KEK-wrapped, AAD-bound to tenant, record and attempt), so the
/// business database holds neither request values nor a digest that could be tested against guessed values.
/// Insert-only; never authoritative — the Authority still decides and verifies everything the record contains.
/// </summary>
public sealed class FormRecordCreationAttempt
{
    public Guid FormRecordCreationAttemptId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The record id derived from the idempotency key (unique per tenant).</summary>
    public Guid FormRecordId { get; set; }

    /// <summary>HMAC-SHA-256 (32 bytes) of the canonical creation request.</summary>
    public byte[] CreationRequestFingerprint { get; set; } = [];

    /// <summary>The per-attempt HMAC key as a field-encryption envelope (JSON of the envelope fields; no plaintext key).</summary>
    public string FingerprintKeyEnvelopeJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }
}
