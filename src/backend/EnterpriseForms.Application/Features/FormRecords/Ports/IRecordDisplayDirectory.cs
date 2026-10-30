using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormRecords.ListRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Ports;

/// <summary>Display names of one organization: form names and user display names (local directory projection, labels only).</summary>
public sealed record RecordDisplayNames(IReadOnlyDictionary<Guid, string> FormNames, IReadOnlyDictionary<Guid, string> UserNames);

/// <summary>
/// FormRecords public port (Phase C7 M3): the display labels My Records shows next to a record — form name, creator name and
/// the short record reference — for features that list verified records without reading the business store themselves
/// (verified field search). Labels only: nothing here decides which records exist or match.
/// </summary>
public interface IRecordDisplayDirectory
{
    Task<RecordDisplayNames> GetNamesAsync(Guid organizationId, CancellationToken cancellationToken);

    string ReferenceFor(Guid formRecordId);
}

public sealed class RecordDisplayDirectory(IBusinessStore store) : IRecordDisplayDirectory
{
    public async Task<RecordDisplayNames> GetNamesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var forms = (await scope.GetFormDefinitionsAsync(organizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.FormDefinitionId, d => d.FormDefinitionName);
        var users = (await scope.GetUsersAsync(organizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        return new RecordDisplayNames(forms, users);
    }

    public string ReferenceFor(Guid formRecordId) => RecordListQuery.ReferenceFor(formRecordId);
}
