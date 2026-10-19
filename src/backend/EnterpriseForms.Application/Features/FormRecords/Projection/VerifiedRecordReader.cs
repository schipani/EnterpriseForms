using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Projection;

/// <summary>Result of a verified read: the accepted head, the authority's verification, and the exact staged rows.</summary>
public sealed record VerifiedRecord(
    AcceptedContentRevision Head,
    VerifyRecordProjectionResult Verification,
    FormRecordRevision Header,
    IReadOnlyList<ISignedContentRow> AllRows,
    IReadOnlyList<ISignedContentRow> AuthorizedRows,
    FormDefinition Definition,
    FormVersion LayoutVersion,
    FormLayout Layout,
    IReadOnlyList<FieldDefinition> Fields,
    IReadOnlyList<FieldOption> Options,
    Encryption.IFieldEncryptionService Encryption)
{
    public IReadOnlyDictionary<Guid, FieldDefinition> FieldsById => Fields.ToDictionary(f => f.FieldDefinitionId);

    public IReadOnlyDictionary<Guid, FieldOption> OptionsById => Options.ToDictionary(o => o.FieldOptionId);

    public IEnumerable<FieldValueRowBase> AuthorizedValues => AuthorizedRows.OfType<FieldValueRowBase>();

    public IEnumerable<FileAttachmentVersion> AuthorizedAttachments => AuthorizedRows.OfType<FileAttachmentVersion>();

    public bool IsComplete => Verification.IsCompleteProjection;

    public string ETag => "\"" + Head.CommittedContentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + (Head.CommittedOperationId?.ToString("N") ?? string.Empty) + "\"";

    public List<FieldValueResponse> ToValueResponses()
    {
        var fields = FieldsById;
        var options = OptionsById;
        return AuthorizedValues
            .Where(v => fields.ContainsKey(v.FieldDefinitionId))
            .OrderBy(v => fields[v.FieldDefinitionId].FieldKey, StringComparer.Ordinal)
            .Select(v => new FieldValueResponse(v.FieldDefinitionId, fields[v.FieldDefinitionId].FieldKey, v.FieldValueItemId, FieldValueCodec.Format(v, options, Encryption), fields[v.FieldDefinitionId].FieldDataType,
                ApplicationEncrypted: v is FieldEncryptedValue,
                LegacyPlaintext: v is not FieldEncryptedValue && fields[v.FieldDefinitionId].FieldProtectionMode == FieldProtectionModes.ApplicationEncrypted))
            .ToList();
    }

    public FormRecordResponse ToRecordResponse(Guid organizationId, string integrityStatus = "Verified") =>
        new(Header.FormRecordId, organizationId, Header.FormDefinitionId, IsComplete ? Header.RecordStatus : "(partial)",
            Verification.CommittedContentRevision, Verification.CommittedOperationId, Verification.AuthorizationRevision, IsComplete, ToValueResponses(), integrityStatus);
}

/// <summary>
/// Verified read (CROSS_SYSTEM §3 step 4, MERKLE §3): authority head → exact staged rows by committed operation id →
/// authority VerifyRecordProjection (complete leaf index, every signature, root, gate, archive receipt) → release only
/// the authorized subset. Local candidate_state, owner columns, share and link rows are never consulted.
/// </summary>
public sealed class VerifiedRecordReader(IBusinessStore store, IAuthorizationAuthorityClient authority, RecordCandidateStager stager, Encryption.IFieldEncryptionService encryption)
{
    public async Task<VerifiedRecord> ReadAsync(RequestContext context, Guid formRecordId, ProtectedAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.ToDelegatedUser();
        var head = await authority.GetCommittedContentRevisionAsync(user, context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        if (head.CommittedContentRevision == 0 || head.CommittedOperationId is null)
        {
            throw new BusinessException("NO_COMMITTED_CONTENT", 404, "record has no accepted content");
        }

        var staged = await stager.ReadStagedAsync(formRecordId, head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false);
        var verification = await authority.VerifyRecordProjectionAsync(user, new VerifyRecordProjectionRequest(context.OrganizationId, formRecordId, action, staged.Items), cancellationToken).ConfigureAwait(false);
        if (!verification.Verified)
        {
            // Fail closed: nothing from the record is released (MRK-01/02/04/18). The authority has quarantined the head and journaled an incident.
            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "record content failed integrity verification: " + verification.FailureReasonCode);
        }

        if (staged.Header is null)
        {
            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "committed header missing after verification");
        }

        var authorizedKeys = verification.AuthorizedItems.Select(i => ((byte)i.ItemKind, i.ItemId)).ToHashSet();
        var authorizedRows = verification.IsCompleteProjection ? staged.Rows : staged.Rows.Where(r => authorizedKeys.Contains((r.ItemKindCode, r.ItemId))).ToList();

        FormDefinition definition;
        FormVersion version;
        List<FieldDefinition> fields;
        List<FieldOption> options;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            definition = await scope.FindFormDefinitionAsync(context.OrganizationId, staged.Header.FormDefinitionId, cancellationToken).ConfigureAwait(false)
                         ?? throw new BusinessException("NOT_FOUND", 404, "form definition missing");
            var versionId = definition.VersionBehavior == "Historical" ? staged.Header.CreatedFormVersionId : definition.CurrentFormVersionId ?? staged.Header.CreatedFormVersionId;
            version = await scope.FindFormVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
                      ?? await scope.FindFormVersionAsync(staged.Header.CreatedFormVersionId, cancellationToken).ConfigureAwait(false)
                      ?? throw new BusinessException("NOT_FOUND", 404, "form version missing");
            fields = await scope.GetFieldDefinitionsAsync(definition.FormDefinitionId, cancellationToken).ConfigureAwait(false);
            options = await scope.GetFieldOptionsAsync(definition.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        }

        return new VerifiedRecord(head, verification, staged.Header, staged.Rows, authorizedRows, definition, version, FormLayoutCodec.Parse(version.LayoutJson), fields, options, encryption);
    }
}
