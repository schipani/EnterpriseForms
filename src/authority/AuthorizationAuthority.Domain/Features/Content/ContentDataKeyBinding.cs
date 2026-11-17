namespace AuthorizationAuthority.Domain.Features.Content;

/// <summary>
/// E3-M2e (docs/security/E3_M2E_CONTRACT_CLOSURE.md §3; OD-E3-16): one Envelope v2 data-key identifier, bound permanently to the single
/// content operation allowed to introduce it, its organization, record, payload kind and key commitment. Inserted in the Prepare
/// transaction (DK-5, OD-M2e-02) and never updated or deleted (P-2; the runtime role holds SELECT and INSERT only). "Accepted" is
/// derived from the binding operation's Committed state, never stored. The identifier is unique across every organization
/// (OD-M2e-01) and stays reserved when its operation aborts or expires (OD-M2e-03).
/// </summary>
public sealed class ContentDataKeyBinding
{
    public Guid ContentDataKeyBindingId { get; set; }

    public Guid DataKeyIdentifier { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormRecordId { get; set; }

    /// <summary>1 field-value batch, 2 document version (<see cref="ContentDataKeyBindingKinds"/>).</summary>
    public short EnvelopePayloadKindCode { get; set; }

    public byte[] DataKeyCommitmentBytes { get; set; } = [];

    /// <summary>The number of rows that carried the identifier in the binding operation (a document version: exactly 1).</summary>
    public int BindingItemCount { get; set; }

    public Guid BindingContentChangeOperationId { get; set; }

    public DateTimeOffset DataKeyBoundOn { get; set; }
}

/// <summary>The closed payload-kind codes of <see cref="ContentDataKeyBinding.EnvelopePayloadKindCode"/>.</summary>
public static class ContentDataKeyBindingKinds
{
    public const short FieldValueBatch = 1;
    public const short DocumentVersion = 2;
}
