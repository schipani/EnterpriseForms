// E3-M2e-2 concurrency (M2-CON-01..04 through the real ContentService on real PostgreSQL), a Release B v2 writer stress, and the
// cross-build checks against the frozen CARV-1 (8915f77) and CARV-2 (a1127a1) builds. No frozen build runs concurrently with a floor raise.
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.Domain.Features.FormRecords;

internal sealed partial class Lab
{
    private async Task ConcurrencyScenariosAsync()
    {
        CurrentGroup = "C";
        var cOrg = await SeedOrgAsync(20, localKeys!, 1);
        var cOther = await SeedOrgAsync(4, localKeys!, 1);

        // M2-CON-01: one organization, two concurrent Prepares introducing the same identifier: serialized by the organization lock,
        // so the loser is refused by DK-4 BEFORE signing (not by the unique index).
        {
            var a = cOrg.Records[0];
            var b = cOrg.Records[1];
            var batch = await BatchAsync(a);
            var row = batch.Seal("same-org");
            var copy = Rows.Transplant(row, cOrg.Id, b.Id, Guid.CreateVersion7(), Guid.CreateVersion7());
            var signatures = mac.Signatures;
            var n1 = Instance("CON1-a", ReleaseB3, acceptance);
            var n2 = Instance("CON1-b", ReleaseB3, acceptance);
            var results = await Task.WhenAll(PrepareAsync(n1, a, Owner(a, row)), PrepareAsync(n2, b, Owner(b, copy)));
            var ok = results.Count(r => r.Outcome == "OK");
            var refused = results.Count(r => r.Message == Reused);
            Record("C-01", "Concurrency", "M2-CON-01: two concurrent Prepares in one organization with the same New identifier: one is prepared, the other refused by DK-4 under the organization lock before signing (only the winner's 3 rows are signed); one binding",
                "1 OK; 1 DATA_KEY_IDENTIFIER_REUSED; signed 3; 1 binding", $"OK {ok}; reused {refused}; signed {mac.Signatures - signatures}; bindings {await BindingCountAsync($"data_key_identifier = '{row.DataKeyIdentifier}'")}; outcomes {string.Join(" / ", results.Select(r => r.Message.Length == 0 ? r.Outcome : r.Message))}",
                ok == 1 && refused == 1 && mac.Signatures - signatures == 3 && await BindingCountAsync($"data_key_identifier = '{row.DataKeyIdentifier}'") == 1);
        }

        // M2-CON-02: two organizations race for one identifier. A has inserted its binding (uncommitted); B passes DK-4 (it cannot see A's
        // row), signs, and its INSERT then waits on A's unique-index entry; A commits; B is refused by the unique index (23505 -> 422),
        // and its whole Prepare transaction rolls back (L-3).
        {
            var a = cOrg.Records[2];
            var b = cOther.Records[0];
            var batch = await BatchAsync(a);
            var row = batch.Seal("cross-org-race");
            var copy = Rows.Transplant(row, cOther.Id, b.Id, Guid.CreateVersion7(), Guid.CreateVersion7());
            var firstInserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondAtInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nodeA = Instance("RACE-A", ReleaseB3, acceptance, interceptor: new BindingInsertGate(true, firstInserted, secondAtInsert));
            var nodeB = Instance("RACE-B", ReleaseB3, acceptance, interceptor: new BindingInsertGate(false, firstInserted, secondAtInsert));
            var opsBefore = await CountAsync($"SELECT count(*) FROM authority.content_change_operations WHERE organization_id = '{cOther.Id}'");
            var signatures = mac.Signatures;
            var taskA = PrepareAsync(nodeA, a, Owner(a, row));
            var reachedFirst = await Task.WhenAny(firstInserted.Task, Task.Delay(TimeSpan.FromSeconds(30))) == firstInserted.Task;
            var taskB = PrepareAsync(nodeB, b, Owner(b, copy));
            var (ra, rb) = (await taskA, await taskB);
            var bindingRows = await BindingRowsAsync($"data_key_identifier = '{row.DataKeyIdentifier}'");
            Record("C-02", "Concurrency", "M2-CON-02: cross-organization race on real PostgreSQL: B passes DK-4 while A's binding is uncommitted, signs, blocks on the unique index and is refused (23505 -> 422 DATA_KEY_IDENTIFIER_REUSED); B's operation, leaves and binding roll back (L-3)",
                "A inserted first; B reached its INSERT; A OK; B DATA_KEY_IDENTIFIER_REUSED; signed 6 (both); one binding (A's); B's organization has no new operation",
                $"A first {reachedFirst}; B at insert {secondAtInsert.Task.IsCompleted}; A {ra.Outcome}; B {rb.Outcome} :: {rb.Message}; signed {mac.Signatures - signatures}; bindings {bindingRows.Count} (org {(bindingRows.Count == 1 && bindingRows[0].Organization == cOrg.Id ? "A" : "?")}); B ops {opsBefore}->{await CountAsync($"SELECT count(*) FROM authority.content_change_operations WHERE organization_id = '{cOther.Id}'")}",
                reachedFirst && secondAtInsert.Task.IsCompleted && ra.Outcome == "OK" && rb.Message == Reused && mac.Signatures - signatures == 6 && bindingRows.Count == 1 && bindingRows[0].Organization == cOrg.Id
                && opsBefore == await CountAsync($"SELECT count(*) FROM authority.content_change_operations WHERE organization_id = '{cOther.Id}'"));
        }

        // M2-CON-03: two owner edits race on one head; the loser's reservation stays; re-staging the same batch is refused; a fresh batch commits.
        {
            var r = cOrg.Records[3];
            var x = await BatchAsync(r);
            var y = await BatchAsync(r);
            var rowX = x.Seal("winner");
            var rowY = y.Seal("loser");
            var pX = await PrepareAsync(acc, r, Owner(r, rowX));
            var pY = await PrepareAsync(acc, r, Owner(r, rowY));
            var cX = await CommitAsync(acc, r, pX);
            var cY = await CommitAsync(acc, r, pY);
            var restageSame = await PrepareAsync(acc, r, Owner(r, [.. r.Items, rowY]));
            var z = await BatchAsync(r);
            var restageFresh = await WriteAsync(acc, r, Owner(r, [.. r.Items, z.Seal("fresh")]));
            Record("C-03", "Concurrency", "M2-CON-03: two edits prepared on the same head: the first commits; the second is refused by the head CAS (aborted, 409); its reservation stays; re-staging its batch is refused; a fresh batch commits",
                $"both prepared; first OK; second 409 + Aborted; loser binding kept; same batch {Reused}; fresh OK",
                $"prepared {pX.Outcome}/{pY.Outcome}; first {cX.Outcome}; second {cY.Outcome}; {await StateAsync(pY.OperationId)}; loser binding {await BindingCountAsync($"data_key_identifier = '{rowY.DataKeyIdentifier}'")}; same {restageSame.Message}; fresh {restageFresh}",
                pX.Outcome == "OK" && pY.Outcome == "OK" && cX.Outcome == "OK" && cY.Outcome.Contains(" 409 ", StringComparison.Ordinal) && (await StateAsync(pY.OperationId)).Contains("Aborted", StringComparison.Ordinal)
                && await BindingCountAsync($"data_key_identifier = '{rowY.DataKeyIdentifier}'") == 1 && restageSame.Message == Reused && restageFresh == "OK");
        }

        // M2-CON-04 / M2-IDM: a crash after the Prepare commit (fault injector). The reservation is durable; a replay of the same Prepare returns
        // the same operation with no new binding and commits; a retry as a NEW operation with the same batch is refused; a fresh batch commits.
        {
            var r = cOrg.Records[4];
            var node = Instance("CRASH", ReleaseB3, acceptance);
            var batch = await BatchAsync(r);
            var row = batch.Seal("crash-then-replay");
            node.Faults.Arm(FaultNames.CrashAfterPrepareCommit);
            var crashed = await PrepareAsync(node, r, Owner(r, row));
            var durable = await StateAsync(crashed.OperationId);
            var bindings = await BindingCountAsync($"data_key_identifier = '{row.DataKeyIdentifier}'");
            string replay;
            PreparedContentOperation? replayed = null;
            try
            {
                replayed = await node.Service.PrepareAsync(Actor(cOrg), crashed.Request, crashed.Key, Guid.CreateVersion7(), CancellationToken.None);
                replay = "OK";
            }
            catch (Exception ex)
            {
                replay = Describe(ex);
            }

            var commit = replayed is null ? ("not replayed", string.Empty) : await CommitAsync(node, r, crashed with { Operation = replayed });
            Record("C-04", "Concurrency", "M2-CON-04 / M2-IDM-01: a crash after the Prepare commit leaves a durable Prepared operation with its reservation; replaying the same Prepare returns it (no new binding) and it commits",
                "SimulatedCrash; 3/NULL Prepared; 1 binding; replay OK same operation; bindings 1; commit OK",
                $"{crashed.Outcome}; {durable}; bindings {bindings}; replay {replay} same {replayed?.OperationId == crashed.OperationId}; bindings {await BindingCountAsync($"data_key_identifier = '{row.DataKeyIdentifier}'")}; commit {commit.Item1}",
                crashed.Outcome.StartsWith("SimulatedCrashException", StringComparison.Ordinal) && durable == "3/NULL Prepared -" && bindings == 1 && replay == "OK" && replayed!.OperationId == crashed.OperationId
                && await BindingCountAsync($"data_key_identifier = '{row.DataKeyIdentifier}'") == 1 && commit.Item1 == "OK");

            var r2 = cOrg.Records[5];
            var b2 = await BatchAsync(r2);
            var row2 = b2.Seal("crash-then-new-operation");
            node.Faults.Arm(FaultNames.CrashAfterPrepareCommit);
            var crashed2 = await PrepareAsync(node, r2, Owner(r2, row2));
            var retrySame = await PrepareAsync(node, r2, Owner(r2, row2));
            var b3 = await BatchAsync(r2);
            var retryFresh = await WriteAsync(node, r2, Owner(r2, b3.Seal("fresh-after-crash")));
            Record("C-05", "Concurrency", "M2-CON-04 / M2-IDM-05: after a crash, a retry as a NEW operation with the same batch is refused (the crashed operation's reservation is permanent); a fresh batch commits",
                $"SimulatedCrash; retry same batch {Reused}; fresh OK", $"{crashed2.Outcome}; same {retrySame.Message}; fresh {retryFresh}",
                crashed2.Outcome.StartsWith("SimulatedCrashException", StringComparison.Ordinal) && retrySame.Message == Reused && retryFresh == "OK");
        }

        // A Release B (floor 3) v2 writer stress: four organizations, one writer instance each, New batches and carries interleaved.
        {
            var orgs = new List<Org>();
            for (var i = 0; i < 4; i++)
            {
                orgs.Add(await SeedOrgAsync(2, localKeys!, 1));
            }

            var outcomes = new System.Collections.Concurrent.ConcurrentBag<string>();
            var newGroups = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();
            var load = orgs.Select((o, i) => Task.Run(async () =>
            {
                var node = Instance("STRESS-" + i, ReleaseB3, acceptance);
                var r = o.Records[0];
                for (var n = 0; n < 8; n++)
                {
                    if (n % 2 == 0)
                    {
                        var batch = await BatchAsync(r);
                        outcomes.Add(await WriteAsync(node, r, Owner(r, batch.Seal("s" + n), batch.Seal("t" + n))));
                        newGroups.AddOrUpdate(o.Id, 1, (_, c) => c + 1);
                    }
                    else
                    {
                        outcomes.Add(await WriteAsync(node, r, Owner(r, [.. r.Items])));
                    }
                }
            })).ToList();
            await Task.WhenAll(load);
            var ledgerByOrg = new List<string>();
            var ledgerOk = true;
            foreach (var o in orgs)
            {
                var count = await BindingCountAsync($"organization_id = '{o.Id}'");
                ledgerByOrg.Add(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                ledgerOk &= count == newGroups.GetValueOrDefault(o.Id);
            }

            var unequal = await CountAsync("SELECT count(*) FROM authority.content_change_operations WHERE operation_state_name = 'Committed' AND prepare_acceptance_rule_version >= 3 AND prepare_acceptance_rule_version IS DISTINCT FROM commit_acceptance_rule_version");
            Record("C-06", "Concurrency", "Release B floor-3 v2 writer stress (4 organizations x 8 writes, New batches and carries interleaved): every write commits; one binding per New group; every committed operation has P = C (deadlocks counted from the server log)",
                "all OK; bindings = New groups per organization; P<>C 0", $"outcomes {string.Join(" / ", outcomes.Distinct())} x{outcomes.Count}; bindings per org {string.Join(",", ledgerByOrg)}; P<>C {unequal}",
                outcomes.Count == 32 && outcomes.All(o => o == "OK") && ledgerOk && unequal == 0);
        }

        await GrantSentinelAsync("after-concurrency");
    }

    // ------------------------------------------------------------------------------------------------ cross-build (group X)
    private async Task CrossBuildScenariosAsync()
    {
        CurrentGroup = "X";
        var xOrg = await SeedOrgAsync(8, localKeys!, 1);
        foreach (var (id, agent, label) in new[] { ("X-01", inputs.Carv2Agent, "CARV-2 build a1127a1"), ("X-02", inputs.Carv1Agent, "CARV-1 build 8915f77") })
        {
            var r = xOrg.Records[id == "X-01" ? 0 : 1];
            var ops = await OpsAsync();
            var before = await SnapshotAsync(r);
            var legacy = await LegacyPrepareAsync(agent, r, Owner(r));
            Record(id, "CrossBuild", $"the {label} cannot prepare on floor 3 (the database refuses its Prepare insert); nothing persisted", "refused (ck_content_operation_rule_floor_v3); no operation; record unchanged",
                $"{legacy.Outcome}; ops {ops}->{await OpsAsync()}; {Diff(before, await SnapshotAsync(r))}",
                legacy.Outcome != "OK" && legacy.Outcome.Contains("ck_content_operation_rule_floor_v3", StringComparison.Ordinal) && ops == await OpsAsync() && before == await SnapshotAsync(r));
        }

        {
            // A New v2 group prepared by this build (P = 3, binding reserved), committed by the CARV-2 build.
            var r = xOrg.Records[2];
            var batch = await BatchAsync(r);
            var row = batch.Seal("cross-build-new");
            var prepared = await PrepareAsync(acc, r, Owner(r, row));
            var before = await SnapshotAsync(r);
            var commit = await LegacyCommitAsync(inputs.Carv2Agent, r, prepared.OperationId, StagedOf(prepared.Operation!, prepared.Rows));
            Record("X-03", "CrossBuild", "a CARV-3 operation carrying a New v2 group cannot be committed by the CARV-2 build; nothing is accepted; the reservation stays",
                "Prepare OK; legacy Commit OPERATION_RULES_OUTDATED 409; Aborted OPERATION_RULES_OUTDATED; record unchanged; binding kept",
                $"Prepare {prepared.Outcome}; legacy {commit}; {await StateAsync(prepared.OperationId)}; {Diff(before, await SnapshotAsync(r))}; binding {await BindingCountAsync($"data_key_identifier = '{row.DataKeyIdentifier}'")}",
                prepared.Outcome == "OK" && commit.Contains("OPERATION_RULES_OUTDATED 409", StringComparison.Ordinal) && (await StateAsync(prepared.OperationId)).EndsWith("Aborted OPERATION_RULES_OUTDATED", StringComparison.Ordinal) && before == await SnapshotAsync(r)
                && await BindingCountAsync($"data_key_identifier = '{row.DataKeyIdentifier}'") == 1);
        }

        {
            // A carried accepted v2 group prepared by this build, committed by the CARV-2 build.
            var r = xOrg.Records[3];
            var batch = await BatchAsync(r);
            var w = await WriteAsync(acc, r, Owner(r, batch.Seal("accepted")));
            var prepared = await PrepareAsync(acc, r, Owner(r, [.. r.Items]));
            var before = await SnapshotAsync(r);
            var commit = await LegacyCommitAsync(inputs.Carv2Agent, r, prepared.OperationId, StagedOf(prepared.Operation!, prepared.Rows));
            Record("X-04", "CrossBuild", "a CARV-3 operation carrying an accepted v2 group cannot be committed by the CARV-2 build (P = 3 is not its rule version)", "setup OK; Prepare OK; legacy Commit OPERATION_RULES_OUTDATED 409; Aborted OPERATION_RULES_OUTDATED; record unchanged",
                $"setup {w}; Prepare {prepared.Outcome}; legacy {commit}; {await StateAsync(prepared.OperationId)}; {Diff(before, await SnapshotAsync(r))}",
                w == "OK" && prepared.Outcome == "OK" && commit.Contains("OPERATION_RULES_OUTDATED 409", StringComparison.Ordinal) && (await StateAsync(prepared.OperationId)).EndsWith("Aborted OPERATION_RULES_OUTDATED", StringComparison.Ordinal) && before == await SnapshotAsync(r));
        }

        {
            // A v1-only CARV-3 operation committed by the CARV-1 build.
            var r = xOrg.Records[4];
            var prepared = await PrepareAsync(acc, r, Owner(r, Rows.V1Field(xOrg.Id, r.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), 44)));
            var before = await SnapshotAsync(r);
            var commit = await LegacyCommitAsync(inputs.Carv1Agent, r, prepared.OperationId, StagedOf(prepared.Operation!, prepared.Rows));
            Record("X-05", "CrossBuild", "a v1-only CARV-3 operation cannot be committed by the CARV-1 build", "Prepare OK; legacy Commit OPERATION_RULES_OUTDATED 409; Aborted OPERATION_RULES_OUTDATED; record unchanged",
                $"Prepare {prepared.Outcome}; legacy {commit}; {await StateAsync(prepared.OperationId)}; {Diff(before, await SnapshotAsync(r))}",
                prepared.Outcome == "OK" && commit.Contains("OPERATION_RULES_OUTDATED 409", StringComparison.Ordinal) && (await StateAsync(prepared.OperationId)).EndsWith("Aborted OPERATION_RULES_OUTDATED", StringComparison.Ordinal) && before == await SnapshotAsync(r));
        }

        await GrantSentinelAsync("after-cross-build");
    }
}
