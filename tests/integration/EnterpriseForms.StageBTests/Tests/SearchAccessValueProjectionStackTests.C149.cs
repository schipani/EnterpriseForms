using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-149 on the real security-test stack: IX-5b regression coverage (searchable-field withdrawal, Tier 1 rebuild, Decimal/Date/
/// DateTime values and unindexable types, a revocation racing value projection, a maintainer restart in the middle of a catch-up)
/// and the R-55 bound on idle maintenance work. No search path reads Tier 2. Synthetic data only; a focused development test
/// class (C-131), not Stage B.
/// </summary>
public sealed partial class SearchAccessValueProjectionStackTests
{
    private async Task<List<JsonElement>> RawValuesAsync(Guid org, Guid record)
    {
        var result = await fixture.Authority.SendAsync(HttpMethod.Get, Prefix + $"/{org:D}/records/{record:D}/values", TestIdentity.WorkloadToken(), null);
        Assert.True(result.IsSuccess, result.Body);
        using var doc = JsonDocument.Parse(result.Body);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static bool IsReady(JsonElement status) => status.GetProperty("status_name").GetString() == "Ready";

    [Fact]
    public async Task C149ASearchableFieldWithdrawalAndATier1RebuildCloseReadinessUntilConsumed()
    {
        var w = await WorkspaceAsync("withdraw");
        var org = w.S.OrganizationId;
        await WaitCompleteAsync(w);
        var r1 = await CreateAsync(w, 3, "open", "synthetic one");
        var r2 = await CreateAsync(w, 8, "closed", "synthetic two");
        await ShareAsync(w, r1, w.S.Reader.AuthorityUserId, null, "exhibit_count", "case_status");
        await ReadyAndVerifiedAsync(org);
        Assert.Contains(await ValuesAsync(org, r1), v => v.Field == w.Status && v.Id == w.S.Reader.AuthorityUserId);

        // Withdrawal: the coverage leaves Complete (its siblings return to Building until re-attested). Readiness closes at once,
        // without any content event, and the withdrawn field's rows stay stored but unservable until the coverage set is consumed.
        await PauseAsync(true);
        try
        {
            var withdrawn = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Status:D}/search-approval/withdraw", w.S.Token(w.S.Administrator), new { reason = "C-149 withdrawal regression" });
            Assert.True(withdrawn.IsSuccess, withdrawn.Body);
            await WaitCompleteAsync(org, "1/1");
            var building = await StatusAsync(org);
            Assert.Equal("Building", building.GetProperty("status_name").GetString());
            Assert.False(building.GetProperty("coverage_current").GetBoolean());
            Assert.Contains(await ValuesAsync(org, r1), v => v.Field == w.Status);
        }
        finally
        {
            await PauseAsync(false);
        }

        await ReadyAndVerifiedAsync(org);
        foreach (var record in new[] { r1, r2 })
        {
            Assert.DoesNotContain(await ValuesAsync(org, record), v => v.Field == w.Status);
        }

        Assert.Empty(await EvaluateAsync(w, w.S.Clerk, StatusIs(w, w.Open)));
        Assert.Equal([r1], await EvaluateAsync(w, w.S.Clerk, CountBetween(w, 3, 3)));
        Assert.Equal([r1], await EvaluateAsync(w, w.S.Reader, CountBetween(w, 3, 3)));
        var before = (await ValuesAsync(org, r1)).Concat(await ValuesAsync(org, r2)).Select(v => (v.Type, v.Id, v.Field, v.Item, v.Integer)).ToHashSet();

        // A Tier 1 rebuild: every coverage returns to Building and Tier 1 is Rebuilding, so Tier 2 is never ready until the builder
        // re-attests and the coverage set is consumed again; afterwards the rows equal those before the rebuild.
        await PauseAsync(true);
        try
        {
            var rebuild = await fixture.Api.PostAsync("/api/v1/organization/search-index/rebuild", w.S.Token(w.S.Administrator), new { rebuildReasonCode = "C149_TIER1_REBUILD" });
            Assert.True(rebuild.IsSuccess, rebuild.Body);
            var during = await StatusAsync(org);
            Assert.Equal("Building", during.GetProperty("status_name").GetString());
        }
        finally
        {
            await PauseAsync(false);
        }

        await WaitCompleteAsync(org, "1/1");
        await ReadyAndVerifiedAsync(org);
        var after = (await ValuesAsync(org, r1)).Concat(await ValuesAsync(org, r2)).Select(v => (v.Type, v.Id, v.Field, v.Item, v.Integer)).ToHashSet();
        Assert.True(after.SetEquals(before), "value rows after the Tier 1 rebuild differ from those before it");
    }

    [Fact]
    public async Task C149DecimalDateAndDateTimeValuesAreProjectedExactlyAndUnindexableTypesNeverEnterARow()
    {
        var s = new StageBScenario(fixture, "ix5b-types");
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "C-149 synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("C-149 types " + s.Name, s.Clerk);

        async Task<Guid> FieldAsync(string key, string type)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = key, fieldDescription = string.Empty, fieldDataType = type, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.GetProperty("fieldDefinitionId").GetGuid();
        }

        var amount = await FieldAsync("seized_amount", "Decimal");
        var hearing = await FieldAsync("hearing_date", "Date");
        var filed = await FieldAsync("filed_at", "DateTime");
        var reference = await FieldAsync("external_reference", "Guid");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("C-149 types", StageBScenario.Section("main", "Main",
            StageBScenario.Field(amount, "decimal", "Amount", 2), StageBScenario.Field(hearing, "date", "Hearing", 2),
            StageBScenario.Field(filed, "dateTime", "Filed", 2), StageBScenario.Field(reference, "guid", "Reference", 2))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        foreach (var (field, ops) in new[] { (amount, new[] { "equals", "between" }), (hearing, new[] { "equals" }), (filed, new[] { "before", "after" }) })
        {
            var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = ops, approvalReference = "C-149", useDevelopmentStepUp = true });
            Assert.True(approved.IsSuccess, approved.Body);
        }

        // A Guid field has no search operator: its approval is refused, so it can never have a coverage or a value row.
        var guidApproval = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{reference:D}/search-approval", s.Token(s.Administrator), new { operators = new[] { "equals" }, approvalReference = "C-149", useDevelopmentStepUp = true });
        Assert.False(guidApproval.IsSuccess, "a Guid field approval was accepted: " + guidApproval.Body);
        await s.WaitGateReadyAsync();
        var org = s.OrganizationId;
        await WaitCompleteAsync(org, "3/3");
        Assert.Equal("0", fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{org:D}' AND field_definition_id = '{reference:D}';").Trim().Split('\n')[0].Trim());

        async Task<Guid> CreateTypedAsync(string amountText, string date, string instant)
        {
            var values = new Dictionary<string, string> { ["seized_amount"] = amountText, ["hearing_date"] = date, ["filed_at"] = instant, ["external_reference"] = Guid.NewGuid().ToString("D") };
            var id = await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values }, "c149-" + Guid.CreateVersion7().ToString("N")), r => r.Root.GetProperty("formRecordId").GetGuid(), "create");
            await s.WaitProjectionAsync(s.Clerk, id);
            return id;
        }

        var r1 = await CreateTypedAsync("1234.50", "2026-03-12", "2026-03-12T09:30:00Z");
        var r2 = await CreateTypedAsync("-0.0001", "2025-12-31", "2026-01-01T00:00:00+02:00");
        await ReadyAndVerifiedAsync(org); // CLEAN means every stored row equals the derivation from Tier 1's postings, exactly

        async Task AssertTypedAsync(Guid record, decimal expectedAmount, DateOnly expectedDate, DateTimeOffset expectedInstant)
        {
            var rows = await RawValuesAsync(org, record);
            Assert.DoesNotContain(rows, v => v.GetProperty("field_definition_id").GetGuid() == reference);
            Assert.All(rows, v => Assert.Equal(s.Clerk.AuthorityUserId, v.GetProperty("principal_entity_id").GetGuid())); // the owner only (no share)
            var a = Assert.Single(rows, v => v.GetProperty("field_definition_id").GetGuid() == amount);
            Assert.Equal("Decimal", a.GetProperty("value_type_name").GetString());
            Assert.Equal(expectedAmount, a.GetProperty("decimal_value").GetDecimal());
            var d = Assert.Single(rows, v => v.GetProperty("field_definition_id").GetGuid() == hearing);
            Assert.Equal("Date", d.GetProperty("value_type_name").GetString());
            Assert.Equal(expectedDate, DateOnly.ParseExact(d.GetProperty("date_value").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            var t = Assert.Single(rows, v => v.GetProperty("field_definition_id").GetGuid() == filed);
            Assert.Equal("DateTime", t.GetProperty("value_type_name").GetString());
            Assert.Equal(expectedInstant, DateTimeOffset.Parse(t.GetProperty("date_time_value").GetString()!, CultureInfo.InvariantCulture));
            foreach (var row in rows)
            {
                // exactly one typed column carries the value, the one of its type (the database constraint, checked from outside too)
                var populated = new[] { "normalized_string_value", "integer_value", "field_option_id", "boolean_value", "decimal_value", "date_value", "date_time_value" }.Count(c => row.GetProperty(c).ValueKind != JsonValueKind.Null);
                Assert.Equal(1, populated);
            }
        }

        await AssertTypedAsync(r1, 1234.5m, new DateOnly(2026, 3, 12), new DateTimeOffset(2026, 3, 12, 9, 30, 0, TimeSpan.Zero));
        await AssertTypedAsync(r2, -0.0001m, new DateOnly(2025, 12, 31), new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero));

        // A content revision of each type is projected after consumption.
        await RetryAsync(async () =>
        {
            var revision = (await s.WaitProjectionAsync(s.Clerk, r1)).Root.GetProperty("record").GetProperty("contentRevision").GetInt64();
            return await fixture.Api.PatchAsync($"/api/v1/form-records/{r1:D}", s.Token(s.Clerk), new { expectedContentRevision = revision, values = new Dictionary<string, string> { ["seized_amount"] = "99.9999", ["hearing_date"] = "2027-01-02", ["filed_at"] = "2026-06-30T23:59:59-04:00" } });
        }, _ => true, "edit");
        await s.WaitProjectionAsync(s.Clerk, r1);
        await ReadyAndVerifiedAsync(org);
        await AssertTypedAsync(r1, 99.9999m, new DateOnly(2027, 1, 2), new DateTimeOffset(2026, 7, 1, 3, 59, 59, TimeSpan.Zero));
    }

    [Fact]
    public async Task C149ARevocationRacingValueProjectionIsNeverServedFromTheRevokedRows()
    {
        var w = await WorkspaceAsync("revoke-race");
        var org = w.S.OrganizationId;
        await WaitCompleteAsync(w);
        var shares = new List<(Guid Record, Guid Share)>();
        for (var i = 1; i <= 3; i++)
        {
            var record = await CreateAsync(w, i, "open", "synthetic " + i.ToString(CultureInfo.InvariantCulture));
            shares.Add((record, await ShareAsync(w, record, w.S.Reader.AuthorityUserId, null, "exhibit_count")));
        }

        await ReadyAndVerifiedAsync(org);
        var reader = w.S.Reader.AuthorityUserId;
        var revoked = new ConcurrentDictionary<Guid, bool>();
        var violations = new ConcurrentBag<string>();
        var observations = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // Observer: a revocation committed before an observation began must never appear in rows read between two Ready statuses
        // at the same checkpoint revision (nothing moved in between, so those rows are exactly the servable state).
        var observer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var committed = revoked.Keys.ToList();
                var first = await StatusAsync(org);
                var rows = new Dictionary<Guid, List<(string Type, Guid Id, Guid Field, Guid Item, long? Integer, Guid? Option)>>();
                foreach (var record in committed)
                {
                    rows[record] = await ValuesAsync(org, record);
                }

                var second = await StatusAsync(org);
                if (IsReady(first) && IsReady(second) && first.GetProperty("checkpoint_revision").GetInt64() == second.GetProperty("checkpoint_revision").GetInt64())
                {
                    Interlocked.Increment(ref observations);
                    foreach (var (record, values) in rows.Where(r => r.Value.Any(v => v.Id == reader)))
                    {
                        violations.Add($"record {record:D}: revoked reader row present while Ready at checkpoint {first.GetProperty("checkpoint_revision").GetInt64()}");
                    }
                }
            }
        }, CancellationToken.None);

        // Concurrent maintainers alongside the loop.
        var catchers = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = await fixture.Authority.SendAsync(HttpMethod.Post, Prefix + $"/{org:D}/catch-up", TestIdentity.WorkloadToken(), new { maximum_events = 2 });
                await Task.Delay(40, CancellationToken.None);
            }
        }, CancellationToken.None);

        try
        {
            var count = 10;
            foreach (var (record, share) in shares)
            {
                await EditAsync(w, w.S.Clerk, record, new() { ["exhibit_count"] = (count++).ToString(CultureInfo.InvariantCulture) }); // a content event
                await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{share:D}/revoke?useDevelopmentStepUp=true", w.S.Token(w.S.Clerk), new { }), _ => true, "revoke");
                revoked[record] = true;
                await EditAsync(w, w.S.Clerk, record, new() { ["exhibit_count"] = (count++).ToString(CultureInfo.InvariantCulture) });
            }

            await w.S.WaitGateReadyAsync();
            await ReadyAndVerifiedAsync(org);
            await Task.Delay(2000, Ct); // a few more observations of the final state
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(observer, catchers);
        }

        Assert.True(violations.IsEmpty, string.Join("; ", violations));
        Assert.True(observations > 0, "no Ready observation was made");
        foreach (var (record, _) in shares)
        {
            Assert.DoesNotContain(await ValuesAsync(org, record), v => v.Id == reader);
        }

        Assert.Empty(await EvaluateAsync(w, w.S.Reader, CountBetween(w, 0, 1000)));
    }

    [Fact]
    public async Task C149AMaintainerRestartMidCatchUpLosesNoEventAndEndsExactlyConsistent()
    {
        var w = await WorkspaceAsync("restart");
        var org = w.S.OrganizationId;
        await WaitCompleteAsync(w);
        var record = await CreateAsync(w, 1, "open", "synthetic one");
        var other = await CreateAsync(w, 2, "closed", "synthetic two");
        await ReadyAsync(org);
        await PauseAsync(true);
        for (var i = 2; i <= 9; i++)
        {
            await EditAsync(w, w.S.Clerk, record, new() { ["exhibit_count"] = i.ToString(CultureInfo.InvariantCulture) });
        }

        await ShareAsync(w, other, w.S.Reader.AuthorityUserId, null, "exhibit_count");
        await EditAsync(w, w.S.Clerk, other, new() { ["exhibit_count"] = "20" });

        // A partial catch-up, then the process restarts in the middle of the backlog (the pause and the loop's memo are lost).
        var partial = (await AuthorityAsync(HttpMethod.Post, $"/{org:D}/catch-up", new { maximum_events = 3 })).GetProperty("status");
        Assert.Equal("Building", partial.GetProperty("status_name").GetString());
        Assert.True(partial.GetProperty("consumed_authorization_revision").GetInt64() < partial.GetProperty("current_authorization_revision").GetInt64()
                    || partial.GetProperty("consumed_content_integrity_revision").GetInt64() < partial.GetProperty("current_content_integrity_revision").GetInt64(), "the backlog was already consumed: " + partial);
        fixture.Stack.Compose("restart authority", 180);
        await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(90));
        await PauseAsync(false);

        // The restarted loop (empty memo: every organization probed) finishes the backlog by itself: no catch-up call here.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        JsonElement status;
        while (!IsReady(status = await StatusAsync(org)))
        {
            Assert.NotEqual("Quarantined", status.GetProperty("status_name").GetString());
            Assert.True(DateTime.UtcNow < deadline, "the restarted maintainer did not finish the backlog: " + status);
            await Task.Delay(500, Ct);
        }

        await w.S.WaitGateReadyAsync();
        await ReadyAndVerifiedAsync(org);
        Assert.All((await ValuesAsync(org, record)).Where(v => v.Field == w.Count), v => Assert.Equal(9, v.Integer));
        Assert.Equal(20, Assert.Single(await ValuesAsync(org, other), v => v.Field == w.Count && v.Id == w.S.Reader.AuthorityUserId).Integer);
    }

    [Fact]
    public async Task C149AnIdleMaintainerOpensNoTransactionForUnchangedOrganizations()
    {
        var w = await WorkspaceAsync("idle");
        var org = w.S.OrganizationId;
        await WaitCompleteAsync(w);
        await CreateAsync(w, 1, "open", "synthetic");
        await ReadyAsync(org);
        string Scalar(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim().Split('\n')[0].Trim();
        long Scans() => long.Parse(Scalar("SELECT coalesce(seq_scan, 0) + coalesce(idx_scan, 0) FROM pg_stat_user_tables WHERE schemaname = 'authority_search_access' AND relname = 'search_access_checkpoints';"), CultureInfo.InvariantCulture);
        long Transactions() => long.Parse(Scalar("SELECT xact_commit + xact_rollback FROM pg_stat_database WHERE datname = current_database();"), CultureInfo.InvariantCulture);
        var organizations = long.Parse(Scalar("SELECT count(*) FROM organization_authorization_states;"), CultureInfo.InvariantCulture);
        var reconciliation = int.Parse(Environment.GetEnvironmentVariable("IX5_ACCESS_RECONCILIATION_SECONDS") ?? "15", CultureInfo.InvariantCulture);

        await Task.Delay(TimeSpan.FromSeconds(11), Ct); // idle backends flush their statistics within 10 s
        var (scans0, transactions0, started) = (Scans(), Transactions(), DateTime.UtcNow);
        await Task.Delay(TimeSpan.FromSeconds(60), Ct);
        await Task.Delay(TimeSpan.FromSeconds(11), Ct);
        var (scans1, transactions1, ended) = (Scans(), Transactions(), DateTime.UtcNow);
        var minutes = (ended - started).TotalMinutes;
        var scansPerOrganizationPerMinute = (scans1 - scans0) / (double)organizations / minutes;
        var report = new
        {
            organizations,
            reconciliation_interval_seconds = reconciliation,
            window_minutes = Math.Round(minutes, 2),
            checkpoint_scans = scans1 - scans0,
            checkpoint_scans_per_organization_per_minute = Math.Round(scansPerOrganizationPerMinute, 2),
            authority_db_transactions = transactions1 - transactions0,
            authority_db_transactions_per_organization_per_minute = Math.Round((transactions1 - transactions0) / (double)organizations / minutes, 1),
            note = "all AuthorityDb transactions, not only Tier 2; checkpoint scans are Tier 2 only",
        };
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5", "r55"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5", "r55", "idle-checkpoint-scans.json"), JsonSerializer.Serialize(report, Indented), Ct);

        // One probe (one checkpoint read) per organization per reconciliation pass, plus slack: the C-148 loop read every checkpoint
        // three times per 250 ms pass (hundreds per organization per minute).
        var bound = 60.0 / reconciliation + 2;
        Assert.True(scansPerOrganizationPerMinute <= bound, $"idle Tier 2 maintenance read checkpoints {scansPerOrganizationPerMinute:F1} times per organization per minute (bound {bound:F1}): " + JsonSerializer.Serialize(report));
    }
}
