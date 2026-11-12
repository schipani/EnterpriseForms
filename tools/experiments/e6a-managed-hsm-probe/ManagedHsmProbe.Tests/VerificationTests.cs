using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace EnterpriseForms.Experiments.ManagedHsmProbe.Tests;

/// <summary>
/// Verification semantics. The E-6b run reported every verify check as failed because the service answers a non-matching
/// signature with HTTP 400 RET_SIGNATURE_VERIFICATION_FAILED. These tests pin down: a genuine signature must verify; a tampered or
/// other-version signature must be rejected, and only a SPECIFIC rejection counts; everything else keeps its strict classification.
/// The fake models behaviour hypotheses; it is not evidence of how the real service behaves.
/// </summary>
public sealed class VerificationTests
{
    private static HsmCallResult Result(HsmOutcome outcome, int? status = null, string? code = null, bool? verified = null) =>
        new(outcome, status, code) { Verified = verified };

    // ---- the interpreter: every row of the truth table ----

    [Theory]
    [InlineData(true, true, "Pass")]
    [InlineData(true, false, "Fail")]
    [InlineData(false, true, "Fail")]
    [InlineData(false, false, "Pass")]
    public void BooleanAnswersAreJudgedAgainstTheExpectation(bool verified, bool expectAccepted, string expectedName)
    {
        var (verdict, _) = HsmExperiments.InterpretVerify(Result(HsmOutcome.Success, 200, verified: verified), expectAccepted);
        Assert.Equal(Enum.Parse<Verdict>(expectedName), verdict);
    }

    [Fact]
    public void ARejectionOfAGenuineSignatureIsAlwaysAFail()
    {
        var (verdict, detail) = HsmExperiments.InterpretVerify(Result(HsmOutcome.VerificationFailed, 400, ManagedHsmRestClient.SignatureVerificationFailedCode, false), expectAccepted: true);
        Assert.Equal(Verdict.Fail, verdict);
        Assert.Contains("must verify was REJECTED", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSpecificRejectionIsAPassOnlyWhenRejectionWasExpected()
    {
        var (verdict, detail) = HsmExperiments.InterpretVerify(Result(HsmOutcome.VerificationFailed, 400, ManagedHsmRestClient.SignatureVerificationFailedCode, false), expectAccepted: false);
        Assert.Equal(Verdict.Pass, verdict);
        Assert.Contains("RET_SIGNATURE_VERIFICATION_FAILED", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Unauthenticated", 401, "Inconclusive")]
    [InlineData("Forbidden", 403, "Inconclusive")]
    [InlineData("Throttled", 429, "Inconclusive")]
    [InlineData("ServiceError", 500, "Inconclusive")]
    [InlineData("Timeout", null, "Inconclusive")]
    [InlineData("Malformed", 200, "Inconclusive")]
    [InlineData("Rejected", 400, "Fail")]
    [InlineData("NotFound", 404, "Fail")]
    [InlineData("WrongKeyIdentifier", 200, "Fail")]
    public void NoOtherErrorIsEverAnExpectedNegative(string outcomeName, int? status, string expectedName)
    {
        var outcome = Enum.Parse<HsmOutcome>(outcomeName);
        var expected = Enum.Parse<Verdict>(expectedName);
        Assert.Equal(expected, HsmExperiments.InterpretVerify(Result(outcome, status), expectAccepted: false).Verdict);
        Assert.Equal(expected, HsmExperiments.InterpretVerify(Result(outcome, status), expectAccepted: true).Verdict);
    }

    // ---- the REST client: only the exact status AND code become VerificationFailed ----

    [Fact]
    public async Task OnlyHttp400WithTheExactCodeBecomesAVerificationFailure()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var client = TestSetup.Client(hsm, configuration);
        var identifier = configuration.KeyVersionIdentifiers["1"];
        var input = new byte[] { 1, 2, 3 };
        var mac = HMACSHA256.HashData(TestKeys.Get(TestKeys.TestKey1Id), input);
        var tampered = (byte[])mac.Clone();
        tampered[0] ^= 0xFF;

        hsm.VerifyStyle = FakeVerifyStyle.Http400OnMismatch;
        var good = await client.VerifyAsync(identifier, input, mac, TestContext.Current.CancellationToken);
        var bad = await client.VerifyAsync(identifier, input, tampered, TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Success, good.Outcome);
        Assert.True(good.Verified);
        Assert.Equal(HsmOutcome.VerificationFailed, bad.Outcome);
        Assert.Equal(400, bad.HttpStatus);
        Assert.False(bad.Verified);

        hsm.VerifyStyle = FakeVerifyStyle.OtherBadRequest;
        Assert.Equal(HsmOutcome.Rejected, (await client.VerifyAsync(identifier, input, mac, TestContext.Current.CancellationToken)).Outcome);
        hsm.VerifyStyle = FakeVerifyStyle.SameCodeWrongStatus;
        Assert.Equal(HsmOutcome.Rejected, (await client.VerifyAsync(identifier, input, tampered, TestContext.Current.CancellationToken)).Outcome);
        hsm.VerifyStyle = FakeVerifyStyle.ServerError;
        Assert.Equal(HsmOutcome.ServiceError, (await client.VerifyAsync(identifier, input, mac, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task AuthenticationAuthorizationAndMalformedAnswersStayStrictOnVerify()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var client = TestSetup.Client(hsm, configuration);
        var identifier = configuration.KeyVersionIdentifiers["1"];
        var input = new byte[] { 9 };
        var mac = HMACSHA256.HashData(TestKeys.Get(TestKeys.TestKey1Id), input);

        hsm.ForcedStatus = HttpStatusCode.Forbidden;
        var forbidden = await client.VerifyAsync(identifier, input, mac, TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Forbidden, forbidden.Outcome);
        Assert.Equal(Verdict.Inconclusive, HsmExperiments.InterpretVerify(forbidden, expectAccepted: false).Verdict);

        hsm.ForcedStatus = HttpStatusCode.Unauthorized;
        var unauthorized = await client.VerifyAsync(identifier, input, mac, TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Unauthenticated, unauthorized.Outcome);

        hsm.ForcedStatus = null;
        hsm.MalformedBody = true;
        var malformed = await client.VerifyAsync(identifier, input, mac, TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Malformed, malformed.Outcome);
        Assert.Equal(Verdict.Inconclusive, HsmExperiments.InterpretVerify(malformed, expectAccepted: false).Verdict);
    }

    // ---- whole-experiment behaviour ----

    private static bool IsVerify(ProbeCheck check) =>
        check.CheckId.StartsWith("A-verify-", StringComparison.Ordinal) || check.CheckId.StartsWith("B-verify-", StringComparison.Ordinal);

    [Fact]
    public async Task AServiceThatRejectsOnlyBadSignaturesWithTheSpecificErrorPassesEveryVerification()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.VerifyStyle = FakeVerifyStyle.Http400OnMismatch;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        var verifies = report.Checks.Where(c => IsVerify(c) || c.CheckId.StartsWith("V-own-verify-", StringComparison.Ordinal) || c.CheckId.StartsWith("V-cross-verify-", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(verifies);
        Assert.All(verifies, c => Assert.Equal(Verdict.Pass, c.Verdict));
        Assert.Contains(verifies, c => c.Detail.Contains("rejected, as expected (HTTP 400 RET_SIGNATURE_VERIFICATION_FAILED)", StringComparison.Ordinal));
        Assert.Equal(Verdict.Pass, report.OverallVerdict);
    }

    [Fact]
    public async Task AServiceThatRejectsGenuineSignaturesFailsEveryVerificationAndTheRun()
    {
        // The E-6b observation: HTTP 400 RET_SIGNATURE_VERIFICATION_FAILED for EVERYTHING, genuine signatures included.
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.VerifyStyle = FakeVerifyStyle.Http400Always;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        var verifies = report.Checks.Where(IsVerify).ToList();
        Assert.NotEmpty(verifies);
        Assert.All(verifies, c =>
        {
            Assert.Equal(Verdict.Fail, c.Verdict);
            Assert.Contains("must verify was REJECTED", c.Detail, StringComparison.Ordinal);
            Assert.Contains("no evidence while the genuine MAC is not accepted", c.Detail, StringComparison.Ordinal);
        });
        Assert.All(report.Checks.Where(c => c.CheckId.StartsWith("V-own-verify-", StringComparison.Ordinal)), c => Assert.Equal(Verdict.Fail, c.Verdict));
        // Signing evidence is untouched: the MAC checks still pass, but the run as a whole does not.
        Assert.Contains(report.Checks, c => c.CheckId == "B-sign-len-00032" && c.Verdict == Verdict.Pass);
        Assert.Equal(Verdict.Fail, report.OverallVerdict);
    }

    [Fact]
    public async Task CrossVersionRejectionWithoutAWorkingPositiveControlIsNeverAPass()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.VerifyStyle = FakeVerifyStyle.Http400Always;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        var cross = report.Checks.Single(c => c.CheckId.StartsWith("V-cross-verify-", StringComparison.Ordinal));
        Assert.NotEqual(Verdict.Pass, cross.Verdict);
        Assert.Contains("not conclusive", cross.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServiceThatAcceptsTamperedAndCrossVersionSignaturesFails()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.VerifyStyle = FakeVerifyStyle.AcceptAll;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.All(report.Checks.Where(IsVerify), c => Assert.Equal(Verdict.Fail, c.Verdict));
        Assert.All(report.Checks.Where(c => c.CheckId.StartsWith("V-cross-verify-", StringComparison.Ordinal)), c => Assert.Equal(Verdict.Fail, c.Verdict));
        Assert.Equal(Verdict.Fail, report.OverallVerdict);
    }

    [Theory]
    [InlineData("OtherBadRequest", "Fail")]
    [InlineData("SameCodeWrongStatus", "Fail")]
    [InlineData("ServerError", "Inconclusive")]
    public async Task UnexpectedVerifyErrorsAreNeverTreatedAsExpectedNegatives(string styleName, string expectedName)
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.VerifyStyle = Enum.Parse<FakeVerifyStyle>(styleName);
        var expected = Enum.Parse<Verdict>(expectedName);
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        var verifies = report.Checks.Where(IsVerify).ToList();
        Assert.NotEmpty(verifies);
        Assert.All(verifies, c =>
        {
            Assert.NotEqual(Verdict.Pass, c.Verdict);
            Assert.DoesNotContain("as expected", c.Detail.Replace("accepted, as expected", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        });
        Assert.All(report.Checks.Where(c => c.CheckId.StartsWith("A-verify-", StringComparison.Ordinal)).Take(1), c => Assert.Equal(expected, c.Verdict));
        Assert.NotEqual(Verdict.Pass, report.OverallVerdict);
    }

    // ---- raw-input limits observed in E-6b stay visible ----

    [Fact]
    public async Task ZeroLengthAndOversizedRawInputStayRejectedAndTheExactBytesAreSent()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.VerifyStyle = FakeVerifyStyle.Http400OnMismatch;
        hsm.RejectEmptyValue = true;
        hsm.MaxValueBytes = 65535;
        hsm.OversizeStatus = HttpStatusCode.RequestEntityTooLarge;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);

        var empty = report.Checks.Single(c => c.CheckId == "B-sign-len-00000");
        var large = report.Checks.Single(c => c.CheckId == "B-sign-len-65536");
        Assert.Equal(Verdict.Fail, empty.Verdict);
        Assert.Equal(Verdict.Fail, large.Verdict);
        Assert.Contains("HTTP 400", empty.Detail, StringComparison.Ordinal);
        Assert.Contains("HTTP 413", large.Detail, StringComparison.Ordinal);
        Assert.Equal(Verdict.Fail, report.ExperimentVerdicts[HsmExperiments.RawInput]);
        Assert.Contains(report.Notes, n => n.Contains("rejected: [0, 65536]", StringComparison.Ordinal));

        // Nothing was digested, truncated or padded to make those cases pass: the empty value and the full 64 KiB were sent verbatim, once each.
        var signBodies = hsm.Requests.Where(r => r.PathAndQuery.Contains("/sign?", StringComparison.Ordinal)).Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("value").GetString()!).ToList();
        Assert.Contains(string.Empty, signBodies);
        Assert.Single(signBodies, v => v.Length == (65536 * 4 / 3) + (65536 % 3 == 0 ? 0 : 1));
        // Accepted lengths remain accepted and verified.
        Assert.Equal(Verdict.Pass, report.Checks.Single(c => c.CheckId == "B-sign-len-04096").Verdict);
        Assert.Equal(Verdict.Pass, report.Checks.Single(c => c.CheckId == "B-verify-len-04096").Verdict);
        // No verification is attempted for an input the service refused to sign.
        Assert.DoesNotContain(report.Checks, c => c.CheckId is "B-verify-len-00000" or "B-verify-len-65536");
    }

    // ---- the observed Azure shape: HTTP 400, code BadParameter, token in the MESSAGE ----

    [Theory]
    [InlineData(400, "BadParameter", "Signature verification failed: RET_SIGNATURE_VERIFICATION_FAILED", true)]
    [InlineData(400, "BadParameter", "RET_SIGNATURE_VERIFICATION_FAILED", true)]
    [InlineData(400, "BadParameter", "(RET_SIGNATURE_VERIFICATION_FAILED).", true)]
    [InlineData(400, "BadParameter", "XRET_SIGNATURE_VERIFICATION_FAILED", false)]
    [InlineData(400, "BadParameter", "RET_SIGNATURE_VERIFICATION_FAILED_2", false)]
    [InlineData(400, "BadParameter", "ret_signature_verification_failed", false)]
    [InlineData(400, "BadParameter", "digest length is wrong", false)]
    [InlineData(400, "BadParameter", null, false)]
    [InlineData(400, "RET_SIGNATURE_VERIFICATION_FAILED", "signature verification failed", false)]
    [InlineData(400, "Forbidden", "RET_SIGNATURE_VERIFICATION_FAILED", false)]
    [InlineData(409, "BadParameter", "RET_SIGNATURE_VERIFICATION_FAILED", false)]
    [InlineData(413, "BadParameter", "RET_SIGNATURE_VERIFICATION_FAILED", false)]
    [InlineData(500, "BadParameter", "RET_SIGNATURE_VERIFICATION_FAILED", false)]
    [InlineData(null, "BadParameter", "RET_SIGNATURE_VERIFICATION_FAILED", false)]
    public void OnlyTheExactStatusCodeAndDelimitedTokenAreRecognized(int? status, string? code, string? message, bool expected) =>
        Assert.Equal(expected, ManagedHsmRestClient.IsSignatureVerificationFailure(status, code, message));

    [Fact]
    public async Task TheObservedAzureShapeIsAVerificationFailureForVerifyOnly()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var client = TestSetup.Client(hsm, configuration);
        var identifier = configuration.KeyVersionIdentifiers["1"];
        var input = new byte[] { 7, 7 };
        var mac = HMACSHA256.HashData(TestKeys.Get(TestKeys.TestKey1Id), input);
        var tampered = (byte[])mac.Clone();
        tampered[^1] ^= 1;

        hsm.VerifyStyle = FakeVerifyStyle.Http400OnMismatch;
        var bad = await client.VerifyAsync(identifier, input, tampered, TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.VerificationFailed, bad.Outcome);
        Assert.Equal("BadParameter", bad.ErrorCode);
        Assert.Contains(ManagedHsmRestClient.SignatureVerificationFailedCode, bad.ErrorMessage, StringComparison.Ordinal);

        // The very same error returned by every other operation stays a plain refusal.
        hsm.VerifyStyle = FakeVerifyStyle.BooleanFalse;
        hsm.ForcedError = (HttpStatusCode.BadRequest, "BadParameter", FakeManagedHsm.VerificationFailedMessage);
        var sign = await client.SignAsync(identifier, input, TestContext.Current.CancellationToken);
        var get = await client.GetKeyAsync(identifier, TestContext.Current.CancellationToken);
        var import = await client.ImportOctKeyAsync(new byte[32], TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Rejected, sign.Outcome);
        Assert.Equal(HsmOutcome.Rejected, get.Outcome);
        Assert.Equal(HsmOutcome.Rejected, import.Outcome);
        Assert.All(new[] { sign, get, import }, r => Assert.Equal(Verdict.Fail, HsmExperiments.Classify(r)));
    }

    [Theory]
    [InlineData("TokenUnderWrongCode")]
    [InlineData("TokenOnlyAsSubstring")]
    [InlineData("OtherBadRequest")]
    [InlineData("SameCodeWrongStatus")]
    public async Task LookAlikeErrorsAreNotExpectedNegatives(string styleName)
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.VerifyStyle = Enum.Parse<FakeVerifyStyle>(styleName);
        var client = TestSetup.Client(hsm, configuration);
        var input = new byte[] { 5 };
        var tampered = HMACSHA256.HashData(TestKeys.Get(TestKeys.TestKey1Id), input);
        tampered[0] ^= 1;
        var result = await client.VerifyAsync(configuration.KeyVersionIdentifiers["1"], input, tampered, TestContext.Current.CancellationToken);
        Assert.NotEqual(HsmOutcome.VerificationFailed, result.Outcome);
        Assert.NotEqual(Verdict.Pass, HsmExperiments.InterpretVerify(result, expectAccepted: false).Verdict);
    }

    [Theory]
    [InlineData(401, "Unauthorized", "Unauthenticated", "Inconclusive")]
    [InlineData(403, "Forbidden", "Forbidden", "Inconclusive")]
    [InlineData(404, "KeyNotFound", "NotFound", "Fail")]
    [InlineData(409, "Conflict", "Rejected", "Fail")]
    [InlineData(429, "Throttled", "Throttled", "Inconclusive")]
    [InlineData(500, "InternalError", "ServiceError", "Inconclusive")]
    [InlineData(503, "Unavailable", "ServiceError", "Inconclusive")]
    public async Task EveryOtherStatusKeepsItsStrictClassificationOnVerify(int status, string code, string outcomeName, string verdictName)
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.ForcedError = ((HttpStatusCode)status, code, "forced by the test");
        var client = TestSetup.Client(hsm, configuration);
        var outcome = Enum.Parse<HsmOutcome>(outcomeName);
        var verdict = Enum.Parse<Verdict>(verdictName);
        var result = await client.VerifyAsync(configuration.KeyVersionIdentifiers["1"], new byte[] { 1 }, new byte[32], TestContext.Current.CancellationToken);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(verdict, HsmExperiments.InterpretVerify(result, expectAccepted: false).Verdict);
        Assert.Equal(verdict, HsmExperiments.InterpretVerify(result, expectAccepted: true).Verdict);
    }

    [Fact]
    public async Task GenuineAndNegativeVerificationAreJudgedIndependentlyAndBothDirectionsOfCrossVersionRejectionAreChecked()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.VerifyStyle = FakeVerifyStyle.Http400OnMismatch;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.All(report.Checks.Where(c => c.CheckId.StartsWith("A-verify-", StringComparison.Ordinal)), c =>
        {
            Assert.Equal(Verdict.Pass, c.Verdict);
            Assert.Contains("genuine: accepted, as expected; tampered: rejected, as expected", c.Detail, StringComparison.Ordinal);
        });
        var own = report.Checks.Single(c => c.CheckId.StartsWith("V-own-verify-", StringComparison.Ordinal));
        var cross = report.Checks.Single(c => c.CheckId.StartsWith("V-cross-verify-", StringComparison.Ordinal));
        Assert.Equal(Verdict.Pass, own.Verdict);
        Assert.Equal(Verdict.Pass, cross.Verdict);
        Assert.Contains("other version's MAC: rejected, as expected", cross.Detail, StringComparison.Ordinal);
        Assert.Contains("reverse: rejected, as expected", cross.Detail, StringComparison.Ordinal);

        // Both directions are really sent: v1 MAC under v2 and v2 MAC under v1, besides the two own-version controls.
        var verifyCalls = hsm.Requests.Count(r => r.PathAndQuery.Contains("/verify?", StringComparison.Ordinal));
        Assert.True(verifyCalls >= 4);
    }

    [Fact]
    public async Task OneFailingOwnVersionControlPreventsAPassingCrossVersionVerdict()
    {
        // The service accepts nothing for version 2's own MAC but behaves for version 1: model it by a service that rejects everything.
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.VerifyStyle = FakeVerifyStyle.Http400Always;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        var own = report.Checks.Single(c => c.CheckId.StartsWith("V-own-verify-", StringComparison.Ordinal));
        var cross = report.Checks.Single(c => c.CheckId.StartsWith("V-cross-verify-", StringComparison.Ordinal));
        Assert.Equal(Verdict.Fail, own.Verdict);
        Assert.NotEqual(Verdict.Pass, cross.Verdict);
        Assert.Equal(Verdict.Fail, report.OverallVerdict);
    }
}
