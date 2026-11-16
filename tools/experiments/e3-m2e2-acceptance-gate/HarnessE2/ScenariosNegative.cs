// E3-M2e-2 negative acceptance matrix (readiness review §6, negative). Every refusal is checked for its reason AND for what it must not
// leave behind (no signature where the rule precedes signing, no operation, no binding, no head change), so a refusal that happens for
// the wrong reason or too late is a failure.
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Integrity.Canonical;
using Npgsql;

internal sealed partial class Lab
{
    private const string BindingMismatch = "AuthorityException DATA_KEY_BINDING_MISMATCH 409 retryable=False";
    private const string Reused = "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.DataKeyIdentifierReused;
    private const string NotEnabled = "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.VersionNotEnabled;
    private Org nOrg = null!;
    private Org nOther = null!;

    /// <summary>A Prepare refusal: expected message prefix, nothing signed, no operation, no binding, record unchanged.</summary>
    private async Task RefusedPrepareAsync(string id, string description, Rec record, List<object> sources, string expected, Node? node = null, VerifiedActor? actor = null, bool exact = true)
    {
        var signatures = mac.Signatures;
        var bindings = await BindingCountAsync();
        var ops = await OpsAsync();
        var before = await SnapshotAsync(record);
        var prepared = await PrepareAsync(node ?? acc, record, sources, actor);
        var messageOk = exact ? prepared.Message == expected : prepared.Outcome != "OK" && prepared.Message.Contains(expected, StringComparison.Ordinal);
        Record(id, "Negative", description, $"{expected}; nothing signed; no operation; no binding; record unchanged",
            $"{prepared.Outcome} :: {prepared.Message}; signatures {signatures}->{mac.Signatures}; ops {ops}->{await OpsAsync()}; bindings {bindings}->{await BindingCountAsync()}; {Diff(before, await SnapshotAsync(record))}",
            messageOk && signatures == mac.Signatures && ops == await OpsAsync() && bindings == await BindingCountAsync() && before == await SnapshotAsync(record));
    }

    /// <summary>A forged Commit refused as a ledger mismatch: abort with the reason, one minimal incident, integrity +1, head unchanged.</summary>
    private async Task ForgedMismatchAsync(string id, string description, Rec record, Guid operation, List<StagedContentItem> staged)
    {
        var before = await SnapshotAsync(record);
        var incidents = await IncidentsAsync(record.Org);
        var integrity = await IntegrityRevisionAsync(record.Org);
        string outcome;
        try
        {
            await acc.Service.CommitAsync(Actor(record.Org), operation, new CommitContentChangeRequest(record.Org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None);
            outcome = "OK";
        }
        catch (Exception ex)
        {
            outcome = Describe(ex);
        }

        var body = await ScalarAsync(adminCs, $"SELECT journal_event_body_json FROM authority.security_journal_entries WHERE organization_id = '{record.Org.Id}' AND journal_event_kind = 'IntegrityVerificationFailed' ORDER BY journal_stream_revision DESC LIMIT 1");
        Record(id, "Negative", description, $"{BindingMismatch}; Aborted {DataKeyLedgerFailures.BindingMismatchReason}; incident +1 (reason only); integrity +1; content head NOT advanced",
            $"{outcome}; {await StateAsync(operation)}; incidents {incidents}->{await IncidentsAsync(record.Org)}; integrity {integrity}->{await IntegrityRevisionAsync(record.Org)}; {Diff(before, await SnapshotAsync(record))}",
            outcome == BindingMismatch && (await StateAsync(operation)).EndsWith("Aborted " + DataKeyLedgerFailures.BindingMismatchReason, StringComparison.Ordinal)
            && await IncidentsAsync(record.Org) == incidents + 1 && body.Contains("\"failure_reason_code\":\"DATA_KEY_BINDING_MISMATCH\"", StringComparison.Ordinal)
            && !body.Contains(DataKey0(staged) ?? "no-identifier", StringComparison.OrdinalIgnoreCase)
            && await IntegrityRevisionAsync(record.Org) == integrity + 1 && before == await SnapshotAsync(record));
    }

    /// <summary>The first v2 data-key identifier in staged bytes (to prove the incident body does not carry it).</summary>
    private static string? DataKey0(List<StagedContentItem> staged)
    {
        foreach (var s in staged)
        {
            var document = RowCanonicalDecoder.Decode(Convert.FromBase64String(s.CanonicalBytesBase64));
            if (EnvelopeV2Rules.IsV2(document))
            {
                return EnvelopeV2Rules.DataKeyIdentifierOf(document)?.ToString("D");
            }
        }

        return null;
    }

    private async Task<string> RuntimeInsertAsync(string sql) => await ExecAsync(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e2-ledger-insert" }.ConnectionString, sql);

    private async Task NegativeScenariosAsync()
    {
        CurrentGroup = "N";
        nOrg = await SeedOrgAsync(60, localKeys!, 1);
        nOther = await SeedOrgAsync(6, localKeys!, 1);
        var off = Instance("OFF-N", ReleaseB3, EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseB3));

        // ---------------------------------------------------------------- identifier reuse (DK-4, permanent reservations, OD-M2e-03)
        var committedRecord = nOrg.Records[0];
        var committedBatch = await BatchAsync(committedRecord);
        var historical = committedBatch.Seal("historical");
        var w0 = await WriteAsync(acc, committedRecord, Owner(committedRecord, historical));
        var w1 = await WriteAsync(acc, committedRecord, Owner(committedRecord)); // removed from the head; the binding stays accepted
        Record("N-00", "Negative", "setup: an accepted v2 group, then removed from the head", "OK OK", $"{w0} {w1}", w0 == "OK" && w1 == "OK");
        // Development run 3 correction (recorded in the verification record): a historical row re-introduced under its OWN item id is refused
        // earlier by the permanent item-identity rule (R-53), before DK-4; DK-4 itself is exercised by N-01b (same envelope, new item).
        await RefusedPrepareAsync("N-01", "M2-REP-01: a historical v2 row (accepted in an earlier revision, absent from the current head) re-introduced under its own item id is refused at Prepare before signing (the permanent item-identity rule precedes DK-4)",
            committedRecord, Owner(committedRecord, historical), "ITEM_IDENTITY_REUSED", exact: false);
        await RefusedPrepareAsync("N-01b", "M2-DK-03: the historical v2 envelope (accepted identifier) moved to a NEW item of the same record is New with a bound identifier: refused by DK-4 before signing",
            committedRecord, Owner(committedRecord, Rows.Transplant(historical, nOrg.Id, committedRecord.Id, Guid.CreateVersion7(), Guid.CreateVersion7())), Reused);

        await RefusedPrepareAsync("N-02", "M2-DK-05: the accepted identifier transplanted to another record of the same organization is refused (DK-4)",
            nOrg.Records[1], Owner(nOrg.Records[1], Rows.Transplant(historical, nOrg.Id, nOrg.Records[1].Id, Guid.CreateVersion7(), Guid.CreateVersion7())), Reused);
        await RefusedPrepareAsync("N-03", "M2-DK-06: the accepted identifier transplanted to another organization is refused (DK-4; global uniqueness)",
            nOther.Records[0], Owner(nOther.Records[0], Rows.Transplant(historical, nOther.Id, nOther.Records[0].Id, Guid.CreateVersion7(), Guid.CreateVersion7())), Reused);

        // A reservation of a Prepared (never committed) operation.
        var preparedRecord = nOrg.Records[2];
        var preparedBatch = await BatchAsync(preparedRecord);
        var reserved = preparedBatch.Seal("reserved");
        var reservation = await PrepareAsync(acc, preparedRecord, Owner(preparedRecord, reserved));
        Record("N-04", "Negative", "setup: a Prepared operation reserves its New identifier (not committed)", "OK; 1 binding; Prepared", $"{reservation.Outcome}; bindings {await BindingCountAsync($"data_key_identifier = '{reserved.DataKeyIdentifier}'")}; {await StateAsync(reservation.OperationId)}",
            reservation.Outcome == "OK" && await BindingCountAsync($"data_key_identifier = '{reserved.DataKeyIdentifier}'") == 1);
        await RefusedPrepareAsync("N-05", "M2-DK-04: the identifier reserved by a Prepared operation is refused for another record", nOrg.Records[3],
            Owner(nOrg.Records[3], Rows.Transplant(reserved, nOrg.Id, nOrg.Records[3].Id, Guid.CreateVersion7(), Guid.CreateVersion7())), Reused);

        // ---------------------------------------------------------------- switch off between Prepare and Commit (M2-ENV-18), two variants
        foreach (var variant in new[] { "override", "instance" })
        {
            var record = nOrg.Records[variant == "override" ? 4 : 5];
            var batch = await BatchAsync(record);
            var row = batch.Seal("off-at-commit");
            var prepared = await PrepareAsync(acc, record, Owner(record, row));
            var before = await SnapshotAsync(record);
            var incidents = await IncidentsAsync(nOrg);
            (string Outcome, string Message) commit;
            if (variant == "override")
            {
                acceptance.SetTestingOverride(false);
                commit = await CommitAsync(acc, record, prepared);
                acceptance.SetTestingOverride(null);
            }
            else
            {
                commit = await CommitAsync(off, record, prepared);
            }

            Record(variant == "override" ? "N-06" : "N-07", "Negative", $"M2-ENV-18: the switch turned off between Prepare and Commit ({(variant == "override" ? "Testing override on the same instance" : "Commit on an instance configured off")}) refuses the New group at Commit; no incident; reservation kept; head unchanged",
                $"Prepare OK; {NotEnabled}; Aborted {EnvelopeV2Rules.NotEnabledAtCommitReason}; incidents unchanged; binding kept",
                $"Prepare {prepared.Outcome}; {commit.Message}; {await StateAsync(prepared.OperationId)}; incidents {incidents}->{await IncidentsAsync(nOrg)}; binding {await BindingCountAsync($"data_key_identifier = '{row.DataKeyIdentifier}'")}; {Diff(before, await SnapshotAsync(record))}",
                prepared.Outcome == "OK" && commit.Message == NotEnabled && (await StateAsync(prepared.OperationId)).EndsWith("Aborted " + EnvelopeV2Rules.NotEnabledAtCommitReason, StringComparison.Ordinal)
                && incidents == await IncidentsAsync(nOrg) && await BindingCountAsync($"data_key_identifier = '{row.DataKeyIdentifier}'") == 1 && before == await SnapshotAsync(record));
            if (variant == "instance")
            {
                await RefusedPrepareAsync("N-08", "M2-DK-04: the identifier of the aborted (switch-off) operation stays permanently reserved: a retry with the same batch is refused", record, Owner(record, row), Reused);
            }
        }

        Record("N-09", "Negative", "the recorded policy is effective again after the override is cleared", "enabled True", $"enabled {acceptance.Enabled}", acceptance.Enabled);

        // An expired operation (Commit by an instance whose clock is 11 minutes ahead): aborted; its identifier stays reserved.
        {
            var record = nOrg.Records[6];
            var batch = await BatchAsync(record);
            var row = batch.Seal("expires");
            var prepared = await PrepareAsync(acc, record, Owner(record, row));
            var late = Instance("LATE", ReleaseB3, acceptance, clock: new OffsetClock(TimeSpan.FromMinutes(11)));
            var commit = await CommitAsync(late, record, prepared);
            Record("N-10", "Negative", "an expired Prepared operation is aborted at Commit (OPERATION_EXPIRED); nothing committed", "Prepare OK; OPERATION_EXPIRED 409; Aborted",
                $"Prepare {prepared.Outcome}; {commit.Outcome}; {await StateAsync(prepared.OperationId)}", prepared.Outcome == "OK" && commit.Outcome.Contains("OPERATION_EXPIRED 409", StringComparison.Ordinal) && (await StateAsync(prepared.OperationId)).Contains("Aborted", StringComparison.Ordinal));
            await RefusedPrepareAsync("N-11", "M2-DK-04: the identifier of the expired operation stays permanently reserved (M2-IDM-05: a retry with the same batch is refused)", record, Owner(record, row), Reused);
        }

        // ---------------------------------------------------------------- DK-3 and batch consistency
        {
            var record = nOrg.Records[7];
            var batch = await BatchAsync(record);
            var w = await WriteAsync(acc, record, Owner(record, batch.Seal("one"), batch.Seal("two")));
            Record("N-12", "Negative", "setup: an accepted two-item batch", "OK", w, w == "OK");
            await RefusedPrepareAsync("N-13", "M2-DK-07 (DK-3): a new item sealed into the existing accepted batch (same identifier) while the batch is carried is refused", record,
                Owner(record, [.. record.Items, batch.Seal("joins-the-batch")]), Reused);

            var field = await BatchAsync(record);
            var fieldRow = field.Seal("mixed");
            var document = await DocumentAsync(record, 64);
            document.DataKeyIdentifier = fieldRow.DataKeyIdentifier; // one identifier for a field batch and a document version
            await RefusedPrepareAsync("N-14", "M2-DK-09: one identifier used by a field batch and a document version in one candidate is refused (batch consistency)", record,
                Owner(record, [.. record.Items, fieldRow, document]), "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.BatchInconsistent);

            var changed = Rows.CloneEncrypted((FieldEncryptedValue)record.Items[0]);
            changed.EncryptionNonceBytes = [.. changed.EncryptionNonceBytes.Select(b => (byte)(b ^ 0x5a))];
            await RefusedPrepareAsync("N-15", "M2-DK-08: a carried row whose Content column (nonce) changed is New with a bound identifier and is refused", record,
                Owner(record, changed, record.Items[1]), "DATA_KEY_IDENTIFIER_REUSED", exact: false);
        }

        // ---------------------------------------------------------------- forged operations (controlled seeding) and binding mismatches
        {
            // F1: a New group with no binding.
            var r = nOrg.Records[10];
            var b = await BatchAsync(r);
            var forged = await SeedPreparedAsync(r, Owner(r, b.Seal("forged")), 3);
            await ForgedMismatchAsync("N-20", "DK-6: a forged Prepared operation (stamp 3) staging a New v2 group with no binding is aborted as a ledger mismatch", r, forged.Operation, forged.Staged);
        }

        foreach (var (id, what, kind, count, commitmentFlip, items) in new[]
        {
            ("N-21", "the binding's commitment differs from the staged rows", (short)1, 1, true, 1),
            ("N-22", "the binding's item count is one more than the staged rows", (short)1, 2, false, 1),
            ("N-23", "the binding's item count is one less than the staged rows", (short)1, 1, false, 2),
            ("N-24", "the binding's kind is a document for a field batch", (short)2, 1, false, 1),
        })
        {
            var r = nOrg.Records[11 + int.Parse(id[2..], System.Globalization.CultureInfo.InvariantCulture) - 21];
            var b = await BatchAsync(r);
            var rows = Enumerable.Range(0, items).Select(i => (object)b.Seal("forged-" + i)).ToArray();
            var forged = await SeedPreparedAsync(r, Owner(r, rows), 3);
            var first = (FieldEncryptedValue)rows[0];
            var commitment = commitmentFlip ? [.. first.DataKeyCommitmentBytes!.Select(x => (byte)(x ^ 0xff))] : first.DataKeyCommitmentBytes!;
            var bound = await RuntimeInsertAsync(BindingSql(first.DataKeyIdentifier!.Value, nOrg.Id, r.Id, forged.Operation, kind, count, commitment));
            Log($"{id} binding insert: {bound}");
            await ForgedMismatchAsync(id, $"DK-6: {what}: abort as a ledger mismatch (binding insert {bound})", r, forged.Operation, forged.Staged);
        }

        {
            // P-4: carried group bound to an accepted operation of ANOTHER record.
            var owner = nOrg.Records[20];
            var b = await BatchAsync(owner);
            var accepted = b.Seal("owned-elsewhere");
            var w = await WriteAsync(acc, owner, Owner(owner, accepted));
            var r = nOrg.Records[21];
            var forged = await SeedPreparedAsync(r, Owner(r, Rows.Transplant(accepted, nOrg.Id, r.Id, Guid.CreateVersion7(), Guid.CreateVersion7())), 3);
            await ForgedMismatchAsync("N-25", $"P-4: a forged operation carrying a group whose binding belongs to an accepted operation of another record (setup {w})", r, forged.Operation, forged.Staged);
        }

        {
            // P-4: carried group bound to a Prepared (never committed) operation.
            var r = nOrg.Records[22];
            var b = await BatchAsync(r);
            var pending = b.Seal("pending");
            var prepared = await PrepareAsync(acc, r, Owner(r, pending));
            var forged = await SeedPreparedAsync(r, Owner(r, pending), 3);
            await ForgedMismatchAsync("N-26", $"P-4: a forged operation carrying a group whose binding belongs to a Prepared, uncommitted operation (setup {prepared.Outcome})", r, forged.Operation, forged.Staged);
        }

        {
            // P-4: more staged rows than the binding count.
            var r = nOrg.Records[23];
            var b = await BatchAsync(r);
            var w = await WriteAsync(acc, r, Owner(r, b.Seal("c1"), b.Seal("c2")));
            var forged = await SeedPreparedAsync(r, Owner(r, [.. r.Items, b.Seal("c3-extra")]), 3);
            await ForgedMismatchAsync("N-27", $"P-4: a forged operation staging three rows of an accepted two-item batch (count 3 > 2) (setup {w})", r, forged.Operation, forged.Staged);
        }

        {
            // P-4 / commitment substitution: a carried row whose commitment column was replaced.
            var r = nOrg.Records[24];
            var b = await BatchAsync(r);
            var w = await WriteAsync(acc, r, Owner(r, b.Seal("s1")));
            var substituted = Rows.CloneEncrypted((FieldEncryptedValue)r.Items[0]);
            substituted.DataKeyCommitmentBytes = [.. substituted.DataKeyCommitmentBytes!.Select(x => (byte)(x ^ 0x0f))];
            var forged = await SeedPreparedAsync(r, Owner(r, substituted), 3);
            await ForgedMismatchAsync("N-28", $"commitment substitution in a forged operation: a carried row with another commitment than its accepted binding is refused by P-4 (setup {w})", r, forged.Operation, forged.Staged);
        }

        {
            // Commitment substitution between Prepare and Commit: the staged bytes differ from the prepared candidate.
            var r = nOrg.Records[25];
            var b = await BatchAsync(r);
            var row = b.Seal("swap");
            var prepared = await PrepareAsync(acc, r, Owner(r, row));
            var staged = StagedOf(prepared.Operation!, prepared.Rows);
            var swapped = Rows.CloneEncrypted(row);
            swapped.DataKeyCommitmentBytes = [.. swapped.DataKeyCommitmentBytes!.Select(x => (byte)(x ^ 0x33))];
            var head = await HeadAsync(r);
            var replacement = Rows.Encode(swapped, prepared.OperationId, head.Revision + 1);
            var tampered = staged.Select(s => s.ItemKind == replacement.Kind && s.ItemId == replacement.ItemId ? s with { CanonicalBytesBase64 = replacement.Base64 } : s).ToList();
            var before = await SnapshotAsync(r);
            var commit = await CommitAsync(acc, r, prepared, stagedOverride: tampered);
            Record("N-29", "Negative", "commitment substitution between Prepare and Commit: staged bytes that differ from the approved candidate are refused (candidate mismatch); nothing accepted",
                "CANDIDATE_MISMATCH 409; record unchanged; operation not committed", $"{commit.Outcome}; {await StateAsync(prepared.OperationId)}; {Diff(before, await SnapshotAsync(r))}",
                commit.Outcome.Contains("409", StringComparison.Ordinal) && !(await StateAsync(prepared.OperationId)).Contains("Committed", StringComparison.Ordinal) && before == await SnapshotAsync(r));
        }

        // ---------------------------------------------------------------- binding mutation, deletion and forged inserts (P-1, P-2, L-8)
        {
            var dk = historical.DataKeyIdentifier!.Value;
            var expectedCs = new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e2-expected-42501" }.ConnectionString;
            var rUpdate = await ExecAsync(expectedCs, $"UPDATE authority.content_data_key_bindings SET binding_item_count = 9 WHERE data_key_identifier = '{dk}'");
            var rDelete = await ExecAsync(expectedCs, $"DELETE FROM authority.content_data_key_bindings WHERE data_key_identifier = '{dk}'");
            var rTruncate = await ExecAsync(expectedCs, "TRUNCATE authority.content_data_key_bindings");
            var aUpdate = await ExecAsync(adminCs, $"UPDATE authority.content_data_key_bindings SET binding_item_count = 9 WHERE data_key_identifier = '{dk}'");
            var aDelete = await ExecAsync(adminCs, $"DELETE FROM authority.content_data_key_bindings WHERE data_key_identifier = '{dk}'");
            var aTruncate = await ExecAsync(adminCs, "TRUNCATE authority.content_data_key_bindings");
            Record("N-30", "Negative", "binding alteration and deletion: the runtime role has no UPDATE/DELETE/TRUNCATE privilege (L-8), and even the superuser is refused by the P-2 immutability triggers; the accepted binding is intact",
                "42501 x3 (expected, labelled); 23514 trg_content_data_key_binding_permanent x3; binding intact",
                $"{rUpdate} | {rDelete} | {rTruncate} || {aUpdate} | {aDelete} | {aTruncate}; binding count {await ScalarAsync(adminCs, $"SELECT binding_item_count FROM authority.content_data_key_bindings WHERE data_key_identifier = '{dk}'")}",
                new[] { rUpdate, rDelete, rTruncate }.All(o => o.StartsWith("42501", StringComparison.Ordinal)) && new[] { aUpdate, aDelete, aTruncate }.All(o => o.StartsWith("23514 trg_content_data_key_binding_permanent", StringComparison.Ordinal))
                && await ScalarAsync(adminCs, $"SELECT binding_item_count FROM authority.content_data_key_bindings WHERE data_key_identifier = '{dk}'") == "1");

            var committedOp = Guid.Parse(await ScalarAsync(adminCs, $"SELECT binding_content_change_operation_id FROM authority.content_data_key_bindings WHERE data_key_identifier = '{dk}'"));
            var forgedCommitted = await RuntimeInsertAsync(BindingSql(Guid.CreateVersion7(), nOrg.Id, committedRecord.Id, committedOp, 1, 1, Rows.Fill(32, 0xaa)));
            var forgedOtherOrg = await RuntimeInsertAsync(BindingSql(Guid.CreateVersion7(), nOther.Id, committedRecord.Id, reservation.OperationId, 1, 1, Rows.Fill(32, 0xab)));
            var duplicate = await RuntimeInsertAsync(BindingSql(dk, nOrg.Id, preparedRecord.Id, reservation.OperationId, 1, 1, Rows.Fill(32, 0xac)));
            Record("N-31", "Negative", "forged ledger inserts by the runtime role with acceptance on: a binding for a Committed operation or for another organization's operation is refused by P-1; a second binding of an identifier by the unique index",
                "23514 trg_content_data_key_binding_guard x2; 23505 uq_content_data_key_binding_identifier", $"{forgedCommitted} | {forgedOtherOrg} | {duplicate}",
                forgedCommitted.StartsWith("23514 trg_content_data_key_binding_guard", StringComparison.Ordinal) && forgedOtherOrg.StartsWith("23514 trg_content_data_key_binding_guard", StringComparison.Ordinal)
                && duplicate.StartsWith("23505 uq_content_data_key_binding_identifier", StringComparison.Ordinal));
        }

        // ---------------------------------------------------------------- stale revision
        {
            var r = nOrg.Records[26];
            var b = await BatchAsync(r);
            var (request, _, _) = await BuildAsync(r, Owner(r, b.Seal("stale")));
            var intervening = await WriteAsync(acc, r, Owner(r));
            var signatures = mac.Signatures;
            var bindings = await BindingCountAsync();
            string outcome;
            try
            {
                await acc.Service.PrepareAsync(Actor(nOrg), request, "m2e2-stale-" + request.OperationId.ToString("N"), Guid.CreateVersion7(), CancellationToken.None);
                outcome = "OK";
            }
            catch (Exception ex)
            {
                outcome = Describe(ex);
            }

            Record("N-32", "Negative", "a Prepare built on a stale head revision is refused (409) before signing; no binding is reserved", "intervening OK; 409; nothing signed; no binding",
                $"intervening {intervening}; {outcome}; signatures {signatures}->{mac.Signatures}; bindings {bindings}->{await BindingCountAsync()}",
                intervening == "OK" && outcome.Contains(" 409 ", StringComparison.Ordinal) && signatures == mac.Signatures && bindings == await BindingCountAsync());
        }

        // ---------------------------------------------------------------- missing policy observation after a policy change
        {
            acceptance.SetTestingOverride(true); // a different effective policy (override set): not recorded yet
            var enabledBefore = acceptance.Enabled;
            var r = nOrg.Records[27];
            var b = await BatchAsync(r);
            var refused = await PrepareAsync(acc, r, Owner(r, b.Seal("unrecorded")));
            var recordedOverride = await ObserveAsync(acceptance);
            var enabledRecorded = acceptance.Enabled;
            acceptance.SetTestingOverride(null);
            var enabledCleared = acceptance.Enabled;
            var recordedAgain = await ObserveAsync(acceptance);
            Record("N-33", "Negative", "missing policy observation after a change: the override's new policy is not effective until recorded; clearing the override changes the policy again, which is not effective until it is recorded again (only the most recent record is trusted)",
                $"enabled False; {NotEnabled}; Recorded; enabled True; cleared enabled False; Recorded; enabled True",
                $"enabled {enabledBefore}; {refused.Message}; {recordedOverride}; enabled {enabledRecorded}; cleared enabled {enabledCleared}; {recordedAgain}; enabled {acceptance.Enabled}",
                !enabledBefore && refused.Message == NotEnabled && recordedOverride == EnvelopeAcceptancePolicyObserver.Recorded && enabledRecorded && !enabledCleared && recordedAgain == EnvelopeAcceptancePolicyObserver.Recorded && acceptance.Enabled);
        }

        // ---------------------------------------------------------------- S-4, S-9 and the downgrade guard with acceptance on
        {
            var r = nOrg.Records[28];
            var b = await BatchAsync(r);
            var row = b.Seal("schema");
            await RefusedPrepareAsync("N-34", "S-4: a signed-schema-2 row declaring envelope version 1 is refused at Prepare", r, Owner(r, Rows.RawEncrypted(row, 2, 1, true)), "ENVELOPE_INVALID: ENVELOPE_SCHEMA_MISMATCH");
            await RefusedPrepareAsync("N-35", "S-4: a signed-schema-1 row declaring envelope version 2 is refused at Prepare", r, Owner(r, Rows.RawEncrypted(row, 1, 2, false)), "ENVELOPE_INVALID: ENVELOPE_SCHEMA_MISMATCH");
            await RefusedPrepareAsync("N-36", "S-9: an unregistered signed schema version (3) is refused at Prepare", r, Owner(r, Rows.RawEncrypted(row, 3, 2, true)), "UNSUPPORTED_SIGNED_SCHEMA: entity kind or schema version has no column-class assignment", exact: false);
            await RefusedPrepareAsync("N-37", "S-9: schema 2 with key_wrap_algorithm_code signed as a 64-bit integer (pinned type) is refused at Prepare", r, Owner(r, Rows.RawEncrypted(row, 2, 2, true, RowColumnType.Int64)), "UNSUPPORTED_SIGNED_SCHEMA: column type or nullability differs from the registered assignment", exact: false);

            foreach (var (id, what, source) in new[] { ("N-38", "S-4 (schema 2, envelope 1)", (object)Rows.RawEncrypted(row, 2, 1, true)), ("N-39", "S-9 (schema version 3)", Rows.RawEncrypted(row, 3, 2, true)) })
            {
                var target = nOrg.Records[id == "N-38" ? 29 : 30];
                var forged = await SeedPreparedAsync(target, Owner(target, source), 3);
                var before = await SnapshotAsync(target);
                string outcome;
                try
                {
                    await acc.Service.CommitAsync(Actor(nOrg), forged.Operation, new CommitContentChangeRequest(nOrg.Id, target.Id, forged.Staged), Guid.CreateVersion7(), CancellationToken.None);
                    outcome = "OK";
                }
                catch (Exception ex)
                {
                    outcome = Describe(ex);
                }

                var state = await StateAsync(forged.Operation);
                Record(id, "Negative", $"{what} at Commit: a forged Prepared operation staging the row is aborted (S-9/S-4 re-applied on the verified staged bytes); nothing accepted",
                    $"refused 422; Aborted {(id == "N-38" ? EnvelopeSchemaTie.MismatchAtCommitReason : ContentService.SignedSchemaInvalidAtCommitReason)}; record unchanged", $"{outcome}; {state}; {Diff(before, await SnapshotAsync(target))}",
                    outcome.Contains(" 422 ", StringComparison.Ordinal) && state.EndsWith("Aborted " + (id == "N-38" ? EnvelopeSchemaTie.MismatchAtCommitReason : ContentService.SignedSchemaInvalidAtCommitReason), StringComparison.Ordinal)
                    && before == await SnapshotAsync(target));
            }

            var d = nOrg.Records[31];
            var db = await BatchAsync(d);
            var w = await WriteAsync(acc, d, Owner(d, db.Seal("protected")));
            var v2 = (FieldEncryptedValue)d.Items[0];
            await RefusedPrepareAsync("N-40", $"downgrade with acceptance on: replacing an accepted v2 item with a v1 envelope is refused (setup {w})", d,
                Owner(d, Rows.V1Field(nOrg.Id, d.Id, v2.FieldDefinitionId, v2.FieldValueItemId, 90)), "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.Downgrade);
        }

        // ---------------------------------------------------------------- share-editor path (D-9)
        {
            var r = nOrg.Records[32];
            var b = await BatchAsync(r);
            var v1Field = Guid.CreateVersion7();
            var v1Item = Guid.CreateVersion7();
            var w = await WriteAsync(acc, r, Owner(r, Rows.V1Field(nOrg.Id, r.Id, v1Field, v1Item, 61), b.Seal("owner-only")));
            var v2Row = r.Items.OfType<FieldEncryptedValue>().Single(v => v.EncryptionEnvelopeVersion == 2);
            await SeedShareAsync(r, v1Item, v2Row.FieldValueItemId);
            var share = Instance("SHARE", ReleaseB3, acceptance, contributorWrites: true);
            var editor = EditorActor(nOrg);

            var carryOk = await WriteAsync(share, r, Carry(r, s => s is FieldEncryptedValue { EncryptionEnvelopeVersion: 1 } v ? Rows.V1Field(nOrg.Id, r.Id, v.FieldDefinitionId, v.FieldValueItemId, 71) : s), editor);
            Record("N-41", "Negative", $"share editor (contributor) path, positive control: a v1 shared item updated while the accepted v2 item is carried is committed (P-4 in the share path) (setup {w})", "OK", carryOk, w == "OK" && carryOk == "OK");

            var fresh = await BatchAsync(r);
            // D-9 deviation (development run 3, recorded): for a v1 -> v2 replacement the share path refuses earlier, on the signed-schema
            // framing change (schema 1 -> 2), with SHARE_ITEM_FRAMING_CHANGED (403), before its envelope-version rule; N-43 (v2 -> v2) reaches
            // SHARE_ENVELOPE_NOT_ALLOWED as approved. Both are refusals before signing.
            await RefusedPrepareAsync("N-42", "share editor: replacing the shared v1 item with a New v2 envelope is refused before signing (observed: the framing rule SHARE_ITEM_FRAMING_CHANGED, 403, precedes SHARE_ENVELOPE_NOT_ALLOWED; D-9 deviation)", r,
                Carry(r, s => s is FieldEncryptedValue { EncryptionEnvelopeVersion: 1 } v ? fresh.Seal(v.FieldDefinitionId, v.FieldValueItemId, "share-new-v2") : s), "SHARE_MUTATION_OUT_OF_SCOPE: SHARE_ITEM_FRAMING_CHANGED", share, editor);
            await RefusedPrepareAsync("N-43", "share editor: re-sealing the shared accepted v2 item into a New v2 envelope is refused (SHARE_ENVELOPE_NOT_ALLOWED)", r,
                Carry(r, s => s is FieldEncryptedValue { EncryptionEnvelopeVersion: 2 } v ? fresh.Seal(v.FieldDefinitionId, v.FieldValueItemId, "share-reseal") : s), "SHARE_ENVELOPE_NOT_ALLOWED", share, editor, exact: false);
        }

        await GrantSentinelAsync("after-negative");
    }
}

/// <summary>A clock offset from the system clock (an instance whose clock runs ahead: the expired-operation case).</summary>
internal sealed class OffsetClock(TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow().Add(offset);
}
