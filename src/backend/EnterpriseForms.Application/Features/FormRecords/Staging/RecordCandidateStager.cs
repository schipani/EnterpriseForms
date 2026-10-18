using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Staging;

/// <summary>
/// Shared prepare → stage → read-back → commit sequence (CROSS_SYSTEM §3). The business runtime canonicalizes the full
/// candidate snapshot (header, every typed value row, every attachment version), the authority signs it, the rows are
/// staged immutably under the operation id, re-read from the untrusted store and submitted as commit evidence. Local
/// candidate state is a hint only.
/// </summary>
public sealed class RecordCandidateStager(IBusinessStore store, IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    public sealed record CandidateInput(FormRecordRevision Header, IReadOnlyList<ISignedContentRow> Rows);

    public async Task<AcceptedContentRevision> StageAndCommitAsync(RequestContext context, AcceptedContentRevision head, CandidateInput candidate, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(candidate);
        var keyVersion = (uint)head.CurrentSigningKeyVersion;
        if (keyVersion == 0)
        {
            throw new BusinessException("SIGNING_UNAVAILABLE", 503, "organization integrity key unavailable");
        }

        // 1. Prepare. Canonical bytes bind the operation id, so it is derived client-side from the idempotency key and
        //    proposed to the authority, which enforces uniqueness and binds its signatures to exactly that id.
        var operationId = DeriveOperationId(context.OrganizationId, candidate.Header.FormRecordId, idempotencyKey);
        if (head.CommittedOperationId == operationId)
        {
            return head; // idempotent replay: this exact operation is already the accepted head (A5, CONS-04)
        }

        var candidateRevision = head.CommittedContentRevision + 1;
        var allRows = new List<ISignedContentRow>(candidate.Rows.Count + 1) { candidate.Header };
        allRows.AddRange(candidate.Rows);
        foreach (var row in allRows)
        {
            row.ContentChangeOperationId = operationId;
            row.ContentRevision = candidateRevision;
            row.RowRevision = candidateRevision;
            row.RowSignatureKeyVersion = (int)keyVersion;
            row.RowSignatureSchemaVersion = BusinessSignedSchemas.CurrentSchemaVersion(row);
        }

        var items = allRows.Select(r => new CandidateContentItem((ManifestItemKind)r.ItemKindCode, r.ItemId, Convert.ToBase64String(BusinessSignedSchemas.Encode(r, keyVersion)))).ToList();
        var prepared = await authority.PrepareContentChangeAsync(context.ToDelegatedUser(), new PrepareContentChangeRequest(
            context.OrganizationId, candidate.Header.FormRecordId, operationId, head.AuthorizationRevision, head.CommittedContentRevision, head.CommittedManifestRootBase64, items),
            "content:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (prepared.OperationId != operationId || prepared.CandidateContentRevision != candidateRevision || prepared.SignatureKeyVersion != (int)keyVersion)
        {
            throw new BusinessException("CANDIDATE_MISMATCH", 409, "authority prepared a different operation than the staged candidate");
        }

        // 2. Stage immutably with the authority-issued signatures.
        var signatures = prepared.SignedItems.ToDictionary(s => ((byte)s.ItemKind, s.ItemId), s => Convert.FromBase64String(s.RowSignatureBase64));
        foreach (var row in allRows)
        {
            row.RowSignature = signatures[(row.ItemKindCode, row.ItemId)];
        }

        candidate.Header.StagedOn = clock.GetUtcNow();
        candidate.Header.CandidateState = "Staged";
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await scope.FindStagedHeaderAsync(candidate.Header.FormRecordId, operationId, cancellationToken).ConfigureAwait(false) is null)
            {
                scope.StageCandidate(candidate.Header, candidate.Rows);
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // 3. Read back from the untrusted store and submit as commit evidence.
        var staged = await ReadStagedAsync(candidate.Header.FormRecordId, operationId, cancellationToken).ConfigureAwait(false);
        return await authority.CommitContentChangeAsync(context.ToDelegatedUser(), operationId, new CommitContentChangeRequest(context.OrganizationId, candidate.Header.FormRecordId, staged.Items), cancellationToken).ConfigureAwait(false);
    }

    public sealed record StagedSnapshot(FormRecordRevision? Header, IReadOnlyList<ISignedContentRow> Rows, IReadOnlyList<StagedContentItem> Items);

    /// <summary>Reads the staged rows for an operation and canonicalizes them exactly as they are stored.</summary>
    public async Task<StagedSnapshot> ReadStagedAsync(Guid formRecordId, Guid operationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var header = await scope.FindStagedHeaderAsync(formRecordId, operationId, cancellationToken).ConfigureAwait(false);
        var rows = await scope.GetStagedRowsAsync(formRecordId, operationId, cancellationToken).ConfigureAwait(false);
        var items = new List<StagedContentItem>();
        if (header is not null)
        {
            items.Add(ToStaged(header));
        }

        items.AddRange(rows.Select(ToStaged));
        return new StagedSnapshot(header, rows, items);
    }

    private static StagedContentItem ToStaged(ISignedContentRow row) =>
        new((ManifestItemKind)row.ItemKindCode, row.ItemId, Convert.ToBase64String(BusinessSignedSchemas.Encode(row, (uint)row.RowSignatureKeyVersion)), Convert.ToBase64String(row.RowSignature));

    /// <summary>Deterministic operation id from (organization, record, idempotency key): retries re-derive the same id and the authority's idempotency check returns the same prepared operation.</summary>
    internal static Guid DeriveOperationId(Guid organizationId, Guid formRecordId, string idempotencyKey)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"content-op:{organizationId:D}:{formRecordId:D}:{idempotencyKey}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
