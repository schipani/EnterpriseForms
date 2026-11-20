using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// SEC-BL-01 WP-1/WP-2 (C-155, <c>docs/security/SEC_BL_01_DR_CONTRACT_CLOSURE.md</c>): the shape of the journal-verification foundations.
/// The shared chain-rules, completeness and checkpoint code is pure: no AuthorityDb, no keys or signing, no quarantine, no alerting, no
/// archive writes. The delivery algorithm (<c>ArchiveProcessor</c>, <c>PlatformJournal</c>, <c>JournalChain</c>) does not use it yet (D+ is
/// WP-5), and no host composes the checkpoint store or the verification code yet (baseline WP-3, verifier WP-4 not authorized).
/// </summary>
public sealed class JournalVerificationBoundaryTests
{
    private const string VerificationNamespace = "AuthorizationAuthority.Application.Features.Journal.Verification";
    private static readonly Assembly AuthorityApplication = typeof(AuthorizationAuthority.Application.AssemblyMarker).Assembly;
    private static readonly string Authority = RepositoryPaths.Combine("src", "authority");

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(Authority, relative.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void TheVerificationCodeHasNoAuthorityDbKeySigningQuarantineAlertOrArchiveWriteDependency()
    {
        var result = Types.InAssembly(AuthorityApplication).That().ResideInNamespace(VerificationNamespace).ShouldNot().HaveDependencyOnAny(
        [
            "AuthorizationAuthority.Application.Common.Ports.IAuthorityStore",
            "AuthorizationAuthority.Application.Common.Ports.IAuthorityScope",
            "AuthorizationAuthority.Application.Common.Ports.IIntegrityMacProvider",
            "AuthorizationAuthority.Application.Common.Ports.ISecurityAlertSink",
            "AuthorizationAuthority.Application.Common.Ports.ISecurityEventArchive",
            "AuthorizationAuthority.Application.Common.OrganizationQuarantine",
            "AuthorizationAuthority.Application.Common.OrganizationGate",
            "AuthorizationAuthority.Application.Features.Journal.ArchiveProcessor",
            "AuthorizationAuthority.Application.Features.Journal.AuditJournal",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "System.Net.Http",
        ]).GetResult();
        Assert.True(result.IsSuccessful, "forbidden dependency: " + string.Join(", ", result.FailingTypeNames ?? []));
        Assert.NotEmpty(Types.InAssembly(AuthorityApplication).That().ResideInNamespace(VerificationNamespace).GetTypes());
    }

    /// <summary>
    /// SEC-BL-01 WP-7 (C-159; VF-14, BL-2; mutations MV-15 and MV-17): the verifier, the baseline and the comparison they share (namespace
    /// <c>Journal.Reconciliation</c>) are read-only on AuthorityDb and never quarantine, sign, gate, re-deliver or write journal entries. Their only
    /// AuthorityDb access is the read-only <c>IJournalVerificationReader</c>; their only archive access is the inventory (sentinel, listing, reads,
    /// stop markers). The verifier may raise alerts (alert-only, OD-DV-3). The foundations test above covers <c>Journal.Verification</c> only.
    /// </summary>
    [Fact]
    public void TheVerifierAndTheBaselineHaveNoAuthorityDbWriteQuarantineSigningGateOrEntryArchiveWriteDependency()
    {
        string[] readOnlyTypes = ["JournalVerifier", "JournalBaselineService", "JournalStreamComparer", "JournalAcceptedExceptions", "DPlusCommitmentInspection"];
        var types = Types.InAssembly(AuthorityApplication).That().ResideInNamespace("AuthorizationAuthority.Application.Features.Journal.Reconciliation").And()
            .HaveNameMatching("^(" + string.Join("|", readOnlyTypes) + ")$");
        var result = types.ShouldNot().HaveDependencyOnAny(
        [
            "AuthorizationAuthority.Application.Common.Ports.IAuthorityStore",
            "AuthorizationAuthority.Application.Common.Ports.IAuthorityScope",
            "AuthorizationAuthority.Application.Common.Ports.IIntegrityMacProvider",
            "AuthorizationAuthority.Application.Common.Ports.ISecurityEventArchive",
            "AuthorizationAuthority.Application.Common.OrganizationQuarantine",
            "AuthorizationAuthority.Application.Common.OrganizationGate",
            "AuthorizationAuthority.Application.Features.Journal.ArchiveProcessor",
            "AuthorizationAuthority.Application.Features.Journal.AuditJournal",
            "AuthorizationAuthority.Application.Features.Journal.JournalChain",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "System.Net.Http",
        ]).GetResult();
        Assert.True(result.IsSuccessful, "forbidden dependency: " + string.Join(", ", result.FailingTypeNames ?? []));
        Assert.Equal(readOnlyTypes.Length, types.GetTypes().Count());
    }

    [Fact]
    public void TheVerificationCodeNeverMutatesAJournalRowOrCallsJournalChainAppend()
    {
        foreach (var file in new[] { "JournalChainRules.cs", "ArchiveCompleteness.cs", "JournalVerificationCheckpoints.cs", "ArchiveIdentityCodec.cs" })
        {
            var source = Source("AuthorizationAuthority.Application/Features/Journal/Verification/" + file);
            Assert.DoesNotContain("JournalChain.Append", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ArchiveDeliveryState =", source, StringComparison.Ordinal);
            Assert.DoesNotContain("JournalEventBodyJson =", source, StringComparison.Ordinal);
            Assert.DoesNotContain("JournalEventDigest =", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IntegrityQuarantine", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// WP-2..WP-4A pinned "the delivery algorithm does not use the verification foundations yet". WP-5a (D+, phase P1, authorized
    /// 2026-11-17) makes <c>ArchiveProcessor</c> use them through <see cref="AuthorizationAuthority.Application.Features.Journal.AuditDeliveryEnforcement"/>
    /// only. The journal writers (recording) and the platform journal still do not: recording semantics are unchanged (DP-14).
    /// </summary>
    [Fact]
    public void OnlyTheArchiveProcessorUsesTheVerificationFoundationsThroughDPlusTheJournalWritersDoNot()
    {
        foreach (var file in new[] { "AuthorizationAuthority.Application/Features/Journal/JournalChain.cs", "AuthorizationAuthority.Application/Features/Journal/AuditJournal.cs",
            "AuthorizationAuthority.Application/Features/PlatformOperators/PlatformJournal.cs" })
        {
            var source = Source(file);
            Assert.DoesNotContain("ISecurityEventArchiveInventory", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Journal.Verification", source, StringComparison.Ordinal);
            Assert.DoesNotContain("JournalChainRules", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AuditDeliveryEnforcement", source, StringComparison.Ordinal);
        }

        var processor = Source("AuthorizationAuthority.Application/Features/Journal/ArchiveProcessor.cs");
        Assert.Contains("AuditDeliveryEnforcement.EvaluateAsync", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("JournalChainRules", processor, StringComparison.Ordinal); // chain logic lives in WP-1's library only
        var enforcement = Source("AuthorizationAuthority.Application/Features/Journal/AuditDeliveryEnforcement.cs");
        Assert.Contains("JournalChainRules.InspectEntry", enforcement, StringComparison.Ordinal);
        Assert.Contains("JournalChainRules.CheckLink", enforcement, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA256", enforcement, StringComparison.Ordinal); // no duplicated digest or link computation
    }

    /// <summary>
    /// WP-5a (§5.1 DP-11, §5.6, §5.7): D+ never quarantines, applies to the audit stream only, and has no configuration that widens,
    /// narrows or disables it. WP-5a.1: the finding is durably recorded before the row state changes; nothing creates a sentinel
    /// automatically; provisioning is an explicit Development/Testing step for disposable stacks only.
    /// </summary>
    [Fact]
    public void DPlusNeverQuarantinesHasAFixedStreamSetRecordsFindingsFirstAndNeverStampsAnArchive()
    {
        var enforcement = Types.InAssembly(AuthorityApplication).That().HaveName("AuditDeliveryEnforcement").ShouldNot().HaveDependencyOnAny(
        [
            "AuthorizationAuthority.Application.Common.OrganizationQuarantine",
            "AuthorizationAuthority.Application.Common.Ports.IAuthorityStore",
            "AuthorizationAuthority.Application.Common.Ports.IAuthorityScope",
            "AuthorizationAuthority.Application.Common.Ports.IIntegrityMacProvider",
            "Microsoft.Extensions.Options",
            "Microsoft.Extensions.Configuration",
        ]).GetResult();
        Assert.True(enforcement.IsSuccessful, "forbidden dependency: " + string.Join(", ", enforcement.FailingTypeNames ?? []));
        // SEC-BL-01 WP-5b (A-6 §26 EN-1, EN-2): every journal stream is D+-enabled, fixed in code, and the only append is the D+ delivery path.
        Assert.Equal([AuthorizationAuthority.Domain.Features.Journal.JournalStreamNames.Audit, AuthorizationAuthority.Domain.Features.Journal.JournalStreamNames.Authorization,
            AuthorizationAuthority.Domain.Features.Journal.JournalStreamNames.Content], AuthorizationAuthority.Application.Features.Journal.AuditDeliveryEnforcement.EnforcedStreams);

        var processor = Source("AuthorizationAuthority.Application/Features/Journal/ArchiveProcessor.cs");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(processor, @"archive\.AppendAsync\("));
        var deliver = processor[processor.IndexOf("private async Task<(int Delivered, bool EndPass)> DeliverEnforcedStreamAsync", StringComparison.Ordinal)..];
        Assert.Contains("archive.AppendAsync(", deliver, StringComparison.Ordinal);
        Assert.DoesNotContain("UnenforcedStreams", processor, StringComparison.Ordinal);
        var refuse = processor[processor.IndexOf("private async Task RefuseAsync", StringComparison.Ordinal)..processor.IndexOf("public const string JournalBodyDigestMismatch", StringComparison.Ordinal)];
        Assert.DoesNotContain("OrganizationQuarantine", refuse, StringComparison.Ordinal);
        Assert.DoesNotContain("IntegrityQuarantine", refuse, StringComparison.Ordinal);

        // WP-5a.1 (A-2 correction): durable finding before state; never logging or alerting as a substitute.
        Assert.True(refuse.IndexOf("TryRecordAsync(finding", StringComparison.Ordinal) is var record && record > 0
            && record < refuse.IndexOf("ApplyRecorded(entry, verdict)", StringComparison.Ordinal), "the finding must be recorded before the row state changes");
        Assert.DoesNotContain("finding not recorded", processor, StringComparison.Ordinal);
        Assert.Contains("return false; // no durable store composed", processor, StringComparison.Ordinal);

        // No automatic sentinel creation anywhere in the host or the delivery path; only the explicit provisioning step and the baseline.
        Assert.DoesNotContain("CreateIdentityAsync", processor, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Authority, "AuthorizationAuthority.Application", "Features", "Journal", "ArchiveIdentityBootstrap.cs")), "the WP-5a automatic bootstrap is removed");
        var program = Source("AuthorizationAuthority.Api/Program.cs");
        Assert.DoesNotContain("Bootstrap(builder.Environment)", program, StringComparison.Ordinal);
        Assert.True(program.IndexOf("AddDPlusStartupValidation()", StringComparison.Ordinal) is var at && at > 0 && at < program.IndexOf("AddHostedService<AuthorityWorkLoop>", StringComparison.Ordinal),
            "the D+ finding-store validation must start before the work loop");
        var command = Source("AuthorizationAuthority.Api/Composition/ArchiveProvisioningCommand.cs");
        Assert.Contains("if (!environment.IsDevelopment() && !environment.IsEnvironment(\"Testing\"))", command, StringComparison.Ordinal);
        Assert.Contains("configuration.GetValue<bool>(DisposableStackKey)", command, StringComparison.Ordinal);
        var provisioning = Source("AuthorizationAuthority.Application/Features/Journal/Reconciliation/ArchiveProvisioning.cs");
        Assert.Contains("CountJournalRowsAsync", provisioning, StringComparison.Ordinal);
        Assert.Contains("HoldsAnyObjectAsync", provisioning, StringComparison.Ordinal);
        Assert.Contains("content.ContainerAbsent", provisioning, StringComparison.Ordinal);
    }

    /// <summary>
    /// WP-2 pinned "no host composes the foundations yet". WP-3/WP-4 (authorized 2026-11-17) compose them in exactly two Development/Testing
    /// host files; the shared infrastructure registration still does not, and the composition refuses every other environment.
    /// </summary>
    [Fact]
    public void OnlyTheDevelopmentTestingCompositionAndTheBaselineCommandComposeTheVerificationCode()
    {
        string[] allowed = ["ArchiveProvisioningCommand.cs", "JournalVerificationComposition.cs", "JournalBaselineCommand.cs"];
        var hosts = new[] { Path.Combine(Authority, "AuthorizationAuthority.Api"), Path.Combine(Authority, "AuthorizationAuthority.Infrastructure", "AuthorityInfrastructureRegistration.cs") };
        var composing = new List<string>();
        foreach (var file in hosts.SelectMany(h => File.Exists(h) ? [h] : Directory.EnumerateFiles(h, "*.cs", SearchOption.AllDirectories))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(file);
            if (source.Contains("IVerificationCheckpointStore", StringComparison.Ordinal) || source.Contains("VerificationCheckpointStoreOptions", StringComparison.Ordinal)
                || source.Contains("ISecurityEventArchiveInventory", StringComparison.Ordinal) || source.Contains("Journal.Verification", StringComparison.Ordinal)
                || source.Contains("Journal.Reconciliation", StringComparison.Ordinal))
            {
                composing.Add(Path.GetFileName(file));
            }
        }

        Assert.Equal(allowed.Order(StringComparer.Ordinal), composing.Order(StringComparer.Ordinal));
        var composition = Source("AuthorizationAuthority.Api/Composition/JournalVerificationComposition.cs");
        Assert.Contains("if (!environment.IsDevelopment() && !environment.IsEnvironment(\"Testing\"))", composition, StringComparison.Ordinal);
        Assert.Contains("must not be configured in the", composition, StringComparison.Ordinal);
        var command = Source("AuthorizationAuthority.Api/Composition/JournalBaselineCommand.cs");
        Assert.Contains("if (!environment.IsDevelopment() && !environment.IsEnvironment(\"Testing\"))", command, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", command, StringComparison.Ordinal); // reports are never overwritten
    }

    [Fact]
    public void TheBaselineAndVerifierCannotWriteAuthorityDbQuarantineSignOrDeliver()
    {
        var result = Types.InAssembly(AuthorityApplication).That().ResideInNamespace("AuthorizationAuthority.Application.Features.Journal.Reconciliation").ShouldNot().HaveDependencyOnAny(
        [
            "AuthorizationAuthority.Application.Common.Ports.IAuthorityStore",
            "AuthorizationAuthority.Application.Common.Ports.IAuthorityScope",
            "AuthorizationAuthority.Application.Common.Ports.IIntegrityMacProvider",
            "AuthorizationAuthority.Application.Common.Ports.ISecurityEventArchive",
            "AuthorizationAuthority.Application.Common.OrganizationQuarantine",
            "AuthorizationAuthority.Application.Common.OrganizationGate",
            "AuthorizationAuthority.Application.Features.Journal.ArchiveProcessor",
            "AuthorizationAuthority.Application.Features.Journal.AuditJournal",
            "AuthorizationAuthority.Application.Features.Journal.JournalChain",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
        ]).GetResult();
        Assert.True(result.IsSuccessful, "forbidden dependency: " + string.Join(", ", result.FailingTypeNames ?? []));
        Assert.NotEmpty(Types.InAssembly(AuthorityApplication).That().ResideInNamespace("AuthorizationAuthority.Application.Features.Journal.Reconciliation").GetTypes());
    }

    [Fact]
    public void TheReaderPortHasOnlyReadOperations()
    {
        var reader = typeof(AuthorizationAuthority.Application.Common.Ports.IJournalVerificationReader).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal);
        var snapshot = typeof(AuthorizationAuthority.Application.Common.Ports.IJournalVerificationSnapshot).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["OpenSnapshotAsync"], reader);
        Assert.Equal(["CountJournalRowsAsync", "ListOrganizationsAsync", "ReadRowsAsync", "ReadStreamHeadAsync", "TryAcquireSweepLockAsync"], snapshot);
    }

    [Fact]
    public void ThePostgreSqlReaderIssuesOnlyAReadOnlyTransactionAndSelects()
    {
        var source = Source("AuthorizationAuthority.Infrastructure/Persistence/JournalVerificationReader.cs");
        Assert.Contains("\"SET TRANSACTION READ ONLY\"", source, StringComparison.Ordinal);
        Assert.Contains("IsolationLevel.RepeatableRead", source, StringComparison.Ordinal);
        // Every SQL literal that names an Authority table is a SELECT; no data-changing or DDL keyword appears in any string literal.
        var literals = System.Text.RegularExpressions.Regex.Matches(source, "\\$?\"(?<text>(?:[^\"\\\\]|\\\\.)*)\"").Select(m => m.Groups["text"].Value).ToList();
        Assert.All(literals.Where(l => l.Contains("authority.", StringComparison.Ordinal)), l => Assert.Matches("^(SELECT |\\(SELECT |FROM |\\{|s\\.)|SELECT ", l));
        Assert.DoesNotContain(literals, l => System.Text.RegularExpressions.Regex.IsMatch(l, "\\b(INSERT|UPDATE|DELETE|TRUNCATE|ALTER|CREATE|DROP|GRANT|REVOKE|COPY|LOCK TABLE)\\b"));
        // The arbitrary-SQL test seam has no product caller.
        var callers = Directory.EnumerateFiles(Authority, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.EndsWith("JournalVerificationReader.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("ExecuteForTestingAsync", StringComparison.Ordinal)).ToList();
        Assert.Empty(callers);
    }

    [Fact]
    public void TheInMemoryTamperingHooksAreNotPublic()
    {
        var infrastructure = typeof(AuthorizationAuthority.Infrastructure.Archive.InMemorySecurityEventArchive).Assembly;
        foreach (var type in new[] { "AuthorizationAuthority.Infrastructure.Archive.InMemorySecurityEventArchive", "AuthorizationAuthority.Infrastructure.Archive.InMemoryVerificationCheckpointStore",
                     "AuthorizationAuthority.Infrastructure.Archive.InMemoryJournalAcceptedExceptionStore", "AuthorizationAuthority.Infrastructure.Archive.InMemoryArchiveProvisioningStore" })
        {
            var methods = infrastructure.GetType(type, throwOnError: true)!.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name.EndsWith("ForTesting", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(methods);
            Assert.All(methods, m => Assert.True(m.IsAssembly, type + "." + m.Name + " must be internal"));
        }
    }

    /// <summary>
    /// WP-4A (contract amendment A-1, AX-2/AX-5): accepted-exception records are created only by the baseline, for an explicit owner
    /// disposition; the scheduled verifier only reads and re-verifies them and can never accept an exception itself.
    /// </summary>
    [Fact]
    public void OnlyTheBaselineCreatesAcceptedExceptionRecordsTheVerifierOnlyReadsThem()
    {
        var verifier = Source("AuthorizationAuthority.Application/Features/Journal/Reconciliation/JournalVerifier.cs");
        Assert.DoesNotContain("exceptionStore.CreateAsync", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("JournalAcceptedExceptions.Serialize", verifier, StringComparison.Ordinal);
        Assert.Contains("JournalAcceptedExceptions.LoadAndVerifyAsync", verifier, StringComparison.Ordinal);
        var baseline = Source("AuthorizationAuthority.Application/Features/Journal/Reconciliation/JournalBaselineService.cs");
        Assert.Contains("exceptionStore.CreateAsync", baseline, StringComparison.Ordinal);
        Assert.Contains("ApplyDispositions(request.Dispositions)", baseline, StringComparison.Ordinal);
        var port = Source("AuthorizationAuthority.Application/Common/Ports/IJournalAcceptedExceptionStore.cs");
        Assert.DoesNotContain("Delete", port, StringComparison.Ordinal); // create-only: no delete or overwrite operation exists
        Assert.DoesNotContain("Replace", port, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEC-BL-01 E-6/E-7 (C-158, A-7 §27): the gate, content release and projection rest on the trusted receipt, never on the row's delivery state
    /// or the mutable enforcement mode; the receipt verifier reads no AuthorityDb state; a missing verifier fails closed.
    /// </summary>
    [Fact]
    public void ProtectedAccessAndReleaseRestOnTrustedReceiptsNotOnMutableDeliveryFields()
    {
        var gate = Source("AuthorizationAuthority.Application/Common/OrganizationGate.cs");
        Assert.Contains("FindLatestJournalEntryAsync", gate, StringComparison.Ordinal);
        Assert.Contains("JournalReceipts.VerifyStreamHeadAsync", gate, StringComparison.Ordinal); // WP-7A (A-9 RV-4): the head is a stream-head receipt
        Assert.DoesNotContain("JournalReceipts.VerifyAsync", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckpointEnforcementModes", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveDeliveryStates.Anchored", gate, StringComparison.Ordinal);

        var content = Source("AuthorizationAuthority.Application/Features/Content/ContentService.cs");
        Assert.DoesNotContain("checkpointJournal.ArchiveDeliveryState", content, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(content, @"ContentCheckpointReceipts\.IsDeliveredAsync\("));
        var projection = Source("AuthorizationAuthority.Application/Features/Projection/ProjectionProcessor.cs");
        Assert.DoesNotContain("journal.ArchiveDeliveryState", projection, StringComparison.Ordinal);
        Assert.Contains("JournalReceipts.VerifyAsync", projection, StringComparison.Ordinal);

        var receipts = Source("AuthorizationAuthority.Application/Features/Journal/TrustedJournalReceipts.cs");
        Assert.DoesNotContain("IAuthorityStore", receipts, StringComparison.Ordinal); // no AuthorityDb input beyond the entry itself
        Assert.DoesNotContain("ArchiveReceiptReference", receipts, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveReceiptDigest", receipts, StringComparison.Ordinal);
        Assert.DoesNotContain("JournalArchivedOn", receipts, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveDeliveryState =", receipts, StringComparison.Ordinal); // WP-7A: read-only on the entry
        Assert.Contains("MaximumCurrentStateMaxAge = TimeSpan.FromSeconds(30)", receipts, StringComparison.Ordinal); // A-9 RV-2 ceiling

        var port = Source("AuthorizationAuthority.Application/Common/Ports/IAuthorityStore.cs");
        Assert.Contains("IJournalReceiptVerifier JournalReceipts => NotComposedJournalReceiptVerifier.Instance;", port, StringComparison.Ordinal);
        var store = Source("AuthorizationAuthority.Infrastructure/Persistence/AuthorityStore.cs");
        Assert.Contains("journalReceipts ?? NotComposedJournalReceiptVerifier.Instance", store, StringComparison.Ordinal);
        Assert.Contains("public Task<SecurityJournalEntry?> FindLatestJournalEntryAsync", store, StringComparison.Ordinal);
    }

    /// <summary>SEC-BL-01 G08 (A-7 §27): the journal triggers and grants (fixed search_path, not SECURITY DEFINER, column-level runtime UPDATE, no runtime DELETE).</summary>
    [Fact]
    public void JournalImmutabilityTriggersAndGrantsAreHardened()
    {
        var sql = AuthorizationAuthority.Infrastructure.Persistence.JournalImmutabilitySql.UpSql;
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(sql, "SET search_path = pg_catalog, pg_temp"));
        Assert.DoesNotContain("SECURITY DEFINER", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OR DELETE ON authority.security_journal_entries", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE TRUNCATE ON authority.security_journal_entries", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("BEFORE INSERT", sql, StringComparison.Ordinal); // M-6 / M2F3-54: no INSERT trigger
        Assert.Contains("REVOKE ALL ON FUNCTION authority.security_journal_entry_guard() FROM PUBLIC", sql, StringComparison.Ordinal);

        var grants = Source("AuthorizationAuthority.Infrastructure/Persistence/AuthorityRuntimeGrants.cs");
        Assert.DoesNotContain("\"security_journal_entries\"", grants[grants.IndexOf("AppendOnlyTables =", StringComparison.Ordinal)..grants.IndexOf("MutableTables =", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("GRANT UPDATE ({string.Join(\", \", JournalImmutabilitySql.RuntimeUpdatableColumns)})", grants, StringComparison.Ordinal);
        Assert.Equal(["archive_delivery_state", "archive_attempt_count", "archive_receipt_reference", "archive_receipt_digest", "journal_archived_on", "last_archive_error_code"],
            AuthorizationAuthority.Infrastructure.Persistence.JournalImmutabilitySql.RuntimeUpdatableColumns);
        var verifier = string.Join(' ', AuthorizationAuthority.Infrastructure.Persistence.AuthorityRuntimeGrants.JournalVerifierStatements("verifier_login"));
        Assert.DoesNotContain("INSERT", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", verifier, StringComparison.Ordinal);
        Assert.Contains("NOLOGIN", verifier, StringComparison.Ordinal);
    }
}
