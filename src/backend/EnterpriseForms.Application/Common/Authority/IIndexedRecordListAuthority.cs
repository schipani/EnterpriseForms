namespace EnterpriseForms.Application.Common.Authority;

// IX-6 (IX-D12, design R1 §E.6): the business API's view of the Authority's indexed My Records listing. The Authority decides the
// discoverable set, filters and sorts on verified index header facts and binds every listed record to its accepted manifest; the
// business API adds display names only.

public sealed record AuthorityIndexedRecordListQuery(Guid? FormDefinitionId, string? RecordStatus, bool IncludeChildren, bool IncludeArchived, string SortBy, string SortDirection, int Offset, int PageSize,
    long? ExpectedAuthorizationRevision);

public sealed record AuthorityIndexedRecordListItem(Guid FormRecordId, Guid FormDefinitionId, string RecordStatusName, Guid? ParentFormRecordId, Guid CreatedByBusinessUserId,
    DateTimeOffset RecordRegisteredOn, DateTimeOffset ContentHeadModifiedOn, bool Editable);

public sealed record AuthorityIndexedRecordListResult(long AuthorizationRevision, int TotalCount, int Offset, int PageSize, IReadOnlyList<AuthorityIndexedRecordListItem> Items,
    IReadOnlyList<Guid> AvailableFormDefinitionIds, int WithoutContentCount, IReadOnlyList<Guid> WithoutContentRecordIds, string ResultBasis, Guid CorrelationId);

public interface IIndexedRecordListAuthority
{
    Task<AuthorityIndexedRecordListResult> ListRecordsAsync(DelegatedUserContext user, AuthorityIndexedRecordListQuery query, CancellationToken cancellationToken);
}
