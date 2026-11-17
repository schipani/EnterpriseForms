// E3-M2e-2 positive acceptance matrix (readiness review §6, positive).
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;

internal sealed partial class Lab
{
    private Org pOrg = null!;
    private Org vOrg = null!;

    internal sealed record BindingRow(Guid BindingId, Guid DataKey, Guid Organization, Guid Record, short Kind, string CommitmentHex, int Count, Guid Operation, DateTimeOffset BoundOn);

    private async Task<List<BindingRow>> BindingRowsAsync(string where, string? database = null)
    {
        var rows = await RowsAsync(database is null ? adminCs : AdminFor(database),
            "SELECT content_data_key_binding_id, data_key_identifier, organization_id, form_record_id, envelope_payload_kind_code, data_key_commitment_bytes, binding_item_count, binding_content_change_operation_id, data_key_bound_on FROM authority.content_data_key_bindings WHERE " + where);
        return [.. rows.Select(r => new BindingRow(Guid.Parse(r[0]), Guid.Parse(r[1]), Guid.Parse(r[2]), Guid.Parse(r[3]), short.Parse(r[4], System.Globalization.CultureInfo.InvariantCulture), r[5],
            int.Parse(r[6], System.Globalization.CultureInfo.InvariantCulture), Guid.Parse(r[7]), DateTimeOffset.Parse(r[8], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal)))];
    }

    /// <summary>The <c>body</c> of the newest ContentHeadCommitted entry of <paramref name="record"/> (or of the given operation).</summary>
    private async Task<JsonElement> CommittedBodyAsync(Rec record, Guid? operation = null, string? database = null)
    {
        var json = await ScalarAsync(database is null ? adminCs : AdminFor(database), $"SELECT journal_event_body_json FROM authority.security_journal_entries WHERE organization_id = '{record.Org.Id}' AND journal_event_kind = 'ContentHeadCommitted' "
            + (operation is { } op ? $"AND content_change_operation_id = '{op}' " : $"AND journal_event_body_json LIKE '%{record.Id}%' ") + "ORDER BY journal_stream_revision DESC LIMIT 1");
        return Prop(JsonDocument.Parse(json).RootElement, "body");
    }

    private static JsonElement Prop(JsonElement element, string snake)
    {
        if (element.TryGetProperty(snake, out var v))
        {
            return v;
        }

        var pascal = string.Concat(snake.Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return element.TryGetProperty(pascal, out var w) ? w : throw new KeyNotFoundException(snake);
    }

    /// <summary>The DK-8 tuples of a body, as binding rows (organization from <paramref name="org"/>; record and operation from the body).</summary>
    private static List<BindingRow> TuplesOf(JsonElement body, Guid org)
    {
        var record = Guid.Parse(Prop(body, "form_record_id").GetString()!);
        var operation = Guid.Parse(Prop(body, "committed_operation_id").GetString()!);
        return [.. Prop(body, "new_data_key_bindings").EnumerateArray().Select(e => new BindingRow(
            Guid.Parse(Prop(e, "content_data_key_binding_id").GetString()!), Guid.Parse(Prop(e, "data_key_identifier").GetString()!), org, record,
            Prop(e, "envelope_payload_kind_code").GetInt16(), Prop(e, "data_key_commitment_hex").GetString()!, Prop(e, "binding_item_count").GetInt32(), operation,
            DateTimeOffset.Parse(Prop(e, "data_key_bound_on").GetString()!, System.Globalization.CultureInfo.InvariantCulture)))];
    }

    private static string Show(IEnumerable<BindingRow> rows) => "[" + string.Join("; ", rows.Select(r => $"{r.DataKey:N}/k{r.Kind}/n{r.Count}/{r.CommitmentHex[..8]}")) + "]";

    private static bool SameRows(IEnumerable<BindingRow> a, IEnumerable<BindingRow> b) =>
        a.OrderBy(r => r.BindingId).Select(Key).SequenceEqual(b.OrderBy(r => r.BindingId).Select(Key), StringComparer.Ordinal);

    private static string Key(BindingRow r) => string.Join('|', r.BindingId, r.DataKey, r.Organization, r.Record, r.Kind, r.CommitmentHex, r.Count, r.Operation, r.BoundOn.UtcTicks);

    /// <summary>RFC 9562 byte order, computed independently of the Authority's comparer.</summary>
    private static bool InByteOrder(IReadOnlyList<Guid> ids)
    {
        for (var i = 1; i < ids.Count; i++)
        {
            if (ids[i - 1].ToByteArray(bigEndian: true).AsSpan().SequenceCompareTo(ids[i].ToByteArray(bigEndian: true)) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static Guid DataKey(object source) => Rows.DataKeyOf(source) ?? throw new InvalidOperationException("not a v2 row");

    // ------------------------------------------------------------------------------------------------ scenarios
    private async Task PositiveScenariosAsync()
    {
        CurrentGroup = "P";
        pOrg = await SeedOrgAsync(40, localKeys!, 1);
        vOrg = await SeedOrgAsync(12, vaultKeys!, 2, inputs.VaultOrganizations[0]);

        await NewGroupsAsync("P-01", pOrg.Records[0], "a New v2 field batch (3 items, wrap code 1, local provider)", async r => { var b = await BatchAsync(r); return [b.Seal("alpha"), b.Seal("beta"), b.Seal(string.Empty)]; }, 1, 1);
        await NewGroupsAsync("P-03", pOrg.Records[1], "a New v2 document version (wrap code 1, local provider)", async r => [await DocumentAsync(r, 5000)], 2, 1);
        await NewGroupsAsync("P-05", vOrg.Records[0], "a New v2 field batch (2 items, wrap code 2, Vault Transit)", async r => { var b = await BatchAsync(r); return [b.Seal("vault-alpha"), b.Seal("vault-beta")]; }, 1, 2);
        await NewGroupsAsync("P-07", vOrg.Records[1], "a New v2 document version (wrap code 2, Vault Transit)", async r => [await DocumentAsync(r, 3 * 1024 * 1024 + 17)], 2, 2);

        // P-09: several New groups in one commit; the DK-8 tuples are in RFC 9562 byte order of the identifier.
        {
            var record = pOrg.Records[2];
            var a = await BatchAsync(record);
            var b = await BatchAsync(record);
            var items = new List<object> { a.Seal("a1"), a.Seal("a2"), b.Seal("b1"), await DocumentAsync(record, 100), await DocumentAsync(record, 0) };
            var prepared = await PrepareAsync(acc, record, Owner(record, [.. items]));
            var (outcome, message) = prepared.Outcome == "OK" ? await CommitAsync(acc, record, prepared) : (prepared.Outcome, prepared.Message);
            var tuples = outcome == "OK" ? TuplesOf(await CommittedBodyAsync(record, prepared.OperationId), pOrg.Id) : [];
            var ledger = await BindingRowsAsync($"binding_content_change_operation_id = '{prepared.OperationId}'");
            Record("P-09", "Positive", "four New groups in one commit (two field batches, two documents incl. an empty one): one binding each; the DK-8 tuples equal the ledger rows and are in RFC 9562 byte order",
                "OK; 4 bindings; tuples = ledger; byte order", $"{outcome} {message}; ledger {Show(ledger)}; tuples {Show(tuples)}; byte order {InByteOrder([.. tuples.Select(t => t.DataKey)])}",
                outcome == "OK" && ledger.Count == 4 && SameRows(ledger, tuples) && InByteOrder([.. tuples.Select(t => t.DataKey)]) && tuples.Count == 4);
        }

        // P-10..P-13: M2-ENV-16 end to end through Commit, carries across later commits, a partial batch and removal.
        {
            var record = pOrg.Records[3];
            var batch = await BatchAsync(record);
            var first = await WriteAsync(acc, record, Owner(record, batch.Seal("one"), batch.Seal("two")));
            var dk = DataKey(record.Items[0]);
            var carried = await PrepareAsync(acc, record, Owner(record, [.. record.Items]));
            var bindingsOfCarry = await BindingCountAsync($"binding_content_change_operation_id = '{carried.OperationId}'");
            var (commit, message) = carried.Outcome == "OK" ? await CommitAsync(acc, record, carried) : (carried.Outcome, carried.Message);
            var body = commit == "OK" ? TuplesOf(await CommittedBodyAsync(record, carried.OperationId), pOrg.Id) : null;
            Record("P-10", "Positive", "M2-ENV-16 through Commit: a committed two-item v2 batch carried byte-identically by the next owner commit is accepted (P-4 against the accepted binding); no new binding; DK-8 empty",
                "first OK; carry Prepare OK; carry Commit OK; 0 new bindings; DK-8 []", $"first {first}; carry Prepare {carried.Outcome}; Commit {commit} {message}; new bindings {bindingsOfCarry}; DK-8 {(body is null ? "-" : Show(body))}; ledger rows for the id {await BindingCountAsync($"data_key_identifier = '{dk}'")}",
                first == "OK" && carried.Outcome == "OK" && commit == "OK" && bindingsOfCarry == 0 && body is { Count: 0 } && await BindingCountAsync($"data_key_identifier = '{dk}'") == 1);

            var outcomes = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                outcomes.Add(await WriteAsync(acc, record, Owner(record, [.. record.Items])));
            }

            Record("P-11", "Positive", "the same carried batch across three further commits", "OK x3; head revision 5; still one binding",
                $"{string.Join(",", outcomes)}; head revision {(await HeadAsync(record)).Revision}; bindings for the id {await BindingCountAsync($"data_key_identifier = '{dk}'")}",
                outcomes.All(o => o == "OK") && (await HeadAsync(record)).Revision == 5 && await BindingCountAsync($"data_key_identifier = '{dk}'") == 1);

            var partial = await WriteAsync(acc, record, Owner(record, record.Items[0]));
            Record("P-12", "Positive", "a partial batch: one item of the two-item batch carried, the other removed (staged rows 1 <= binding count 2)", "OK; binding count still 2",
                $"{partial}; binding count {await ScalarAsync(adminCs, $"SELECT binding_item_count FROM authority.content_data_key_bindings WHERE data_key_identifier = '{dk}'")}",
                partial == "OK" && await ScalarAsync(adminCs, $"SELECT binding_item_count FROM authority.content_data_key_bindings WHERE data_key_identifier = '{dk}'") == "2");

            var removal = await WriteAsync(acc, record, Owner(record));
            Record("P-13", "Positive", "removal: the last v2 item removed; the binding stays (permanent, never released)", "OK; no v2 item in the head; binding kept",
                $"{removal}; v2 items {record.Items.Count}; binding {await BindingCountAsync($"data_key_identifier = '{dk}'")}", removal == "OK" && record.Items.Count == 0 && await BindingCountAsync($"data_key_identifier = '{dk}'") == 1);
        }

        // P-14: an item re-sealed into a new batch is New (fresh binding) while its sibling stays carried under the old binding.
        {
            var record = pOrg.Records[4];
            var a = await BatchAsync(record);
            var first = await WriteAsync(acc, record, Owner(record, a.Seal("x"), a.Seal("y")));
            var old = (FieldEncryptedValue)record.Items[0];
            var b = await BatchAsync(record);
            var resealed = b.Seal(old.FieldDefinitionId, old.FieldValueItemId, "x-updated");
            var second = await WriteAsync(acc, record, Owner(record, resealed, record.Items[1]));
            Record("P-14", "Positive", "re-sealing one item of an accepted batch: the re-sealed item is a New group (fresh binding, count 1) and its sibling is carried under the old binding", "OK OK; new binding count 1; old binding count 2",
                $"{first} {second}; new {await ScalarAsync(adminCs, $"SELECT coalesce(max(binding_item_count)::text,'none') FROM authority.content_data_key_bindings WHERE data_key_identifier = '{resealed.DataKeyIdentifier}'")}; old {await ScalarAsync(adminCs, $"SELECT binding_item_count FROM authority.content_data_key_bindings WHERE data_key_identifier = '{old.DataKeyIdentifier}'")}",
                first == "OK" && second == "OK" && await ScalarAsync(adminCs, $"SELECT coalesce(max(binding_item_count)::text,'none') FROM authority.content_data_key_bindings WHERE data_key_identifier = '{resealed.DataKeyIdentifier}'") == "1");
        }

        // P-15/P-16: carried v2 groups with acceptance switched off (OD-M2e-07(c)): the Testing override, and an instance configured off.
        {
            var record = pOrg.Records[5];
            var batch = await BatchAsync(record);
            var first = await WriteAsync(acc, record, Owner(record, batch.Seal("keep-1"), batch.Seal("keep-2")));
            acceptance.SetTestingOverride(false);
            var enabledOff = acceptance.Enabled;
            var carry = await WriteAsync(acc, record, Owner(record, [.. record.Items]));
            var fresh = await BatchAsync(record);
            var refused = await PrepareAsync(acc, record, Owner(record, [.. record.Items, fresh.Seal("new-while-off")]));
            acceptance.SetTestingOverride(null);
            Record("P-15", "Positive", "switch turned off by the Testing override: a carried accepted v2 batch still commits (P-4); a New group is refused at Prepare; clearing the override restores the recorded policy",
                "first OK; enabled False; carry OK; New ENVELOPE_VERSION_NOT_ENABLED; enabled True again",
                $"first {first}; enabled {enabledOff}; carry {carry}; New {refused.Message}; enabled {acceptance.Enabled}",
                first == "OK" && !enabledOff && carry == "OK" && refused.Message == "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.VersionNotEnabled && acceptance.Enabled);

            var off = Instance("OFF", ReleaseB3, EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseB3));
            var carryOff = await WriteAsync(off, record, Owner(record, [.. record.Items]));
            var refusedOff = await PrepareAsync(off, record, Owner(record, [.. record.Items, fresh.Seal("new-on-off-instance")]));
            Record("P-16", "Positive", "an Authority instance configured off (Release B floor 3): the carried accepted batch commits; a New group is refused", "carry OK; New ENVELOPE_VERSION_NOT_ENABLED",
                $"carry {carryOff}; New {refusedOff.Message}", carryOff == "OK" && refusedOff.Message == "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.VersionNotEnabled);
        }

        // P-17/P-18: historical v1 compatibility with acceptance on.
        {
            var record = pOrg.Records[6];
            var v1a = await WriteAsync(acc, record, Owner(record, Rows.V1Field(pOrg.Id, record.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), 11), Rows.V1Document(pOrg.Id, record.Id, Guid.CreateVersion7(), Guid.CreateVersion7())));
            var v1b = await WriteAsync(acc, record, Owner(record, [.. record.Items]));
            var body = TuplesOf(await CommittedBodyAsync(record), pOrg.Id);
            Record("P-17", "Positive", "a v1-only record (v1 field and v1 document) is written and carried with acceptance on; its DK-8 array is empty; no binding", "OK OK; DK-8 []; 0 bindings",
                $"{v1a} {v1b}; DK-8 {Show(body)}; bindings {await BindingCountAsync($"form_record_id = '{record.Id}'")}", v1a == "OK" && v1b == "OK" && body.Count == 0 && await BindingCountAsync($"form_record_id = '{record.Id}'") == 0);

            var mixed = pOrg.Records[7];
            var batch = await BatchAsync(mixed);
            var v1Item = Guid.CreateVersion7();
            var v1Field = Guid.CreateVersion7();
            var m1 = await WriteAsync(acc, mixed, Owner(mixed, batch.Seal("v2-item"), Rows.V1Field(pOrg.Id, mixed.Id, v1Field, v1Item, 21)));
            var m2 = await WriteAsync(acc, mixed, Owner(mixed, [.. mixed.Items.Select(s => s is FieldEncryptedValue { EncryptionEnvelopeVersion: 1 } ? (object)Rows.V1Field(pOrg.Id, mixed.Id, v1Field, v1Item, 31) : s)]));
            Record("P-18", "Positive", "a mixed record: the v1 item is updated with a fresh v1 envelope while the v2 batch is carried", "OK OK", $"{m1} {m2}", m1 == "OK" && m2 == "OK");
        }

        // P-19/P-20: idempotent Prepare and Commit replay.
        {
            var record = pOrg.Records[8];
            var batch = await BatchAsync(record);
            var prepared = await PrepareAsync(acc, record, Owner(record, batch.Seal("replayed")));
            var bindings = await BindingCountAsync($"binding_content_change_operation_id = '{prepared.OperationId}'");
            var ops = await OpsAsync();
            string replayOutcome;
            Guid replayOperation = Guid.Empty;
            try
            {
                var replay = await acc.Service.PrepareAsync(Actor(pOrg), prepared.Request, prepared.Key, Guid.CreateVersion7(), CancellationToken.None, BusinessBuild.Current.Declared);
                replayOutcome = "OK";
                replayOperation = replay.OperationId;
            }
            catch (Exception ex)
            {
                replayOutcome = Describe(ex);
            }

            Record("P-19", "Positive", "Prepare replay (same idempotency key and request) returns the same operation and inserts nothing (M2-IDM-01)", "OK; same operation; bindings 1 -> 1; operations unchanged",
                $"{prepared.Outcome}; replay {replayOutcome}; same {replayOperation == prepared.OperationId}; bindings {bindings} -> {await BindingCountAsync($"binding_content_change_operation_id = '{prepared.OperationId}'")}; ops {ops} -> {await OpsAsync()}",
                prepared.Outcome == "OK" && replayOutcome == "OK" && replayOperation == prepared.OperationId && bindings == 1 && await BindingCountAsync($"binding_content_change_operation_id = '{prepared.OperationId}'") == 1 && ops == await OpsAsync());

            var staged = StagedOf(prepared.Operation!, prepared.Rows);
            var (commit, _) = await CommitAsync(acc, record, prepared);
            var journal = await CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{pOrg.Id}'");
            var head = await HeadAsync(record);
            string replayCommit;
            long replayRevision = -1;
            try
            {
                var revision = await acc.Service.CommitAsync(Actor(pOrg), prepared.OperationId, new CommitContentChangeRequest(pOrg.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None, BusinessBuild.Current.Declared);
                replayCommit = "OK";
                replayRevision = revision.CommittedContentRevision;
            }
            catch (Exception ex)
            {
                replayCommit = Describe(ex);
            }

            Record("P-20", "Positive", "Commit replay of a committed operation returns the committed head and writes nothing (M2-IDM-03): no journal entry, no binding, no head change", "OK; replay OK at the same revision; journal and bindings unchanged",
                $"{commit}; replay {replayCommit} revision {replayRevision} (head {head.Revision}); journal {journal} -> {await CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{pOrg.Id}'")}; bindings {await BindingCountAsync($"binding_content_change_operation_id = '{prepared.OperationId}'")}",
                commit == "OK" && replayCommit == "OK" && replayRevision == head.Revision && journal == await CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{pOrg.Id}'")
                && await BindingCountAsync($"binding_content_change_operation_id = '{prepared.OperationId}'") == 1);
        }

        // P-21: archived DK-8 tuples, their digest chain and their equality with the ledger (in-memory archive, D-4).
        foreach (var org in new[] { pOrg, vOrg })
        {
            var verdict = await VerifyArchivedContentStreamAsync(org, PrimaryDatabase);
            var ledger = await BindingRowsAsync($"organization_id = '{org.Id}' AND binding_content_change_operation_id IN (SELECT content_change_operation_id FROM authority.content_change_operations WHERE operation_state_name = 'Committed')");
            Record(org == pOrg ? "P-21" : "P-22", "Positive", $"archive ({(org == pOrg ? "wrap-1 organization" : "wrap-2 organization")}): every content-stream body is archived byte-identically, the digest chain verifies, and the archived DK-8 tuples equal the ledger rows of committed operations",
                "archived all; chain verified; tuples = ledger", $"{verdict.Summary}; ledger {ledger.Count} rows; archived tuples {verdict.Tuples.Count}; equal {SameRows(ledger, verdict.Tuples)}",
                verdict.ChainValid && verdict.AllArchived && ledger.Count > 0 && SameRows(ledger, verdict.Tuples));
        }

        await GrantSentinelAsync("after-positive");
    }

    /// <summary>A New-group write with full Prepare and Commit assertions (DK-4, DK-5, CARV, DK-6/P-4, DK-8).</summary>
    private async Task NewGroupsAsync(string id, Rec record, string what, Func<Rec, Task<List<object>>> make, short kind, int wrapCode)
    {
        var items = await make(record);
        var signatures = mac.Signatures;
        var prepared = await PrepareAsync(acc, record, Owner(record, [.. items]));
        var signed = mac.Signatures - signatures;
        var expectedIds = items.Select(DataKey).Distinct().ToList();
        var ledger = await BindingRowsAsync($"binding_content_change_operation_id = '{prepared.OperationId}'");
        var expectedLedger = expectedIds.Select(dk => (dk, items.Count(i => DataKey(i) == dk), Convert.ToHexStringLower(Rows.CommitmentOf(items.First(i => DataKey(i) == dk))!))).ToList();
        var ledgerMatches = ledger.Count == expectedIds.Count && expectedLedger.All(e => ledger.Any(l => l.DataKey == e.dk && l.Count == e.Item2 && l.CommitmentHex == e.Item3 && l.Kind == kind && l.Organization == record.Org.Id && l.Record == record.Id));
        var wraps = items.Select(i => i switch { FieldEncryptedValue v => v.KeyWrapAlgorithmCode, FileAttachmentVersion a => a.KeyWrapAlgorithmCode, _ => null }).Distinct().ToList();
        Record(id, "Positive", $"{what}: Prepare registers exactly one binding per New group in the Prepare transaction (DK-4 then DK-5) with the organization, record, kind, M1 commitment and item count; P = 3",
            $"OK; {expectedIds.Count} binding(s) equal to the M1 envelope tuple; signed {items.Count + 2}; 3/NULL Prepared; wrap code {wrapCode}",
            $"{prepared.Outcome} {prepared.Message}; ledger {Show(ledger)}; matches {ledgerMatches}; signed {signed}; {(prepared.Outcome == "OK" ? await StateAsync(prepared.OperationId) : "-")}; wrap {string.Join(",", wraps)}",
            prepared.Outcome == "OK" && ledgerMatches && signed == items.Count + 2 && await StateAsync(prepared.OperationId) == "3/NULL Prepared -" && wraps.Count == 1 && wraps[0] == wrapCode);

        if (prepared.Outcome != "OK")
        {
            return;
        }

        var head = await HeadAsync(record);
        var (commit, message) = await CommitAsync(acc, record, prepared);
        var tuples = commit == "OK" ? TuplesOf(await CommittedBodyAsync(record, prepared.OperationId), record.Org.Id) : [];
        var after = await BindingRowsAsync($"binding_content_change_operation_id = '{prepared.OperationId}'");
        var nextId = id[..2] + (int.Parse(id[2..], System.Globalization.CultureInfo.InvariantCulture) + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        Record(nextId, "Positive", $"{what}: Commit verifies the bindings (DK-6, P-4), commits with C = 3, advances the head by one and archives the complete binding tuple (DK-8) equal to the ledger row",
            "OK; 3/3 Committed; head +1; ledger unchanged; DK-8 tuples = ledger",
            $"{commit} {message}; {await StateAsync(prepared.OperationId)}; head {head.Revision} -> {(await HeadAsync(record)).Revision}; ledger unchanged {SameRows(ledger, after)}; DK-8 {Show(tuples)}",
            commit == "OK" && await StateAsync(prepared.OperationId) == "3/3 Committed -" && (await HeadAsync(record)).Revision == head.Revision + 1 && SameRows(ledger, after) && SameRows(after, tuples));
    }

    internal sealed record ArchiveVerdict(bool AllArchived, bool ChainValid, List<BindingRow> Tuples, string Summary, List<(long Revision, byte[] Body)> Bodies);

    /// <summary>
    /// Delivers the organization's journal to the in-memory archive (the production ArchiveProcessor), then verifies the content stream
    /// from the ARCHIVE: each archived body equals the stored one, its digest equals the recorded digest, each entry chains to the
    /// previous one (stored and in the canonical envelope), and the DK-8 tuples of every ContentHeadCommitted body are collected.
    /// </summary>
    private async Task<ArchiveVerdict> VerifyArchivedContentStreamAsync(Org org, string database)
    {
        var store = Store("archiver", ReleaseB3, database);
        var target = archive.For(database);
        var processor = new ArchiveProcessor(store, target, TimeProvider.System);
        for (var pass = 0; pass < 200 && await processor.ProcessOrganizationAsync(org.Id, CancellationToken.None) > 0; pass++)
        {
        }

        var entries = await RowsAsync(AdminFor(database), $"SELECT journal_stream_revision, journal_event_body_json, encode(journal_event_digest,'hex'), coalesce(encode(journal_previous_digest,'hex'),'NULL'), archive_delivery_state, journal_event_kind FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_stream_name = '{JournalStreamNames.Content}' ORDER BY journal_stream_revision");
        var allArchived = true;
        var chain = true;
        string? previous = null;
        var tuples = new List<BindingRow>();
        var bodies = new List<(long, byte[])>();
        var failures = new List<string>();
        foreach (var e in entries)
        {
            var revision = long.Parse(e[0], System.Globalization.CultureInfo.InvariantCulture);
            var stored = Encoding.UTF8.GetBytes(e[1]);
            var archived = await target.ReadAsync(org.Id, JournalStreamNames.Content, revision, CancellationToken.None);
            if (archived is null || !archived.AsSpan().SequenceEqual(stored) || e[4] != ArchiveDeliveryStates.Anchored)
            {
                allArchived = false;
                failures.Add($"r{revision} not archived ({e[4]})");
                continue;
            }

            bodies.Add((revision, archived));
            var digest = Convert.ToHexStringLower(CanonicalJson.Digest(archived));
            var envelope = JsonDocument.Parse(archived).RootElement;
            var envelopePrevious = Prop(envelope, "previous_digest_hex");
            var envelopePreviousHex = envelopePrevious.ValueKind == JsonValueKind.Null ? "NULL" : envelopePrevious.GetString();
            if (digest != e[2] || e[3] != (previous ?? "NULL") || envelopePreviousHex != (previous ?? "NULL"))
            {
                chain = false;
                failures.Add($"r{revision} chain");
            }

            previous = e[2];
            if (e[5] == "ContentHeadCommitted")
            {
                var body = Prop(envelope, "body");
                if (body.TryGetProperty("new_data_key_bindings", out _) || body.TryGetProperty("NewDataKeyBindings", out _))
                {
                    tuples.AddRange(TuplesOf(body, org.Id));
                }
            }
        }

        return new ArchiveVerdict(allArchived && entries.Count > 0, chain, tuples, $"{entries.Count} content entries; archived {(allArchived ? "all" : "NOT all")}; chain {(chain ? "verified" : "BROKEN")}{(failures.Count == 0 ? string.Empty : " (" + string.Join(",", failures.Take(5)) + ")")}", bodies);
    }
}
