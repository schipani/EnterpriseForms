using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Ports;

/// <summary>
/// FormRecords public port (ADR-ARCH-001): the staged rows the business store holds for one accepted operation of a record,
/// as the observed items the Authority verifies. The rows prove nothing by themselves — they are untrusted until the
/// Authority has checked them against the head it accepted (Phase C7 AC-5 batch verification).
/// </summary>
public interface IAcceptedRecordItemsReader
{
    Task<IReadOnlyList<StagedContentItem>> ReadObservedItemsAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken);

    /// <summary>
    /// Phase C7 M3: the observed items together with the header facts and typed plaintext values decoded from the very row
    /// objects those items were canonicalized from. Once the Authority has verified <see cref="AcceptedRecordContent.Items"/>,
    /// the facts and values are exactly the verified signed columns (status, creator, parent; value, field, group). Encrypted
    /// values are never decoded here.
    /// </summary>
    Task<AcceptedRecordContent> ReadAcceptedContentAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken);
}

/// <summary>Signed header columns of an accepted revision (Phase C7 M3): what a verified search may filter and display.</summary>
public sealed record AcceptedRecordHeaderFacts(Guid FormDefinitionId, string RecordStatus, Guid CreatedByUserId, Guid? ParentFormRecordId);

/// <summary>
/// One plaintext typed value of an accepted revision (Phase C7 M3). <c>ValueType</c> is String, Integer, Decimal, Date,
/// DateTime, Boolean, Guid or Option; exactly the matching typed member is set. <c>ItemId</c> is the manifest item it was
/// verified as (the readability of a value is decided per item). <c>FormRecordGroupId</c> is null for a
/// top-level value.
/// </summary>
public sealed record AcceptedFieldValue(Guid FieldDefinitionId, Guid ItemId, Guid? FormRecordGroupId, string ValueType, string? Text = null, long? IntegerValue = null, decimal? DecimalValue = null,
    DateOnly? Date = null, DateTimeOffset? Instant = null, bool? Boolean = null, Guid? Identifier = null);

public sealed record AcceptedRecordContent(IReadOnlyList<StagedContentItem> Items, AcceptedRecordHeaderFacts? Header, IReadOnlyList<AcceptedFieldValue> Values);

public sealed class AcceptedRecordItemsReader(RecordCandidateStager stager) : IAcceptedRecordItemsReader
{
    public async Task<IReadOnlyList<StagedContentItem>> ReadObservedItemsAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) =>
        (await stager.ReadStagedAsync(formRecordId, committedOperationId, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<AcceptedRecordContent> ReadAcceptedContentAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken)
    {
        var snapshot = await stager.ReadStagedAsync(formRecordId, committedOperationId, cancellationToken).ConfigureAwait(false);
        var header = snapshot.Header is { } h ? new AcceptedRecordHeaderFacts(h.FormDefinitionId, h.RecordStatus, h.CreatedByUserId, h.ParentFormRecordId) : null;
        var values = new List<AcceptedFieldValue>(snapshot.Rows.Count);
        foreach (var row in snapshot.Rows)
        {
            AcceptedFieldValue? value = row switch
            {
                FieldStringValue v => new(v.FieldDefinitionId, v.ItemId, v.FormRecordGroupId, "String", Text: v.StringValue),
                FieldIntegerValue v => new(v.FieldDefinitionId, v.ItemId, v.FormRecordGroupId, "Integer", IntegerValue: v.IntegerValue),
                FieldDecimalValue v => new(v.FieldDefinitionId, v.ItemId, v.FormRecordGroupId, "Decimal", DecimalValue: v.DecimalValue),
                FieldDateValue v => new(v.FieldDefinitionId, v.ItemId, v.FormRecordGroupId, "Date", Date: v.DateValue),
                FieldDateTimeValue v => new(v.FieldDefinitionId, v.ItemId, v.FormRecordGroupId, "DateTime", Instant: v.DateTimeValue),
                FieldBooleanValue v => new(v.FieldDefinitionId, v.ItemId, v.FormRecordGroupId, "Boolean", Boolean: v.BooleanValue),
                FieldGuidValue v => new(v.FieldDefinitionId, v.ItemId, v.FormRecordGroupId, "Guid", Identifier: v.GuidValue),
                FieldOptionValue v => new(v.FieldDefinitionId, v.ItemId, v.FormRecordGroupId, "Option", Identifier: v.FieldOptionId),
                _ => null, // encrypted values, groups, attachments: never decoded for search
            };
            if (value is not null)
            {
                values.Add(value);
            }
        }

        return new AcceptedRecordContent(snapshot.Items, header, values);
    }
}
