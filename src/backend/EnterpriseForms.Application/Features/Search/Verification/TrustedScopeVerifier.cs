using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Ports;
using EnterpriseForms.Application.Features.Search.FieldSearch;

namespace EnterpriseForms.Application.Features.Search.Verification;

/// <summary>
/// One record of a verified scope: its accepted head, the items the caller may read on it, the content those verified items
/// were canonicalized from (Phase C7 M3: header facts and typed plaintext values) and the Authority-held times.
/// </summary>
public sealed record VerifiedScopeRecord(Guid FormRecordId, long CommittedContentRevision, Guid CommittedOperationId, bool IsCompleteProjection, IReadOnlyList<VerifiedProjectionItem> ReadableItems,
    AcceptedRecordContent? Content = null, DateTimeOffset? RegisteredOn = null, DateTimeOffset? HeadModifiedOn = null);

/// <summary>Cost of one verification (PB-09 measurement; no record contents).</summary>
public sealed record ScopeVerificationMetrics(int AuthorityCalls, int VerificationBatches, long VerifiedBytes, TimeSpan Elapsed);

/// <summary>
/// The complete, verified set of records of one form that the caller may discover, at one Authority snapshot. Every
/// record with accepted content was authenticated against its accepted head; records without content (revision 0 —
/// pending creations, never accepted) can match nothing and are counted, not verified.
/// </summary>
public sealed record VerifiedScope(Guid SearchCatalogueSnapshotId, Guid FormDefinitionId, long AuthorizationRevision, long ContentIntegrityRevision, int CatalogueEntryCount, int RecordsWithoutContent,
    IReadOnlyList<VerifiedScopeRecord> Records, ScopeVerificationMetrics Metrics, DateTimeOffset SnapshotExpiresOn = default);

/// <summary>
/// Phase C7 M1/M3 — the trusted half of a field search (architecture §3.5): the expected population comes from the Authority's
/// catalogue (AC-5), the business store only supplies bytes, and every contentful record is verified in batches of at most
/// 50 (OD-6) against the head the catalogue fixed. Any problem fails the whole scope (D3): a quarantined or unverifiable
/// record (an accepted record whose business content is missing or altered included — never skipped), a changed head or
/// snapshot, or a limit (D4: records, bytes, time) — never a partial set. The values the executor evaluates are decoded
/// from the same row objects whose canonical bytes the Authority verified.
/// </summary>
public sealed class TrustedScopeVerifier(IAuthorizationAuthorityClient authority, IAcceptedRecordItemsReader records, FieldSearchLimits limits, TimeProvider clock)
{
    public const int BatchSize = 50;

    public Task<VerifiedScope> VerifyAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken) =>
        VerifyAsync(context, formDefinitionId, TimeSpan.FromSeconds(limits.MaximumExecutionSeconds), null, cancellationToken);

    /// <param name="budget">The remaining execution budget (never more than the configured maximum).</param>
    /// <param name="catalogueIssued">Told the Authority snapshot id as soon as the catalogue exists (execution evidence, OD-8).</param>
    public async Task<VerifiedScope> VerifyAsync(RequestContext context, Guid formDefinitionId, TimeSpan budget, Action<Guid>? catalogueIssued, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var started = clock.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : budget);
        var user = context.ToDelegatedUser();
        var calls = 0;
        var batches = 0;
        long bytes = 0;
        try
        {
            calls++;
            var catalogue = await authority.IssueSearchCatalogueAsync(user, new SearchCatalogueRequest(context.OrganizationId, formDefinitionId, limits.MaximumEligibleRecords), deadline.Token).ConfigureAwait(false);
            catalogueIssued?.Invoke(catalogue.SearchCatalogueSnapshotId);
            if (catalogue.Entries.Any(e => e.ContentHeadQuarantined || e.ResourceQuarantined))
            {
                throw SearchException.VerificationUnavailable("RECORD_QUARANTINED", retryable: false);
            }

            var contentful = catalogue.Entries.Where(e => e.CommittedContentRevision > 0 && e.CommittedOperationId is not null).ToList();
            var verified = new List<VerifiedScopeRecord>(contentful.Count);
            foreach (var chunk in contentful.Chunk(BatchSize))
            {
                var items = new List<BatchVerificationItem>(chunk.Length);
                var contents = new Dictionary<Guid, AcceptedRecordContent>(chunk.Length);
                foreach (var entry in chunk)
                {
                    var content = await records.ReadAcceptedContentAsync(entry.FormRecordId, entry.CommittedOperationId!.Value, deadline.Token).ConfigureAwait(false);
                    bytes += content.Items.Sum(i => ((long)i.CanonicalBytesBase64.Length * 3 / 4) + ((long)i.RowSignatureBase64.Length * 3 / 4));
                    if (bytes > limits.MaximumVerifiedBytes)
                    {
                        throw SearchException.VerificationLimit("bytes");
                    }

                    contents[entry.FormRecordId] = content;
                    items.Add(new BatchVerificationItem(entry.FormRecordId, entry.CommittedOperationId.Value, content.Items));
                }

                calls++;
                batches++;
                var response = await authority.VerifyRecordProjectionsAsync(user, new VerifyRecordProjectionsRequest(context.OrganizationId, items), deadline.Token).ConfigureAwait(false);
                if (response.AuthorizationRevision != catalogue.AuthorizationRevision)
                {
                    throw SearchException.SnapshotChanged();
                }

                var byRecord = response.Results.ToDictionary(r => r.FormRecordId);
                foreach (var entry in chunk)
                {
                    if (!byRecord.TryGetValue(entry.FormRecordId, out var result))
                    {
                        throw SearchException.VerificationUnavailable("RECORD_NOT_VERIFIED", retryable: false);
                    }

                    if (!result.Verified)
                    {
                        throw result.FailureReasonCode switch
                        {
                            "HEAD_CHANGED" or "RECORD_UNAVAILABLE" => SearchException.SnapshotChanged(),
                            "AWAITING_ARCHIVE" => SearchException.VerificationUnavailable("AWAITING_ARCHIVE", retryable: true),
                            "QUARANTINED" => SearchException.VerificationUnavailable("RECORD_QUARANTINED", retryable: false),
                            _ => SearchException.VerificationUnavailable("RECORD_VERIFICATION_FAILED", retryable: false),
                        };
                    }

                    verified.Add(new VerifiedScopeRecord(entry.FormRecordId, result.CommittedContentRevision, result.CommittedOperationId ?? entry.CommittedOperationId!.Value, result.IsCompleteProjection, result.ReadableItems,
                        contents[entry.FormRecordId], entry.RecordRegisteredOn, entry.ContentHeadModifiedOn));
                }
            }

            // The snapshot must still hold now that every record is verified (OD-5: any authorization change invalidates).
            calls++;
            var validation = await authority.ValidateSearchCatalogueAsync(user, catalogue.SearchCatalogueSnapshotId, deadline.Token).ConfigureAwait(false);
            if (!validation.Valid)
            {
                throw validation.InvalidationReason == "ORGANIZATION_GATE_CLOSED"
                    ? SearchException.VerificationUnavailable("AUTHORITY_GATE_CLOSED", retryable: true)
                    : SearchException.SnapshotChanged();
            }

            return new VerifiedScope(catalogue.SearchCatalogueSnapshotId, formDefinitionId, catalogue.AuthorizationRevision, catalogue.ContentIntegrityRevision, catalogue.Entries.Count, catalogue.Entries.Count - contentful.Count,
                verified, new ScopeVerificationMetrics(calls, batches, bytes, clock.GetElapsedTime(started)), catalogue.SnapshotExpiresOn);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw SearchException.VerificationLimit("time");
        }
        catch (AuthorityClientException ex)
        {
            throw ex.ErrorCode switch
            {
                "LIMIT_EXCEEDED" => SearchException.VerificationLimit("records"),
                "AUTHORITY_NOT_READY" => SearchException.SnapshotChanged(),
                "INTEGRITY_QUARANTINE" => SearchException.VerificationUnavailable("RECORD_QUARANTINED", retryable: false),
                _ when ex.HttpStatusCode >= 500 || ex.Retryable => SearchException.VerificationUnavailable("AUTHORITY_UNAVAILABLE", retryable: true),
                _ => ex,
            };
        }
    }
}
