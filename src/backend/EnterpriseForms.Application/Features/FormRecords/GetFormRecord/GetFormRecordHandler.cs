using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.FormRecords.GetFormRecord;

/// <summary>
/// Verified read (CROSS_SYSTEM §3 step 4, MERKLE §3): authority head → exact staged rows by committed operation id →
/// authority VerifyRecordProjection (complete leaf index, every signature, root, gate, archive receipt) → release only
/// the authorized subset. Local `candidate_state`, owner columns and share rows are never consulted.
/// </summary>
public sealed class GetFormRecordHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, RecordCandidateStager stager)
{
    public async Task<FormRecordResponse> HandleAsync(RequestContext context, Guid formRecordId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.ToDelegatedUser();
        var head = await authority.GetCommittedContentRevisionAsync(user, context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        if (head.CommittedContentRevision == 0 || head.CommittedOperationId is null)
        {
            throw new BusinessException("NO_COMMITTED_CONTENT", 404, "record has no accepted content");
        }

        var observed = await stager.ReadStagedAsync(formRecordId, head.CommittedOperationId.Value, (uint)head.IntegrityKeyVersion, cancellationToken).ConfigureAwait(false);
        var verification = await authority.VerifyRecordProjectionAsync(user, new VerifyRecordProjectionRequest(context.OrganizationId, formRecordId, ProtectedAction.Read, observed), cancellationToken).ConfigureAwait(false);
        if (!verification.Verified)
        {
            // Fail closed: nothing from the record is released (MRK-01/02/04/18). The authority has quarantined the head and journaled an incident.
            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "record content failed integrity verification: " + verification.FailureReasonCode);
        }

        var authorizedItems = verification.AuthorizedItems.Where(i => i.ItemKind == ManifestItemKind.FieldValue).Select(i => i.ItemId).ToHashSet();
        Domain.Features.FormRecords.FormRecordRevision? header;
        List<Domain.Features.FormRecords.FieldStringValue> values;
        List<Domain.Features.FormDefinitions.FieldDefinition> fields;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            header = await scope.FindStagedHeaderAsync(formRecordId, head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false);
            values = await scope.GetStagedValuesAsync(formRecordId, head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false);
            fields = header is null ? [] : await scope.GetFieldDefinitionsAsync(header.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        }

        if (header is null)
        {
            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "committed header missing after verification");
        }

        var fieldKeys = fields.ToDictionary(f => f.FieldDefinitionId, f => f.FieldKey);
        var released = values.Where(v => authorizedItems.Contains(v.FieldValueItemId))
            .OrderBy(v => fieldKeys.GetValueOrDefault(v.FieldDefinitionId, string.Empty), StringComparer.Ordinal)
            .Select(v => new FieldValueResponse(v.FieldDefinitionId, fieldKeys.GetValueOrDefault(v.FieldDefinitionId, string.Empty), v.FieldValueItemId, v.StringValue))
            .ToList();

        return new FormRecordResponse(formRecordId, context.OrganizationId, header.FormDefinitionId, verification.IsCompleteProjection ? header.RecordStatus : "(partial)",
            verification.CommittedContentRevision, verification.CommittedOperationId, verification.AuthorizationRevision, verification.IsCompleteProjection, released, "Verified");
    }
}
