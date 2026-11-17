// E3-M2f-2G group GH (docs/security/E3_M2F2G_VERIFICATION_RECORD.md; closure §3, §7, §9; M2F-35, M2F-36, M2F-38): this tree's REAL
// Authority HOST process (dotnet AuthorizationAuthority.Api.dll from the copy without appsettings.Development.json; every setting explicit)
// on its own disposable database, with the run's disposable Vault as the HMAC signer (Transit mount "transit", a token holding only the
// repository's authority-transit-policy), driven over HTTP by REAL business builds through their own Authority, verifier and builder
// clients (BusinessAgent: 11e15ee, 0be5f52, this tree). This is the only place where the header grammar, the Api's header parsing,
// workload authentication and the business clients' own wire behaviour are exercised. SpiceDB is not reachable from the run: user Verify
// requests that PASS the capability check stop later as not ready, which is recorded as "admitted" (they are not the capability refusal).
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Persistence;
using AuthorizationAuthority.Infrastructure.Signing;
using EnterpriseForms.Integrity.Merkle;
using Microsoft.EntityFrameworkCore;
using Npgsql;

internal sealed partial class Lab
{
    private const string GHostDatabase = "m2f2ghost";
    private const string HostUserIssuer = "https://test-idp.m2e2.invalid";

    internal sealed record Credential(string Issuer, string Audience, string Key);

    private static string B64(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text));

    private static readonly Credential BusinessCredential = new("enterpriseforms-business-api", "authorization-authority", B64("m2e2-workload-test-only-shared-key-000000"));
    private static readonly Credential VerifierCredential = new("enterpriseforms-field-protection-verifier", "authorization-authority-verifier", B64("m2f2g-verifier-test-only-shared-key-0000000"));
    private static readonly Credential BuilderCredential = new("enterpriseforms-search-index-builder", "authorization-authority-search-index-builder", B64("m2f2g-builder-test-only-shared-key-00000000"));
    private static readonly string HostUserKey = B64("m2e2-user-assertion-test-only-key-000000");

    private sealed class HostHandle(Process process, StringBuilder output, string baseAddress, string label)
    {
        public Process Process { get; } = process;

        public StringBuilder Output { get; } = output;

        public string BaseAddress { get; } = baseAddress;

        public string Label { get; } = label;

        public string Text
        {
            get
            {
                lock (Output)
                {
                    return Output.ToString();
                }
            }
        }
    }

    private Dictionary<string, string> GHostSettings(string? deploymentFloor)
    {
        var settings = HostSettings(GHostDatabase, runtimeCs);
        settings["Signing__Provider"] = "VaultTransit";
        settings["Signing__VaultAddress"] = Target.RequireVault(inputs.VaultAddress);
        settings["Signing__VaultTransit__Token"] = inputs.VaultAuthorityToken ?? throw new InvalidOperationException("no Vault authority token");
        settings["Identity__Verifier__Issuer"] = VerifierCredential.Issuer;
        settings["Identity__Verifier__Audience"] = VerifierCredential.Audience;
        settings["Identity__Verifier__SharedKeyBase64"] = VerifierCredential.Key;
        settings["Identity__SearchIndexBuilder__Issuer"] = BuilderCredential.Issuer;
        settings["Identity__SearchIndexBuilder__Audience"] = BuilderCredential.Audience;
        settings["Identity__SearchIndexBuilder__SharedKeyBase64"] = BuilderCredential.Key;
        settings["SearchIndex__Mode"] = "Dark";
        settings["SearchIndex__Seal__Provider"] = "LocalDevelopment";
        settings["SearchIndex__Seal__LocalPrivateKeyPkcs8Base64"] = SealOptions.LocalPrivateKeyPkcs8Base64!;
        settings["SearchIndex__Seal__PinnedKeys__0__KeyName"] = "search-entry-seal-v1";
        settings["SearchIndex__Seal__PinnedKeys__0__KeyVersion"] = "1";
        settings["SearchIndex__Seal__PinnedKeys__0__SubjectPublicKeyInfoBase64"] = SealOptions.PinnedKeys[0].SubjectPublicKeyInfoBase64;
        if (deploymentFloor is not null)
        {
            settings["Content__MinimumBusinessEnvelopeCapability"] = deploymentFloor;
        }

        return settings;
    }

    /// <summary>Starts the host and returns once it answers the liveness probe (or after it exited / the timeout).</summary>
    private async Task<(HostHandle? Host, string Detail)> StartHostAsync(string label, Dictionary<string, string> settings, TimeSpan timeout)
    {
        if (File.Exists(Path.Combine(inputs.HostBinaries, "appsettings.Development.json")))
        {
            throw new IsolationViolation("the host copy still contains appsettings.Development.json");
        }

        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = inputs.HostBinaries };
        start.ArgumentList.Add(Path.Combine(inputs.HostBinaries, "AuthorizationAuthority.Api.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        foreach (var (key, value) in settings)
        {
            start.Environment[key] = value;
        }

        var output = new StringBuilder();
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var baseAddress = settings["ASPNETCORE_URLS"].TrimEnd('/') + "/";
        using var probe = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            try
            {
                using var response = await probe.GetAsync("internal/v1/health/live");
                if (response.IsSuccessStatusCode)
                {
                    Log($"host {label} live at {baseAddress}");
                    return (new HostHandle(process, output, baseAddress, label), "live");
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(500);
        }

        var exited = process.HasExited;
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        await File.WriteAllTextAsync(Path.Combine(inputs.EvidenceDir, $"host-{label}-{DateTime.UtcNow:HHmmssfff}.log"), text);
        Log($"host {label} did not become live: exited {exited} code {process.ExitCode}");
        var named = text.Contains(EnvelopeCapabilityFloor.ConfigurationKey, StringComparison.Ordinal) ? "; output names " + EnvelopeCapabilityFloor.ConfigurationKey : string.Empty;
        return (null, $"exited {exited} code {process.ExitCode}{named}; {(text.Length > 300 ? text[^300..] : text).ReplaceLineEndings(" ")}");
    }

    private async Task StopHostAsync(HostHandle host)
    {
        if (!host.Process.HasExited)
        {
            host.Process.Kill(entireProcessTree: true);
        }

        await host.Process.WaitForExitAsync();
        await File.WriteAllTextAsync(Path.Combine(inputs.EvidenceDir, $"host-{host.Label}-{DateTime.UtcNow:HHmmssfff}.log"), host.Text);
        host.Process.Dispose();
    }

    /// <summary>A disposable organization of the host database: its row-integrity key is a real Vault Transit HMAC key of the run.</summary>
    private async Task<Org> SeedHostOrgAsync(int records)
    {
        var org = await SeedOrgAsync(records, localKeys!, 1, database: GHostDatabase, journalDigest: true);
        var keyName = IntegrityKeyNaming.ProviderKeyName(org.Id, IntegrityKeyPurposes.RowIntegrityV1);
        var vault = new VaultTransitHmacProvider(new VaultFactory(Target.RequireVault(inputs.VaultAddress)), Microsoft.Extensions.Options.Options.Create(new VaultTransitOptions { Token = inputs.VaultAuthorityToken }));
        var version = await vault.ProvisionKeyAsync(org.Id, IntegrityKeyPurposes.RowIntegrityV1, keyName, CancellationToken.None);
        var seedCs = Target.Require(new NpgsqlConnectionStringBuilder(adminCs) { Options = "-c session_replication_role=replica", ApplicationName = "m2e2-seed-host", Database = GHostDatabase }.ConnectionString);
        await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(seedCs).Options);
        var key = await context.KeyRegistrations.SingleAsync(k => k.OrganizationId == org.Id);
        key.ProviderName = "VaultTransit";
        key.ProviderKeyName = keyName;
        key.KeyPurposeName = IntegrityKeyPurposes.RowIntegrityV1;
        key.CurrentSigningKeyVersion = version;
        foreach (var binding in await context.IdentityBindings.Where(b => b.OrganizationId == org.Id).ToListAsync())
        {
            binding.IdentityIssuerUri = HostUserIssuer;
        }

        await context.SaveChangesAsync();
        return org;
    }

    private sealed class VaultFactory(string address) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
    }

    private async Task<long> AnchorAsync(Org org, string database)
    {
        var processor = new ArchiveProcessor(Store("archiver", ReleaseB3, database), archive.For(database), TimeProvider.System);
        for (var pass = 0; pass < 200 && await processor.ProcessOrganizationAsync(org.Id, CancellationToken.None) > 0; pass++)
        {
        }

        return await CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_stream_name = '{JournalStreamNames.Content}' AND archive_delivery_state <> '{ArchiveDeliveryStates.Anchored}'", database);
    }

    // ------------------------------------------------------------------------------------------------ sending through a business build
    private readonly Dictionary<string, List<string>> sentHeaders = new(StringComparer.Ordinal);

    /// <summary>One request of business build <paramref name="build"/> to the host; returns the agent output and records the headers it sent.</summary>
    private async Task<JsonElement> HostSendAsync(HostHandle host, string build, string kind, Org org, object? request = null, Guid? operationId = null, string? key = null,
        Guid? snapshotId = null, object? records = null, string[]? headerOverride = null)
    {
        var credential = kind switch { "census" => VerifierCredential, "backfill" or "deep-verify" => BuilderCredential, _ => BusinessCredential };
        var output = await RunAgentAsync(inputs.BusinessAgents[build], "send", new
        {
            Kind = kind, BaseAddress = host.BaseAddress, WorkloadIssuer = credential.Issuer, WorkloadAudience = credential.Audience, WorkloadKey = credential.Key,
            UserIssuer = HostUserIssuer, UserSubject = org.Owner.ToString("D"), UserAudience = "enterpriseforms", UserKey = HostUserKey, OrganizationId = org.Id,
            FormDefinitionId = Rows.Form, Request = request, OperationId = operationId, IdempotencyKey = key ?? "m2f2g-host-" + Guid.CreateVersion7().ToString("N"), SnapshotId = snapshotId,
            Records = records, HeaderOverride = headerOverride,
        }, environment: null);
        if (headerOverride is null && output.TryGetProperty("Sent", out var sent))
        {
            lock (sentHeaders)
            {
                if (!sentHeaders.TryGetValue(build, out var list))
                {
                    sentHeaders[build] = list = [];
                }

                list.AddRange(sent.EnumerateArray().Select(s => "[" + string.Join(",", s.GetProperty("CapabilityHeaderValues").EnumerateArray().Select(v => v.GetString())) + "]"));
            }
        }

        return output;
    }

    private static string HostOutcome(JsonElement output)
    {
        if (!output.GetProperty("Ok").GetBoolean())
        {
            return output.TryGetProperty("ErrorCode", out var code) && code.ValueKind == JsonValueKind.String
                ? $"{code.GetString()} {output.GetProperty("HttpStatusCode")} retryable={output.GetProperty("Retryable")}"
                : output.GetProperty("Error").GetString() ?? "error";
        }

        var r = output.GetProperty("Result");
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("Results", out var results))
        {
            var first = results.EnumerateArray().First();
            return first.TryGetProperty("UnitOutcome", out var unit) ? "OK " + unit.GetString() + " " + first.GetProperty("OutcomeCode").GetString()
                : first.TryGetProperty("Outcome", out var named) ? "OK " + named.GetString()
                : "OK verified " + first.GetProperty("Verified");
        }

        return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("Verified", out var v) ? "OK verified " + v : "OK";
    }

    private const string HostRefused = "CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT 409 retryable=False";

    private async Task<(string Outcome, Guid Operation, List<StagedContentItem>? Staged, List<Staged> Rows)> HostPrepareAsync(HostHandle host, string build, Rec record, List<object> sources,
        List<StagedContentItem>? currentOverride = null, List<StagedContentItem>? candidatesOverride = null)
    {
        var (request, rows, operation) = await BuildAsync(record, sources, GHostDatabase);
        if (build != "current")
        {
            // A historical build stages and observes with its OWN registry.
            var encoded = await EncodeWithAsync(inputs.BusinessAgents[build], [.. rows.Select(r => (r, operation, request.ExpectedContentRevision + 1))]);
            candidatesOverride ??= [.. rows.Select((r, i) => new StagedContentItem(r.Kind, r.ItemId, encoded[i], string.Empty))];
            currentOverride ??= record.Accepted.Count == 0 ? null : await ObservedAsync(record, inputs.BusinessAgents[build], GHostDatabase);
        }

        if (currentOverride is not null || candidatesOverride is not null)
        {
            request = request with
            {
                CurrentItems = currentOverride ?? request.CurrentItems,
                CandidateItems = candidatesOverride is null ? request.CandidateItems : [.. candidatesOverride.Select(s => new CandidateContentItem(s.ItemKind, s.ItemId, s.CanonicalBytesBase64))],
            };
        }

        var output = await HostSendAsync(host, build, "prepare", record.Org, request, key: "m2f2g-host-" + operation.ToString("N"));
        if (!output.GetProperty("Ok").GetBoolean())
        {
            return (HostOutcome(output), operation, null, rows);
        }

        var signatures = output.GetProperty("Result").GetProperty("SignedItems").EnumerateArray().ToDictionary(s => ((ManifestItemKind)s.GetProperty("ItemKind").GetInt32(), s.GetProperty("ItemId").GetGuid()), s => s.GetProperty("RowSignatureBase64").GetString()!);
        return ("OK", operation, [.. request.CandidateItems.Select(ci => new StagedContentItem(ci.ItemKind, ci.ItemId, ci.CanonicalBytesBase64, signatures[(ci.ItemKind, ci.ItemId)]))], rows);
    }

    private async Task<string> HostCommitAsync(HostHandle host, string build, Rec record, Guid operation, List<StagedContentItem> staged, List<Staged>? rows = null, List<object>? sources = null)
    {
        var output = await HostSendAsync(host, build, "commit", record.Org, new CommitContentChangeRequest(record.Org.Id, record.Id, staged), operationId: operation);
        var outcome = HostOutcome(output);
        if (outcome == "OK" && rows is not null && sources is not null)
        {
            record.Accepted = [.. rows.Select(c => (c, Convert.FromBase64String(staged.Single(s => s.ItemKind == c.Kind && s.ItemId == c.ItemId).RowSignatureBase64)))];
            record.Sources = sources;
        }

        return outcome;
    }

    private async Task<string> HostWriteAsync(HostHandle host, string build, Rec record, List<object> sources)
    {
        var p = await HostPrepareAsync(host, build, record, sources);
        return p.Outcome != "OK" ? "prepare: " + p.Outcome : await HostCommitAsync(host, build, record, p.Operation, p.Staged!, p.Rows, sources);
    }

    /// <summary>One of the seven content requests of <paramref name="build"/>, with that build's own encoding of the accepted rows.</summary>
    private async Task<string> HostEntryAsync(HostHandle host, string build, Entry entry, Rec record, Guid? snapshot = null, Guid? pendingOperation = null, List<Staged>? pendingRows = null,
        List<StagedContentItem>? pendingStaged = null, string[]? headerOverride = null)
    {
        var head = await HeadIdentityAsync(record, GHostDatabase);
        var observed = build == "current" ? record.AcceptedStaged() : await ObservedAsync(record, inputs.BusinessAgents[build], GHostDatabase);
        JsonElement output;
        switch (entry)
        {
            case Entry.Prepare:
            {
                var operation = Guid.CreateVersion7();
                var sources = Owner(record, [.. record.Items]);
                var staged = sources.Select(s => Rows.Encode(s, operation, head.Revision + 1)).ToList();
                var bytes = build == "current" ? staged.Select(s => s.Base64).ToList() : await EncodeWithAsync(inputs.BusinessAgents[build], [.. staged.Select(s => (s, operation, head.Revision + 1))]);
                var root = await HeadAsync(record, GHostDatabase);
                var request = new PrepareContentChangeRequest(record.Org.Id, record.Id, operation, 7, head.Revision, Convert.ToBase64String(root.Root), [.. staged.Select((s, i) => new CandidateContentItem(s.Kind, s.ItemId, bytes[i]))], CurrentItems: observed);
                output = await HostSendAsync(host, build, "prepare", record.Org, request, headerOverride: headerOverride);
                break;
            }

            case Entry.Commit:
            {
                var staged = pendingStaged!;
                if (build != "current")
                {
                    var encoded = await EncodeWithAsync(inputs.BusinessAgents[build], [.. pendingRows!.Select(r => (r, pendingOperation!.Value, head.Revision + 1))]);
                    staged = [.. pendingStaged!.Select((s, i) => s with { CanonicalBytesBase64 = encoded[i] })];
                }

                output = await HostSendAsync(host, build, "commit", record.Org, new CommitContentChangeRequest(record.Org.Id, record.Id, staged), operationId: pendingOperation, headerOverride: headerOverride);
                break;
            }

            case Entry.Verify:
                output = await HostSendAsync(host, build, "verify", record.Org, new VerifyRecordProjectionRequest(record.Org.Id, record.Id, RequestedAction.Read, observed), headerOverride: headerOverride);
                break;
            case Entry.BatchVerify:
                output = await HostSendAsync(host, build, "batch-verify", record.Org, new VerifyRecordProjectionsRequest(record.Org.Id, [new BatchVerificationItem(record.Id, head.Operation, observed)]), headerOverride: headerOverride);
                break;
            case Entry.Census:
                output = await HostSendAsync(host, build, "census", record.Org, snapshotId: snapshot, records: new[] { new ProtectionCensusItem(record.Id, head.Operation, observed) }, headerOverride: headerOverride);
                break;
            case Entry.Backfill:
                output = await HostSendAsync(host, build, "backfill", record.Org, records: new[] { new SearchIndexBackfillItem(record.Id, head.Operation, observed) }, headerOverride: headerOverride);
                break;
            default:
                output = await HostSendAsync(host, build, "deep-verify", record.Org, records: new[] { new SearchIndexBackfillItem(record.Id, head.Operation, observed) }, headerOverride: headerOverride);
                break;
        }

        return HostOutcome(output);
    }

    // ------------------------------------------------------------------------------------------------ the group
    private async Task CapabilityHostScenariosAsync()
    {
        CurrentGroup = "GH";
        if (inputs.VaultAuthorityToken is null || !inputs.BusinessAgents.ContainsKey("11e15ee") || !inputs.BusinessAgents.ContainsKey("0be5f52") || !inputs.BusinessAgents.ContainsKey("current"))
        {
            Record("GH-00", "Setup", "host group prerequisites (Vault authority token, three business agents)", "present", "missing", false);
            return;
        }

        await CreateEmptyDatabaseAsync(GHostDatabase);
        var migrated = await HostCommandAsync(AdminFor(GHostDatabase), "m2f2g-host-migrate", "--migrate");
        var floors = new List<int>();
        foreach (var floor in new[] { "1", "2", "3" })
        {
            floors.Add((await HostCommandAsync(AdminFor(GHostDatabase), "m2f2g-host-floor" + floor, "--apply-content-acceptance-floor", floor)).Code);
        }

        var hb = await SeedHostOrgAsync(8);
        var hu = await SeedHostOrgAsync(7);
        var hu2 = await SeedHostOrgAsync(3);
        var before = await AcceptanceRecordsAsync(GHostDatabase);
        var (host, startDetail) = await StartHostAsync("GH-A", GHostSettings(null), TimeSpan.FromSeconds(120));
        if (host is null)
        {
            Record("GH-00", "Setup", "the real Authority host starts against the disposable host database (Vault Transit signer, Release B floor 3, acceptance configured on)", "live", startDetail, false);
            return;
        }

        try
        {
            for (var i = 0; i < 120 && await AcceptanceRecordsAsync(GHostDatabase) <= before; i++)
            {
                await Task.Delay(500);
            }

            var policy = await AcceptanceRecordsAsync(GHostDatabase) > before;
            var writes = new List<string>();
            foreach (var i in Enumerable.Range(0, 7))
            {
                var r = hb.Records[i];
                var batch = await BatchAsync(r);
                List<object> items = i == 2 ? [batch.Seal("h-" + i), await DocumentAsync(r, 2500)] : [batch.Seal("h-" + i), batch.Seal("h-b-" + i)];
                writes.Add(await HostWriteAsync(host, "current", r, Owner(r, [.. items])));
            }

            foreach (var o in new[] { hu, hu2 })
            {
                foreach (var r in o.Records)
                {
                    writes.Add(await HostWriteAsync(host, "current", r, Owner(r, Rows.V1Field(o.Id, r.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), (byte)(40 + o.Records.IndexOf(r))))));
                }
            }

            var pending = await HostPrepareAsync(host, "current", hb.Records[1], Owner(hb.Records[1], [.. hb.Records[1].Items]));
            var unanchored = (await AnchorAsync(hb, GHostDatabase)) + (await AnchorAsync(hu, GHostDatabase)) + (await AnchorAsync(hu2, GHostDatabase));
            var censusHb = await SeedCensusAsync(hb, hb.Records[4], GHostDatabase);
            var censusHu = await SeedCensusAsync(hu, hu.Records[4], GHostDatabase);
            var approvals = await SeedApprovalsAsync(hb, [hb.Records[5], hb.Records[6]], GHostDatabase) + await SeedApprovalsAsync(hu, [hu.Records[5], hu.Records[6]], GHostDatabase);
            var entryHb = await HostEntryAsync(host, "current", Entry.Backfill, hb.Records[6]);
            var entryHu = await HostEntryAsync(host, "current", Entry.Backfill, hu.Records[6]);
            var bound = await BindingCountAsync($"organization_id = '{hb.Id}'", GHostDatabase);
            Record("GH-00", "Setup", "the real Authority host (Development, settings file removed, every setting explicit; Vault Transit signer; Release B floor 3; acceptance configured on and recorded by the host itself) runs on its own disposable database; the CURRENT business build (BusinessAgent, its own clients over HTTP) writes 7 schema-2 heads in one organization and v1 heads in two others; a pending operation; census decisions and snapshots; approvals and two backfilled entries; every content entry anchored",
                "migrate 0; floors 0,0,0; host live; policy recorded; writes OK x17; pending OK; unanchored 0; census OK x2; approvals 4; backfills Indexed x2; bindings > 0",
                $"migrate {migrated.Code}; floors {string.Join(",", floors)}; host {startDetail}; policy {policy}; writes {string.Join(",", writes.Distinct())} ({writes.Count}); pending {pending.Outcome}; unanchored {unanchored}; census {censusHb.Split(' ')[0]},{censusHu.Split(' ')[0]}; approvals {approvals}; backfills {entryHb} / {entryHu}; bindings {bound}",
                migrated.Code == 0 && floors.All(c => c == 0) && policy && writes.Count == 17 && writes.All(w => w == "OK") && pending.Outcome == "OK" && unanchored == 0
                && censusHb.StartsWith("OK", StringComparison.Ordinal) && censusHu.StartsWith("OK", StringComparison.Ordinal) && approvals == 4 && entryHb == "OK Indexed" && entryHu == "OK Indexed" && bound > 0);
            var snapHb = Guid.Parse(censusHb.Split(' ')[1]);
            var snapHu = Guid.Parse(censusHu.Split(' ')[1]);
            hostSnapshots[hu.Id] = snapHu;
            await AnchorAsync(hb, GHostDatabase);
            await AnchorAsync(hu, GHostDatabase);

            // The seven requests, by record, in the bound organization.
            (Entry Entry, Rec Record)[] seven = [(Entry.Prepare, hb.Records[0]), (Entry.Commit, hb.Records[1]), (Entry.Verify, hb.Records[2]), (Entry.BatchVerify, hb.Records[3]), (Entry.Census, hb.Records[4]), (Entry.Backfill, hb.Records[5]), (Entry.DeepVerify, hb.Records[6])];

            async Task<string> Seven(string build, string[]? headerOverride = null)
            {
                var results = new List<string>();
                foreach (var (entry, record) in seven)
                {
                    results.Add(entry + "=" + await HostEntryAsync(host, build, entry, record, snapHb, pending.Operation, pending.Rows, pending.Staged, headerOverride));
                }

                return string.Join("; ", results);
            }

            // GH-02 (M2F-36): the 0be5f52 build (faithful encoding, no header) is refused at every entry point of a bound organization.
            {
                var printBefore = await OrgFingerprintAsync(hb, GHostDatabase);
                var outcomes = await Seven("0be5f52");
                var printAfter = await OrgFingerprintAsync(hb, GHostDatabase);
                Record("GH-02", "Security", "M2F-36 / CAP-S5 on the real host: the E3-M2e-2 business build 0be5f52 (it reads and encodes schema 2 faithfully but sends no header) is refused at all seven content endpoints of an organization with a binding, with the fixed non-retryable 409 seen by its own client; nothing in the organization changed",
                    $"7 x {HostRefused}; organization rows unchanged", $"{outcomes}; rows {FingerprintDiff(printBefore, printAfter)}",
                    outcomes.Split("; ").All(o => o.EndsWith("=" + HostRefused, StringComparison.Ordinal)) && printBefore == printAfter);
            }

            // GH-03 (M2F-35): the real pre-M2b-2 build's own request (schema-2 rows re-encoded as schema 1) is refused before verification.
            {
                var printBefore = await OrgFingerprintAsync(hb, GHostDatabase);
                var incidents = await IncidentCountAsync(hb, GHostDatabase);
                var outcomes = await Seven("11e15ee");
                var printAfter = await OrgFingerprintAsync(hb, GHostDatabase);
                Record("GH-03", "Security", "M2F-35 real G-2 on the real host: the 11e15ee business build's own requests (its registry re-encodes the schema-2 rows as schema 1) are refused at all seven endpoints before signed-content verification: no head quarantined, no incident, the pending operation untouched, every Authority row of the organization unchanged",
                    $"7 x {HostRefused}; incidents unchanged; organization rows unchanged", $"{outcomes}; incidents {incidents}->{await IncidentCountAsync(hb, GHostDatabase)}; rows {FingerprintDiff(printBefore, printAfter)}",
                    outcomes.Split("; ").All(o => o.EndsWith("=" + HostRefused, StringComparison.Ordinal)) && printBefore == printAfter && incidents == await IncidentCountAsync(hb, GHostDatabase));
            }

            // GH-04: level-1 organizations remain compatible: both historical builds work in an unbound organization (full write, census,
            // backfill, deep verify); user Verify is admitted past the capability check (it then needs SpiceDB, which the run does not have).
            {
                var results = new List<string>();
                foreach (var build in new[] { "11e15ee", "0be5f52" })
                {
                    var r = build == "11e15ee" ? hu.Records[0] : hu.Records[1];
                    results.Add($"{build}: write " + await HostWriteAsync(host, build, r, Owner(r, Rows.V1Field(hu.Id, r.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), (byte)(build == "11e15ee" ? 61 : 62)))));
                }

                await AnchorAsync(hu, GHostDatabase);
                var census = await HostEntryAsync(host, "11e15ee", Entry.Census, hu.Records[4], snapHu);
                var backfill = await HostEntryAsync(host, "11e15ee", Entry.Backfill, hu.Records[5]);
                var deep = await HostEntryAsync(host, "0be5f52", Entry.DeepVerify, hu.Records[6]);
                var verify = await HostEntryAsync(host, "0be5f52", Entry.Verify, hu.Records[2]);
                var batch = await HostEntryAsync(host, "11e15ee", Entry.BatchVerify, hu.Records[3]);
                Record("GH-04", "Security", "compatibility (CAP-S5) on the real host: in an organization WITHOUT a binding both historical builds keep working through their own clients: Prepare + Commit of a v1 change, protection census, backfill and deep-sweep verification succeed; user Verify and batch Verify are admitted past the capability check (they then stop as not ready on the unreachable SpiceDB, never with the capability refusal)",
                    "writes OK x2; census Verified; backfill Indexed; deep Pass; verify and batch not the capability refusal (AUTHORITY_NOT_READY 503)",
                    $"{string.Join("; ", results)}; census {census}; backfill {backfill}; deep {deep}; verify {verify}; batch {batch}",
                    results.All(r => r.EndsWith("write OK", StringComparison.Ordinal)) && census == "OK Verified" && backfill == "OK Indexed" && deep.StartsWith("OK Pass", StringComparison.Ordinal)
                    && verify.StartsWith("AUTHORITY_NOT_READY 503", StringComparison.Ordinal) && batch.StartsWith("AUTHORITY_NOT_READY 503", StringComparison.Ordinal));
            }

            // GH-05: mixed clients cannot bypass the floor.
            {
                // (a) an operation prepared by the current build in the bound organization cannot be committed by the 0be5f52 build.
                var rec = hb.Records[1];
                var legacyCommit = await HostCommitAsync(host, "0be5f52", rec, pending.Operation, pending.Staged!);
                var stateA = await StateAsync(pending.Operation, GHostDatabase);
                var currentCommit = await HostCommitAsync(host, "current", rec, pending.Operation, pending.Staged!, pending.Rows, Owner(rec, [.. rec.Items]));

                // (b) an operation the 11e15ee build prepared while hu2 had no binding cannot be committed by it after hu2's first binding.
                var target = hu2.Records[1];
                var legacy = await HostPrepareAsync(host, "11e15ee", target, Owner(target, Rows.V1Field(hu2.Id, target.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), 71)));
                var first = await HostWriteAsync(host, "current", hu2.Records[0], Owner(hu2.Records[0], (await BatchAsync(hu2.Records[0])).Seal("hu2-first")));
                var legacyAfter = legacy.Outcome == "OK" ? await HostCommitAsync(host, "11e15ee", target, legacy.Operation, legacy.Staged!) : "not prepared";
                var stateB = await StateAsync(legacy.Operation, GHostDatabase);
                var finish = legacy.Outcome == "OK" ? await HostCommitAsync(host, "current", target, legacy.Operation, legacy.Staged!) : "not prepared";
                Record("GH-05", "Security", "mixed clients on the real host cannot bypass the floor: (a) a current-build operation in a bound organization cannot be committed by the 0be5f52 build (refused, still Prepared; the current build then commits it); (b) an operation the 11e15ee build prepared before the organization's first binding cannot be committed by it afterwards (refused, still Prepared; the current build can finish it)",
                    $"(a) {HostRefused}; 3/NULL Prepared; current OK. (b) legacy prepare OK; first binding OK; {HostRefused}; 3/NULL Prepared; current OK",
                    $"(a) {legacyCommit}; {stateA}; current {currentCommit}. (b) prepare {legacy.Outcome}; first {first}; legacy commit {legacyAfter}; {stateB}; current {finish}",
                    legacyCommit == HostRefused && stateA == "3/NULL Prepared -" && currentCommit == "OK" && legacy.Outcome == "OK" && first == "OK" && legacyAfter == HostRefused && stateB == "3/NULL Prepared -" && finish == "OK");
            }

            // GH-06 (M2F-06 on the wire): the header grammar through the real host's parsing; the bound organization's Verify.
            {
                var variants = new (string Name, string[] Values)[]
                {
                    ("absent", []), ("\"1\"", ["1"]), ("\"3\"", ["3"]), ("\"02\"", ["02"]), ("\"\"", [""]), ("\"two\"", ["two"]), ("\"1,2\"", ["1,2"]), ("\"+2\"", ["+2"]),
                    ("instances 1 and 2", ["1", "2"]), ("instances 2 and 2", ["2", "2"]), ("\"2.0\"", ["2.0"]),
                };
                var outcomes = new List<string>();
                foreach (var (name, values) in variants)
                {
                    outcomes.Add(name + " -> " + await HostEntryAsync(host, "current", Entry.Verify, hb.Records[2], headerOverride: values));
                }

                var control = await HostEntryAsync(host, "current", Entry.Verify, hb.Records[2], headerOverride: ["2"]);
                var padded = await HostEntryAsync(host, "current", Entry.Verify, hb.Records[2], headerOverride: [" 2 "]);
                Log("DIAGNOSTIC padded header ' 2 ' (HTTP optional whitespace) -> " + padded);
                Record("GH-06", "Security", "M2F-06 / CAP-S3 / CAP-S6 through the real host's header parsing (current build's client, header replaced on the wire): absent, 1, 3, 02, empty, two, 1,2, +2, two instances (1 and 2; 2 and 2) and 2.0 are all level 1 and refused; exactly \"2\" is admitted (then stops as not ready on the unreachable SpiceDB). A value padded with HTTP optional whitespace is recorded as a diagnostic",
                    $"11 x {HostRefused}; \"2\" admitted (AUTHORITY_NOT_READY 503)", $"{string.Join("; ", outcomes)}; \"2\" -> {control}; padded -> {padded}",
                    outcomes.All(o => o.EndsWith("-> " + HostRefused, StringComparison.Ordinal)) && control.StartsWith("AUTHORITY_NOT_READY 503", StringComparison.Ordinal));
            }

            // GH-01: what the real builds actually sent (all requests above without an override).
            {
                string Summary(string build) => sentHeaders.TryGetValue(build, out var list) ? string.Join(",", list.GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}")) : "none";
                var ok = sentHeaders.TryGetValue("11e15ee", out var a) && a.Count > 0 && a.All(x => x == "[]")
                         && sentHeaders.TryGetValue("0be5f52", out var b) && b.Count > 0 && b.All(x => x == "[]")
                         && sentHeaders.TryGetValue("current", out var c) && c.Count > 0 && c.All(x => x == "[2]");
                Record("GH-01", "Premise", "CAP-S2/S5 on the wire: every request the 11e15ee and 0be5f52 builds sent carried NO capability header; every request of the current build carried exactly one header with value 2 (recorded below each build's own client)",
                    "11e15ee [] only; 0be5f52 [] only; current [2] only", $"11e15ee {Summary("11e15ee")}; 0be5f52 {Summary("0be5f52")}; current {Summary("current")}", ok);
            }

            // GH-07 (INV-4): what the running host reports.
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(host.BaseAddress) };
                using var ready = await client.GetAsync("internal/v1/health/ready");
                var body = await ready.Content.ReadAsStringAsync();
                var startup = host.Text.Contains("deployment floor 1", StringComparison.Ordinal);
                Record("GH-07", "Control", "INV-4 / §9 on the real host: with the deployment floor not configured the host logs at startup that the capability floor is enforced with deployment floor 1 (supporting evidence only, INV-2)",
                    "startup log names deployment floor 1", $"startup log {(startup ? "names deployment floor 1" : "DOES NOT name it")}; readiness {(int)ready.StatusCode} {body.ReplaceLineEndings(" ")}", startup);
                var readinessShows = body.Contains("envelope capability floor enforced; deployment floor 1", StringComparison.Ordinal);
                Demonstrate("GH-07r", "INCONCLUSIVE", "the composed readiness text (E3-M2f-2 record §11: not executed in-process) on the real host: readiness needs SpiceDB, which the disposable run does not provide; whether the capability text is appended when every other dependency is ready is not observable here",
                    "readiness detail contains the capability text, or readiness fails earlier on SpiceDB", $"{(int)ready.StatusCode}; contains capability text {readinessShows}; {(body.Length > 300 ? body[..300] : body).ReplaceLineEndings(" ")}", readinessShows || (int)ready.StatusCode == 503);
            }
        }
        finally
        {
            await StopHostAsync(host);
        }

        // GH-08 (M2F-38 on the real host): deployment floor 2 refuses level 1 even in an organization without a binding.
        {
            var (hostD2, detail) = await StartHostAsync("GH-D2", GHostSettings("2"), TimeSpan.FromSeconds(120));
            if (hostD2 is null)
            {
                Record("GH-08", "Security", "M2F-38 on the real host (deployment floor 2)", "host live", detail, false);
            }
            else
            {
                try
                {
                    var r = hu.Records[2];
                    var before08 = await OrgFingerprintAsync(hu, GHostDatabase);
                    var legacy = await HostWriteAsync(hostD2, "0be5f52", r, Owner(r, Rows.V1Field(hu.Id, r.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), 81)));
                    var after08 = await OrgFingerprintAsync(hu, GHostDatabase);
                    var census = await HostEntryAsync(hostD2, "11e15ee", Entry.Census, hu.Records[4], SnapshotFor(hu));
                    var current = await HostWriteAsync(hostD2, "current", r, Owner(r, Rows.V1Field(hu.Id, r.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), 82)));
                    var startup = hostD2.Text.Contains("deployment floor 2", StringComparison.Ordinal);
                    Record("GH-08", "Security", "M2F-38 on the real host: with Content:MinimumBusinessEnvelopeCapability = 2 the host refuses the 0be5f52 build's write and the 11e15ee build's census in an organization WITHOUT a binding (organization rows unchanged); the current build writes; the startup log names deployment floor 2",
                        $"prepare: {HostRefused}; census {HostRefused}; rows unchanged; current OK; startup floor 2", $"legacy {legacy}; census {census}; rows {FingerprintDiff(before08, after08)}; current {current}; startup {startup}",
                        legacy == "prepare: " + HostRefused && census == HostRefused && before08 == after08 && current == "OK" && startup);
                }
                finally
                {
                    await StopHostAsync(hostD2);
                }
            }
        }

        // GH-09 (M2F-29 on the real host): an invalid deployment floor refuses the start.
        {
            var (hostBad, detail) = await StartHostAsync("GH-D3", GHostSettings("3"), TimeSpan.FromSeconds(60));
            if (hostBad is not null)
            {
                await StopHostAsync(hostBad);
            }

            Record("GH-09", "Security", "M2F-29 / §9 on the real host: Content:MinimumBusinessEnvelopeCapability = \"3\" refuses the start (fail closed)", "not live; exits non-zero; names the configuration key",
                $"{(hostBad is null ? "not live" : "LIVE")}; {detail}", hostBad is null && detail.Contains("code", StringComparison.Ordinal) && !detail.Contains("code 0", StringComparison.Ordinal) && detail.Contains(EnvelopeCapabilityFloor.ConfigurationKey, StringComparison.Ordinal));
        }

        await GrantSentinelAsync("after-capability-host");
    }

    private readonly Dictionary<Guid, Guid> hostSnapshots = [];

    private Guid SnapshotFor(Org org) => hostSnapshots.TryGetValue(org.Id, out var s) ? s : Guid.Empty;
}
