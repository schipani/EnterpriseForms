using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormRecords.Lifecycle;
using EnterpriseForms.Application.Features.FormRecords.ListRecords;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Domain.Features.FormRecords;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>Phase C6 Milestone 1 — the pure lifecycle rules (architecture §2): transition table, deterministic unarchive, content lock, list default.</summary>
public sealed class PhaseC6LifecycleTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 22, 14, 30, 15, 123, TimeSpan.Zero).AddTicks(7);
    private static readonly DateTimeOffset Earlier = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryApprovedTransitionProducesTheDocumentedHeader()
    {
        var submit = RecordLifecycle.Plan("Draft", null, "Submit", Now);
        Assert.Equal("Submitted", submit!.Value.Status);
        // The signed timestamp is truncated to the microsecond precision RowHmac/v1 accepts.
        Assert.Equal(new DateTimeOffset(Now.UtcTicks - (Now.UtcTicks % 10), TimeSpan.Zero), submit.Value.SubmittedOn);

        Assert.Equal(("Draft", (DateTimeOffset?)null), RecordLifecycle.Plan("Submitted", Earlier, "Reopen", Now));
        Assert.Equal(("Archived", (DateTimeOffset?)null), RecordLifecycle.Plan("Draft", null, "Archive", Now));
        Assert.Equal(("Archived", (DateTimeOffset?)Earlier), RecordLifecycle.Plan("Submitted", Earlier, "Archive", Now));
    }

    [Fact]
    public void UnarchiveReturnsARecordToExactlyTheStateItWasArchivedFromWithoutAnyExtraSignedColumn()
    {
        // Never submitted (or reopened before archiving): back to Draft. Submitted before archiving: back to Submitted, same submission time.
        Assert.Equal(("Draft", (DateTimeOffset?)null), RecordLifecycle.Plan("Archived", null, "Unarchive", Now));
        Assert.Equal(("Submitted", (DateTimeOffset?)Earlier), RecordLifecycle.Plan("Archived", Earlier, "Unarchive", Now));

        // Round trips.
        var submitted = RecordLifecycle.Plan("Draft", null, "Submit", Now)!.Value;
        var archived = RecordLifecycle.Plan(submitted.Status, submitted.SubmittedOn, "Archive", Now)!.Value;
        Assert.Equal(submitted, RecordLifecycle.Plan(archived.Status, archived.SubmittedOn, "Unarchive", Now));
        var reopened = RecordLifecycle.Plan(submitted.Status, submitted.SubmittedOn, "Reopen", Now)!.Value;
        var archivedDraft = RecordLifecycle.Plan(reopened.Status, reopened.SubmittedOn, "Archive", Now)!.Value;
        Assert.Equal(reopened, RecordLifecycle.Plan(archivedDraft.Status, archivedDraft.SubmittedOn, "Unarchive", Now));
    }

    [Theory]
    [InlineData("Draft", "Reopen")]
    [InlineData("Draft", "Unarchive")]
    [InlineData("Draft", "Submit-typo")]
    [InlineData("Submitted", "Submit")]
    [InlineData("Submitted", "Unarchive")]
    [InlineData("Archived", "Submit")]
    [InlineData("Archived", "Reopen")]
    [InlineData("Archived", "Archive")]
    [InlineData("Deleted", "Unarchive")]
    [InlineData("Deleted", "Archive")]
    [InlineData("", "Submit")]
    public void EveryOtherCombinationIsRefused(string status, string transition) =>
        Assert.Null(RecordLifecycle.Plan(status, null, transition, Now));

    [Fact]
    public void AllowedTransitionsAreExactlyThosePlanAcceptsAndOnlyDraftIsEditable()
    {
        foreach (var status in new[] { "Draft", "Submitted", "Archived", "Deleted", "Unknown" })
        {
            var allowed = RecordLifecycle.AllowedFor(status);
            foreach (var transition in RecordLifecycle.Transitions)
            {
                Assert.Equal(allowed.Contains(transition), RecordLifecycle.Plan(status, Earlier, transition, Now) is not null);
            }

            Assert.Equal(status == "Draft", RecordLifecycle.IsContentEditable(status));
        }
    }

    [Fact]
    public void TheLockReadsTheVerifiedHeaderStatusAndRefusesEveryNonDraftState()
    {
        VerifiedRecord Verified(string status) =>
            new(null!, null!, new FormRecordRevision { RecordStatus = status }, [], [], null!, null!, null!, [], [], null!, [], []);

        RecordLifecycle.RequireContentEditable(Verified("Draft"));
        foreach (var locked in new[] { "Submitted", "Archived", "Deleted", "draft", "" })
        {
            var refused = Assert.Throws<BusinessException>(() => RecordLifecycle.RequireContentEditable(Verified(locked)));
            Assert.Equal("RECORD_NOT_EDITABLE", refused.ErrorCode);
            Assert.Equal(409, refused.HttpStatusCode);
        }
    }

    [Fact]
    public void TheDefaultListHidesArchivedRecordsAndAskingForThemIsExplicit()
    {
        var formNames = new Dictionary<Guid, string>();
        FormRecord Record(string status) => new() { FormRecordId = Guid.CreateVersion7(), RecordStatus = status, ModifiedOn = Now };
        var draft = Record("Draft");
        var submitted = Record("Submitted");
        var archived = Record("Archived");
        var all = new[] { draft, submitted, archived };

        var ordinary = RecordListQuery.Create(null, null, false, null, null, 25).Apply(all, formNames);
        Assert.DoesNotContain(archived, ordinary);
        Assert.Equal(2, ordinary.Count);

        Assert.Contains(archived, RecordListQuery.Create(null, null, false, null, null, 25, includeArchived: true).Apply(all, formNames));
        var onlyArchived = RecordListQuery.Create(null, "Archived", false, null, null, 25).Apply(all, formNames);
        Assert.Equal([archived], onlyArchived);

        // A cursor computed for one choice is never valid for the other.
        Assert.NotEqual(RecordListQuery.Create(null, null, false, null, null, 25).ScopeDigest(), RecordListQuery.Create(null, null, false, null, null, 25, includeArchived: true).ScopeDigest());
    }

    [Fact]
    public void EveryTransitionHasADistinctCatalogAuditAction()
    {
        var actions = RecordLifecycle.Transitions.Select(RecordLifecycle.AuditActionFor).ToList();
        Assert.Equal(["RECORD_SUBMITTED", "RECORD_REOPENED", "RECORD_ARCHIVED", "RECORD_UNARCHIVED"], actions);
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordLifecycle.AuditActionFor("Delete"));
    }
}
