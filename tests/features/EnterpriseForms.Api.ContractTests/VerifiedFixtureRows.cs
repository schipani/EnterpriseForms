using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2c: unit tests that feed one candidate's rows back into the candidate builder as the record's current content model a
/// committed, Authority-verified snapshot. In production those rows come from the store with signed schema 1 (the stager assigns
/// it); here they never went through staging and still carry schema 0, which the verified reader (Mode A) refuses. This marks
/// the encrypted ones as staging would, so the tests exercise the verified path with rows shaped like real verified rows.
/// </summary>
internal static class VerifiedFixtureRows
{
    public static IReadOnlyList<T> AsCommitted<T>(IReadOnlyList<T> rows)
        where T : ISignedContentRow
    {
        foreach (var row in rows.OfType<FieldEncryptedValue>().Where(r => r.RowSignatureSchemaVersion == 0))
        {
            row.RowSignatureSchemaVersion = 1;
        }

        return rows;
    }
}
