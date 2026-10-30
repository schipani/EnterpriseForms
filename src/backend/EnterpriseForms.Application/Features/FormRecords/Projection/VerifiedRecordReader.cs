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

using EnterpriseForms.Application.Features.SecurityOperations.Incidents;
using EnterpriseForms.Domain.Features.SecurityOperations;
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
    Encryption.IFieldEncryptionService Encryption,
    IReadOnlyList<GroupDefinition> Groups,
    IReadOnlyList<StagedContentItem> ObservedItems)
{
    public IReadOnlyDictionary<Guid, FieldDefinition> FieldsById => Fields.ToDictionary(f => f.FieldDefinitionId);

    public IReadOnlyDictionary<Guid, FieldOption> OptionsById => Options.ToDictionary(o => o.FieldOptionId);

    public IEnumerable<FieldValueRowBase> AuthorizedValues => AuthorizedRows.OfType<FieldValueRowBase>();

    public IEnumerable<FileAttachmentVersion> AuthorizedAttachments => AuthorizedRows.OfType<FileAttachmentVersion>();

    /// <summary>Committed repeating-section entries (complete reads only: a partial projection never carries occurrence structure).</summary>
    public IReadOnlyList<FormRecordGroupRevision> Occurrences => IsComplete ? AllRows.OfType<FormRecordGroupRevision>().ToList() : [];

    public bool IsComplete => Verification.IsCompleteProjection;

    public string ETag => "\"" + Head.CommittedContentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + (Head.CommittedOperationId?.ToString("N") ?? string.Empty) + "\"";

    public List<FieldValueResponse> ToValueResponses()
    {
        var fields = FieldsById;
        var options = OptionsById;
        // A partial projection never releases values of repeating sections (they are not field-shareable in V1, C-17).
        return AuthorizedValues
            .Where(v => fields.ContainsKey(v.FieldDefinitionId) && (IsComplete || v.FormRecordGroupId is null))
            .OrderBy(v => v.FormRecordGroupId is null ? 0 : 1)
            .ThenBy(v => v.FormRecordGroupId)
            .ThenBy(v => fields[v.FieldDefinitionId].FieldKey, StringComparer.Ordinal)
            .Select(v => new FieldValueResponse(v.FieldDefinitionId, fields[v.FieldDefinitionId].FieldKey, v.FieldValueItemId, FieldValueCodec.Format(v, options, Encryption), fields[v.FieldDefinitionId].FieldDataType,
                ApplicationEncrypted: v is FieldEncryptedValue,
                LegacyPlaintext: v is not FieldEncryptedValue && fields[v.FieldDefinitionId].FieldProtectionMode == FieldProtectionModes.ApplicationEncrypted,
                FormRecordGroupId: v.FormRecordGroupId))
            .ToList();
    }

    public FormRecordResponse ToRecordResponse(Guid organizationId, string integrityStatus = "Verified") =>
        new(Header.FormRecordId, organizationId, Header.FormDefinitionId, IsComplete ? Header.RecordStatus : "(partial)",
            Verification.CommittedContentRevision, Verification.CommittedOperationId, Verification.AuthorizationRevision, IsComplete, ToValueResponses(), integrityStatus,
            IsComplete ? RecordGroupProjection.Build(Groups, Layout, Occurrences) : []);
}

/// <summary>Occurrence lists of a record for the wire: every repeating section placed in the layout (empty lists included, so a client knows the group key) plus any section no longer placed that still holds entries.</summary>
public static class RecordGroupProjection
{
    public static List<RecordGroupResponse> Build(IReadOnlyList<GroupDefinition> groups, FormLayout layout, IReadOnlyList<FormRecordGroupRevision> occurrences)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(occurrences);
        var placed = layout.Sections.Where(s => s.Repeat is not null).Select(s => s.Repeat!.GroupDefinitionId).ToList();
        var ids = placed.Concat(occurrences.Select(o => o.GroupDefinitionId).Distinct().OrderBy(id => id)).Distinct().ToList();
        var byId = groups.ToDictionary(g => g.GroupDefinitionId);
        return ids.Where(byId.ContainsKey)
            .Select(id => new RecordGroupResponse(id, byId[id].GroupKey, occurrences.Where(o => o.GroupDefinitionId == id).OrderBy(o => o.GroupSortOrder).ThenBy(o => o.FormRecordGroupId).Select(o => new RecordOccurrenceResponse(o.FormRecordGroupId, o.GroupSortOrder)).ToList()))
            .ToList();
    }
}

/// <summary>
/// Verified read (CROSS_SYSTEM §3 step 4, MERKLE §3): authority head → exact staged rows by committed operation id →
/// authority VerifyRecordProjection (complete leaf index, every signature, root, gate, archive receipt) → release only
/// the authorized subset. Local candidate_state, owner columns, share and link rows are never consulted.
/// </summary>
public sealed class VerifiedRecordReader(IBusinessStore store, IAuthorizationAuthorityClient authority, RecordCandidateStager stager, Encryption.IFieldEncryptionService encryption, SecurityOperations.Incidents.ISecurityIncidentRecorder incidents)
{
    public Task<VerifiedRecord> ReadAsync(RequestContext context, Guid formRecordId, ProtectedAction action, CancellationToken cancellationToken) =>
        ReadCoreAsync(context, formRecordId, action, null, cancellationToken);

    /// <summary>
    /// Phase C6 M2: verified read of one earlier accepted revision. <paramref name="historicOperationId"/> must come from the
    /// Authority's history (AC-1), never from business rows. The exact staged rows of that operation are verified by the
    /// Authority against the manifest it accepted for it (full read of the record now is required). Projections of the
    /// current head are not touched. On any mismatch nothing is released (and, AC-4, the Authority quarantines the record).
    /// </summary>
    public Task<VerifiedRecord> ReadHistoricAsync(RequestContext context, Guid formRecordId, Guid historicOperationId, CancellationToken cancellationToken) =>
        ReadCoreAsync(context, formRecordId, ProtectedAction.Read, historicOperationId, cancellationToken);

    private async Task<VerifiedRecord> ReadCoreAsync(RequestContext context, Guid formRecordId, ProtectedAction action, Guid? historicOperationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.ToDelegatedUser();
        var source = historicOperationId is null ? SecurityIncidentDetectionSources.RecordRead : SecurityIncidentDetectionSources.RecordVersionRead;
        AcceptedContentRevision head;
        try
        {
            head = await authority.GetCommittedContentRevisionAsync(user, context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (SecurityIncidentClassifier.FromAuthorityError(ex) is not null)
        {
            await incidents.RecordAsync(SecurityIncidentObservation.For(context, SecurityIncidentClassifier.FromAuthorityError(ex)!, formRecordId, source, ex.ErrorCode), cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (head.CommittedContentRevision == 0 || head.CommittedOperationId is null)
        {
            throw new BusinessException("NO_COMMITTED_CONTENT", 404, "record has no accepted content");
        }

        var historic = historicOperationId is { } requested && requested != head.CommittedOperationId ? historicOperationId : null;
        var staged = await stager.ReadStagedAsync(formRecordId, historic ?? head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false);
        VerifyRecordProjectionResult verification;
        try
        {
            verification = await authority.VerifyRecordProjectionAsync(user, new VerifyRecordProjectionRequest(context.OrganizationId, formRecordId, action, staged.Items, historic), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (SecurityIncidentClassifier.FromAuthorityError(ex) is not null)
        {
            await incidents.RecordAsync(SecurityIncidentObservation.For(context, SecurityIncidentClassifier.FromAuthorityError(ex)!, formRecordId, source, ex.ErrorCode, head.CommittedContentRevision), cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (!verification.Verified)
        {
            // Fail closed: nothing from the record is released (MRK-01/02/04/18). The authority has quarantined the head and journaled an incident;
            // the business-side triage record is written first and never changes this outcome (the recorder cannot throw).
            if (SecurityIncidentClassifier.FromVerificationFailure(verification.FailureReasonCode) is { } incidentType)
            {
                await incidents.RecordAsync(SecurityIncidentObservation.For(context, incidentType, formRecordId, source, verification.FailureReasonCode ?? string.Empty, verification.CommittedContentRevision), cancellationToken).ConfigureAwait(false);
            }

            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "record content failed integrity verification: " + verification.FailureReasonCode);
        }

        if (staged.Header is null)
        {
            await incidents.RecordAsync(SecurityIncidentObservation.For(context, SecurityIncidentTypes.OperationalVerificationFailure, formRecordId, source, "COMMITTED_HEADER_MISSING", verification.CommittedContentRevision), cancellationToken).ConfigureAwait(false);
            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "committed header missing after verification");
        }

        var authorizedKeys = verification.AuthorizedItems.Select(i => ((byte)i.ItemKind, i.ItemId)).ToHashSet();
        var authorizedRows = verification.IsCompleteProjection ? staged.Rows : staged.Rows.Where(r => authorizedKeys.Contains((r.ItemKindCode, r.ItemId))).ToList();

        FormDefinition definition;
        FormVersion version;
        List<FieldDefinition> fields;
        List<FieldOption> options;
        List<GroupDefinition> groups;
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
            groups = await scope.GetGroupDefinitionsAsync(definition.FormDefinitionId, cancellationToken).ConfigureAwait(false);

            // Phase C6 M0 (S2): the head just verified by the Authority is projected onto form_records (monotonic, atomic), so a
            // projection left stale by a lost commit response is repaired by the next verified read. Not a modification of the record.
            // Phase C6 M1: the verified signed header's lifecycle status is projected too (listing/filtering only), at the verified revision.
            // Phase C6 M2: never from an earlier revision (the projections describe the current head only).
            if (historic is null)
            {
                var headChanged = await scope.ProjectAcceptedHeadAsync(context.OrganizationId, formRecordId, verification.CommittedContentRevision, verification.CommittedOperationId, cancellationToken).ConfigureAwait(false);
                var statusChanged = await scope.ProjectLifecycleAsync(context.OrganizationId, formRecordId, verification.CommittedContentRevision, staged.Header.RecordStatus, staged.Header.SubmittedOn, cancellationToken).ConfigureAwait(false);
                if (headChanged || statusChanged)
                {
                    await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return new VerifiedRecord(head, verification, staged.Header, staged.Rows, authorizedRows, definition, version, FormLayoutCodec.Parse(version.LayoutJson), fields, options, encryption, groups, staged.Items);
    }
}
