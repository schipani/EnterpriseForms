using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// IX-2 (C-129) on the real security-test stack (business API and Worker, Authorization Authority, AuthorityDb, BusinessDb,
/// SpiceDB, Vault Transit): the <c>search-index-builder</c> workload backfills records committed before an approval, the
/// Authority verifies every submitted byte against the accepted head before deriving and sealing, and only the Authority's
/// attestation sets Complete. Races with commits, tampered business bytes, credential separation, builder and Vault outages,
/// SearchProjectionRebuild after an Authority-domain loss, the S-C deep sweep and the O-2 reader parity are exercised end to end.
/// Dark mode: no indexed query exists. Synthetic data only.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SearchIndexBuilderStackTests(StageBFixture fixture)
{
    private sealed record Ws(StageBScenario S, Guid FormId, Guid Reference, Guid Count);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Sql(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    private string Scalar(string sql) => Sql(sql).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private async Task<Ws> WorkspaceAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "IX-2 synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("IX-2 " + s.Name, s.Clerk);
        async Task<Guid> FieldAsync(string key, string label, string type)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = type, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.GetProperty("fieldDefinitionId").GetGuid();
        }

        var reference = await FieldAsync("case_reference", "Case reference", "String");
        var count = await FieldAsync("exhibit_count", "Exhibit count", "Integer");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("IX-2", StageBScenario.Section("main", "Main", StageBScenario.Field(reference, "text", "Case reference", 2), StageBScenario.Field(count, "number", "Exhibit count", 2))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        return new Ws(s, formId, reference, count);
    }

    private async Task ApproveAsync(Ws w)
    {
        foreach (var (field, ops) in new[] { (w.Reference, new[] { "equals", "startsWith" }), (w.Count, new[] { "equals", "between" }) })
        {
            var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{field:D}/search-approval", w.S.Token(w.S.Administrator), new { operators = ops, approvalReference = "IX-2", useDevelopmentStepUp = true });
            Assert.True(approved.IsSuccess, approved.Body);
        }
    }

    private async Task<Guid> CreateAsync(Ws w, string reference, int count)
    {
        var key = "ix2-" + Guid.CreateVersion7().ToString("N");
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["case_reference"] = reference, ["exhibit_count"] = count.ToString(CultureInfo.InvariantCulture) };
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/records", w.S.Token(w.S.Clerk), new { values }, key);
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var id = created.Root.GetProperty("formRecordId").GetGuid();
                Assert.True((await w.S.WaitProjectionAsync(w.S.Clerk, id)).IsSuccess);
                return id;
            }

            Assert.True(attempt < 60 && created.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING", "create failed: " + created.Body);
            await Task.Delay(250 + Random.Shared.Next(500), Ct);
        }
    }

    private async Task<HttpResult> EditAsync(Ws w, Guid record, string reference)
    {
        for (var attempt = 0; ; attempt++)
        {
            var head = AuthorityOracle.Head(fixture.Stack, record);
            var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = head.ContentRevision, values = new Dictionary<string, string>(StringComparer.Ordinal) { ["case_reference"] = reference } });
            if (edit.IsSuccess || attempt >= 30 || edit.ErrorCode is not ("AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "CONTENT_REVISION_CONFLICT" or "STALE_CONTENT_REVISION"))
            {
                return edit;
            }

            await Task.Delay(300, Ct);
        }
    }

    private string Coverages(Guid org) => Scalar($"SELECT string_agg(coverage_state_name, ',' ORDER BY coverage_state_name) FROM authority_search.search_field_coverages WHERE organization_id = '{org:D}' AND coverage_state_name <> 'Withdrawn';");

    private string Projection(Guid org) => Scalar($"SELECT projection_status_name || '|' || coalesce(projection_status_reason_code, '') FROM authority_search.search_projection_states WHERE organization_id = '{org:D}';");

    private int EntriesAtHead(Guid org) => int.Parse(Scalar($"SELECT count(*) FROM authority_search.search_record_entries e JOIN record_content_heads h ON h.organization_id = e.organization_id AND h.form_record_id = e.form_record_id AND h.committed_operation_id = e.indexed_operation_id AND h.committed_content_revision = e.indexed_content_revision AND h.committed_manifest_root = e.indexed_manifest_root WHERE e.organization_id = '{org:D}';"), CultureInfo.InvariantCulture);

    private static async Task<string> WaitAsync(Func<string> probe, Func<string, bool> done, int seconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        string last;
        do
        {
            last = probe();
            if (done(last))
            {
                return last;
            }

            await Task.Delay(1000, Ct);
        }
        while (DateTime.UtcNow < deadline);
        return last;
    }

    private Task<HttpResult> BuilderAsync(string route, object body, string? token = null) =>
        fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search-index-builder/" + route, token ?? TestIdentity.SearchIndexBuilderToken(), body);

    private void RecreateWorker(params (string Name, string? Value)[] environment)
    {
        foreach (var (name, value) in environment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        fixture.Stack.Compose("up -d --no-deps --force-recreate worker", 240);
    }

    // ---- backfill, attestation, throughput -------------------------------------------------------------------------------

    /// <summary>IX-D6 end to end and backfill throughput: records committed before the approval are backfilled by the worker and attested Complete by the Authority.</summary>
    [Fact]
    public async Task TheWorkerBackfillsExistingRecordsAndTheAuthorityAttestsComplete()
    {
        const int recordCount = 60;
        var w = await WorkspaceAsync("ix2-backfill");
        var records = new List<Guid>();
        for (var i = 0; i < recordCount; i++)
        {
            records.Add(await CreateAsync(w, $"Backfill {i:D3}", i));
        }

        var org = w.S.OrganizationId;
        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_record_entries WHERE organization_id = '{org:D}';"));
        var started = DateTime.UtcNow;
        await ApproveAsync(w);
        var coverages = await WaitAsync(() => Coverages(org), v => v == "Complete,Complete", 300);
        var elapsed = DateTime.UtcNow - started;
        Assert.Equal("Complete,Complete", coverages);
        Assert.Equal(recordCount, EntriesAtHead(org));
        Assert.Equal(recordCount, int.Parse(Scalar($"SELECT count(*) FROM authority_search.search_string_postings WHERE organization_id = '{org:D}';"), CultureInfo.InvariantCulture));
        Assert.Equal("Active|", Projection(org));
        Assert.Equal("2", Scalar($"SELECT count(*) FROM authority_search.search_field_coverages c JOIN security_journal_entries j ON j.security_journal_entry_id = c.coverage_attested_journal_entry_id AND j.journal_event_kind = 'SearchIndexCoverageAttested' WHERE c.organization_id = '{org:D}';"));
        Assert.NotEmpty(Sql($"SELECT 1 FROM security_journal_entries WHERE organization_id = '{org:D}' AND journal_event_kind = 'SearchIndexPopulationIssued';"));

        // The S-A pass after Complete includes the new completeness check and stays clean.
        var passBaseline = Scalar($"SELECT coalesce(last_successful_cheap_pass_on::text, '') FROM authority_search.search_projection_states WHERE organization_id = '{org:D}';");
        Assert.NotEqual(passBaseline, await WaitAsync(() => Scalar($"SELECT coalesce(last_successful_cheap_pass_on::text, '') FROM authority_search.search_projection_states WHERE organization_id = '{org:D}';"), v => v != passBaseline, 60));
        Assert.Equal("Active|", Projection(org));

        var logs = fixture.Stack.Logs("worker", 400).Split('\n').Where(l => l.Contains("IX2-BACKFILL", StringComparison.Ordinal)).ToList();
        var measurement = new
        {
            records = recordCount,
            approval_to_complete_seconds = elapsed.TotalSeconds,
            records_per_second_end_to_end = recordCount / elapsed.TotalSeconds,
            worker_backfill_lines = logs.TakeLast(10).ToList(),
            measured_on = DateTimeOffset.UtcNow,
            note = "Local Docker, Vault Transit (not Managed HSM). Includes the worker's 3 s polling interval and attestation. Not a PERF-01 result.",
        };
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-2"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-2", "backfill-throughput.json"), JsonSerializer.Serialize(measurement, Indented), Ct);
    }

    /// <summary>IX-67 on the real stack: user commits racing the worker's backfill never leave an entry behind its head and never quarantine.</summary>
    [Fact]
    public async Task IX67CommitsRacingTheBackfillKeepEveryEntryAtItsHead()
    {
        var w = await WorkspaceAsync("ix2-race");
        var records = new List<Guid>();
        for (var i = 0; i < 24; i++)
        {
            records.Add(await CreateAsync(w, $"Race {i:D2}", i));
        }

        var org = w.S.OrganizationId;
        await ApproveAsync(w);
        var edits = await Task.WhenAll(records.Where((_, i) => i % 2 == 0).Select(async (r, i) =>
        {
            var results = new List<HttpResult>();
            for (var round = 0; round < 3; round++)
            {
                results.Add(await EditAsync(w, r, $"Raced {i:D2}-{round}"));
            }

            return results;
        }));
        Assert.All(edits.SelectMany(e => e), e => Assert.True(e.IsSuccess, e.Body));

        Assert.Equal("Complete,Complete", await WaitAsync(() => Coverages(org), v => v == "Complete,Complete", 300));
        Assert.Equal(records.Count, EntriesAtHead(org));
        Assert.Equal("0", Scalar($"SELECT count(*) FROM record_content_heads WHERE organization_id = '{org:D}' AND content_head_quarantined;"));
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"RACED {i:D2}-2").Order(StringComparer.Ordinal),
            Sql($"SELECT normalized_string_value FROM authority_search.search_string_postings WHERE organization_id = '{org:D}' AND normalized_string_value LIKE 'RACED %';").Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        Assert.Equal("Active|", Projection(org));
    }

    // ---- IX-25 forged bytes, IX-18 replay, credential separation -------------------------------------------------------

    /// <summary>IX-25: bytes the Authority cannot verify are refused; the record is quarantined; nothing is indexed for it; Complete is withheld.</summary>
    [Fact]
    public async Task IX25TamperedOrFabricatedBytesAreRefusedQuarantineTheRecordAndBlockComplete()
    {
        var w = await WorkspaceAsync("ix2-forgery");
        var tampered = await CreateAsync(w, "Tampered original", 1);
        var fabricated = await CreateAsync(w, "Fabricated original", 2);
        var honest = await CreateAsync(w, "Honest", 3);
        var org = w.S.OrganizationId;

        // A hostile business DBA changes the accepted row the builder will read: the worker submits bytes that do not verify.
        fixture.Stack.AttackerSql($"UPDATE field_string_values SET string_value = 'Tampered by business DBA' WHERE form_record_id = '{tampered:D}';");
        await ApproveAsync(w);

        // A compromised builder submits fabricated bytes for another record directly.
        var head = AuthorityOracle.Head(fixture.Stack, fabricated);
        var forged = await BuilderAsync("backfill", new { organizationId = org, formDefinitionId = w.FormId, records = new[] { new { formRecordId = fabricated, expectedCommittedOperationId = head.OperationId, observedItems = new[] { new { itemKind = "FieldValue", itemId = Guid.CreateVersion7(), canonicalBytesBase64 = Convert.ToBase64String(new byte[64]), rowSignatureBase64 = Convert.ToBase64String(new byte[32]) } } } } });
        Assert.True(forged.IsSuccess, forged.Body);
        var forgedResult = forged.Root.GetProperty("results")[0];
        Assert.True(forgedResult.GetProperty("record_quarantined").GetBoolean(), forged.Body);

        Assert.Equal("t|t", await WaitAsync(() => Scalar($"SELECT bool_or(form_record_id = '{tampered:D}' AND content_head_quarantined)::text::char || '|' || bool_or(form_record_id = '{fabricated:D}' AND content_head_quarantined)::text::char FROM record_content_heads WHERE organization_id = '{org:D}';"), v => v == "t|t", 120));
        // The honest record is indexed; the tampered one never is. The fabricated record may have been indexed from its genuine
        // bytes by the worker before the forged submission quarantined it: its entry then stays at its (quarantined) head, which
        // the IX-1 guard treats as unusable — never from the forged bytes.
        Assert.Equal("1", await WaitAsync(() => Scalar($"SELECT count(*) FROM authority_search.search_record_entries WHERE organization_id = '{org:D}' AND form_record_id = '{honest:D}';"), v => v == "1", 60));
        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_record_entries WHERE organization_id = '{org:D}' AND form_record_id = '{tampered:D}';"));
        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_record_entries e JOIN record_content_heads h ON h.form_record_id = e.form_record_id WHERE e.organization_id = '{org:D}' AND e.form_record_id = '{fabricated:D}' AND e.indexed_operation_id IS DISTINCT FROM h.committed_operation_id;"));
        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_string_postings WHERE organization_id = '{org:D}' AND (normalized_string_value LIKE '%TAMPERED%' OR form_record_id = '{tampered:D}');"));
        Assert.NotEmpty(Sql($"SELECT 1 FROM security_journal_entries WHERE organization_id = '{org:D}' AND journal_event_kind = 'IntegrityVerificationFailed' AND journal_event_body_json LIKE '%{tampered:D}%' AND journal_event_body_json LIKE '%search-index-builder%';")); // found by the worker

        var attestation = await BuilderAsync("attestation", new { organizationId = org, formDefinitionId = w.FormId });
        Assert.True(attestation.IsSuccess, attestation.Body);
        Assert.False(attestation.Root.GetProperty("coverage_complete").GetBoolean());
        Assert.Equal("RECORD_QUARANTINED", attestation.Root.GetProperty("incomplete_reason_code").GetString());
        Assert.Equal("Building,Building", Coverages(org));
        Assert.Equal("Active|", Projection(org)); // record quarantine, never an index or organization quarantine
        Assert.NotEmpty(Sql($"SELECT 1 FROM security_journal_entries WHERE organization_id = '{org:D}' AND journal_event_kind = 'IntegrityVerificationFailed' AND journal_event_body_json LIKE '%ix2-builder-harness%';"));
    }

    /// <summary>IX-18: an older operation of a record is refused at the head check whatever bytes accompany it (no verification, no quarantine).</summary>
    [Fact]
    public async Task IX18AReplayOfAnOlderOperationIsRefusedAtTheHeadCheck()
    {
        var w = await WorkspaceAsync("ix2-replay");
        var record = await CreateAsync(w, "First", 1);
        var first = AuthorityOracle.Head(fixture.Stack, record);
        Assert.True((await EditAsync(w, record, "Second")).IsSuccess);
        var org = w.S.OrganizationId;
        await ApproveAsync(w);
        Assert.Equal("Complete,Complete", await WaitAsync(() => Coverages(org), v => v == "Complete,Complete", 120));

        var replay = await BuilderAsync("backfill", new { organizationId = org, formDefinitionId = w.FormId, records = new[] { new { formRecordId = record, expectedCommittedOperationId = first.OperationId, observedItems = Array.Empty<object>() } } });
        Assert.True(replay.IsSuccess, replay.Body);
        Assert.Equal("HEAD_CHANGED", replay.Root.GetProperty("results")[0].GetProperty("outcome").GetString());
        Assert.False(replay.Root.GetProperty("results")[0].GetProperty("record_quarantined").GetBoolean());
        Assert.Equal("SECOND", Scalar($"SELECT normalized_string_value FROM authority_search.search_string_postings WHERE organization_id = '{org:D}';"));
        Assert.Equal(1, EntriesAtHead(org));
    }

    /// <summary>IX-D6 credential separation: only the builder credential reaches the builder plane, and it reaches nothing else.</summary>
    [Fact]
    public async Task OnlyTheBuilderCredentialReachesTheBuilderPlaneAndItReachesNothingElse()
    {
        var w = await WorkspaceAsync("ix2-credentials");
        var org = w.S.OrganizationId;
        var body = new { organizationId = org, formDefinitionId = w.FormId };
        foreach (var token in new[] { TestIdentity.WorkloadToken(), TestIdentity.VerifierToken(), TestIdentity.OperatorToken(), TestIdentity.ReconcilerToken(), TestIdentity.SearchIndexBuilderToken(role: "field-protection-verifier") })
        {
            foreach (var route in new[] { "work", "population", "backfill", "attestation", "deep-sweep/work", "deep-sweep/verify" })
            {
                var refused = await BuilderAsync(route, body, token);
                Assert.Contains(refused.Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
            }
        }

        var builder = TestIdentity.SearchIndexBuilderToken();
        Assert.Contains((await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/verifier/protection-coverage", builder, new { organizationId = org })).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Contains((await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search-catalogue", builder, new { organizationId = org, formDefinitionId = w.FormId, maximumEntries = 10 }, w.S.Token(w.S.Clerk))).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Contains((await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search-index/rebuild", builder, new { organizationId = org, rebuildReasonCode = "BUILDER_ATTEMPT" }, w.S.Token(w.S.Administrator))).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.True((await BuilderAsync("work", new { })).IsSuccess);
    }

    // ---- IX-26 / IX-27 outages ------------------------------------------------------------------------------------------

    /// <summary>IX-26/IX-27: a builder the Authority refuses withholds everything; the coverage stays Building; a restored builder completes it.</summary>
    [Fact]
    public async Task IX27ABuilderOutageKeepsTheCoverageBuildingAndARestoredBuilderCompletesIt()
    {
        var w = await WorkspaceAsync("ix2-builder-outage");
        var records = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            records.Add(await CreateAsync(w, $"Outage {i}", i));
        }

        var org = w.S.OrganizationId;
        RecreateWorker(("IX2_BUILDER_KEY", Convert.ToBase64String("search-index-builder-key-unknown-to-authority"u8.ToArray())));
        try
        {
            await ApproveAsync(w);
            await Task.Delay(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal("Building,Building", Coverages(org));
            Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_record_entries WHERE organization_id = '{org:D}';"));
            var attestation = await BuilderAsync("attestation", new { organizationId = org, formDefinitionId = w.FormId });
            Assert.Equal((false, "RECORDS_NOT_INDEXED", 6), (attestation.Root.GetProperty("coverage_complete").GetBoolean(), attestation.Root.GetProperty("incomplete_reason_code").GetString(), attestation.Root.GetProperty("missing_record_count").GetInt32()));
            Assert.Contains("refuses the builder credential", await WaitAsync(() => fixture.Stack.Logs("worker", 20000).Contains("Search index builder stopped: the Authority refuses the builder credential", StringComparison.Ordinal) ? "refuses the builder credential" : "not yet", v => v != "not yet", 60), StringComparison.Ordinal); // C-130: fail closed
        }
        finally
        {
            RecreateWorker(("IX2_BUILDER_KEY", null));
        }

        Assert.Equal("Complete,Complete", await WaitAsync(() => Coverages(org), v => v == "Complete,Complete", 180));
        Assert.Equal(records.Count, EntriesAtHead(org));
    }

    /// <summary>IX-27: a Vault (MAC and seal provider) outage during backfill writes nothing and quarantines nothing; the round resumes afterwards.</summary>
    [Fact]
    public async Task IX27AVaultOutageDuringBackfillWritesNothingAndTheRoundResumes()
    {
        var w = await WorkspaceAsync("ix2-vault-outage");
        var records = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            records.Add(await CreateAsync(w, $"Vault {i}", i));
        }

        var org = w.S.OrganizationId;
        fixture.Stack.Pause("vault");
        try
        {
            await ApproveAsync(w);
            await Task.Delay(TimeSpan.FromSeconds(20), Ct);
            Assert.Equal("Building,Building", Coverages(org));
            Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_record_entries WHERE organization_id = '{org:D}';"));
            Assert.Equal("0", Scalar($"SELECT count(*) FROM record_content_heads WHERE organization_id = '{org:D}' AND content_head_quarantined;"));
        }
        finally
        {
            fixture.Stack.Unpause("vault");
        }

        Assert.Equal("Complete,Complete", await WaitAsync(() => Coverages(org), v => v == "Complete,Complete", 180));
        Assert.Equal(records.Count, EntriesAtHead(org));
        Assert.Equal("Active|", Projection(org));
    }

    // ---- SearchProjectionRebuild (IX-19 simulated, IX-44, IX-45, IX-59) -------------------------------------------------

    /// <summary>
    /// IX-19 (simulated restore, partial: no G14 replay exists) and IX-59: an Authority-domain loss of entries under a Complete
    /// coverage quarantines the index (not the organization: IX-44, records stay readable and writable); only a Security
    /// Administrator's SearchProjectionRebuild clears it, the worker rebuilds and the Authority attests again.
    /// </summary>
    [Fact]
    public async Task IX19AnEntryLossQuarantinesTheIndexAndOnlyARebuildRestoresIt()
    {
        var w = await WorkspaceAsync("ix2-rebuild");
        var records = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            records.Add(await CreateAsync(w, $"Rebuild {i}", i));
        }

        var org = w.S.OrganizationId;
        await ApproveAsync(w);
        Assert.Equal("Complete,Complete", await WaitAsync(() => Coverages(org), v => v == "Complete,Complete", 180));

        // A partial restore of authority_search: three records' derived rows are gone while their heads are unchanged.
        var lost = string.Join(",", records.Take(3).Select(r => $"'{r:D}'"));
        fixture.Stack.AuthorityAdministratorSql($"""
            SELECT set_config('authority_search.organization_id', '{org:D}', false);
            DELETE FROM authority_search.search_string_postings WHERE form_record_id IN ({lost});
            DELETE FROM authority_search.search_integer_postings WHERE form_record_id IN ({lost});
            DELETE FROM authority_search.search_record_entries WHERE form_record_id IN ({lost});
            """);
        Assert.Equal("Quarantined|COMPLETE_COVERAGE_MISSING_ENTRY", await WaitAsync(() => Projection(org), v => v.StartsWith("Quarantined", StringComparison.Ordinal), 60));
        Assert.Equal("f", Scalar($"SELECT integrity_quarantine_enabled::text::char FROM organization_authorization_states WHERE organization_id = '{org:D}';"));

        // IX-44: the organization keeps working.
        Assert.True((await w.S.ProjectionAsync(w.S.Clerk, records[0])).IsSuccess);
        Assert.True((await EditAsync(w, records[5], "Edited while quarantined")).IsSuccess);
        await Task.Delay(TimeSpan.FromSeconds(12), Ct); // a few sweep cycles and builder rounds: nothing clears a quarantine
        Assert.StartsWith("Quarantined", Projection(org), StringComparison.Ordinal);

        var notAdministrator = await fixture.Api.PostAsync("/api/v1/organization/search-index/rebuild", w.S.Token(w.S.Clerk), new { rebuildReasonCode = "CLERK_ATTEMPT" });
        Assert.Equal(HttpStatusCode.Forbidden, notAdministrator.Status);
        var rebuild = await fixture.Api.PostAsync("/api/v1/organization/search-index/rebuild", w.S.Token(w.S.Administrator), new { rebuildReasonCode = "PARTIAL_RESTORE_SIMULATION" });
        Assert.True(rebuild.IsSuccess, rebuild.Body);
        Assert.Equal(("Quarantined", "Rebuilding"), (rebuild.Root.GetProperty("previousProjectionStatus").GetString(), rebuild.Root.GetProperty("projectionStatus").GetString()));
        Assert.NotEmpty(Sql($"SELECT 1 FROM security_journal_entries WHERE organization_id = '{org:D}' AND journal_event_kind = 'SearchProjectionRebuildRequested' AND journal_event_body_json LIKE '%PARTIAL_RESTORE_SIMULATION%';"));

        Assert.Equal("Active|", await WaitAsync(() => Projection(org), v => v == "Active|", 180));
        Assert.Equal("Complete,Complete", Coverages(org));
        Assert.Equal(records.Count, EntriesAtHead(org));
        Assert.Equal("EDITED WHILE QUARANTINED", Scalar($"SELECT normalized_string_value FROM authority_search.search_string_postings WHERE form_record_id = '{records[5]:D}';"));
        Assert.NotEmpty(Sql($"SELECT 1 FROM security_journal_entries WHERE organization_id = '{org:D}' AND journal_event_kind = 'SearchIndexRebuilt';"));
    }

    /// <summary>IX-45 on the real stack: with the organization's authorization quarantined, a rebuild is refused and nothing changes.</summary>
    [Fact]
    public async Task IX45ARebuildIsRefusedWhileTheOrganizationIsQuarantined()
    {
        var w = await WorkspaceAsync("ix2-s1-quarantine");
        await CreateAsync(w, "Quarantine", 1);
        var org = w.S.OrganizationId;
        await ApproveAsync(w);
        Assert.Equal("Complete,Complete", await WaitAsync(() => Coverages(org), v => v == "Complete,Complete", 120));

        // The S1 organization quarantine state (set directly: this test is about the rebuild's precedence, not the S1 detection).
        fixture.Stack.AuthorityAdministratorSql($"UPDATE organization_authorization_states SET integrity_quarantine_enabled = true WHERE organization_id = '{org:D}';");
        var rebuild = await fixture.Api.PostAsync("/api/v1/organization/search-index/rebuild", w.S.Token(w.S.Administrator), new { rebuildReasonCode = "DURING_S1_QUARANTINE" });
        Assert.Equal(HttpStatusCode.Locked, rebuild.Status);
        Assert.Equal("Active|", Projection(org));
        Assert.Empty(Sql($"SELECT 1 FROM security_journal_entries WHERE organization_id = '{org:D}' AND journal_event_kind = 'SearchProjectionRebuildRequested';"));
        Assert.Equal(HttpStatusCode.Locked, (await BuilderAsync("attestation", new { organizationId = org, formDefinitionId = w.FormId })).Status);
    }

    // ---- S-C deep sweep -------------------------------------------------------------------------------------------------

    /// <summary>S-C: the worker's deep sweep passes for honest business bytes; tampered business bytes quarantine the record and write no S-C timestamp.</summary>
    [Fact]
    public async Task TheDeepSweepPassesHonestBytesAndQuarantinesARecordWhoseBusinessBytesChanged()
    {
        var w = await WorkspaceAsync("ix2-deep");
        var honest = await CreateAsync(w, "Deep honest", 1);
        var victim = await CreateAsync(w, "Deep victim", 2);
        var org = w.S.OrganizationId;
        await ApproveAsync(w);
        Assert.Equal("Complete,Complete", await WaitAsync(() => Coverages(org), v => v == "Complete,Complete", 120));
        var written = Scalar($"SELECT entry_written_on FROM authority_search.search_record_entries WHERE form_record_id = '{honest:D}';");

        fixture.Stack.AttackerSql($"UPDATE field_integer_values SET integer_value = 999 WHERE form_record_id = '{victim:D}';");
        RecreateWorker(("IX2_BUILDER_DEEP_SWEEP_INTERVAL_SECONDS", "5"));
        try
        {
            Assert.Equal("MATCH", await WaitAsync(() => Scalar($"SELECT coalesce(last_deep_attempt_outcome_code, '') FROM authority_search.search_record_entries WHERE form_record_id = '{honest:D}';"), v => v == "MATCH", 120));
            Assert.True(string.CompareOrdinal(Scalar($"SELECT last_deep_verified_on FROM authority_search.search_record_entries WHERE form_record_id = '{honest:D}';"), written) > 0);
            Assert.Equal("BUSINESS_BYTES_MISMATCH", await WaitAsync(() => Scalar($"SELECT coalesce(last_deep_attempt_outcome_code, '') FROM authority_search.search_record_entries WHERE form_record_id = '{victim:D}';"), v => v == "BUSINESS_BYTES_MISMATCH", 120));
            Assert.Equal("t", Scalar($"SELECT content_head_quarantined::text::char FROM record_content_heads WHERE form_record_id = '{victim:D}';"));
            Assert.Equal(Scalar($"SELECT entry_written_on FROM authority_search.search_record_entries WHERE form_record_id = '{victim:D}';"), Scalar($"SELECT last_deep_verified_on FROM authority_search.search_record_entries WHERE form_record_id = '{victim:D}';"));
            Assert.Equal("Active|", Projection(org)); // a business-byte finding is a record quarantine, not an index Fail
        }
        finally
        {
            RecreateWorker(("IX2_BUILDER_DEEP_SWEEP_INTERVAL_SECONDS", null));
        }
    }

    // ---- O-2 parity ----------------------------------------------------------------------------------------------------

    /// <summary>O-2 equality: the set-based reader returns exactly the per-record reader's items, header facts and values.</summary>
    [Fact]
    public async Task TheSetBasedReaderReturnsExactlyWhatThePerRecordReaderReturns()
    {
        var w = await WorkspaceAsync("ix2-parity");
        for (var i = 0; i < 55; i++)
        {
            await CreateAsync(w, $"Parity {i:D2}", i);
        }

        var parity = await fixture.Api.PostAsync("/api/v1/testing/accepted-content-reader-parity", w.S.Token(w.S.Clerk), new { formDefinitionId = w.FormId });
        Assert.True(parity.IsSuccess, parity.Body);
        Assert.Equal(55, parity.Root.GetProperty("recordsCompared").GetInt32());
        Assert.Equal(0, parity.Root.GetProperty("mismatches").GetInt32());
        Assert.True(parity.Root.GetProperty("itemsCompared").GetInt32() >= 55 * 3, parity.Body);
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-2"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-2", "o2-reader-parity.json"), parity.Body, Ct);
    }
}
