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
    /// <summary>Phase C9: whether <see cref="LayoutVersion"/> is the record's view layout or its edit layout (CLAUDE.md §3).</summary>
    public RecordLayoutPurpose LayoutPurpose { get; init; } = RecordLayoutPurpose.View;

    /// <summary>
    /// Phase C9: the version an edit of this record is validated against now. For an edit read it is <see cref="LayoutVersion"/>;
    /// for a view read it is resolved as well, and is null when no edit layout is available (editing then fails closed).
    /// </summary>
    public FormVersion? EditLayoutVersion { get; init; }

    /// <summary>Phase C9: number of the version the record was created with (the signed header's version id), when that version exists.</summary>
    public int? CreatedFormVersionNumber { get; init; }

    public IReadOnlyDictionary<Guid, FieldDefinition> FieldsById => Fields.ToDictionary(f => f.FieldDefinitionId);

    public IReadOnlyDictionary<Guid, FieldOption> OptionsById => Options.ToDictionary(o => o.FieldOptionId);

    public IEnumerable<FieldValueRowBase> AuthorizedValues => AuthorizedRows.OfType<FieldValueRowBase>();

    public IEnumerable<FileAttachmentVersion> AuthorizedAttachments => AuthorizedRows.OfType<FileAttachmentVersion>();

    /// <summary>Committed repeating-section entries (complete reads only: a partial projection never carries occurrence structure).</summary>
    public IReadOnlyList<FormRecordGroupRevision> Occurrences => IsComplete ? AllRows.OfType<FormRecordGroupRevision>().ToList() : [];

    public bool IsComplete => Verification.IsCompleteProjection;

    public string ETag => "\"" + Head.CommittedContentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + (Head.CommittedOperationId?.ToString("N") ?? string.Empty) + "\"";

    public async Task<List<FieldValueResponse>> ToValueResponsesAsync(CancellationToken cancellationToken)
    {
        var fields = FieldsById;
        var options = OptionsById;
        // A partial projection never releases values of repeating sections (they are not field-shareable in V1, C-17).
        var released = AuthorizedValues.Where(v => fields.ContainsKey(v.FieldDefinitionId) && (IsComplete || v.FormRecordGroupId is null)).ToList();
        // E-1: exactly the released rows are decrypted (asynchronous key operations), then projected.
        var decrypted = await DecryptedFieldValues.DecryptAsync(Encryption, released, cancellationToken).ConfigureAwait(false);
        return released
            .OrderBy(v => v.FormRecordGroupId is null ? 0 : 1)
            .ThenBy(v => v.FormRecordGroupId)
            .ThenBy(v => fields[v.FieldDefinitionId].FieldKey, StringComparer.Ordinal)
            .Select(v => new FieldValueResponse(v.FieldDefinitionId, fields[v.FieldDefinitionId].FieldKey, v.FieldValueItemId, FieldValueCodec.Format(v, options, decrypted), fields[v.FieldDefinitionId].FieldDataType,
                ApplicationEncrypted: v is FieldEncryptedValue,
                LegacyPlaintext: v is not FieldEncryptedValue && fields[v.FieldDefinitionId].FieldProtectionMode == FieldProtectionModes.ApplicationEncrypted,
                FormRecordGroupId: v.FormRecordGroupId))
            .ToList();
    }

    public Task<FormRecordResponse> ToRecordResponseAsync(Guid organizationId, CancellationToken cancellationToken) => ToRecordResponseAsync(organizationId, "Verified", cancellationToken);

    public async Task<FormRecordResponse> ToRecordResponseAsync(Guid organizationId, string integrityStatus, CancellationToken cancellationToken) =>
        new(Header.FormRecordId, organizationId, Header.FormDefinitionId, IsComplete ? Header.RecordStatus : "(partial)",
            Verification.CommittedContentRevision, Verification.CommittedOperationId, Verification.AuthorizationRevision, IsComplete, await ToValueResponsesAsync(cancellationToken).ConfigureAwait(false), integrityStatus,
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
    /// <summary>Verified read; the layout is the edit layout for <see cref="ProtectedAction.Edit"/> and the view layout otherwise (Phase C9).</summary>
    public Task<VerifiedRecord> ReadAsync(RequestContext context, Guid formRecordId, ProtectedAction action, CancellationToken cancellationToken) =>
        ReadCoreAsync(context, formRecordId, action, action == ProtectedAction.Edit ? RecordLayoutPurpose.Edit : RecordLayoutPurpose.View, null, cancellationToken);

    /// <summary>
    /// Phase C9: verified read whose layout is chosen for <paramref name="purpose"/> independently of the Authority action
    /// (the edit page reads with <see cref="ProtectedAction.Read"/> but needs the edit layout). An unavailable edit layout fails
    /// closed with 409 EDIT_LAYOUT_UNAVAILABLE.
    /// </summary>
    public Task<VerifiedRecord> ReadAsync(RequestContext context, Guid formRecordId, ProtectedAction action, RecordLayoutPurpose purpose, CancellationToken cancellationToken) =>
        ReadCoreAsync(context, formRecordId, action, purpose, null, cancellationToken);

    /// <summary>
    /// Phase C6 M2: verified read of one earlier accepted revision. <paramref name="historicOperationId"/> must come from the
    /// Authority's history (AC-1), never from business rows. The exact staged rows of that operation are verified by the
    /// Authority against the manifest it accepted for it (full read of the record now is required). Projections of the
    /// current head are not touched. On any mismatch nothing is released (and, AC-4, the Authority quarantines the record).
    /// </summary>
    public Task<VerifiedRecord> ReadHistoricAsync(RequestContext context, Guid formRecordId, Guid historicOperationId, CancellationToken cancellationToken) =>
        ReadCoreAsync(context, formRecordId, ProtectedAction.Read, RecordLayoutPurpose.View, historicOperationId, cancellationToken);

    private async Task<VerifiedRecord> ReadCoreAsync(RequestContext context, Guid formRecordId, ProtectedAction action, RecordLayoutPurpose purpose, Guid? historicOperationId, CancellationToken cancellationToken)
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
        FormVersion? editVersion;
        int? createdVersionNumber;
        List<FieldDefinition> fields;
        List<FieldOption> options;
        List<GroupDefinition> groups;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            definition = await scope.FindFormDefinitionAsync(context.OrganizationId, staged.Header.FormDefinitionId, cancellationToken).ConfigureAwait(false)
                         ?? throw new BusinessException("NOT_FOUND", 404, "form definition missing");
            // Phase C9 (CLAUDE.md §3): the view or edit layout of the form's version behaviour; an unavailable edit layout fails closed.
            var selection = await RecordLayoutSelection.SelectAsync(definition, staged.Header.CreatedFormVersionId, purpose, id => scope.FindFormVersionAsync(id, cancellationToken)).ConfigureAwait(false);
            version = selection.Layout;
            editVersion = selection.EditLayout;
            createdVersionNumber = selection.CreatedVersionNumber;
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

        return new VerifiedRecord(head, verification, staged.Header, staged.Rows, authorizedRows, definition, version, FormLayoutCodec.Parse(version.LayoutJson), fields, options, encryption, groups, staged.Items)
        {
            LayoutPurpose = purpose,
            EditLayoutVersion = editVersion,
            CreatedFormVersionNumber = createdVersionNumber,
        };
    }
}

/// <summary>The layouts chosen for one record read (Phase C9).</summary>
public sealed record RecordLayoutSelectionResult(FormVersion Layout, FormVersion? EditLayout, int? CreatedVersionNumber);

/// <summary>
/// Phase C9 (CLAUDE.md §3; D9-1, D9-6): chooses the record's layout for a purpose with <see cref="FormVersionResolution"/>.
/// Historical views and edits with the created version, Latest with the current version, Hybrid views with the created
/// version and edits with the current one. A version row counts only when it belongs to the record's form. The edit layout
/// is never substituted: an unresolvable, missing or foreign edit version is 409 EDIT_LAYOUT_UNAVAILABLE for an edit read
/// (and reported as null for a view read). The view keeps the earlier tolerant fallback to the created version.
/// </summary>
public static class RecordLayoutSelection
{
    public static async Task<RecordLayoutSelectionResult> SelectAsync(FormDefinition definition, Guid createdFormVersionId, RecordLayoutPurpose purpose, Func<Guid, Task<FormVersion?>> findVersion)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(findVersion);
        var found = new Dictionary<Guid, FormVersion?>();
        async Task<FormVersion?> OwnAsync(Guid? id)
        {
            if (id is not { } versionId)
            {
                return null;
            }

            if (!found.TryGetValue(versionId, out var version))
            {
                var row = await findVersion(versionId).ConfigureAwait(false);
                found[versionId] = version = row is not null && row.FormDefinitionId == definition.FormDefinitionId ? row : null;
            }

            return version;
        }

        var created = await OwnAsync(createdFormVersionId).ConfigureAwait(false);
        var edit = await OwnAsync(FormVersionResolution.Resolve(definition.VersionBehavior, RecordLayoutPurpose.Edit, createdFormVersionId, definition.CurrentFormVersionId)).ConfigureAwait(false);
        FormVersion layout;
        if (purpose == RecordLayoutPurpose.Edit)
        {
            layout = edit ?? throw new BusinessException("EDIT_LAYOUT_UNAVAILABLE", 409, "the form version this record is edited with is not available; nothing was changed");
        }
        else
        {
            layout = await OwnAsync(FormVersionResolution.Resolve(definition.VersionBehavior, RecordLayoutPurpose.View, createdFormVersionId, definition.CurrentFormVersionId)).ConfigureAwait(false)
                     ?? created
                     ?? throw new BusinessException("NOT_FOUND", 404, "form version missing");
        }

        return new RecordLayoutSelectionResult(layout, edit, created?.VersionNumber);
    }
}

/// <summary>
/// Phase C9 (D9-5): a write made against an edit layout that is no longer the record's edit layout (a version was published,
/// or the form's version behaviour changed, after the editor opened the record) is refused with 409 EDIT_LAYOUT_CHANGED,
/// so the user reloads. The expectation is optional on the wire (a client that omits it is validated against the current
/// edit layout, as before); the web application always sends it.
/// </summary>
public static class EditLayoutGuard
{
    public static void Require(VerifiedRecord verified, Guid? expectedLayoutVersionId)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (verified.LayoutPurpose != RecordLayoutPurpose.Edit)
        {
            throw new InvalidOperationException("the edit-layout guard needs a record read for editing");
        }

        if (expectedLayoutVersionId is { } expected && expected != verified.LayoutVersion.FormVersionId)
        {
            throw new BusinessException("EDIT_LAYOUT_CHANGED", 409, "the form layout this record is edited with has changed; reload the record and try again");
        }
    }
}
