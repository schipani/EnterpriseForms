using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// CONS-23 on the real stack (owner decision 1, C-201; trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md "Long streams and strict
/// revocation"): "Stream terminates within documented baseline recheck bound". A large encrypted document is downloaded; the client
/// reads its first megabyte and stops reading, so the server's writes block on full transport buffers; the access is then changed and
/// the client resumes. The server re-checks at the Authority (at most every 5 s, before the next slice of plaintext) and stops the
/// stream, audited as DOCUMENT_DOWNLOAD_FAILED, on a revocation, a replacement of the document (a change of its accepted version) or an
/// unavailable Authority. An undisturbed slow download completes byte for byte through its re-checks. Bytes already in the transport
/// buffers when access ends are the protocol's acknowledged finite exposure window and are reported, not denied.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class C10DownloadRecheckTests(StageBFixture fixture)
{
    private const int DocumentBytes = 40 * 1024 * 1024;
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan RecheckBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainAllowance = TimeSpan.FromSeconds(20);

    private sealed record Workspace(StageBScenario S, Guid Record, Guid Attachment, byte[] Bytes);

    private static string DownloadPath(Guid record, Guid attachment) => $"/api/v1/form-records/{record:D}/documents/{attachment:D}/download";

    private async Task<Workspace> WorkspaceAsync(string name, int seed)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);
        var bytes = StageBScenario.SyntheticBytes(DocumentBytes, seed);
        var document = await s.UploadAsync(s.Clerk, record, "cons23-large.bin", "application/octet-stream", bytes, timeout: TimeSpan.FromMinutes(5));
        return new Workspace(s, record, document.GetProperty("fileAttachmentId").GetGuid(), bytes);
    }

    private static async Task<Guid> ShareDocumentAsync(Workspace w)
    {
        var created = await w.S.Fixture.Api.PostAsync($"/api/v1/form-records/{w.Record:D}/shares", w.S.Token(w.S.Clerk), new
        {
            useDevelopmentStepUp = true, recipientUserId = w.S.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" },
            documents = new[] { new { fileAttachmentId = w.Attachment, canDownloadDocument = true, canReplaceDocument = false } },
        });
        Assert.True(created.IsSuccess, created.Body);
        await w.S.WaitGateReadyAsync(Settle);
        await w.S.WaitProjectionAsync(w.S.Reader, w.Record, Settle);
        return created.Root.GetProperty("recordShareId").GetGuid();
    }

    private string[] DownloadFailures(Workspace w) =>
        fixture.Stack.AttackerSql($"SELECT event_metadata_json->>'reasonCode' FROM audit_events WHERE tenant_id = '{w.S.OrganizationId:D}' AND form_record_id = '{w.Record:D}' AND event_action = 'DOCUMENT_DOWNLOAD_FAILED' ORDER BY audit_sequence;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Opens the download, reads 1 MiB, then stops reading long enough for the server's writes to block.</summary>
    private async Task<PausedDownload> PausedAsync(Workspace w, TestUser user)
    {
        var download = await fixture.StartPausedDownloadAsync(DownloadPath(w.Record, w.Attachment), w.S.Token(user), 1024 * 1024);
        Assert.Equal(HttpStatusCode.OK, download.Status);
        Assert.Equal(DocumentBytes, download.DeclaredLength);
        await Task.Delay(TimeSpan.FromSeconds(2));
        return download;
    }

    /// <summary>Resumes after the access change and asserts the stream stopped early, within the bound, audited with an expected reason.</summary>
    private async Task AssertStoppedAsync(Workspace w, PausedDownload download, Stopwatch sinceChange, string context, Func<string, bool> expectedReason)
    {
        var receivedAtChange = download.Received;
        await download.ReadRestAsync();
        var ended = sinceChange.Elapsed;
        var observed = $"{context}: received {download.Received} of {DocumentBytes} bytes ({download.Received - receivedAtChange} after the change), ended {ended.TotalSeconds:F1} s after the change, failure '{download.Failure}'";
        Assert.False(download.Completed, "CONS-23: the stream was not stopped. " + observed);
        Assert.True(download.Received < DocumentBytes, observed);
        Assert.True(ended <= RecheckBound + DrainAllowance, "CONS-23: the stream did not stop within the re-check bound. " + observed);
        var failures = DownloadFailures(w);
        Assert.True(failures.Length == 1 && expectedReason(failures[0]), $"expected one DOCUMENT_DOWNLOAD_FAILED with the {context} reason, found [{string.Join(", ", failures)}]. {observed}");
        TestContext.Current.SendDiagnosticMessage("CONS-23 " + observed + "; reason " + failures[0]);
    }

    [Fact]
    public async Task AnUndisturbedSlowDownloadCompletesByteForByteThroughItsRechecks()
    {
        var w = await WorkspaceAsync("cons23-uninterrupted", 231);
        await ShareDocumentAsync(w);
        var started = Stopwatch.StartNew();
        using var download = await fixture.StartPausedDownloadAsync(DownloadPath(w.Record, w.Attachment), w.S.Token(w.S.Reader), 1024 * 1024);
        await download.ReadRestAsync(bytesPerSecond: 3 * 1024 * 1024); // about 13 s: two or more re-checks at the 5 s interval

        Assert.True(download.Completed, $"received {download.Received} of {DocumentBytes}, failure '{download.Failure}'");
        Assert.Equal("no", download.Header("X-Accel-Buffering")); // a buffering proxy must not read ahead of the re-checks
        Assert.True(started.Elapsed > TimeSpan.FromSeconds(10), "the download was not slow enough to cross a re-check");
        Assert.Equal(SHA256.HashData(w.Bytes), download.Sha256);
        Assert.Empty(DownloadFailures(w));
    }

    [Fact]
    public async Task ARevocationDuringTheDownloadStopsTheStreamWithinTheRecheckBound()
    {
        var w = await WorkspaceAsync("cons23-revocation", 232);
        var share = await ShareDocumentAsync(w);
        using var download = await PausedAsync(w, w.S.Reader);

        var revoked = await fixture.Api.PostAsync($"/api/v1/form-records/{w.Record:D}/shares/{share:D}/revoke?useDevelopmentStepUp=true", w.S.Token(w.S.Clerk), new { });
        Assert.True(revoked.IsSuccess, revoked.Body);
        await w.S.WaitGateReadyAsync(Settle);
        var sinceChange = Stopwatch.StartNew();

        await AssertStoppedAsync(w, download, sinceChange, "revocation", reason => reason.Length > 0 && reason is not ("AUTHORITY_UNAVAILABLE" or "INTEGRITY_VERIFICATION_FAILED")); // the Authority's denial code
    }

    [Fact]
    public async Task ARevocationWhileTheClientIsReadingStopsTheStreamWithinABoundedAmountOfPlaintext()
    {
        // Independent review finding 8a/8c: the client keeps reading (1 MiB/s) through the revocation, so the stop comes from the
        // periodic re-check, not from a check on resume; the plaintext received after the revocation took effect is bounded by the
        // interval plus the check and transport buffers (asserted at 16 MiB, reported exactly).
        var w = await WorkspaceAsync("cons23-revocation-active", 236);
        var share = await ShareDocumentAsync(w);
        using var download = await fixture.StartPausedDownloadAsync(DownloadPath(w.Record, w.Attachment), w.S.Token(w.S.Reader), 64 * 1024);
        var reading = download.ReadRestAsync(bytesPerSecond: 1024 * 1024);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var revoked = await fixture.Api.PostAsync($"/api/v1/form-records/{w.Record:D}/shares/{share:D}/revoke?useDevelopmentStepUp=true", w.S.Token(w.S.Clerk), new { });
        Assert.True(revoked.IsSuccess, revoked.Body);
        await w.S.WaitGateReadyAsync(Settle);
        var receivedAtChange = download.Received;
        var sinceChange = Stopwatch.StartNew();
        await reading;

        var after = download.Received - receivedAtChange;
        var observed = $"active revocation: received {download.Received} of {DocumentBytes} bytes ({after} after the change), ended {sinceChange.Elapsed.TotalSeconds:F1} s after the change, failure '{download.Failure}', reasons [{string.Join(", ", DownloadFailures(w))}]";
        TestContext.Current.SendDiagnosticMessage("CONS-23 " + observed);
        Assert.False(download.Completed, "CONS-23: the stream was not stopped. " + observed);
        Assert.True(after <= 16L * 1024 * 1024, "CONS-23: too much plaintext after the revocation. " + observed);
        Assert.True(sinceChange.Elapsed <= RecheckBound + DrainAllowance, observed);
        Assert.Single(DownloadFailures(w));
    }

    [Fact]
    public async Task AReplacementOfTheDocumentDuringTheDownloadStopsTheStream()
    {
        // The relevant revision: the streamed version is no longer the document's accepted current version.
        var w = await WorkspaceAsync("cons23-replacement", 233);
        using var download = await PausedAsync(w, w.S.Clerk);

        await w.S.UploadAsync(w.S.Clerk, w.Record, "cons23-replacement.bin", "application/octet-stream", StageBScenario.SyntheticBytes(10_000, 234), replacesFileAttachmentId: w.Attachment);
        var sinceChange = Stopwatch.StartNew();

        await AssertStoppedAsync(w, download, sinceChange, "replacement", reason => reason == "DOCUMENT_VERSION_CHANGED");
    }

    [Fact]
    public async Task AnUnavailableAuthorityDuringTheDownloadStopsTheStream()
    {
        var w = await WorkspaceAsync("cons23-authority-unavailable", 235);
        using var download = await PausedAsync(w, w.S.Clerk);
        Stopwatch sinceChange;
        try
        {
            fixture.Stack.Stop("authority");
            sinceChange = Stopwatch.StartNew();
            await AssertStoppedAsync(w, download, sinceChange, "authority unavailable", reason => reason == "AUTHORITY_UNAVAILABLE");
        }
        finally
        {
            fixture.Stack.Compose("up -d --wait authority", 240);
            // The restart drops the in-memory Testing contributor-writes override; re-arm it for the classes that follow.
            Assert.True((await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled = (bool?)true })).IsSuccess);
            await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(120));
        }

        Assert.Equal(HttpStatusCode.OK, (await fixture.DownloadAsync(DownloadPath(w.Record, w.Attachment), w.S.Token(w.S.Clerk))).Status);
    }
}
