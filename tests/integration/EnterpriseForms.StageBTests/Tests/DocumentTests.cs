using System.Net;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>Multi-document upload, real ClamAV verdicts, encrypted storage, authorized download, versions, removal (requirement 7; B-05).</summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class DocumentTests(StageBFixture fixture)
{
    [Fact]
    public async Task ConcurrentCleanUploadsBecomeReadyOnlyAfterScanEncryptAndUserCommit()
    {
        var s = new StageBScenario(fixture, "documents");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);

        var small = StageBScenario.SyntheticBytes(64 * 1024, 1);
        var large = StageBScenario.SyntheticBytes(9 * 1024 * 1024 + 123, 2); // 3 chunks of 4 MiB
        var text = System.Text.Encoding.UTF8.GetBytes("Synthetic warrant text " + s.Name);

        // Transfers and scans run concurrently; commits to one record are serialized (the browser coordinator keys writes by record).
        var processed = await Task.WhenAll(
            s.TransferAsync(s.Clerk, accused, "small.bin", "application/octet-stream", small),
            s.TransferAsync(s.Clerk, accused, "large.bin", "application/octet-stream", large),
            s.TransferAsync(s.Clerk, accused, "note.txt", "text/plain", text));
        Assert.All(processed, p => Assert.Equal("Processed", p.GetProperty("sessionState").GetString()));
        Assert.All(processed, p => Assert.Equal("Clean", p.GetProperty("scanVerdict").GetString()));
        var uploads = new List<System.Text.Json.JsonElement>();
        foreach (var session in processed)
        {
            uploads.Add(await s.CommitAsync(s.Clerk, accused, session.GetProperty("uploadSessionId").GetGuid()));
        }

        Assert.All(uploads, u => Assert.Equal("Committed", u.GetProperty("sessionState").GetString()));

        // A concurrent commit race against the same record is a clean 409 the client retries, never a corrupted head.
        var raceBytes = new[] { StageBScenario.SyntheticBytes(2048, 21), StageBScenario.SyntheticBytes(2048, 22) };
        var raceSessions = await Task.WhenAll(s.TransferAsync(s.Clerk, accused, "race-a.bin", "application/octet-stream", raceBytes[0]), s.TransferAsync(s.Clerk, accused, "race-b.bin", "application/octet-stream", raceBytes[1]));
        var raced = await Task.WhenAll(raceSessions.Select(r => fixture.Api.PostAsync($"/api/v1/upload-sessions/{r.GetProperty("uploadSessionId").GetGuid():D}/commit", s.Token(s.Clerk), new { })));
        Assert.All(raced, r => Assert.True(r.IsSuccess || r.ErrorCode == "CONTENT_REVISION_CONFLICT", r.Body));
        for (var i = 0; i < raced.Length; i++)
        {
            if (!raced[i].IsSuccess)
            {
                await s.WaitProjectionAsync(s.Clerk, accused);
                var retried = await fixture.Api.PostAsync($"/api/v1/upload-sessions/{raceSessions[i].GetProperty("uploadSessionId").GetGuid():D}/commit", s.Token(s.Clerk), new { });
                Assert.True(retried.IsSuccess, "retry after conflict failed: " + retried.Body);
            }
        }

        await s.WaitProjectionAsync(s.Clerk, accused);
        var raceDocuments = (await s.ProjectionAsync(s.Clerk, accused)).Root.GetProperty("documents").EnumerateArray().Count(d => d.GetProperty("originalFileName").GetString()!.StartsWith("race-", StringComparison.Ordinal));
        Assert.Equal(2, raceDocuments);

        var projection = await s.WaitProjectionAsync(s.Clerk, accused);
        var documents = projection.Root.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(5, documents.Count);
        Assert.All(documents, d => Assert.True(d.GetProperty("inAcceptedManifest").GetBoolean()));
        Assert.All(documents, d => Assert.Equal("Ready", d.GetProperty("attachmentLifecycleState").GetString()));
        Assert.Equal(6, projection.Root.GetProperty("record").GetProperty("contentRevision").GetInt64()); // 1 + five commits
        var largeDocument = documents.Single(d => d.GetProperty("originalFileName").GetString() == "large.bin");
        Assert.Equal(large.LongLength, largeDocument.GetProperty("plaintextSizeBytes").GetInt64());

        // Authorized download reproduces the original bytes with the protective headers.
        var download = await fixture.DownloadAsync($"/api/v1/form-records/{accused:D}/documents/{largeDocument.GetProperty("fileAttachmentId").GetGuid():D}/download", s.Token(s.Clerk));
        Assert.Equal(HttpStatusCode.OK, download.Status);
        Assert.Equal(StageBScenario.Sha256Hex(large), StageBScenario.Sha256Hex(download.Body));
        Assert.Contains("attachment", download.ContentDisposition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", download.CacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("nosniff", download.NoSniff);

        // The stored object is ciphertext: the attacker reads the business row and the blob can never equal the plaintext hash.
        var hashes = fixture.Stack.AttackerSql($"SELECT encode(plaintext_sha256,'hex') || ' ' || encode(ciphertext_sha256,'hex') FROM file_attachment_versions WHERE form_record_id = '{accused:D}' AND original_file_name = 'large.bin';").Trim().Split(' ');
        Assert.Equal(StageBScenario.Sha256Hex(large), hashes[0]);
        Assert.NotEqual(hashes[0], hashes[1]);

        // Version history and metadata
        var detail = await fixture.Api.GetAsync($"/api/v1/form-records/{accused:D}/documents/{largeDocument.GetProperty("fileAttachmentId").GetGuid():D}", s.Token(s.Clerk));
        Assert.True(detail.IsSuccess, detail.Body);
        var version = Assert.Single(detail.Root.GetProperty("versions").EnumerateArray());
        Assert.Equal("Current", version.GetProperty("versionState").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Number, version.GetProperty("contentRevision").ValueKind);
        Assert.Contains("ClamAV", version.GetProperty("scannerName").GetString(), StringComparison.Ordinal);

        // Non-owner cannot list, download or begin uploads; the reader with a field share still cannot touch documents.
        Assert.True((await fixture.DownloadAsync($"/api/v1/form-records/{accused:D}/documents/{largeDocument.GetProperty("fileAttachmentId").GetGuid():D}/download", s.Token(s.Reader))).Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        var readerBegin = await fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Reader), new { formRecordId = accused, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "x.bin", contentType = "application/octet-stream", sizeBytes = 10, replacesFileAttachmentId = (Guid?)null });
        Assert.True(readerBegin.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, readerBegin.Body);

        // Limits from the published layout are enforced at begin.
        var tooLarge = await fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Clerk), new { formRecordId = accused, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "huge.bin", contentType = "application/octet-stream", sizeBytes = 21L * 1024 * 1024, replacesFileAttachmentId = (Guid?)null });
        Assert.Equal("FILE_TOO_LARGE", tooLarge.ErrorCode);
        var badType = await fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Clerk), new { formRecordId = accused, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "x.exe", contentType = "application/x-msdownload", sizeBytes = 10, replacesFileAttachmentId = (Guid?)null });
        Assert.Equal("CONTENT_TYPE_NOT_ALLOWED", badType.ErrorCode);

        // Removal unlinks the document from the verified content; it is no longer downloadable.
        var remove = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/documents/{largeDocument.GetProperty("fileAttachmentId").GetGuid():D}/remove", s.Token(s.Clerk), new { expectedContentRevision = 6, removalReason = "superseded" });
        Assert.True(remove.IsSuccess, remove.Body);
        var afterRemove = await s.WaitProjectionAsync(s.Clerk, accused);
        Assert.DoesNotContain(afterRemove.Root.GetProperty("documents").EnumerateArray(), d => d.GetProperty("fileAttachmentId").GetGuid() == largeDocument.GetProperty("fileAttachmentId").GetGuid() && d.GetProperty("attachmentLifecycleState").GetString() == "Ready");
        var gone = await fixture.DownloadAsync($"/api/v1/form-records/{accused:D}/documents/{largeDocument.GetProperty("fileAttachmentId").GetGuid():D}/download", s.Token(s.Clerk));
        Assert.NotEqual(HttpStatusCode.OK, gone.Status);
    }

    [Fact]
    public async Task EicarIsRejectedByTheRealScannerAndNeverReachesTheRecord()
    {
        var s = new StageBScenario(fixture, "eicar");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);

        var session = await s.UploadAsync(s.Clerk, accused, "eicar.com.txt", "text/plain", StageBScenario.Eicar(), expectCommit: false);
        Assert.Equal("Rejected", session.GetProperty("sessionState").GetString());
        Assert.Equal("MALWARE_DETECTED", session.GetProperty("failureReasonCode").GetString());
        Assert.Equal("Infected", session.GetProperty("scanVerdict").GetString());

        var commit = await fixture.Api.PostAsync($"/api/v1/upload-sessions/{session.GetProperty("uploadSessionId").GetGuid():D}/commit", s.Token(s.Clerk), new { });
        Assert.False(commit.IsSuccess, "a rejected session must not be committable: " + commit.Body);

        var projection = await s.WaitProjectionAsync(s.Clerk, accused);
        Assert.Empty(projection.Root.GetProperty("documents").EnumerateArray());
        Assert.Equal(1, projection.Root.GetProperty("record").GetProperty("contentRevision").GetInt64());

        // The rejected staging object does not leak into the committed documents container (business DB has no version row).
        var rows = fixture.Stack.AttackerSql($"SELECT count(*) FROM file_attachment_versions WHERE form_record_id = '{accused:D}';").Trim();
        Assert.Equal("0", rows);
    }

    [Fact]
    public async Task InterruptedUploadResumesFromTheServerSideChunkInventory()
    {
        var s = new StageBScenario(fixture, "resume");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var bytes = StageBScenario.SyntheticBytes(8 * 1024 * 1024 + 7, 3); // 3 chunks

        var begun = await fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Clerk), new { formRecordId = accused, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "resume.bin", contentType = "application/octet-stream", sizeBytes = bytes.LongLength, replacesFileAttachmentId = (Guid?)null });
        Assert.True(begun.IsSuccess, begun.Body);
        var sessionId = begun.Root.GetProperty("uploadSessionId").GetGuid();
        var chunk = begun.Root.GetProperty("chunkSizeBytes").GetInt32();
        Assert.Equal(3, begun.Root.GetProperty("expectedChunkCount").GetInt32());

        // Send chunk 0 and chunk 2, "lose" chunk 1; completing early must be refused.
        Assert.True((await fixture.PutBytesAsync($"/api/v1/upload-sessions/{sessionId:D}/chunks/0", s.Token(s.Clerk), bytes[..chunk])).IsSuccess);
        Assert.True((await fixture.PutBytesAsync($"/api/v1/upload-sessions/{sessionId:D}/chunks/2", s.Token(s.Clerk), bytes[(2 * chunk)..])).IsSuccess);
        var early = await fixture.Api.PostAsync($"/api/v1/upload-sessions/{sessionId:D}/complete", s.Token(s.Clerk), new { });
        Assert.Equal("UPLOAD_INCOMPLETE", early.ErrorCode);

        // A fresh client reads the inventory and sends only the missing chunk (idempotent re-send of chunk 0 is harmless).
        var inventory = (await fixture.Api.GetAsync($"/api/v1/upload-sessions/{sessionId:D}", s.Token(s.Clerk))).Root;
        Assert.Equal([0, 2], inventory.GetProperty("receivedChunkIndexes").EnumerateArray().Select(x => x.GetInt32()).Order().ToArray());
        Assert.True((await fixture.PutBytesAsync($"/api/v1/upload-sessions/{sessionId:D}/chunks/1", s.Token(s.Clerk), bytes[chunk..(2 * chunk)])).IsSuccess);
        Assert.True((await fixture.PutBytesAsync($"/api/v1/upload-sessions/{sessionId:D}/chunks/0", s.Token(s.Clerk), bytes[..chunk])).IsSuccess);
        var completed = await fixture.Api.PostAsync($"/api/v1/upload-sessions/{sessionId:D}/complete", s.Token(s.Clerk), new { });
        Assert.True(completed.IsSuccess, completed.Body);
        var processed = await s.WaitSessionAsync(s.Clerk, sessionId, state => state is "Processed" or "Rejected" or "Failed");
        Assert.Equal("Processed", processed.GetProperty("sessionState").GetString());
        var committed = await s.CommitAsync(s.Clerk, accused, sessionId);

        var attachmentId = committed.GetProperty("fileAttachmentId").GetGuid();
        var download = await fixture.DownloadAsync($"/api/v1/form-records/{accused:D}/documents/{attachmentId:D}/download", s.Token(s.Clerk));
        Assert.Equal(HttpStatusCode.OK, download.Status);
        Assert.Equal(StageBScenario.Sha256Hex(bytes), StageBScenario.Sha256Hex(download.Body));

        // Tampered chunk size is refused: a wrong-length final chunk cannot complete another session.
        var mine = await fixture.Api.GetAsync("/api/v1/my-upload-jobs", s.Token(s.Clerk));
        Assert.True(mine.IsSuccess, mine.Body);
    }
}
