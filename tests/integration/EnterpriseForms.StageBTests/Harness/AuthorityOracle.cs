using System.Globalization;
using EnterpriseForms.SecurityTests.Harness;

namespace EnterpriseForms.StageBTests.Harness;

/// <summary>
/// Phase C6 M0 test oracle. Which operation is a record's accepted state is read from the independently administered
/// AuthorityDb (read-only observer), never from business-database projections or "the highest staged revision" — those
/// are exactly what the field-protection status estimates are built from, so checking an estimate against them would
/// test it against itself. The rows inside an accepted operation are then read from the business database, which is
/// where the content lives.
/// </summary>
public static class AuthorityOracle
{
    public sealed record AcceptedHead(Guid? OperationId, long ContentRevision);

    /// <summary>The Authority-accepted head of each record (revision 0 and no operation: no accepted content).</summary>
    public static Dictionary<Guid, AcceptedHead> Heads(ComposeStack stack, IEnumerable<Guid> records)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var ids = records.Distinct().ToList();
        var result = new Dictionary<Guid, AcceptedHead>();
        if (ids.Count == 0)
        {
            return result;
        }

        var list = string.Join(",", ids.Select(id => $"'{id:D}'"));
        var output = stack.AuthorityObserverSql($"SELECT form_record_id || '|' || coalesce(committed_operation_id::text, '') || '|' || committed_content_revision FROM record_content_heads WHERE form_record_id IN ({list});");
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            result[Guid.Parse(parts[0])] = new AcceptedHead(parts[1].Length == 0 ? null : Guid.Parse(parts[1]), long.Parse(parts[2], CultureInfo.InvariantCulture));
        }

        return result;
    }

    public static AcceptedHead Head(ComposeStack stack, Guid record) =>
        Heads(stack, [record]).TryGetValue(record, out var head) ? head : throw new InvalidOperationException("the authority has no content head for record " + record.ToString("D"));

    /// <summary>
    /// Records, and rows, of a field in <paramref name="table"/> (e.g. <c>field_string_values</c>, <c>field_encrypted_values</c>)
    /// inside each record's Authority-accepted operation.
    /// </summary>
    public static (int Records, int Values) RowsInAcceptedHeads(ComposeStack stack, string table, Guid fieldDefinitionId, IEnumerable<Guid> records)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var pairs = Heads(stack, records).Where(h => h.Value.OperationId is not null).Select(h => $"('{h.Key:D}'::uuid, '{h.Value.OperationId:D}'::uuid)").ToList();
        if (pairs.Count == 0)
        {
            return (0, 0);
        }

        var output = stack.AttackerSql($"SELECT count(DISTINCT form_record_id) || '|' || count(*) FROM {table} WHERE field_definition_id = '{fieldDefinitionId:D}' AND (form_record_id, content_change_operation_id) IN (VALUES {string.Join(",", pairs)});").Trim();
        var parts = output.Split('|');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Whether an accepted operation was a workload reprotection operation (it carries the authorizing command) or a user operation.</summary>
    public static Guid? ReprotectionCommandOf(ComposeStack stack, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var value = stack.AuthorityObserverSql($"SELECT coalesce(reprotection_command_id::text, '') FROM content_change_operations WHERE content_change_operation_id = '{operationId:D}';").Trim();
        return value.Length == 0 ? null : Guid.Parse(value);
    }

    /// <summary>The Authority's state of a content operation (Prepared, Committed, Aborted, Quarantined), or empty when unknown to the Authority.</summary>
    public static string OperationState(ComposeStack stack, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(stack);
        return stack.AuthorityObserverSql($"SELECT operation_state_name FROM content_change_operations WHERE content_change_operation_id = '{operationId:D}';").Trim();
    }

    /// <summary>
    /// Asserts the business projection of each record's accepted head (form_records.accepted_content_change_operation_id at
    /// record_revision) equals the Authority's head; returns a diagnostic of the mismatches (empty when none).
    /// </summary>
    public static string ProjectionMismatches(ComposeStack stack, IEnumerable<Guid> records)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var heads = Heads(stack, records);
        var mismatches = new List<string>();
        foreach (var (record, head) in heads)
        {
            var projected = stack.AttackerSql($"SELECT coalesce(accepted_content_change_operation_id::text, '') || '|' || record_revision FROM form_records WHERE form_record_id = '{record:D}';").Trim();
            var expected = (head.OperationId?.ToString("D") ?? string.Empty) + "|" + head.ContentRevision.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(projected, expected, StringComparison.Ordinal))
            {
                mismatches.Add($"{record:D}: projected {projected}, authority {expected}");
            }
        }

        return string.Join("; ", mismatches);
    }
}
