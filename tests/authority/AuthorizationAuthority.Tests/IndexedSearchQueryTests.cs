using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using AuthorizationAuthority.Infrastructure.Persistence;
using EnterpriseForms.Integrity.Merkle;
using EnterpriseForms.Integrity.Search;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-3 unit tests (C-132): predicate validation and evaluation with the V1 semantics; I-2, the per-match binding of postings and
/// header bytes to the accepted manifest; the fixed candidate templates; the nonce-bound query digest; the V1 sort; the version 2
/// header facts of the derivation; the result-snapshot and reader grants. Real PostgreSQL, SpiceDB, sessions, pages and triggers
/// are exercised by IndexedSearchQueryStackTests.
/// </summary>
public sealed class IndexedSearchQueryTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid TextField = Guid.CreateVersion7();
    private static readonly Guid NumberField = Guid.CreateVersion7();
    private static readonly Guid OptionField = Guid.CreateVersion7();
    private static readonly Guid Open = Guid.CreateVersion7();
    private static readonly Guid Closed = Guid.CreateVersion7();
    private static readonly IndexedSearchOptions Options = new();

    private static IndexedSearchCondition C(Guid field, string op, string type, string? value = null, string? lower = null, string? upper = null) => new(field, op, type, value, lower, upper);

    private static PreparedIndexedCondition P(Guid field, string op, string type, string? value = null, string? lower = null, string? upper = null) =>
        IndexedSearchPredicates.Prepare([C(field, op, type, value, lower, upper)], Options)[0];

    private static bool Holds(PreparedIndexedCondition c, params SearchSealValue[] values) => IndexedSearchPredicates.Holds(c, values);

    // ---- predicates: V1 semantics --------------------------------------------------------------------------------------------

    [Fact]
    public void TextComparesNormalizedTrimmedAndCaseInsensitiveLikeV1()
    {
        var stored = new SearchSealValue.NormalizedString(SearchValueNormalizationV1.NormalizeString("  Ärger Straße-12 "));
        Assert.True(Holds(P(TextField, "equals", "String", "ärger straße-12"), stored));
        Assert.False(Holds(P(TextField, "equals", "String", "ärger"), stored));
        Assert.True(Holds(P(TextField, "startsWith", "String", "ÄRG"), stored));
        Assert.True(Holds(P(TextField, "contains", "String", "STRASSE".Replace("SS", "ß", StringComparison.Ordinal)), stored));
        Assert.False(Holds(P(TextField, "contains", "String", "strasse"), stored)); // ordinal ignore case: ß is not SS (as V1)
        Assert.True(Holds(P(TextField, "contains", "String", "e-1"), stored));
        Assert.False(Holds(P(TextField, "equals", "String", "x")));               // a missing value never matches
    }

    [Fact]
    public void ValuesAreParsedStrictlyAndTheValidatorRefusesWhatV1Refuses()
    {
        string Code(params IndexedSearchCondition[] conditions)
        {
            try
            {
                IndexedSearchPredicates.Prepare(conditions, Options);
                return "OK";
            }
            catch (AuthorityException ex)
            {
                return ex.ErrorCode;
            }
        }

        Assert.Equal("OK", Code(C(TextField, "contains", "String", "abc")));
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(TextField, "contains", "String", " ab ")));        // partial text needs 3 characters
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(TextField, "equals", "String", "a\u0007b")));    // control character
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(TextField, "equals", "String", "é")));     // not NFC
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(TextField, "equals", "String", new string('x', 201))));
        Assert.Equal(IndexedSearchErrorCodes.OperatorNotSupported, Code(C(TextField, "greaterThan", "String", "a")));
        Assert.Equal(IndexedSearchErrorCodes.OperatorNotSupported, Code(C(TextField, "regex", "String", "a")));
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(NumberField, "equals", "Integer", "1.5")));
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(NumberField, "between", "Integer", null, "5", "2")));
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(NumberField, "between", "Integer", "3", "1", "5")));
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(NumberField, "equals", "Date", "2026-13-01")));
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(NumberField, "before", "DateTime", "2026-01-01T10:00:00")));  // an instant needs an offset
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(OptionField, "is", "Option", Guid.Empty.ToString("D"))));
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(OptionField, "isTrue", "Boolean", "true")));        // nullary takes no value
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code([]));
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(Enumerable.Range(0, 6).Select(_ => C(TextField, "equals", "String", "a")).ToArray()));
        Assert.Equal(AuthorityErrorCodes.InvalidRequest, Code(C(TextField, "equals", "Guid", "a")));                 // value types outside V1
    }

    [Fact]
    public void NumbersDatesInstantsBooleansAndOptionsCompareLikeV1()
    {
        var five = new SearchSealValue.IntegerValue(5);
        Assert.True(Holds(P(NumberField, "between", "Integer", null, "5", "9"), five));
        Assert.True(Holds(P(NumberField, "between", "Integer", null, "1", "5"), five));
        Assert.False(Holds(P(NumberField, "greaterThan", "Integer", "5"), five));
        Assert.True(Holds(P(NumberField, "greaterThanOrEqual", "Integer", "5"), five));
        Assert.True(Holds(P(NumberField, "lessThan", "Integer", "6"), five));
        var onePointFive = new SearchSealValue.DecimalParts(2, new BigInteger(150)); // 1.50
        Assert.True(Holds(P(NumberField, "equals", "Decimal", "1.5"), onePointFive));
        Assert.True(Holds(P(NumberField, "lessThan", "Decimal", "1.51"), onePointFive));
        var date = new SearchSealValue.DateValue(new DateOnly(2026, 3, 12));
        Assert.False(Holds(P(NumberField, "before", "Date", "2026-03-12"), date)); // strict
        Assert.True(Holds(P(NumberField, "after", "Date", "2026-03-11"), date));
        Assert.True(Holds(P(NumberField, "between", "Date", null, "2026-03-12", "2026-03-12"), date));
        var instant = new SearchSealValue.Timestamp(new DateTimeOffset(2026, 1, 1, 15, 0, 0, TimeSpan.Zero));
        Assert.True(Holds(P(NumberField, "before", "DateTime", "2026-01-01T10:00:01-05:00"), instant));
        Assert.False(Holds(P(NumberField, "after", "DateTime", "2026-01-01T15:00:00Z"), instant));
        Assert.True(Holds(P(NumberField, "isTrue", "Boolean"), new SearchSealValue.BooleanValue(true)));
        Assert.False(Holds(P(NumberField, "isFalse", "Boolean"), new SearchSealValue.BooleanValue(true)));
        // Option: "is" = exactly that selection; "includes" = contains it (multi-select).
        var both = new SearchSealValue[] { new SearchSealValue.OptionValue(Open), new SearchSealValue.OptionValue(Closed) };
        Assert.False(IndexedSearchPredicates.Holds(P(OptionField, "is", "Option", Open.ToString("D")), both));
        Assert.True(IndexedSearchPredicates.Holds(P(OptionField, "includes", "Option", Open.ToString("D")), both));
        Assert.True(Holds(P(OptionField, "is", "Option", Open.ToString("D")), new SearchSealValue.OptionValue(Open)));
    }

    [Fact]
    public void ConditionsAreConjunctiveAndAFieldWithoutValuesNeverMatches()
    {
        var conditions = IndexedSearchPredicates.Prepare([C(TextField, "startsWith", "String", "cr-"), C(NumberField, "greaterThan", "Integer", "1")], Options);
        var values = new Dictionary<Guid, IReadOnlyList<SearchSealValue>> { [TextField] = [new SearchSealValue.NormalizedString("CR-1")], [NumberField] = [new SearchSealValue.IntegerValue(2)] };
        Assert.True(IndexedSearchPredicates.Matches(conditions, values));
        values[NumberField] = [];
        Assert.False(IndexedSearchPredicates.Matches(conditions, values));
        values.Remove(NumberField);
        Assert.False(IndexedSearchPredicates.Matches(conditions, values));
    }

    // ---- I-2: binding to the accepted manifest -------------------------------------------------------------------------------

    private sealed record Built(SearchRecordBindingData Data, IReadOnlyList<PreparedIndexedCondition> Conditions, Dictionary<Guid, string> Types);

    private static FieldSearchApproval Approval(Guid field, string type) => new()
    {
        FieldSearchApprovalId = Guid.CreateVersion7(), OrganizationId = Org, FormDefinitionId = Ids.Form, FieldDefinitionId = field, ApprovedFieldDataType = type, ApprovedFieldClassification = "Internal",
        ApprovedProtectionMode = "Standard", ApprovedOperatorsJson = "[\"equals\",\"startsWith\",\"contains\",\"greaterThan\",\"is\",\"includes\"]", ApprovalStateName = FieldSearchApprovalStates.Active,
    };

    /// <summary>A record at revision 1: header (with a parent), a text value, an integer value and two option selections; derived and laid out like a commit.</summary>
    private static Built Build(string text = "Ärger Straße", long number = 5, IndexedSearchCondition[]? conditions = null)
    {
        var record = Guid.CreateVersion7();
        var parent = Guid.CreateVersion7();
        var op = Guid.CreateVersion7();
        var rows = new[]
        {
            Header(Org, record, "Submitted", parent), Text(Org, record, TextField, Guid.CreateVersion7(), text), Integer(Org, record, NumberField, Guid.CreateVersion7(), number),
            Option(Org, record, OptionField, Guid.CreateVersion7(), Open), Option(Org, record, OptionField, Guid.CreateVersion7(), Closed),
        };
        var staged = rows.Select(r => Encode(r, Org, op, 1)).ToList();
        var approvals = new[] { Approval(TextField, "String"), Approval(NumberField, "Integer"), Approval(OptionField, "Option") };
        var content = SearchPostingDerivationV1.Derive(Org, Ids.Form, record, staged.Select(s => new StagedContentItem(s.Kind, s.ItemId, s.Base64, Convert.ToBase64String(new byte[32]))).ToList(), approvals);
        var leaves = staged.Select(s => new SearchManifestLeafFacts(s.Key, (byte)s.Kind, s.ItemId, s.Digest, SHA256.HashData(s.Bytes.Concat(Encoding.ASCII.GetBytes("sig")).ToArray()))).ToList();
        var root = MerkleManifestV1.Build(leaves.Select(l => new MerkleLeafInput((MerkleItemKind)l.ManifestItemKindCode, l.ManifestItemId, l.RowSignature, l.CanonicalPayloadDigest, 1, op))).MerkleRoot;
        var entry = new SearchRecordEntry
        {
            SearchRecordEntryId = Guid.CreateVersion7(), OrganizationId = Org, FormDefinitionId = Ids.Form, FormRecordId = record, IndexedOperationId = op, IndexedContentRevision = 1, IndexedManifestRoot = root,
            RecordStatusName = content.RecordStatusName, HeaderSourceCanonicalBytes = content.HeaderSourceCanonicalBytes, ParentFormRecordId = content.ParentFormRecordId, CreatedByBusinessUserId = content.CreatedByUserId,
            EntryDerivationVersion = SearchPostingDerivationV1.CurrentEntryDerivationVersion, DerivedFieldSearchApprovalIds = approvals.Select(a => a.FieldSearchApprovalId).ToArray(),
            DerivedFieldItemCounts = SearchFieldItemCounts.Derive(approvals.Select(a => a.FieldSearchApprovalId).ToArray(), content.Postings.Select(p => p.FieldSearchApprovalId)),
            IndexedPostingCount = content.Postings.Count, SealKeyName = "k", SealKeyVersion = 1, EntrySealSignature = new byte[64],
        };
        var rowsIndex = SearchIndexWriter.ToRows(entry, content);
        var data = new SearchRecordBindingData(entry, rowsIndex.AllPostings.ToList(), [], 1, op, root, false, Guid.CreateVersion7(), leaves.Count, root, leaves);
        conditions ??= [C(TextField, "contains", "String", "straße"), C(NumberField, "greaterThan", "Integer", "4"), C(OptionField, "includes", "Option", Open.ToString("D"))];
        var prepared = IndexedSearchPredicates.Prepare(conditions, Options);
        return new Built(data, prepared, prepared.GroupBy(c => c.FieldDefinitionId).ToDictionary(g => g.Key, g => g.First().ValueType));
    }

    private static IndexedMatchVerification Verify(Built b, SearchRecordBindingData? data = null) => IndexedSearchBinding.VerifyMatch(data ?? b.Data, b.Conditions, b.Types);

    [Fact]
    public void AnHonestMatchIsBoundAndItsHeaderFactsAreDecodedFromTheAcceptedHeader()
    {
        var b = Build();
        var result = Verify(b);
        Assert.True(result.Bound, result.FindingCode);
        Assert.Equal("Submitted", result.Header!.RecordStatusName);
        Assert.Equal(b.Data.Entry.ParentFormRecordId, result.Header.ParentFormRecordId);
        Assert.NotNull(result.Header.ParentFormRecordId);
        Assert.Equal(Ids.Creator, result.Header.CreatedByUserId);
    }

    [Fact]
    public void AnyInconsistencyBetweenPostingsHeaderLeavesAndHeadFailsTheMatch()
    {
        var b = Build();
        SearchRecordBindingData With(Func<SearchRecordBindingData, SearchRecordBindingData> change) => change(b.Data);
        SearchPostingBase Clone(SearchPostingBase p) => p switch
        {
            SearchStringPosting s => new SearchStringPosting { SearchStringPostingId = s.SearchStringPostingId, NormalizedStringValue = s.NormalizedStringValue, OrganizationId = s.OrganizationId, FormDefinitionId = s.FormDefinitionId, FieldDefinitionId = s.FieldDefinitionId, FormRecordId = s.FormRecordId, ManifestItemId = s.ManifestItemId, ManifestItemKey = s.ManifestItemKey, SourceEntityKind = s.SourceEntityKind, IndexedOperationId = s.IndexedOperationId, FieldSearchApprovalId = s.FieldSearchApprovalId, SourceCanonicalBytes = s.SourceCanonicalBytes },
            _ => p,
        };

        // A posting whose typed column was changed (a non-match made to match): I-2 recomputes the value from the source bytes.
        var changedValue = b.Data.Postings.Select(Clone).ToList();
        ((SearchStringPosting)changedValue.First(p => p is SearchStringPosting)).NormalizedStringValue = "OTHER";
        Assert.Equal("MATCH_POSTING_VALUE_MISMATCH", Verify(b, With(d => d with { Postings = changedValue })).FindingCode);

        // Source bytes replaced: they no longer hash to the accepted leaf.
        var changedBytes = b.Data.Postings.Select(Clone).ToList();
        var text = (SearchStringPosting)changedBytes.First(p => p is SearchStringPosting);
        text.SourceCanonicalBytes = [.. text.SourceCanonicalBytes, 0x00];
        Assert.Equal("MATCH_POSTING_NOT_IN_ACCEPTED_MANIFEST", Verify(b, With(d => d with { Postings = changedBytes })).FindingCode);

        // A leaf digest changed: the leaf set no longer recomputes the head root.
        var leaves = b.Data.Leaves.Select((l, i) => i == 1 ? l with { CanonicalPayloadDigest = SHA256.HashData(l.CanonicalPayloadDigest) } : l).ToList();
        Assert.Equal("MATCH_ROOT_MISMATCH", Verify(b, With(d => d with { Leaves = leaves })).FindingCode);
        Assert.Equal("MATCH_MANIFEST_COMMIT_MISMATCH", Verify(b, With(d => d with { ManifestLeafCount = d.ManifestLeafCount + 1 })).FindingCode);
        Assert.Equal("MATCH_LEAF_DUPLICATE", Verify(b, With(d => d with { Leaves = [.. d.Leaves, d.Leaves[0]], ManifestLeafCount = d.Leaves.Count + 1 })).FindingCode);

        // The entry's stored header facts disagree with the accepted header bytes, or the entry is stale or of the old derivation.
        Assert.Equal("MATCH_HEADER_FACT_MISMATCH", Verify(b, With(d => d with { Entry = Copy(d.Entry, e => e.RecordStatusName = "Draft") })).FindingCode);
        Assert.Equal("MATCH_HEADER_FACT_MISMATCH", Verify(b, With(d => d with { Entry = Copy(d.Entry, e => e.ParentFormRecordId = null) })).FindingCode);
        Assert.Equal("MATCH_ENTRY_DERIVATION_OUTDATED", Verify(b, With(d => d with { Entry = Copy(d.Entry, e => e.EntryDerivationVersion = 1) })).FindingCode);
        Assert.Equal("MATCH_ENTRY_NOT_AT_HEAD", Verify(b, With(d => d with { HeadOperationId = Guid.CreateVersion7() })).FindingCode);
        Assert.Equal("MATCH_POSTING_BINDING_MISMATCH", Verify(b, With(d => d with { Entry = Copy(d.Entry, e => e.DerivedFieldSearchApprovalIds = [Guid.CreateVersion7()]) })).FindingCode);
    }

    [Fact]
    public void ACandidateWhoseVerifiedValuesDoNotSatisfyThePredicateIsIndexDamage()
    {
        // The SQL statement returned the record, but its verified value (5) does not satisfy "greaterThan 9".
        var b = Build(conditions: [C(NumberField, "greaterThan", "Integer", "9")]);
        Assert.Equal("MATCH_PREDICATE_NOT_SATISFIED_BY_VERIFIED_VALUE", Verify(b).FindingCode);
    }

    [Fact]
    public void ACoordinatedRewriteOfPostingLeafAndRootPassesI2AsTheDesignStates()
    {
        // OR-1 (§18.1.1): I-2 is unkeyed. A writer holding the runtime credential who rewrites the posting's bytes, the leaf digest
        // and the head root consistently passes it; only the keyed seal (verified for every released page record) and the open
        // path detect that. This test records the limitation; it does not claim protection.
        var honest = Build("CR-1", conditions: [C(TextField, "equals", "String", "CR-1")]);
        var forged = Build("CR-2", conditions: [C(TextField, "equals", "String", "CR-2")]);
        Assert.True(Verify(honest).Bound);
        Assert.True(Verify(forged).Bound);
        Assert.NotEqual(Convert.ToHexString(honest.Data.HeadManifestRoot), Convert.ToHexString(forged.Data.HeadManifestRoot));
    }

    private static SearchRecordEntry Copy(SearchRecordEntry e, Action<SearchRecordEntry> change)
    {
        var copy = new SearchRecordEntry
        {
            SearchRecordEntryId = e.SearchRecordEntryId, OrganizationId = e.OrganizationId, FormDefinitionId = e.FormDefinitionId, FormRecordId = e.FormRecordId, IndexedOperationId = e.IndexedOperationId,
            IndexedContentRevision = e.IndexedContentRevision, IndexedManifestRoot = e.IndexedManifestRoot, RecordStatusName = e.RecordStatusName, HeaderSourceCanonicalBytes = e.HeaderSourceCanonicalBytes,
            ParentFormRecordId = e.ParentFormRecordId, CreatedByBusinessUserId = e.CreatedByBusinessUserId, EntryDerivationVersion = e.EntryDerivationVersion, DerivedFieldSearchApprovalIds = e.DerivedFieldSearchApprovalIds,
            DerivedFieldItemCounts = e.DerivedFieldItemCounts,
            IndexedPostingCount = e.IndexedPostingCount, SealKeyName = e.SealKeyName, SealKeyVersion = e.SealKeyVersion, EntrySealSignature = e.EntrySealSignature,
        };
        change(copy);
        return copy;
    }

    [Fact]
    public void TheDerivationRecordsVersion3HeaderFactsAndItemCountsAndOlderEntriesAreNotIndexedAtTheHead()
    {
        var b = Build();
        Assert.Equal(3, b.Data.Entry.EntryDerivationVersion);
        Assert.NotNull(b.Data.Entry.HeaderSourceCanonicalBytes);
        Assert.NotNull(b.Data.Entry.DerivedFieldItemCounts);
        var head = new RecordContentHead { CommittedOperationId = b.Data.Entry.IndexedOperationId, CommittedContentRevision = 1, CommittedManifestRoot = b.Data.HeadManifestRoot };
        var required = b.Data.Entry.DerivedFieldSearchApprovalIds.ToHashSet();
        Assert.True(SearchIndexBuilderService.IsIndexedAtHead(b.Data.Entry, head, required));
        // C-141: version 2 (header facts, no item counts) and version 1 entries are re-derived by the builder before attestation.
        Assert.False(SearchIndexBuilderService.IsIndexedAtHead(Copy(b.Data.Entry, e => { e.EntryDerivationVersion = 2; e.DerivedFieldItemCounts = null; }), head, required));
        Assert.False(SearchIndexBuilderService.IsIndexedAtHead(Copy(b.Data.Entry, e => e.EntryDerivationVersion = 1), head, required));
        // A version 2 entry is still bound by I-2 (its header facts are present); only the share-only step needs the counts.
        Assert.True(Verify(b, b.Data with { Entry = Copy(b.Data.Entry, e => { e.EntryDerivationVersion = 2; e.DerivedFieldItemCounts = null; }) }).Bound);
    }

    // ---- candidate templates, digest, sort, options, grants ------------------------------------------------------------------

    [Fact]
    public void EveryVocabularyOperatorHasAFixedTemplateAndValuesAreOnlyParameters()
    {
        var cases = new (string Type, string Op, string? Value, string? Lower, string? Upper)[]
        {
            ("String", "equals", "it's", null, null), ("String", "startsWith", "50%_\\x", null, null), ("String", "contains", "a'b", null, null),
            ("Integer", "equals", "4711", null, null), ("Integer", "greaterThan", "4712", null, null), ("Integer", "greaterThanOrEqual", "4713", null, null), ("Integer", "lessThan", "4714", null, null),
            ("Integer", "lessThanOrEqual", "4715", null, null), ("Integer", "between", null, "4716", "4717"), ("Decimal", "between", null, "1.57", "2.58"), ("Date", "before", "2026-01-01", null, null),
            ("Date", "after", "2026-01-01", null, null), ("Date", "equals", "2026-01-01", null, null), ("DateTime", "between", null, "2026-01-01T00:00:00Z", "2026-02-01T00:00:00Z"),
            ("Boolean", "isTrue", null, null, null), ("Boolean", "isFalse", null, null, null), ("Option", "is", Open.ToString("D"), null, null), ("Option", "includes", Open.ToString("D"), null, null),
        };
        foreach (var (type, op, value, lower, upper) in cases)
        {
            using var command = new NpgsqlCommand();
            var clause = CandidateTemplates.Clause(P(TextField, op, type, value, lower, upper), 0, command.Parameters);
            Assert.StartsWith("  AND ", clause, StringComparison.Ordinal);
            Assert.Contains("OFFSET 0", clause, StringComparison.Ordinal);
            Assert.Contains("p.form_record_id = e.form_record_id", clause, StringComparison.Ordinal);
            Assert.Contains("p.indexed_operation_id = e.indexed_operation_id", clause, StringComparison.Ordinal);
            foreach (var literal in new[] { value, lower, upper }.Where(v => v is not null))
            {
                Assert.DoesNotContain(literal!, clause, StringComparison.Ordinal);
            }

            Assert.Contains(command.Parameters, p => p.ParameterName == "f0");
        }

        Assert.Equal("50\\%\\_\\\\x", CandidateTemplates.EscapeLike("50%_\\x"));
        using var isCommand = new NpgsqlCommand();
        Assert.Contains("AND NOT EXISTS", CandidateTemplates.Clause(P(OptionField, "is", "Option", Open.ToString("D")), 3, isCommand.Parameters), StringComparison.Ordinal);
    }

    [Fact]
    public void TheQueryDigestIsBoundToTheNonceAndToEveryPartOfTheQuery()
    {
        var query = new IndexedSearchQuery(Ids.Form, [C(TextField, "equals", "String", "CR-1")], null, false, false, null, null, 25);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var digest = IndexedSearchService.QueryDigest(nonce, query);
        Assert.Equal(32, digest.Length);
        Assert.Equal(digest, IndexedSearchService.QueryDigest(nonce, query));
        Assert.NotEqual(digest, IndexedSearchService.QueryDigest(RandomNumberGenerator.GetBytes(32), query));
        Assert.NotEqual(digest, IndexedSearchService.QueryDigest(nonce, query with { Conditions = [C(TextField, "equals", "String", "CR-2")] }));
        Assert.NotEqual(digest, IndexedSearchService.QueryDigest(nonce, query with { PageSize = 10 }));
        Assert.NotEqual(digest, IndexedSearchService.QueryDigest(nonce, query with { IncludeArchived = true }));
    }

    [Fact]
    public void TheSortIsTheV1SortWithTheSameTieBreakers()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DerivedHeaderFacts H(string status) => new(Ids.Form, status, null, Ids.Creator);
        var a = Guid.Parse("01900000-0000-7000-8000-000000000001");
        var b = Guid.Parse("01900000-0000-7000-8000-000000000002");
        var c = Guid.Parse("01900000-0000-7000-8000-000000000003");
        var matches = new List<(Guid Id, DerivedHeaderFacts Header, DateTimeOffset Created, DateTimeOffset Modified)>
        {
            (a, H("Draft"), t0, t0.AddMinutes(5)), (b, H("Submitted"), t0.AddMinutes(1), t0.AddMinutes(5)), (c, H("Draft"), t0.AddMinutes(2), t0.AddMinutes(1)),
        };
        Assert.Equal([b, a, c], IndexedSearchService.Sort(matches, null, null).Select(m => m.Id));                 // modifiedOn desc, then id desc
        Assert.Equal([c, a, b], IndexedSearchService.Sort(matches, "modifiedOn", "asc").Select(m => m.Id));
        Assert.Equal([a, b, c], IndexedSearchService.Sort(matches, "createdOn", "asc").Select(m => m.Id));
        Assert.Equal([c, a, b], IndexedSearchService.Sort(matches, "recordStatus", null).Select(m => m.Id));         // asc by status, then modified asc, id asc
        Assert.Equal([c, a, b], IndexedSearchService.Sort(matches, "formName", null).Select(m => m.Id));
    }

    [Fact]
    public void QueryOptionsAreBoundedAndOffByDefault()
    {
        Assert.False(new IndexedSearchOptions().QueryEnabled);
        IndexedSearchOptions.Validate(new IndexedSearchOptions());
        foreach (var bad in new Action<IndexedSearchOptions>[] { o => o.MaximumResults = 10_001, o => o.SnapshotLifetimeSeconds = 901, o => o.MaximumSnapshotsPerSession = 4, o => o.MaximumShareOnlyItems = 20_001, o => o.MaximumConditions = 6, o => o.StatementTimeoutSeconds = 0 })
        {
            var options = new IndexedSearchOptions();
            bad(options);
            Assert.Throws<InvalidOperationException>(() => IndexedSearchOptions.Validate(options));
        }
    }

    [Fact]
    public void ResultSnapshotsAreInsertedAndDeletedButNeverUpdatedAndTheReaderLoginIsSeparate()
    {
        var statements = AuthorityRuntimeGrants.SearchIndexStatements("authority_runtime").ToList();
        foreach (var table in new[] { "search_result_snapshots", "search_result_snapshot_entries" })
        {
            Assert.Contains($"GRANT SELECT, INSERT, DELETE ON authority_search.{table} TO authority_runtime", statements);
        }

        // The only update is the page's served-on timestamp (D-IX3-2): column-limited, on the snapshot table only.
        Assert.Equal(["GRANT UPDATE (last_page_served_on) ON authority_search.search_result_snapshots TO authority_runtime"],
            statements.Where(s => s.Contains("UPDATE", StringComparison.Ordinal) && s.Contains("search_result_snapshot", StringComparison.Ordinal)).ToList());

        var reader = AuthorityRuntimeGrants.SearchReaderLoginStatements("authority_search_reader_login").ToList();
        Assert.Contains("GRANT authority_search_reader TO authority_search_reader_login", reader);
        Assert.Contains("ALTER ROLE authority_search_reader_login SET default_transaction_read_only = on", reader);
        Assert.Contains("ALTER ROLE authority_search_reader_login SET log_parameter_max_length = 0", reader);
        var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql("Host=unit-test.invalid;Database=unused;Username=unused;Password=unused").Options;
        using var context = new AuthorityDbContext(options);
        Assert.Throws<ArgumentException>(() => AuthorityRuntimeGrants.Apply(context, "authority_runtime", "authority_runtime"));
        Assert.Throws<ArgumentException>(() => AuthorityRuntimeGrants.Apply(context, "authority_runtime", "authority_search_reader"));
        Assert.Throws<ArgumentException>(() => AuthorityRuntimeGrants.Apply(context, "authority_runtime", "Reader; DROP"));
    }
}
