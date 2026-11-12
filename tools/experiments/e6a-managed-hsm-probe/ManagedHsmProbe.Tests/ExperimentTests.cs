using System.Net;
using Xunit;

namespace EnterpriseForms.Experiments.ManagedHsmProbe.Tests;

/// <summary>
/// The experiment verdict logic against each modelled service behaviour. These tests show that the probe distinguishes the
/// hypotheses; they say nothing about which one real Managed HSM implements.
/// </summary>
public sealed class ExperimentTests
{
    [Fact]
    public async Task IfTheServiceMacsTheRawValueEveryExperimentPassesWithKnownKeys()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.All(report.ExperimentVerdicts, v => Assert.Equal(Verdict.Pass, v.Value));
        Assert.Equal(Verdict.Pass, report.OverallVerdict);
        Assert.Contains(report.Checks, c => c.CheckId == "B-sign-len-65536" && c.Verdict == Verdict.Pass);
        Assert.Contains(report.Checks, c => c.CheckId == "B-sign-row-ROWV1-FORM-RECORD-USER-OWNED" && c.Verdict == Verdict.Pass);
    }

    [Fact]
    public async Task IfTheServiceHashesWhateverItReceivesBothPathsFailAndBExplainsTheHashing()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.Semantics = FakeSemantics.HashThenHmac;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Verdict.Fail, report.ExperimentVerdicts[HsmExperiments.DigestPath]);
        Assert.Equal(Verdict.Fail, report.ExperimentVerdicts[HsmExperiments.RawInput]);
        Assert.All(report.Checks.Where(c => c.CheckId.StartsWith("B-sign-", StringComparison.Ordinal)),
            c => Assert.Contains("hashed the input first", c.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task IfOnlyDigestLengthInputsAreAcceptedAPassAt32BytesIsNotGeneralized()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.Semantics = FakeSemantics.DigestLengthOnly;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Verdict.Pass, report.ExperimentVerdicts[HsmExperiments.DigestPath]);
        Assert.Equal(Verdict.Fail, report.ExperimentVerdicts[HsmExperiments.RawInput]);
        var raw = report.Checks.Where(c => c.CheckId.StartsWith("B-sign-", StringComparison.Ordinal)).ToList();
        Assert.Equal(Verdict.Pass, Assert.Single(raw, c => c.CheckId == "B-sign-len-00032").Verdict);
        Assert.All(raw.Where(c => c.CheckId != "B-sign-len-00032"), c => Assert.Equal(Verdict.Fail, c.Verdict));
        Assert.Contains(report.Notes, n => n.Contains("accepted by the service: [32]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASizeLimitShowsUpPerLength()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.MaxValueBytes = 4096;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Verdict.Fail, report.ExperimentVerdicts[HsmExperiments.RawInput]);
        Assert.Equal(Verdict.Fail, report.Checks.Single(c => c.CheckId == "B-sign-len-65536").Verdict);
        Assert.Equal(Verdict.Pass, report.Checks.Single(c => c.CheckId == "B-sign-len-04096").Verdict);
    }

    [Fact]
    public async Task WithAnHsmGeneratedKeyNoByteCompatibilityCheckCanPass()
    {
        var (hsm, configuration) = TestSetup.KnownKeys(KeyProvenance.HsmGenerated);
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        var byteChecks = report.Checks.Where(c => c.CheckId.StartsWith("A-sign-", StringComparison.Ordinal) || c.CheckId.StartsWith("B-sign-", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(byteChecks);
        Assert.All(byteChecks, c => Assert.Equal(Verdict.Inconclusive, c.Verdict));
        Assert.Equal(Verdict.Inconclusive, report.ExperimentVerdicts[HsmExperiments.RawInput]);
        Assert.Equal(Verdict.Pass, report.ExperimentVerdicts[HsmExperiments.Versions]);
        Assert.Contains(report.Notes, n => n.Contains("cannot be concluded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithAnHsmGeneratedKeyAServiceThatHashesNonDigestInputsIsStillDetected()
    {
        var (hsm, configuration) = TestSetup.KnownKeys(KeyProvenance.HsmGenerated);
        hsm.Semantics = FakeSemantics.HashUnlessDigestLength;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Verdict.Fail, report.ExperimentVerdicts[HsmExperiments.RawInput]);
        Assert.Contains(report.Checks, c => c.CheckId == "B-sign-len-01024" && c.Verdict == Verdict.Fail && c.Detail.Contains("hashed the input first", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServiceThatSilentlyUsesTheLatestVersionIsCaughtEvenWhenItEchoesTheRequestedKid()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.IgnoreVersionUseLatest = true;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Verdict.Fail, report.ExperimentVerdicts[HsmExperiments.Versions]);
        Assert.Equal(Verdict.Fail, report.Checks.Single(c => c.CheckId == "V-explicit-v1").Verdict);
        Assert.Equal(Verdict.Fail, report.Checks.Single(c => c.CheckId == "V-no-silent-latest-v1").Verdict);
        Assert.Equal(Verdict.Fail, report.Checks.Single(c => c.CheckId == "V-distinct-v1-v2").Verdict);

        var (generatedHsm, generated) = TestSetup.KnownKeys(KeyProvenance.HsmGenerated);
        generatedHsm.IgnoreVersionUseLatest = true;
        var unknownKeyReport = await TestSetup.Experiments(generatedHsm, generated).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Verdict.Fail, unknownKeyReport.Checks.Single(c => c.CheckId == "V-no-silent-latest-v1").Verdict);
    }

    [Fact]
    public async Task ANonexistentVersionIsRejectedAndEveryRequestNamedAMappedOrDeliberatelyMissingVersion()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        var nonexistent = report.Checks.Single(c => c.CheckId == "V-nonexistent");
        Assert.Equal(Verdict.Pass, nonexistent.Verdict);
        var allowed = configuration.KeyVersionIdentifiers.Values.Append(nonexistent.Evidence!["requestedVersion"]).ToHashSet(StringComparer.Ordinal);
        foreach (var request in hsm.Requests.Where(r => r.Method == "POST" || r.PathAndQuery.Contains("/versions", StringComparison.Ordinal) is false))
        {
            var segments = request.PathAndQuery.Split('?')[0].Trim('/').Split('/');
            Assert.True(segments.Length >= 3 && allowed.Contains(segments[2]), "request without an explicit mapped version: " + request.PathAndQuery);
        }
    }

    [Fact]
    public async Task AnExportableKeyFailsInspection()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.Exportable = true;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Verdict.Fail, report.ExperimentVerdicts[HsmExperiments.KeyInspection]);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task InfrastructureFailuresAreInconclusiveNeverPass(HttpStatusCode status)
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.ForcedStatus = status;
        var report = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(report.Checks, c => c.Verdict == Verdict.Pass && !c.CheckId.StartsWith("V-no-silent-latest", StringComparison.Ordinal));
        Assert.Equal(Verdict.Inconclusive, report.ExperimentVerdicts[HsmExperiments.RawInput]);
        Assert.Equal(Verdict.Inconclusive, report.ExperimentVerdicts[HsmExperiments.DigestPath]);
        Assert.NotEqual(Verdict.Pass, report.OverallVerdict);
    }

    [Fact]
    public async Task AMalformedServiceAnswerIsInconclusiveAndAnAnswerForAnotherKeyFails()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.MalformedBody = true;
        configuration.Experiments = [ProbeExperiment.DigestPath, ProbeExperiment.RawInput];
        var malformed = await TestSetup.Experiments(hsm, configuration).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Verdict.Inconclusive, malformed.OverallVerdict);

        var (wrongHsm, wrong) = TestSetup.KnownKeys();
        wrongHsm.WrongKid = true;
        wrong.Experiments = [ProbeExperiment.DigestPath];
        Assert.Equal(Verdict.Fail, (await TestSetup.Experiments(wrongHsm, wrong).RunAsync(TestContext.Current.CancellationToken)).OverallVerdict);
    }

    [Fact]
    public async Task ImportRecordsTheNewVersionsAndNeverWritesTheKeyMaterial()
    {
        var hsm = new FakeManagedHsm();
        var configuration = new ProbeConfiguration
        {
            Mode = ProbeMode.ManagedHsm,
            AllowAzureCalls = true,
            AllowTestKeyImport = true,
            HsmEndpoint = FakeManagedHsm.Endpoint.AbsoluteUri,
            KeyName = FakeManagedHsm.KeyName,
            Credential = ProbeCredentialKind.AzureCli,
            KeyProvenance = KeyProvenance.ImportedKnownTestKeys,
            KnownTestKeyByVersion = new(StringComparer.Ordinal) { ["1"] = TestKeys.TestKey1Id, ["2"] = TestKeys.TestKey2Id },
        };
        configuration.ValidateForManagedHsm(forImport: true);
        var report = await TestSetup.Experiments(hsm, configuration).ImportTestKeysAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Verdict.Pass, report.OverallVerdict);
        Assert.Equal(2, hsm.VersionIds.Count);
        var json = report.ToJson();
        foreach (var keyId in new[] { TestKeys.TestKey1Id, TestKeys.TestKey2Id })
        {
            var key = TestKeys.Get(keyId);
            Assert.DoesNotContain(Codec.ToHex(key), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Codec.ToBase64Url(key), json, StringComparison.Ordinal);
        }

        Assert.Contains(report.Notes, n => n.Contains(hsm.VersionIds[1], StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedImportIsReportedAsAFailWithTheBringYourOwnKeyNote()
    {
        var hsm = new FakeManagedHsm { AllowImport = false };
        var configuration = new ProbeConfiguration
        {
            Mode = ProbeMode.ManagedHsm,
            AllowAzureCalls = true,
            AllowTestKeyImport = true,
            HsmEndpoint = FakeManagedHsm.Endpoint.AbsoluteUri,
            KeyName = FakeManagedHsm.KeyName,
            Credential = ProbeCredentialKind.AzureCli,
            KeyProvenance = KeyProvenance.ImportedKnownTestKeys,
            KnownTestKeyByVersion = new(StringComparer.Ordinal) { ["1"] = TestKeys.TestKey1Id },
        };
        var report = await TestSetup.Experiments(hsm, configuration).ImportTestKeysAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Verdict.Fail, report.OverallVerdict);
        Assert.Contains(report.Notes, n => n.Contains("BYOK", StringComparison.Ordinal));
    }
}
