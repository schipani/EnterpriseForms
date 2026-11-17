using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// E3-M2f-3 (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §5, §9): the identity and closed vocabulary of capability-refusal evidence on
/// the organization audit stream. The only producer is <see cref="CapabilityRefusalEvidenceRecorder"/> (E3-M2f-3b); E3-M2f-3i
/// provisions the partial index whose predicate names <see cref="StreamName"/> and <see cref="EventKind"/>.
/// </summary>
public static class CapabilityRefusalEvidence
{
    /// <summary>The stream that carries the evidence (non-gating, archived like every stream).</summary>
    public const string StreamName = JournalStreamNames.Audit;

    /// <summary>The event kind of one capability-refusal evidence entry (closure §9.1).</summary>
    public const string EventKind = "EnvelopeCapabilityRefused";

    /// <summary>Body schema version (closure §9.2).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The one refusal code in scope (PD-11).</summary>
    public const string RefusalCode = AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient;

    /// <summary>LK-1: the lookup reads at most this many evidence entries of the organization, newest first.</summary>
    public const int LookupLimit = 8;

    /// <summary>The seven entry points of the capability check (closure §9.2 enumeration), fixed by the route.</summary>
    public static class EntryPoints
    {
        public const string Prepare = "prepare";
        public const string Commit = "commit";
        public const string Verify = "verify";
        public const string VerifyBatch = "verify-batch";
        public const string ProtectionCensus = "protection-census";
        public const string IndexBackfill = "index-backfill";
        public const string IndexDeepVerify = "index-deep-verify";

        public static readonly IReadOnlyList<string> All = [Prepare, Commit, Verify, VerifyBatch, ProtectionCensus, IndexBackfill, IndexDeepVerify];
    }

    /// <summary>The approved workload enumeration (closure §9.2); derived from the authenticated route policy, never from a claim.</summary>
    public static class Workloads
    {
        public const string BusinessApi = "business-api";
        public const string FieldProtectionVerifier = "field-protection-verifier";
        public const string SearchIndexBuilder = "search-index-builder";

        /// <summary>Fail-safe value of the enumeration; unreachable because each enforced route admits exactly one workload role.</summary>
        public const string Other = "other";

        public static readonly IReadOnlyList<string> All = [BusinessApi, FieldProtectionVerifier, SearchIndexBuilder, Other];
    }

    /// <summary>
    /// Closure §5.1: the workload is a function of the entry point (each route is authorized for exactly one workload role), so seven
    /// keys exist per organization. An unknown entry point maps to <see cref="Workloads.Other"/>, which the recorder refuses to record.
    /// </summary>
    public static string WorkloadOf(string entryPoint) => entryPoint switch
    {
        EntryPoints.Prepare or EntryPoints.Commit or EntryPoints.Verify or EntryPoints.VerifyBatch => Workloads.BusinessApi,
        EntryPoints.ProtectionCensus => Workloads.FieldProtectionVerifier,
        EntryPoints.IndexBackfill or EntryPoints.IndexDeepVerify => Workloads.SearchIndexBuilder,
        _ => Workloads.Other,
    };

    /// <summary>Closure §5.2: the authoritative hourly bucket, <c>date_trunc('hour', t)</c> in UTC.</summary>
    public static DateTimeOffset BucketOf(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}

/// <summary>
/// What a capability refusal carries to the evidence handler: the organization locked by the refused transaction and the parsed client
/// level, nothing else. It travels in <see cref="Exception.Data"/> of the unchanged <see cref="AuthorityException"/> (which is sealed),
/// so the exception type, error code, status, retryable flag and detail, and therefore the 409 on the wire, are exactly those of
/// E3-M2f-2. It is never serialized into a response. Only <see cref="EnvelopeCapabilityFloor"/> attaches it (architecture test).
/// </summary>
[Serializable]
public sealed record CapabilityRefusalContext(Guid OrganizationId, int ClientCapabilityLevel)
{
    /// <summary>The <see cref="Exception.Data"/> key.</summary>
    public const string ExceptionDataKey = "AuthorizationAuthority.CapabilityRefusalContext";

    /// <summary>Attaches the context to a capability refusal and returns it.</summary>
    public static AuthorityException Attach(AuthorityException refusal, Guid organizationId, int clientCapabilityLevel)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        refusal.Data[ExceptionDataKey] = new CapabilityRefusalContext(organizationId, clientCapabilityLevel);
        return refusal;
    }

    /// <summary>The context of a capability refusal, or null for every other exception (including other 409s).</summary>
    public static CapabilityRefusalContext? From(Exception? exception) =>
        exception is AuthorityException { ErrorCode: AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient } refusal
        && refusal.Data[ExceptionDataKey] is CapabilityRefusalContext context
            ? context
            : null;
}

/// <summary>The closed evidence body (closure §9.2). Serialized snake_case by <see cref="CanonicalJson"/>; no other field exists.</summary>
public sealed record CapabilityRefusalEvidenceBody(
    int EvidenceSchemaVersion,
    string RefusalCode,
    Guid OrganizationId,
    string WorkloadName,
    string EntryPoint,
    int ClientCapabilityLevel,
    DateTimeOffset EvidenceDatabaseTimeUtc,
    DateTimeOffset EvidenceBucketStartUtc,
    DateTimeOffset RefusalObservedOnUtc)
{
    /// <summary>The JSON member names of the body, exactly (closure §9.2).</summary>
    public static readonly IReadOnlyList<string> MemberNames =
    [
        "evidence_schema_version", "refusal_code", "organization_id", "workload_name", "entry_point", "client_capability_level",
        "evidence_database_time_utc", "evidence_bucket_start_utc", "refusal_observed_on_utc",
    ];
}
