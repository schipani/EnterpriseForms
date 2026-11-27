using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.Documents.Documents;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// CONS-23 contract (trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md "Long streams and strict revocation"; C-201): a streaming
/// download is re-checked at the Authority at most every configured interval before more plaintext is written, in bounded slices,
/// and stops for good on the first failed re-check. The re-check rule releases only the admitted document version.
/// </summary>
public sealed class Cons23DownloadRecheckTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => ticks;

        public void Advance(TimeSpan by) => ticks += by.Ticks;
    }

    /// <summary>A response body that records each write; <paramref name="onWrite"/> models a slow client (time passes inside a write).</summary>
    private sealed class RecordingSink(Action<int>? onWrite = null) : MemoryStream
    {
        public List<int> Writes { get; } = [];

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes.Add(buffer.Length);
            onWrite?.Invoke(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NoRecheckRunsWithinTheIntervalAndEveryWriteIsCutIntoBoundedSlices()
    {
        var clock = new ManualClock();
        var sink = new RecordingSink();
        var checks = 0;
        var stream = new RecheckedDownloadStream(sink, _ => { checks++; return Task.CompletedTask; }, Interval, clock, clock.GetTimestamp());

        await stream.WriteAsync(new byte[(3 * DocumentStreamOptions.MaximumSliceBytes) + 10], TestContext.Current.CancellationToken);

        Assert.Equal(0, checks);
        Assert.Equal([DocumentStreamOptions.MaximumSliceBytes, DocumentStreamOptions.MaximumSliceBytes, DocumentStreamOptions.MaximumSliceBytes, 10], sink.Writes);
    }

    [Fact]
    public async Task OnceTheIntervalHasPassedTheRecheckRunsBeforeTheNextSliceIsWritten()
    {
        var clock = new ManualClock();
        var sink = new RecordingSink();
        var events = new List<string>();
        var stream = new RecheckedDownloadStream(sink, _ => { events.Add("check@" + sink.Writes.Count); return Task.CompletedTask; }, Interval, clock, clock.GetTimestamp());

        await stream.WriteAsync(new byte[100], TestContext.Current.CancellationToken);
        clock.Advance(Interval); // the admission check started at timestamp 0
        await stream.WriteAsync(new byte[100], TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(4));
        await stream.WriteAsync(new byte[100], TestContext.Current.CancellationToken);

        Assert.Equal(["check@1"], events); // before the second write; not again 4 s later
        Assert.Equal(1, stream.RecheckCount);
    }

    [Fact]
    public async Task ASlowClientCannotCarryPlaintextPastTheIntervalWithinOneLargeWrite()
    {
        // Backpressure: each slice's write blocks for 6 s, so the decryptor's single large write is re-checked before every later slice.
        var clock = new ManualClock();
        var sink = new RecordingSink(_ => clock.Advance(TimeSpan.FromSeconds(6)));
        var checksBeforeWrite = new List<int>();
        var stream = new RecheckedDownloadStream(sink, _ => { checksBeforeWrite.Add(sink.Writes.Count); return Task.CompletedTask; }, Interval, clock, clock.GetTimestamp());

        await stream.WriteAsync(new byte[4 * DocumentStreamOptions.MaximumSliceBytes], TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], checksBeforeWrite);
        Assert.Equal(4, sink.Writes.Count);
    }

    [Fact]
    public async Task AFailedRecheckWritesNothingFurtherAndTheStreamStaysStopped()
    {
        var clock = new ManualClock();
        var sink = new RecordingSink(_ => clock.Advance(TimeSpan.FromSeconds(6)));
        var stream = new RecheckedDownloadStream(sink, _ => throw new DocumentStreamInterruptedException("DOWNLOAD_NOT_PERMITTED"), Interval, clock, clock.GetTimestamp());

        var stopped = await Assert.ThrowsAsync<DocumentStreamInterruptedException>(() => stream.WriteAsync(new byte[3 * DocumentStreamOptions.MaximumSliceBytes], TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("DOWNLOAD_NOT_PERMITTED", stopped.ReasonCode);
        Assert.Single(sink.Writes); // only the slice before the interval passed
        clock.Advance(-TimeSpan.FromSeconds(60)); // even with no interval elapsed, a stopped stream never writes again
        await Assert.ThrowsAsync<DocumentStreamInterruptedException>(() => stream.WriteAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<DocumentStreamInterruptedException>(() => stream.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Single(sink.Writes);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public async Task AnUnavailableAuthorityStopsTheStream()
    {
        var clock = new ManualClock();
        var sink = new RecordingSink();
        var stream = new RecheckedDownloadStream(sink, _ => throw new HttpRequestException("authority unreachable"), Interval, clock, clock.GetTimestamp());
        clock.Advance(Interval);

        await Assert.ThrowsAsync<HttpRequestException>(() => stream.WriteAsync(new byte[10], TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(sink.Writes);
        await Assert.ThrowsAsync<DocumentStreamInterruptedException>(() => stream.WriteAsync(new byte[10], TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void SynchronousWritesAreRefused()
    {
        var clock = new ManualClock();
        using var stream = new RecheckedDownloadStream(new MemoryStream(), _ => Task.CompletedTask, Interval, clock, clock.GetTimestamp());
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Flush());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void AnIntervalOutsideOneToFiveSecondsRefusesToStart(int seconds) =>
        Assert.Throws<InvalidOperationException>(() => new DocumentStreamOptions { StreamRecheckIntervalSeconds = seconds }.Validate());

    [Fact]
    public void TheDefaultIntervalIsTheDocumentedMaximum()
    {
        var options = new DocumentStreamOptions();
        options.Validate();
        Assert.Equal(TimeSpan.FromSeconds(5), options.RecheckInterval);
    }

    // ---- the re-check rule ----

    private static readonly Guid Attachment = Guid.CreateVersion7();
    private static readonly Guid AdmittedVersion = Guid.CreateVersion7();

    private static FileAttachmentVersion Version(Guid versionId) => new() { FileAttachmentId = Attachment, FileAttachmentVersionId = versionId, VersionNumber = 2 };

    private static VerifiedRecord Fresh(long authorizationRevision, bool complete, FileAttachmentVersion? attachment, bool downloadable = false)
    {
        var verification = new VerifyRecordProjectionResult(true, null, 7, Guid.CreateVersion7(), authorizationRevision, [], complete, Guid.CreateVersion7(),
            DownloadableItems: downloadable ? [new VerifiedProjectionItem(ManifestItemKind.Attachment, Attachment)] : []);
        return new VerifiedRecord(null!, verification, null!, [], attachment is null ? [] : [attachment], new FormDefinition(), new FormVersion(), new FormLayout(2, "Test", []), [], [], null!, [], []);
    }

    private static readonly DocumentStreamAdmission Current = new(Attachment, AdmittedVersion, HistoricVersion: false, AuthorizationRevision: 40);
    private static readonly DocumentStreamAdmission Historic = new(Attachment, AdmittedVersion, HistoricVersion: true, AuthorizationRevision: 40);

    [Fact]
    public void TheAdmittedVersionStillReleasedContinuesAtTheSameOrALaterAuthorizationRevision()
    {
        Assert.Null(Current.Evaluate(Fresh(40, complete: true, Version(AdmittedVersion))));
        Assert.Null(Current.Evaluate(Fresh(41, complete: true, Version(AdmittedVersion)))); // an unrelated change that still releases it
        Assert.Null(Current.Evaluate(Fresh(41, complete: false, Version(AdmittedVersion), downloadable: true))); // a share recipient whose binding allows download
        Assert.Null(Historic.Evaluate(Fresh(41, complete: true, Version(Guid.CreateVersion7()))));
    }

    [Fact]
    public void EveryChangeThatNoLongerReleasesTheAdmittedVersionStopsTheStream()
    {
        Assert.Equal("AUTHORIZATION_REVISION_REGRESSED", Current.Evaluate(Fresh(39, complete: true, Version(AdmittedVersion))));
        Assert.Equal("DOCUMENT_NOT_RELEASED", Current.Evaluate(Fresh(41, complete: true, null))); // removed, or not in the recipient's scope
        Assert.Equal("DOCUMENT_VERSION_CHANGED", Current.Evaluate(Fresh(41, complete: true, Version(Guid.CreateVersion7())))); // replaced
        Assert.Equal("DOWNLOAD_NOT_PERMITTED", Current.Evaluate(Fresh(41, complete: false, Version(AdmittedVersion), downloadable: false))); // view-only binding
        Assert.Equal("FULL_READ_REQUIRED", Historic.Evaluate(Fresh(41, complete: false, Version(AdmittedVersion), downloadable: true)));
        Assert.Equal("DOCUMENT_NOT_RELEASED", Historic.Evaluate(Fresh(41, complete: true, null)));
    }

    // ---- the browser session and the failure classification (independent review findings 2 and 8b) ----

    private sealed class Sessions(bool valid, Exception? failure = null) : IBrowserSessionStateAuthority
    {
        public int Validations { get; private set; }

        public Task<BrowserSessionState> CreateSessionAsync(DelegatedUserContext user, string sessionDigestBase64, DateTimeOffset absoluteExpiresOn, string? replacedSessionDigestBase64, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrowserSessionState> ValidateSessionAsync(DelegatedUserContext user, string sessionDigestBase64, CancellationToken cancellationToken)
        {
            Validations++;
            return failure is null ? Task.FromResult(new BrowserSessionState(valid, valid ? null : "REVOKED", null, Guid.CreateVersion7())) : Task.FromException<BrowserSessionState>(failure);
        }

        public Task<BrowserSessionState> RevokeSessionAsync(DelegatedUserContext user, string sessionDigestBase64, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static RequestContext Context(string? sessionDigest) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "https://issuer.test", "subject", "Test", "assertion", Guid.CreateVersion7()) { SessionDigestBase64 = sessionDigest };

    [Fact]
    public async Task ARevokedOrExpiredBrowserSessionStopsTheStream()
    {
        var sessions = new Sessions(valid: false);
        var stopped = await Assert.ThrowsAsync<DocumentStreamInterruptedException>(() => DocumentStreamRechecks.RequireValidSessionAsync(sessions, Context("digest"), TestContext.Current.CancellationToken));
        Assert.Equal(DocumentStreamRechecks.SessionInvalid, stopped.ReasonCode);
        Assert.Equal(1, sessions.Validations);
    }

    [Fact]
    public async Task AValidSessionContinuesAndABearerRequestHasNoSessionToCheck()
    {
        var sessions = new Sessions(valid: true);
        await DocumentStreamRechecks.RequireValidSessionAsync(sessions, Context("digest"), TestContext.Current.CancellationToken);
        await DocumentStreamRechecks.RequireValidSessionAsync(sessions, Context(null), TestContext.Current.CancellationToken);
        Assert.Equal(1, sessions.Validations);
    }

    [Fact]
    public async Task ASessionRegistryThatCannotAnswerFailsTheRecheck()
    {
        var sessions = new Sessions(valid: true, new HttpRequestException("unreachable"));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => DocumentStreamRechecks.RequireValidSessionAsync(sessions, Context("digest"), TestContext.Current.CancellationToken));
        Assert.Equal(DocumentStreamRechecks.AuthorityUnavailable, DocumentStreamRechecks.Classify(failure, CancellationToken.None)!.ReasonCode);
    }

    [Fact]
    public void EveryRecheckFailureBecomesAnInterruptionExceptTheRequestsOwnCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Null(DocumentStreamRechecks.Classify(new OperationCanceledException(cancelled.Token), cancelled.Token));
        Assert.Equal(DocumentStreamRechecks.AuthorityUnavailable, DocumentStreamRechecks.Classify(new TaskCanceledException("authority timeout"), CancellationToken.None)!.ReasonCode); // a timeout, not the client
        Assert.Equal("INTEGRITY_VERIFICATION_FAILED", DocumentStreamRechecks.Classify(new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423), CancellationToken.None)!.ReasonCode);
        Assert.Equal("PERMISSION_DENIED", DocumentStreamRechecks.Classify(new AuthorityClientException("PERMISSION_DENIED", 403, retryable: false), CancellationToken.None)!.ReasonCode);
        Assert.Equal(DocumentStreamRechecks.AuthorityUnavailable, DocumentStreamRechecks.Classify(new HttpRequestException("down"), CancellationToken.None)!.ReasonCode);
        var already = new DocumentStreamInterruptedException(DocumentStreamRechecks.SessionInvalid);
        Assert.Same(already, DocumentStreamRechecks.Classify(already, CancellationToken.None));
    }
}
