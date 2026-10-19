using System.Net;
using System.Text;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Document-encryption audit regression tests (2026-10-19). Reads what is actually at rest in the blob emulator and the
/// business database, independently of the API, and covers the gaps found by the audit: historic versions released from
/// unverified rows, plaintext staging left behind by cancelled/expired/failed sessions, expired sessions still accepting
/// plaintext, and the independence of document-byte encryption from the File field's classification.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class DocumentEncryptionAuditTests(StageBFixture fixture)
{
    private const string NoOperation = "00000000-0000-0000-0000-000000000000";

    private AzuriteProbe Probe => new(fixture.Stack);

    private static byte[] Marked(string marker, int repeat) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(marker + "|", repeat)));

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private static string StagingKey(StageBScenario s, Guid recordId, Guid sessionId) => $"staging/{s.OrganizationId:N}/{recordId:N}/{sessionId:N}";

    [Fact]
    public async Task EveryCommittedVersionIsCiphertextAtRestEvenWhenTheFileFieldIsLabelledPublicPlaintext()
    {
        var s = new StageBScenario(fixture, "doc-at-rest");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var documentsField = s.AccusedFields["case_documents"];

        // The policy never records a File field as plaintext, whatever the designer asks for.
        var formId = fixture.Stack.AttackerSql($"SELECT form_definition_id FROM field_definitions WHERE field_definition_id = '{documentsField:D}';").Trim();
        var asked = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId}/fields", s.Token(s.Designer), new { fieldKey = "extra_documents", fieldDisplayName = "Extra documents", fieldDescription = string.Empty, fieldDataType = "File", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
        Assert.Equal(HttpStatusCode.Created, asked.Status);
        Assert.Equal("ApplicationEncrypted", asked.Root.GetProperty("fieldProtectionMode").GetString());

        // A hostile database writer relabels the live document field as Public plaintext; document bytes must not care.
        fixture.Stack.AttackerSql($"UPDATE field_definitions SET field_classification = 'Public', field_protection_mode = 'Standard' WHERE field_definition_id = '{documentsField:D}';");
        Assert.Equal("Public|Standard", fixture.Stack.AttackerSql($"SELECT field_classification || '|' || field_protection_mode FROM field_definitions WHERE field_definition_id = '{documentsField:D}';").Trim());

        var accused = await s.CreateAccusedAsync(s.Clerk);
        var marker = "PLAINTEXT-MARKER-" + Guid.NewGuid().ToString("N");
        var plaintext = Marked(marker, 40_000); // 2,000,000 bytes: two AES-GCM segments
        var session = await s.TransferAsync(s.Clerk, accused, "public-labelled.txt", "text/plain", plaintext);
        Assert.Equal("Processed", session.GetProperty("sessionState").GetString());
        var sessionId = session.GetProperty("uploadSessionId").GetGuid();
        var committed = await s.CommitAsync(s.Clerk, accused, sessionId);
        var attachmentId = committed.GetProperty("fileAttachmentId").GetGuid();

        // Envelope metadata of every copy of the version row.
        var envelope = fixture.Stack.AttackerSql($"SELECT DISTINCT encryption_algorithm_name || '|' || encryption_envelope_version || '|' || encryption_key_reference || '|' || octet_length(encryption_nonce_prefix) || '|' || octet_length(wrapped_data_key) || '|' || encryption_segment_size_bytes || '|' || storage_object_key || '|' || ciphertext_size_bytes || '|' || encode(ciphertext_sha256,'hex') || '|' || encode(plaintext_sha256,'hex') FROM file_attachment_versions WHERE file_attachment_id = '{attachmentId:D}';").Trim();
        var parts = envelope.Split('|');
        Assert.Equal("AES-256-GCM", parts[0]);
        Assert.Equal("1", parts[1]);
        Assert.Equal("local-dev-kek-v1", parts[2]); // a key reference only; never key material
        Assert.Equal("8", parts[3]);
        Assert.Equal("60", parts[4]); // 12-byte wrap nonce + 32-byte DEK ciphertext + 16-byte tag
        Assert.Equal("1048576", parts[5]);
        Assert.Equal(StageBScenario.Sha256Hex(plaintext), parts[9]);

        // What is actually stored: ciphertext whose size and digest match the signed metadata, never the plaintext.
        var (status, blob) = Probe.GetBlob("documents", parts[6]);
        Assert.Equal(200, status);
        Assert.Equal(long.Parse(parts[7], System.Globalization.CultureInfo.InvariantCulture), blob.LongLength);
        Assert.Equal(parts[8], StageBScenario.Sha256Hex(blob));
        Assert.NotEqual(StageBScenario.Sha256Hex(plaintext), StageBScenario.Sha256Hex(blob));
        Assert.False(Contains(blob, Encoding.ASCII.GetBytes(marker)), "plaintext marker found in the stored object");
        var segments = (plaintext.LongLength + (1024 * 1024) - 1) / (1024 * 1024);
        Assert.True(segments >= 2);
        Assert.Equal(plaintext.LongLength + (segments * 20), blob.LongLength); // every 1 MiB segment adds a 4-byte length and a 16-byte tag

        // No plaintext column copy in the business database.
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE string_value LIKE '%{marker}%';").Trim());

        // The plaintext staging object is gone once processing finished (committed blob and uncommitted blocks).
        Assert.Equal(404, Probe.GetBlockList("upload-staging", StagingKey(s, accused, sessionId)).Status);

        // The authorized download still returns the original bytes.
        var download = await fixture.DownloadAsync($"/api/v1/form-records/{accused:D}/documents/{attachmentId:D}/download", s.Token(s.Clerk));
        Assert.Equal(HttpStatusCode.OK, download.Status);
        Assert.Equal(StageBScenario.Sha256Hex(plaintext), StageBScenario.Sha256Hex(download.Body));
    }

    [Fact]
    public async Task ReplacedVersionsHaveIndependentKeysAndHistoricDownloadsAreVerifiedByTheAuthority()
    {
        var s = new StageBScenario(fixture, "doc-versions");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var first = StageBScenario.SyntheticBytes(70_000, 41);
        var third = StageBScenario.SyntheticBytes(50_000, 43);

        var v1 = await s.UploadAsync(s.Clerk, accused, "evidence.bin", "application/octet-stream", first);
        var attachmentId = v1.GetProperty("fileAttachmentId").GetGuid();
        await s.UploadAsync(s.Clerk, accused, "evidence.bin", "application/octet-stream", first, replacesFileAttachmentId: attachmentId); // identical bytes as v2
        await s.UploadAsync(s.Clerk, accused, "evidence-final.bin", "application/octet-stream", third, replacesFileAttachmentId: attachmentId);

        // One envelope per version number; every version has its own DEK, nonce prefix, object and ciphertext — even v1 and v2,
        // which hold identical plaintext.
        var rows = fixture.Stack.AttackerSql($"SELECT DISTINCT version_number || '|' || encode(wrapped_data_key,'hex') || '|' || encode(encryption_nonce_prefix,'hex') || '|' || storage_object_key || '|' || encode(ciphertext_sha256,'hex') || '|' || encode(plaintext_sha256,'hex') FROM file_attachment_versions WHERE file_attachment_id = '{attachmentId:D}' AND content_change_operation_id <> '{NoOperation}';")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.Split('|')).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "2", "3"], rows.Select(r => r[0]).Order(StringComparer.Ordinal).ToArray());
        for (var column = 1; column <= 4; column++)
        {
            Assert.Equal(3, rows.Select(r => r[column]).Distinct(StringComparer.Ordinal).Count());
        }

        var byVersion = rows.ToDictionary(r => r[0], StringComparer.Ordinal);
        Assert.Equal(byVersion["1"][5], byVersion["2"][5]); // same plaintext …
        Assert.NotEqual(byVersion["1"][4], byVersion["2"][4]); // … different ciphertext
        foreach (var row in rows)
        {
            var (status, blob) = Probe.GetBlob("documents", row[3]);
            Assert.Equal(200, status);
            Assert.Equal(row[4], StageBScenario.Sha256Hex(blob));
            Assert.NotEqual(row[5], StageBScenario.Sha256Hex(blob));
        }

        // History and downloads: current = v3; v1 and v2 are released only after authority verification of their snapshot.
        var detail = await fixture.Api.GetAsync($"/api/v1/form-records/{accused:D}/documents/{attachmentId:D}", s.Token(s.Clerk));
        Assert.True(detail.IsSuccess, detail.Body);
        var states = detail.Root.GetProperty("versions").EnumerateArray().ToDictionary(v => v.GetProperty("versionNumber").GetInt32(), v => v.GetProperty("versionState").GetString());
        Assert.Equal("Current", states[3]);
        Assert.Equal("Superseded", states[1]);
        Assert.Equal("Superseded", states[2]);
        var path = $"/api/v1/form-records/{accused:D}/documents/{attachmentId:D}/download";
        Assert.Equal(StageBScenario.Sha256Hex(third), StageBScenario.Sha256Hex((await fixture.DownloadAsync(path, s.Token(s.Clerk))).Body));
        var historic = await fixture.DownloadAsync(path + "?versionNumber=1", s.Token(s.Clerk));
        Assert.Equal(HttpStatusCode.OK, historic.Status);
        Assert.Equal(StageBScenario.Sha256Hex(first), StageBScenario.Sha256Hex(historic.Body));
        Assert.Equal(StageBScenario.Sha256Hex(first), StageBScenario.Sha256Hex((await fixture.DownloadAsync(path + "?versionNumber=2", s.Token(s.Clerk))).Body));
        Assert.Contains((await fixture.DownloadAsync(path + "?versionNumber=1", s.Token(s.Reader))).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.DownloadAsync(path + "?versionNumber=9", s.Token(s.Clerk))).Status);

        // Hostile database writer edits the metadata of a historic version (file name): the release is refused, no bytes leak.
        fixture.Stack.AttackerSql($"UPDATE file_attachment_versions SET original_file_name = 'renamed-by-dba.exe' WHERE file_attachment_id = '{attachmentId:D}' AND version_number = 1;");
        var tampered = await fixture.DownloadAsync(path + "?versionNumber=1", s.Token(s.Clerk));
        Assert.Equal(HttpStatusCode.Locked, tampered.Status);
        Assert.False(Contains(tampered.Body, first.AsSpan(0, 64).ToArray()));
    }

    [Fact]
    public async Task ProcessedButUncommittedVersionCannotBePromotedIntoHistoryByADatabaseWriter()
    {
        var s = new StageBScenario(fixture, "doc-promote");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var accepted = StageBScenario.SyntheticBytes(30_000, 51);
        var pending = StageBScenario.SyntheticBytes(30_000, 52);
        var v1 = await s.UploadAsync(s.Clerk, accused, "accepted.bin", "application/octet-stream", accepted);
        var attachmentId = v1.GetProperty("fileAttachmentId").GetGuid();

        // Another content revision so the v1 commit operation becomes historic.
        var head = await s.WaitProjectionAsync(s.Clerk, accused);
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{accused:D}", s.Token(s.Clerk), new { expectedContentRevision = head.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["given_name"] = "Edited" } });
        Assert.True(edit.IsSuccess, edit.Body);
        await s.WaitProjectionAsync(s.Clerk, accused);

        // A replacement is scanned and encrypted but never committed by the user.
        var processed = await s.TransferAsync(s.Clerk, accused, "never-accepted.bin", "application/octet-stream", pending, replacesFileAttachmentId: attachmentId);
        Assert.Equal("Processed", processed.GetProperty("sessionState").GetString());
        var path = $"/api/v1/form-records/{accused:D}/documents/{attachmentId:D}/download";
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.DownloadAsync(path + "?versionNumber=2", s.Token(s.Clerk))).Status);

        // The writer binds the processed row to the earlier v1 operation with a borrowed signature.
        var historicOperation = fixture.Stack.AttackerSql($"SELECT content_change_operation_id FROM file_attachment_versions WHERE file_attachment_id = '{attachmentId:D}' AND version_number = 1 AND content_change_operation_id <> '{NoOperation}' ORDER BY content_revision ASC LIMIT 1;").Trim();
        fixture.Stack.AttackerSql($"UPDATE file_attachment_versions SET content_change_operation_id = '{historicOperation}', content_revision = 99, row_signature = (SELECT row_signature FROM file_attachment_versions WHERE file_attachment_id = '{attachmentId:D}' AND version_number = 1 AND content_change_operation_id = '{historicOperation}'), version_state = 'Superseded' WHERE file_attachment_id = '{attachmentId:D}' AND version_number = 2 AND content_change_operation_id = '{NoOperation}';");

        var promoted = await fixture.DownloadAsync(path + "?versionNumber=2", s.Token(s.Clerk));
        Assert.Equal(HttpStatusCode.Locked, promoted.Status);
        Assert.False(Contains(promoted.Body, pending.AsSpan(0, 64).ToArray()));
    }

    [Fact]
    public async Task CancelledExpiredAndRejectedUploadsLeaveNoPlaintextInStaging()
    {
        var s = new StageBScenario(fixture, "doc-staging");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var field = s.AccusedFields["case_documents"];
        var chunk = Marked("STAGED-PLAINTEXT-" + Guid.NewGuid().ToString("N"), 1);

        async Task<Guid> OpenWithOneChunkAsync(string name)
        {
            var begun = await fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Clerk), new { formRecordId = accused, fieldDefinitionId = field, originalFileName = name, contentType = "application/octet-stream", sizeBytes = (4L * 1024 * 1024) + 10, replacesFileAttachmentId = (Guid?)null });
            Assert.Equal(HttpStatusCode.Created, begun.Status);
            var id = begun.Root.GetProperty("uploadSessionId").GetGuid();
            var first = StageBScenario.SyntheticBytes(4 * 1024 * 1024, 61);
            Assert.True((await fixture.PutBytesAsync($"/api/v1/upload-sessions/{id:D}/chunks/0", s.Token(s.Clerk), first)).IsSuccess);
            var blocks = Probe.GetBlockList("upload-staging", StagingKey(s, accused, id));
            Assert.Equal(200, blocks.Status);
            Assert.Contains("<UncommittedBlocks><Block>", blocks.Body, StringComparison.Ordinal); // plaintext is staged as an uncommitted block
            return id;
        }

        // Cancel discards the uncommitted plaintext blocks (Delete Blob alone would not).
        var cancelled = await OpenWithOneChunkAsync("cancelled.bin");
        Assert.True((await fixture.Api.SendAsync(HttpMethod.Delete, $"/api/v1/upload-sessions/{cancelled:D}", s.Token(s.Clerk), null, null)).IsSuccess);
        Assert.Equal(404, Probe.GetBlockList("upload-staging", StagingKey(s, accused, cancelled)).Status);

        // An expired session refuses more plaintext immediately and the sweep purges what it holds.
        var expired = await OpenWithOneChunkAsync("expired.bin");
        fixture.Stack.AttackerSql($"UPDATE upload_sessions SET session_expires_on = now() - interval '1 minute' WHERE upload_session_id = '{expired:D}';");
        var late = await fixture.PutBytesAsync($"/api/v1/upload-sessions/{expired:D}/chunks/1", s.Token(s.Clerk), chunk.AsSpan(0, 10).ToArray());
        Assert.Equal("UPLOAD_SESSION_EXPIRED", late.ErrorCode);
        Assert.Equal("UPLOAD_SESSION_EXPIRED", (await fixture.Api.PostAsync($"/api/v1/upload-sessions/{expired:D}/complete", s.Token(s.Clerk), new { })).ErrorCode);
        var settled = await s.WaitSessionAsync(s.Clerk, expired, state => state == "Expired", TimeSpan.FromSeconds(120));
        Assert.Equal("SESSION_EXPIRED", settled.GetProperty("failureReasonCode").GetString());
        await WaitPurgedAsync(expired);
        Assert.Equal(404, Probe.GetBlockList("upload-staging", StagingKey(s, accused, expired)).Status);

        // A rejected (infected) upload never leaves its bytes in staging either.
        var eicar = await s.TransferAsync(s.Clerk, accused, "eicar.txt", "text/plain", StageBScenario.Eicar());
        Assert.Equal("Rejected", eicar.GetProperty("sessionState").GetString());
        Assert.Equal(404, Probe.GetBlockList("upload-staging", StagingKey(s, accused, eicar.GetProperty("uploadSessionId").GetGuid())).Status);

        // The scanner leaves no temporary copies behind.
        Assert.Equal("0", fixture.Stack.Compose("exec -T clamav sh -c \"find /tmp /var/tmp -type f 2>/dev/null | wc -l\"", 60).Trim());
    }

    [Fact]
    public async Task RemovedDocumentsKeepOnlyCiphertextAndAreNoLongerReleased()
    {
        var s = new StageBScenario(fixture, "doc-remove");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var bytes = StageBScenario.SyntheticBytes(20_000, 71);
        var committed = await s.UploadAsync(s.Clerk, accused, "to-remove.bin", "application/octet-stream", bytes);
        var attachmentId = committed.GetProperty("fileAttachmentId").GetGuid();
        var objectKey = fixture.Stack.AttackerSql($"SELECT DISTINCT storage_object_key FROM file_attachment_versions WHERE file_attachment_id = '{attachmentId:D}';").Trim();

        var head = await s.WaitProjectionAsync(s.Clerk, accused);
        var remove = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/documents/{attachmentId:D}/remove", s.Token(s.Clerk), new { expectedContentRevision = head.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), removalReason = "audit check" });
        Assert.True(remove.IsSuccess, remove.Body);
        await s.WaitProjectionAsync(s.Clerk, accused);

        // Removal is a soft delete (B-05): the ciphertext is retained for recovery; nothing is released any more.
        var (status, blob) = Probe.GetBlob("documents", objectKey);
        Assert.Equal(200, status);
        Assert.NotEqual(StageBScenario.Sha256Hex(bytes), StageBScenario.Sha256Hex(blob));
        var path = $"/api/v1/form-records/{accused:D}/documents/{attachmentId:D}/download";
        Assert.NotEqual(HttpStatusCode.OK, (await fixture.DownloadAsync(path, s.Token(s.Clerk))).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await fixture.DownloadAsync(path + "?versionNumber=1", s.Token(s.Clerk))).Status);
    }

    private async Task WaitPurgedAsync(Guid sessionId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (fixture.Stack.AttackerSql($"SELECT staging_object_purged_on IS NOT NULL FROM upload_sessions WHERE upload_session_id = '{sessionId:D}';").Trim() == "t")
            {
                return;
            }

            await Task.Delay(2000);
        }

        throw new TimeoutException("staging object was not purged for session " + sessionId);
    }
}
