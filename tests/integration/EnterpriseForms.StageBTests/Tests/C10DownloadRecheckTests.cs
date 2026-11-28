using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// CONS-23 on the real stack (owner decision 1, C-201; trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md "Long streams and strict
/// revocation"): "Stream terminates within documented baseline recheck bound". A 19 MiB encrypted document is downloaded from inside
/// the Docker network (<see cref="InNetworkDownload"/>: only socket buffers between Kestrel and the client; from the Windows host,
/// Docker Desktop's port forwarder reads ahead by many megabytes). The server re-checks at the Authority, at most every 5 s and before
/// the next slice of plaintext, and stops the stream, audited as DOCUMENT_DOWNLOAD_FAILED, on a revocation, a replacement of the
/// document (a change of its accepted version) or an unavailable Authority; the server-side stop instant is the audit event's time.
/// An undisturbed slow download completes byte for byte through its re-checks. Bytes already in the socket buffers when access ends
/// are the protocol's acknowledged finite exposure window and are reported.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class C10DownloadRecheckTests(StageBFixture fixture)
{
    private const int DocumentBytes = 19 * 1024 * 1024; // the Accused documents field accepts up to 20 MiB
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan RecheckBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckAndClockAllowance = TimeSpan.FromSeconds(5); // one re-check's own duration plus host/VM clock skew

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

    /// <summary>The DOCUMENT_DOWNLOAD_FAILED events of the record: reason code and server time.</summary>
    private (string Reason, DateTimeOffset At)[] DownloadFailures(Workspace w) =>
        [.. fixture.Stack.AttackerSql($"SELECT (event_metadata_json->>'reasonCode') || '|' || to_char(event_occurred_on AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM audit_events WHERE tenant_id = '{w.S.OrganizationId:D}' AND form_record_id = '{w.Record:D}' AND event_action = 'DOCUMENT_DOWNLOAD_FAILED' ORDER BY audit_sequence;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|'))
            .Select(parts => (parts[0], DateTimeOffset.Parse(parts[1], CultureInfo.InvariantCulture)))];

    /// <summary>Opens the download in the network, reads 1 MiB, then stops reading long enough for the server's writes to block.</summary>
    private async Task<InNetworkDownload> PausedAsync(Workspace w, TestUser user)
    {
        var download = InNetworkDownload.Start(fixture.Stack, DownloadPath(w.Record, w.Attachment), w.S.Token(user), pauseAfterBytes: 1024 * 1024, secondsPer64KiB: null);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (download.Received() < 1024 * 1024)
        {
            Assert.True(DateTime.UtcNow < deadline, "the in-network download did not start");
            await Task.Delay(250);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        return download;
    }

    /// <summary>
    /// After the access change: the body ends short, exactly one DOCUMENT_DOWNLOAD_FAILED with the expected reason is audited, and the
    /// server stopped within the re-check bound of the later of the change and the client resuming (a blocked write releases nothing).
    /// </summary>
    private async Task AssertStoppedAsync(Workspace w, InNetworkDownload download, DateTimeOffset changedAt, long receivedAtChange, string context, Func<string, bool> expectedReason, bool resume = true)
    {
        var resumedAt = DateTimeOffset.UtcNow;
        if (resume)
        {
            await download.ResumeAsync();
        }

        var total = await download.TotalAsync();
        var failures = DownloadFailures(w);
        var observed = $"{context}: received {total} of {DocumentBytes} bytes ({total - receivedAtChange} after the change); failures [{string.Join(", ", failures.Select(f => $"{f.Reason} at +{(f.At - changedAt).TotalSeconds:F1} s"))}]";
        TestContext.Current.SendDiagnosticMessage("CONS-23 " + observed);
        Assert.True(total < DocumentBytes, "CONS-23: the stream was not stopped. " + observed);
        Assert.True(failures.Length == 1 && expectedReason(failures[0].Reason), $"expected one DOCUMENT_DOWNLOAD_FAILED with the {context} reason. {observed}");
        var reference = resume ? (resumedAt > changedAt ? resumedAt : changedAt) : changedAt;
        Assert.True(failures[0].At - reference <= RecheckBound + CheckAndClockAllowance, "CONS-23: the server did not stop within the re-check bound. " + observed);
    }

    private static bool DenialReason(string reason) => reason.Length > 0 && reason is not ("AUTHORITY_UNAVAILABLE" or "INTEGRITY_VERIFICATION_FAILED");

    [Fact]
    public async Task AnUndisturbedSlowDownloadCompletesByteForByteThroughItsRechecks()
    {
        var w = await WorkspaceAsync("cons23-uninterrupted", 231);
        await ShareDocumentAsync(w);
        var started = Stopwatch.StartNew();
        using var download = await fixture.StartPausedDownloadAsync(DownloadPath(w.Record, w.Attachment), w.S.Token(w.S.Reader), 1024 * 1024);
        await download.ReadRestAsync(bytesPerSecond: 3 * 1024 * 1024 / 2); // about 12 s: two or more re-checks at the 5 s interval

        Assert.True(download.Completed, $"received {download.Received} of {DocumentBytes}, failure '{download.Failure}'");
        Assert.True(started.Elapsed > TimeSpan.FromSeconds(10), "the download was not slow enough to cross a re-check");
        Assert.Equal(SHA256.HashData(w.Bytes), download.Sha256);
        Assert.Equal("no", download.Header("X-Accel-Buffering")); // a buffering proxy must not read ahead of the re-checks
        Assert.Empty(DownloadFailures(w));
    }

    [Fact]
    public async Task ARevocationDuringTheDownloadStopsTheStreamWithinTheRecheckBound()
    {
        var w = await WorkspaceAsync("cons23-revocation", 232);
        var share = await ShareDocumentAsync(w);
        var download = await PausedAsync(w, w.S.Reader);
        var receivedAtChange = download.Received();

        var revoked = await fixture.Api.PostAsync($"/api/v1/form-records/{w.Record:D}/shares/{share:D}/revoke?useDevelopmentStepUp=true", w.S.Token(w.S.Clerk), new { });
        Assert.True(revoked.IsSuccess, revoked.Body);
        var changedAt = DateTimeOffset.UtcNow;
        await w.S.WaitGateReadyAsync(Settle);

        await AssertStoppedAsync(w, download, changedAt, receivedAtChange, "revocation", DenialReason);
    }

    [Fact]
    public async Task ARevocationWhileTheClientIsReadingStopsTheStreamWithinABoundedAmountOfPlaintext()
    {
        // Independent review finding 8a/8c: the client keeps reading (512 KiB/s) through the revocation, so the stop comes from the
        // periodic re-check, not from a check on resume. The server must stop within the re-check bound of the revocation, and the
        // plaintext read after it is bounded by that interval at the read rate plus the socket buffers (asserted at 8 MiB of 19 MiB).
        var w = await WorkspaceAsync("cons23-revocation-active", 236);
        var share = await ShareDocumentAsync(w);
        var download = InNetworkDownload.Start(fixture.Stack, DownloadPath(w.Record, w.Attachment), w.S.Token(w.S.Reader), pauseAfterBytes: null, secondsPer64KiB: 0.125);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        var revoked = await fixture.Api.PostAsync($"/api/v1/form-records/{w.Record:D}/shares/{share:D}/revoke?useDevelopmentStepUp=true", w.S.Token(w.S.Clerk), new { });
        Assert.True(revoked.IsSuccess, revoked.Body);
        var changedAt = DateTimeOffset.UtcNow;
        var receivedAtChange = download.Received();
        await w.S.WaitGateReadyAsync(Settle);

        await AssertStoppedAsync(w, download, changedAt, receivedAtChange, "active revocation", DenialReason, resume: false);
        var total = await download.TotalAsync();
        Assert.True(total - receivedAtChange <= 8L * 1024 * 1024, $"CONS-23: {total - receivedAtChange} bytes of plaintext after the revocation");
    }

    [Fact]
    public async Task AReplacementOfTheDocumentDuringTheDownloadStopsTheStream()
    {
        // The relevant revision: the streamed version is no longer the document's accepted current version.
        var w = await WorkspaceAsync("cons23-replacement", 233);
        var download = await PausedAsync(w, w.S.Clerk);
        var receivedAtChange = download.Received();

        await w.S.UploadAsync(w.S.Clerk, w.Record, "cons23-replacement.bin", "application/octet-stream", StageBScenario.SyntheticBytes(10_000, 234), replacesFileAttachmentId: w.Attachment);
        var changedAt = DateTimeOffset.UtcNow;

        await AssertStoppedAsync(w, download, changedAt, receivedAtChange, "replacement", reason => reason == "DOCUMENT_VERSION_CHANGED");
    }

    [Fact]
    public async Task AnUnavailableAuthorityDuringTheDownloadStopsTheStream()
    {
        var w = await WorkspaceAsync("cons23-authority-unavailable", 235);
        var download = await PausedAsync(w, w.S.Clerk);
        var receivedAtChange = download.Received();
        try
        {
            fixture.Stack.Stop("authority");
            var changedAt = DateTimeOffset.UtcNow;
            await AssertStoppedAsync(w, download, changedAt, receivedAtChange, "authority unavailable", reason => reason == "AUTHORITY_UNAVAILABLE");
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
