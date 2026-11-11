using System.Numerics;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.SearchAccess;
using EnterpriseForms.Integrity.Search;

namespace AuthorizationAuthority.Application.Features.SearchAccess;

/// <summary>C-150 IX-5c: how one shadow comparison ended. Counts and codes only: no record id, value or predicate.</summary>
public static class SearchAccessShadowClassifications
{
    /// <summary>Tier 2 found exactly Tier 1's matches at the same snapshot.</summary>
    public const string Match = "Match";

    /// <summary>Tier 2 differs from Tier 1 at the same snapshot (an unexplained difference; alerted).</summary>
    public const string Mismatch = "Mismatch";

    /// <summary>No equivalent snapshot could be compared (it moved, Tier 2 or the gate was not ready): never counted as a match or a mismatch.</summary>
    public const string NotComparable = "NotComparable";

    public const string Cancelled = "Cancelled";

    public const string Failed = "Failed";

    /// <summary>The bounded queue was full: the observation was not compared.</summary>
    public const string Dropped = "Dropped";
}

public static class SearchAccessShadowReasonCodes
{
    public const string SnapshotMoved = "SNAPSHOT_MOVED";
    public const string Tier2NotReady = "TIER2_NOT_READY";
    public const string GateClosed = "ORGANIZATION_GATE_CLOSED";
    public const string OrganizationNotFound = "ORGANIZATION_NOT_FOUND";
    public const string Disabled = "TIER2_DISABLED";
    public const string TimeBudget = "TIME_BUDGET";
}

/// <summary>
/// One comparison: the classification, why it was not comparable, and counts — Tier 1's matches, Tier 2's (after the metadata filter),
/// those only one side found, and Tier 2 candidates excluded by Tier 1's metadata filter (status, children, archived). Never ids or values.
/// </summary>
public sealed record SearchAccessShadowResult(string Classification, string? ReasonCode, int Tier1Count, int? Tier2Count, int OnlyInTier1Count, int OnlyInTier2Count, int ExcludedByMetadataCount,
    int ConditionCount, Guid CorrelationId, double ElapsedMilliseconds, DateTimeOffset ComparedOn);

public sealed partial class SearchAccessProjectionService
{
    /// <summary>
    /// C-150 IX-5c shadow preparation: evaluates a committed Tier 1 search on the Tier 2 rows and compares the two answers, never serving
    /// either. Only an <i>equivalent authorized snapshot</i> is compared — one read-only REPEATABLE READ snapshot in which the organization's
    /// authorization revision, content-integrity revision and the form's Tier 1 content revision all still equal the ones the Tier 1 result
    /// was committed at, the organization gate (S1, archive receipts) is Ready and the Tier 2 checkpoint is Ready (A-3 exact equality).
    /// Anything else is <see cref="SearchAccessShadowClassifications.NotComparable"/>: a revocation or edit after the search is never a
    /// mismatch and never re-evaluated at another snapshot.
    /// <para>
    /// The Tier 2 answer follows the serving design without SpiceDB: P = the user and the user's teams (canonical membership in the same
    /// snapshot); candidates from the Tier 2 record grants of P (effective owner or share recipient); the value rows of P for the conditions'
    /// fields under Tier 1's predicate approvals; a share-only record must read every accepted item of each condition's field (the UF-25
    /// rule); each condition is decided with Tier 1's own predicate semantics (<see cref="IndexedSearchPredicates.Holds"/>) on the record's
    /// readable values. Tier 2 holds no record header, so a Tier 2 candidate Tier 1 did not return is checked against Tier 1's verified
    /// header facts (status, parent); one Tier 1's metadata filter excludes is counted as explained, not as a difference.
    /// </para>
    /// Organization-bound (both RLS contexts), read-only, and its only output is the result record.
    /// </summary>
    public async Task<SearchAccessShadowResult> CompareShadowAsync(IndexedSearchShadowObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var started = clock.GetTimestamp();
        SearchAccessShadowResult Result(string classification, string? reason, int? tier2 = null, int onlyTier1 = 0, int onlyTier2 = 0, int excluded = 0) =>
            new(classification, reason, observation.Tier1Matches.Count, tier2, onlyTier1, onlyTier2, excluded, observation.Conditions.Count, observation.CorrelationId, clock.GetElapsedTime(started).TotalMilliseconds, clock.GetUtcNow());

        if (options.Mode == SearchAccessMode.Off)
        {
            return Result(SearchAccessShadowClassifications.NotComparable, SearchAccessShadowReasonCodes.Disabled);
        }

        await using var scope = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(observation.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return Result(SearchAccessShadowClassifications.NotComparable, SearchAccessShadowReasonCodes.OrganizationNotFound);
        }

        if (state.AuthorizationRevision != observation.AuthorizationRevision || state.ContentIntegrityRevision != observation.ContentIntegrityRevision)
        {
            return Result(SearchAccessShadowClassifications.NotComparable, SearchAccessShadowReasonCodes.SnapshotMoved);
        }

        if (state.IntegrityQuarantineEnabled || await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false) != ProjectionGateState.Ready)
        {
            return Result(SearchAccessShadowClassifications.NotComparable, SearchAccessShadowReasonCodes.GateClosed);
        }

        var access = scope.SearchAccess;
        await access.BindOrganizationAsync(observation.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (await access.GetTier1FormContentRevisionAsync(observation.OrganizationId, observation.FormDefinitionId, cancellationToken).ConfigureAwait(false) != observation.FormContentRevision)
        {
            return Result(SearchAccessShadowClassifications.NotComparable, SearchAccessShadowReasonCodes.SnapshotMoved);
        }

        var checkpoint = await access.FindCheckpointAsync(observation.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null || !IsReady(checkpoint, await ReadinessInputsAsync(access, state, cancellationToken).ConfigureAwait(false)))
        {
            return Result(SearchAccessShadowClassifications.NotComparable, SearchAccessShadowReasonCodes.Tier2NotReady);
        }

        var tier2 = await EvaluateTier2Async(scope, observation, cancellationToken).ConfigureAwait(false);
        var tier1 = observation.Tier1Matches.ToHashSet();
        var onlyTier1 = tier1.Count(id => !tier2.Contains(id));
        var extra = tier2.Where(id => !tier1.Contains(id)).ToList();
        var excluded = 0;
        if (extra.Count > 0)
        {
            var headers = (await access.GetTier1RecordHeadersAsync(observation.OrganizationId, observation.FormDefinitionId, extra, cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.FormRecordId);
            excluded = extra.Count(id => headers.TryGetValue(id, out var header) && !MetadataMatches(observation, header));
        }

        var onlyTier2 = extra.Count - excluded;
        return Result(onlyTier1 == 0 && onlyTier2 == 0 ? SearchAccessShadowClassifications.Match : SearchAccessShadowClassifications.Mismatch, null, tier2.Count - excluded, onlyTier1, onlyTier2, excluded);
    }

    private static async Task<HashSet<Guid>> EvaluateTier2Async(Common.Ports.IAuthorityScope scope, IndexedSearchShadowObservation o, CancellationToken cancellationToken)
    {
        var access = scope.SearchAccess;
        var canonical = await CanonicalAccessEdgesAsync(scope, o.OrganizationId, cancellationToken).ConfigureAwait(false);
        var principals = canonical.WithSubject("user", o.AuthorityUserId, "team", "member").Where(e => e.SubjectRelation.Length == 0).Select(e => ("team", e.ResourceId)).Append(("user", o.AuthorityUserId)).ToHashSet();
        var grants = (await access.GetRecordGrantsAsync(o.OrganizationId, null, cancellationToken).ConfigureAwait(false))
            .Where(g => principals.Contains((g.AccessPrincipalTypeName, g.AccessPrincipalEntityId))).ToList();
        var owned = grants.Where(g => g.AccessKindName == SearchAccessKinds.EffectiveOwner).Select(g => g.FormRecordId).ToHashSet();
        var discoverable = grants.Select(g => g.FormRecordId).ToHashSet();
        var result = new HashSet<Guid>();
        if (discoverable.Count == 0)
        {
            return result;
        }

        var fields = o.Conditions.Select(c => c.FieldDefinitionId).ToHashSet();
        var approvals = o.PredicateFieldSearchApprovalIds.ToHashSet();
        var values = (await access.GetValuePostingsAsync(o.OrganizationId, discoverable, cancellationToken).ConfigureAwait(false))
            .Where(v => v.FormDefinitionId == o.FormDefinitionId && fields.Contains(v.FieldDefinitionId) && approvals.Contains(v.FieldSearchApprovalId) && principals.Contains((v.AccessPrincipalTypeName, v.AccessPrincipalEntityId)))
            .ToList();
        var byRecord = values.GroupBy(v => v.FormRecordId).ToDictionary(g => g.Key, g => g.ToList());
        var counts = await access.GetFieldItemCountsAsync(o.OrganizationId, byRecord.Keys.ToList(), cancellationToken).ConfigureAwait(false);
        foreach (var (record, rows) in byRecord)
        {
            var all = o.Conditions.All(condition =>
            {
                var field = rows.Where(v => v.FieldDefinitionId == condition.FieldDefinitionId).DistinctBy(v => v.ManifestItemId).ToList();
                if (field.Count == 0)
                {
                    return false;
                }

                if (!owned.Contains(record) && (!counts.TryGetValue((record, field[0].FieldSearchApprovalId), out var accepted) || field.Count != accepted))
                {
                    return false; // share-only: every accepted item of the field must be readable through P
                }

                var sealValues = field.Select(SealValue).OfType<SearchSealValue>().ToList();
                return sealValues.Count == field.Count && IndexedSearchPredicates.Holds(condition, sealValues);
            });
            if (all)
            {
                result.Add(record);
            }
        }

        return result;
    }

    /// <summary>Tier 1's metadata filter (IndexedSearchService.MetadataMatches) on Tier 1's verified header facts.</summary>
    private static bool MetadataMatches(IndexedSearchShadowObservation o, Common.Ports.Tier1RecordHeader entry) =>
        (o.IncludeChildren || entry.ParentFormRecordId is null)
        && (o.RecordStatus is null || string.Equals(entry.RecordStatusName, o.RecordStatus, StringComparison.Ordinal))
        && (o.RecordStatus is not null || o.IncludeArchived || !string.Equals(entry.RecordStatusName, "Archived", StringComparison.Ordinal));

    /// <summary>A Tier 2 value row as the typed value Tier 1's predicate semantics evaluate; null for an incomplete row (it then cannot match).</summary>
    internal static SearchSealValue? SealValue(SearchAccessValuePosting v) => v.ValueTypeName switch
    {
        "String" when v.NormalizedStringValue is { } text => new SearchSealValue.NormalizedString(text),
        "Integer" when v.IntegerValue is { } integer => new SearchSealValue.IntegerValue(integer),
        "Decimal" when v.DecimalValue is { } number => DecimalParts(number),
        "Date" when v.DateValue is { } date => new SearchSealValue.DateValue(date),
        "DateTime" when v.DateTimeValue is { } instant => new SearchSealValue.Timestamp(instant),
        "Boolean" when v.BooleanValue is { } flag => new SearchSealValue.BooleanValue(flag),
        "Option" when v.FieldOptionId is { } option => new SearchSealValue.OptionValue(option),
        _ => null,
    };

    private static SearchSealValue.DecimalParts DecimalParts(decimal value)
    {
        var bits = decimal.GetBits(value);
        var magnitude = new BigInteger((uint)bits[0]) | (new BigInteger((uint)bits[1]) << 32) | (new BigInteger((uint)bits[2]) << 64);
        return new SearchSealValue.DecimalParts((bits[3] >> 16) & 0xFF, value < 0 ? -magnitude : magnitude);
    }
}
