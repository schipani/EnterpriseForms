using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// R-53 (C-148) security regression tests: the Authority binds every content item id permanently to its organization, record,
/// kind and identity anchor, on every write path, against the verified current snapshot and the permanent bindings. Real
/// <see cref="Application.Features.Content.ContentService"/>, canonical encoding and HMAC provider over the in-memory store.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static async Task<AuthorityException> RefusedAsync(Harness h, Guid actor, IEnumerable<Row> rows)
    {
        var operation = Guid.CreateVersion7();
        return await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(actor, operation, h.Stage(rows, operation), withCurrent: true));
    }

    private static List<Row> Without(List<Row> rows, Guid item) => rows.Where(r => r.ItemId != item).ToList();

    [Fact]
    public async Task R53AnExistingItemMayBeEditedInItsFieldButNeverMovedToAnotherField()
    {
        var h = await new Harness().CreatedAsync();
        Assert.Equal(2, (await h.WriteAsync(h.Owner, Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("edited"))), withCurrent: true)).CommittedContentRevision);

        // To another existing field (the shared one) and to a brand-new field: both refused, nothing changes.
        var moved = await RefusedAsync(h, h.Owner, Change(h.Rows(), h.Other, r => r.With("field_definition_id", RowValue.Uuid(h.SharedField))));
        Assert.Equal(AuthorityErrorCodes.ItemIdentityChanged, moved.ErrorCode);
        var toNew = await RefusedAsync(h, h.Owner, Change(h.Rows(), h.Other, r => r.With("field_definition_id", RowValue.Uuid(Guid.CreateVersion7()))));
        Assert.Equal(AuthorityErrorCodes.ItemIdentityChanged, toNew.ErrorCode);
        Assert.Equal(2, h.Head.CommittedContentRevision);

        // The bindings were recorded at commit from the verified bytes, one per item.
        var other = h.Store.ItemIdentities.Single(i => i.ManifestItemId == h.Other);
        Assert.Equal((h.Org, h.Record, h.OtherField), (other.OrganizationId, other.FormRecordId, other.BoundFieldDefinitionId));
        Assert.Equal(h.Rows().Count, h.Store.ItemIdentities.Count);
    }

    [Fact]
    public async Task R53ARemovedItemCanNeverReturnUnderItsOriginalFieldOrAnother()
    {
        var h = await new Harness().CreatedAsync();
        await h.WriteAsync(h.Owner, Without(h.Rows(), h.Other), withCurrent: true);
        Assert.Equal(AuthorityErrorCodes.ItemIdentityReused, (await RefusedAsync(h, h.Owner, h.Rows())).ErrorCode); // original field
        var underAnother = Without(h.Rows(), h.Other).Append(Text(h.Org, h.Record, h.SharedField, h.Other, "revived")).ToList();
        Assert.Equal(AuthorityErrorCodes.ItemIdentityReused, (await RefusedAsync(h, h.Owner, underAnother)).ErrorCode);

        // A value entered again gets a new id, which is accepted (the product's behaviour, TQ-6).
        var reentered = Without(h.Rows(), h.Other).Append(Text(h.Org, h.Record, h.OtherField, Guid.CreateVersion7(), "re-entered")).ToList();
        Assert.Equal(3, (await h.WriteAsync(h.Owner, reentered, withCurrent: true)).CommittedContentRevision);
    }

    [Fact]
    public async Task R53LegacyItemsAcceptedBeforeTheBindingsExistedAreAlsoProtectedFromReuse()
    {
        var h = await new Harness().CreatedAsync();
        await h.WriteAsync(h.Owner, Without(h.Rows(), h.Other), withCurrent: true);
        h.Store.ItemIdentities.RemoveAll(i => i.ManifestItemId == h.Other); // an item retired before R-53: no binding, only historical leaves
        Assert.Equal(AuthorityErrorCodes.ItemIdentityReused, (await RefusedAsync(h, h.Owner, h.Rows())).ErrorCode);

        // An id accepted in another record (any organization) may not be reused here either.
        var foreign = Guid.CreateVersion7();
        h.Store.ManifestLeaves.Add(new RecordManifestLeaf { RecordManifestLeafId = Guid.CreateVersion7(), RecordManifestCommitId = Guid.CreateVersion7(), ManifestItemId = foreign, ManifestItemKindCode = (byte)ManifestItemKind.FieldValue, ManifestItemKey = new byte[17] });
        var reuse = Without(h.Rows(), h.Other).Append(Text(h.Org, h.Record, h.OtherField, foreign, "copied")).ToList();
        Assert.Equal(AuthorityErrorCodes.ItemIdentityReused, (await RefusedAsync(h, h.Owner, reuse)).ErrorCode);
    }

    [Fact]
    public async Task R53MultiSelectSelectionsKeepTheirIdsAndARemovedSelectionCannotReturn()
    {
        var h = new Harness();
        Guid tags = Guid.CreateVersion7(), a = Guid.CreateVersion7(), b = Guid.CreateVersion7(), c = Guid.CreateVersion7();
        Guid optionA = Guid.CreateVersion7(), optionB = Guid.CreateVersion7(), optionC = Guid.CreateVersion7();
        List<Row> With(params (Guid Item, Guid Option)[] selections) => h.Rows().Concat(selections.Select(s => Option(h.Org, h.Record, tags, s.Item, s.Option))).ToList();
        await h.WriteAsync(h.Owner, With((a, optionA), (b, optionB)), withCurrent: true);

        // Remove b, add c (new id): accepted. Changing a selection's option keeps its id (content): accepted.
        await h.WriteAsync(h.Owner, With((a, optionA), (c, optionC)), withCurrent: true);
        await h.WriteAsync(h.Owner, With((a, optionB), (c, optionC)), withCurrent: true);
        Assert.Equal(AuthorityErrorCodes.ItemIdentityReused, (await RefusedAsync(h, h.Owner, With((a, optionB), (c, optionC), (b, optionB)))).ErrorCode);
        var moved = With((c, optionC)).Append(Option(h.Org, h.Record, h.SharedField, a, optionB)).ToList();
        Assert.Equal(AuthorityErrorCodes.ItemIdentityChanged, (await RefusedAsync(h, h.Owner, moved)).ErrorCode);
        Assert.Equal(3, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task R53RepeatingGroupValuesStayInTheirEntryAndRemovedEntriesCannotReturn()
    {
        var h = new Harness();
        Guid field = Guid.CreateVersion7(), entry1 = Guid.CreateVersion7(), entry2 = Guid.CreateVersion7(), value1 = Guid.CreateVersion7(), value2 = Guid.CreateVersion7();
        List<Row> Rows(params (Guid Entry, Guid Value, string Text)[] entries) => h.Rows()
            .Concat(entries.SelectMany((e, i) => new[] { Group(h.Org, h.Record, e.Entry, i + 1), Text(h.Org, h.Record, field, e.Value, e.Text, e.Entry) })).ToList();
        await h.WriteAsync(h.Owner, Rows((entry1, value1, "first"), (entry2, value2, "second")), withCurrent: true);
        await h.WriteAsync(h.Owner, Rows((entry2, value2, "second edited"), (entry1, value1, "first")), withCurrent: true); // edit + reorder

        // A value may not move to another entry.
        var swapped = h.Rows().Concat([Group(h.Org, h.Record, entry1, 1), Group(h.Org, h.Record, entry2, 2), Text(h.Org, h.Record, field, value1, "first", entry2), Text(h.Org, h.Record, field, value2, "second", entry1)]).ToList();
        Assert.Equal(AuthorityErrorCodes.ItemIdentityChanged, (await RefusedAsync(h, h.Owner, swapped)).ErrorCode);

        // Remove entry 2 (and its value); a replacement entry gets new ids; the removed ids cannot return.
        Guid entry3 = Guid.CreateVersion7(), value3 = Guid.CreateVersion7();
        await h.WriteAsync(h.Owner, Rows((entry1, value1, "first"), (entry3, value3, "third")), withCurrent: true);
        Assert.Equal(AuthorityErrorCodes.ItemIdentityReused, (await RefusedAsync(h, h.Owner, Rows((entry1, value1, "first"), (entry3, value3, "third"), (entry2, value2, "back")))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.ItemIdentityReused, (await RefusedAsync(h, h.Owner, Rows((entry1, value1, "first"), (entry3, value2, "moved into entry 3")))).ErrorCode);
        Assert.Equal(3, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task R53ADocumentKeepsItsIdAcrossVersionsButNotAcrossFields()
    {
        var h = await new Harness().CreatedAsync();
        Assert.Equal(2, (await h.WriteAsync(h.Owner, Change(h.Rows(), h.Document, r => r.With("version_number", RowValue.Int32(2)).With("storage_object_key", RowValue.Text("objects/two"))), withCurrent: true)).CommittedContentRevision);
        var moved = await RefusedAsync(h, h.Owner, Change(h.Rows(), h.Document, r => r.With("version_number", RowValue.Int32(2)).With("storage_object_key", RowValue.Text("objects/two")).With("field_definition_id", RowValue.Uuid(Guid.CreateVersion7()))));
        Assert.Equal(AuthorityErrorCodes.ItemIdentityChanged, moved.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R53AShareEditorDirectOrThroughATeamCanEditASharedItemButNotMoveIt(bool viaTeam)
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor("contributor", viaTeam);
        if (viaTeam)
        {
            h.Edge(ResourceTypeNames.Team, h.Team, "member", ResourceTypeNames.User, h.Contributor);
        }

        Assert.Equal(2, (await h.WriteAsync(h.Contributor, Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("contributor edit"))), withCurrent: true)).CommittedContentRevision);
        var moved = await RefusedAsync(h, h.Contributor, Change(h.Rows("contributor edit"), h.Shared, r => r.With("field_definition_id", RowValue.Uuid(h.OtherField))));
        Assert.Equal(AuthorityErrorCodes.ItemIdentityChanged, moved.ErrorCode);
        // The owner cannot move the shared item onto another field either (it would hand that field to the share).
        Assert.Equal(AuthorityErrorCodes.ItemIdentityChanged, (await RefusedAsync(h, h.Owner, Change(h.Rows("contributor edit"), h.Shared, r => r.With("field_definition_id", RowValue.Uuid(h.OtherField))))).ErrorCode);
    }

    [Fact]
    public async Task R53ANewOwnerAfterTransferInheritsTheBindingsUnchanged()
    {
        var h = await new Harness().CreatedAsync();
        h.Remove(h.Store.Edges.Single(e => e.RelationshipName == "owner"));
        h.Edge(ResourceTypeNames.FormRecord, h.Record, "owner", ResourceTypeNames.User, h.Contributor);
        Assert.Equal(2, (await h.WriteAsync(h.Contributor, Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("new owner"))), withCurrent: true)).CommittedContentRevision);
        Assert.Equal(AuthorityErrorCodes.ItemIdentityChanged, (await RefusedAsync(h, h.Contributor, Change(h.Rows(), h.Other, r => r.With("field_definition_id", RowValue.Uuid(h.SharedField))))).ErrorCode);
    }

    [Fact]
    public async Task R53AdversarialPayloadsFailClosed()
    {
        var h = await new Harness().CreatedAsync();
        var edit = Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("x")));

        // No current snapshot on a record with accepted content.
        var operation = Guid.CreateVersion7();
        var missing = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage(edit, operation), withCurrent: false));
        Assert.Contains("CURRENT_SNAPSHOT_REQUIRED", missing.Message, StringComparison.Ordinal);

        // A forged current snapshot that claims the item already lives in the target field: its bytes do not match the accepted digests.
        var moved = Change(h.Rows(), h.Other, r => r.With("field_definition_id", RowValue.Uuid(h.SharedField)));
        var forgedCurrent = h.Accepted.Select(a => a.Row.ItemId == h.Other
            ? new StagedContentItem(a.Row.Kind, a.Row.ItemId, Encode(Text(h.Org, h.Record, h.SharedField, h.Other, "not shared"), h.Org, Guid.CreateVersion7(), 1).Base64, Convert.ToBase64String(a.Signature))
            : new StagedContentItem(a.Row.Kind, a.Row.ItemId, a.Row.Base64, Convert.ToBase64String(a.Signature))).ToList();
        operation = Guid.CreateVersion7();
        var forged = await Assert.ThrowsAsync<AuthorityException>(() => h.Service.PrepareAsync(Harness.Actor(h.Owner), h.Request(operation, h.Stage(moved, operation), withCurrent: false) with { CurrentItems = forgedCurrent }, "key-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.CurrentSnapshotMismatch, forged.ErrorCode);

        // The same id under two item kinds in one candidate.
        var duplicate = h.Rows().Append(Option(h.Org, h.Record, h.OtherField, h.Other, Guid.CreateVersion7())).ToList();
        Assert.Equal(AuthorityErrorCodes.ItemIdentityReused, (await RefusedAsync(h, h.Owner, duplicate)).ErrorCode);

        // A current snapshot sent for a record that has no content yet.
        var fresh = new Harness();
        operation = Guid.CreateVersion7();
        var stray = h.Accepted.Select(a => new StagedContentItem(a.Row.Kind, a.Row.ItemId, a.Row.Base64, Convert.ToBase64String(a.Signature))).ToList();
        var unexpected = await Assert.ThrowsAsync<AuthorityException>(() => fresh.Service.PrepareAsync(Harness.Actor(fresh.Owner), fresh.Request(operation, fresh.Stage(fresh.Rows(), operation), withCurrent: false) with { CurrentItems = stray }, "key-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None));
        Assert.Contains("CURRENT_SNAPSHOT_UNEXPECTED", unexpected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R53ACommitRacingAnotherBindingOfTheSameNewIdIsRefused()
    {
        var h = await new Harness().CreatedAsync();
        var newItem = Guid.CreateVersion7();
        var rows = h.Rows().Append(Text(h.Org, h.Record, h.OtherField, newItem, "new")).ToList();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(rows, operation);
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true);

        // Between prepare and commit another record binds the same id (a concurrent commit elsewhere).
        h.Store.ItemIdentities.Add(new ContentItemIdentity { ContentItemIdentityId = Guid.CreateVersion7(), ManifestItemId = newItem, OrganizationId = h.Org, FormRecordId = Guid.CreateVersion7(), ManifestItemKindCode = (byte)ManifestItemKind.FieldValue, BoundFieldDefinitionId = h.OtherField, FirstCommittedOperationId = Guid.CreateVersion7(), IdentityBoundOn = DateTimeOffset.UtcNow });
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Owner, prepared, candidate));
        Assert.Equal(AuthorityErrorCodes.ItemIdentityChanged, ex.ErrorCode);
        Assert.Equal(1, h.Head.CommittedContentRevision);
    }
}
