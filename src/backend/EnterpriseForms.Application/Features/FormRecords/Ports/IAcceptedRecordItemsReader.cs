using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Staging;

namespace EnterpriseForms.Application.Features.FormRecords.Ports;

/// <summary>
/// FormRecords public port (ADR-ARCH-001): the staged rows the business store holds for one accepted operation of a record,
/// as the observed items the Authority verifies. The rows prove nothing by themselves — they are untrusted until the
/// Authority has checked them against the head it accepted (Phase C7 AC-5 batch verification).
/// </summary>
public interface IAcceptedRecordItemsReader
{
    Task<IReadOnlyList<StagedContentItem>> ReadObservedItemsAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken);
}

public sealed class AcceptedRecordItemsReader(RecordCandidateStager stager) : IAcceptedRecordItemsReader
{
    public async Task<IReadOnlyList<StagedContentItem>> ReadObservedItemsAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) =>
        (await stager.ReadStagedAsync(formRecordId, committedOperationId, cancellationToken).ConfigureAwait(false)).Items;
}
