using Xunit;

namespace EnterpriseForms.Experiments.ManagedHsmProbe.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void TheDefaultModeIsLocalWithAzureCallsDisallowed()
    {
        var configuration = ProbeConfiguration.Parse("{}");
        Assert.Equal(ProbeMode.Local, configuration.Mode);
        Assert.False(configuration.AllowAzureCalls);
        Assert.False(configuration.AllowTestKeyImport);
        Assert.Throws<ProbeConfigurationException>(() => configuration.ValidateForManagedHsm(forImport: false));
    }

    [Theory]
    [InlineData("http://probe-test.managedhsm.azure.net/")]
    [InlineData("https://probe-test.vault.azure.net/")]
    [InlineData("https://managedhsm.azure.net/")]
    [InlineData("https://probe-test.managedhsm.azure.net.evil.example/")]
    [InlineData("https://probe-test.managedhsm.azure.net:8443/")]
    [InlineData("https://user:pass@probe-test.managedhsm.azure.net/")]
    [InlineData("https://probe-test.managedhsm.azure.net/keys/x")]
    [InlineData("https://probe-test.managedhsm.azure.net/?x=1")]
    [InlineData("probe-test.managedhsm.azure.net")]
    [InlineData("")]
    public void NonHttpsOrNonManagedHsmEndpointsAreRejected(string endpoint) =>
        Assert.NotEmpty(ProbeConfiguration.ValidateEndpoint(endpoint));

    [Fact]
    public void TheManagedHsmEndpointShapeIsAccepted() =>
        Assert.Empty(ProbeConfiguration.ValidateEndpoint("https://probe-test.managedhsm.azure.net/"));

    [Fact]
    public void AValidFakeConfigurationPasses() => TestSetup.KnownKeys();

    [Theory]
    [InlineData("org-11111111111141118111111111111111-row-integrity-v1")]
    [InlineData("e6a-probe-")]
    [InlineData("E6A-PROBE-X")]
    [InlineData("other-key")]
    public void OnlyE6aProbeKeyNamesAreAccepted(string keyName)
    {
        var (_, configuration) = TestSetup.KnownKeys();
        configuration.KeyName = keyName;
        var error = Assert.Throws<ProbeConfigurationException>(() => configuration.ValidateForManagedHsm(forImport: false));
        Assert.Contains(error.Errors, e => e.StartsWith("KeyName", StringComparison.Ordinal));
    }

    [Fact]
    public void BothExplicitOptInsAndAnExplicitCredentialAndProvenanceAreRequired()
    {
        var (_, configuration) = TestSetup.KnownKeys();
        configuration.AllowAzureCalls = false;
        configuration.Credential = null;
        configuration.KeyProvenance = null;
        configuration.Mode = ProbeMode.Local;
        var error = Assert.Throws<ProbeConfigurationException>(() => configuration.ValidateForManagedHsm(forImport: false));
        Assert.Contains(error.Errors, e => e.StartsWith("Mode", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("AllowAzureCalls", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("Credential", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("KeyProvenance", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0", "abc")]
    [InlineData("-1", "abc")]
    [InlineData("01", "abc")]
    [InlineData("one", "abc")]
    [InlineData("1", "")]
    [InlineData("1", "../etc")]
    [InlineData("1", "0123456789012345678901234567890123456789012345678901234567890123456789")]
    public void InvalidVersionMappingsAreRejected(string version, string identifier)
    {
        var (_, configuration) = TestSetup.KnownKeys(KeyProvenance.HsmGenerated);
        configuration.KeyVersionIdentifiers[version] = identifier;
        Assert.Throws<ProbeConfigurationException>(() => configuration.ValidateForManagedHsm(forImport: false));
    }

    [Fact]
    public void DuplicateIdentifiersAMissingPrimaryAndASingleVersionForTheVersionsExperimentAreRejected()
    {
        var (_, duplicate) = TestSetup.KnownKeys(KeyProvenance.HsmGenerated);
        duplicate.KeyVersionIdentifiers["2"] = duplicate.KeyVersionIdentifiers["1"];
        Assert.Throws<ProbeConfigurationException>(() => duplicate.ValidateForManagedHsm(forImport: false));

        var (_, primary) = TestSetup.KnownKeys();
        primary.PrimaryKeyVersion = 3;
        Assert.Throws<ProbeConfigurationException>(() => primary.ValidateForManagedHsm(forImport: false));

        var (_, single) = TestSetup.KnownKeys(KeyProvenance.HsmGenerated);
        single.KeyVersionIdentifiers.Remove("2");
        var error = Assert.Throws<ProbeConfigurationException>(() => single.ValidateForManagedHsm(forImport: false));
        Assert.Contains(error.Errors, e => e.Contains("two mapped key versions", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnmappedVersionIsRefusedAndNeverResolvedToLatest()
    {
        var (_, configuration) = TestSetup.KnownKeys();
        var error = Assert.Throws<ProbeConfigurationException>(() => configuration.ResolveVersionIdentifier(3));
        Assert.Contains("never falls back to the latest version", error.Message, StringComparison.Ordinal);
        Assert.Throws<ProbeConfigurationException>(() => configuration.ResolveVersionIdentifier(0));
        Assert.Equal(configuration.KeyVersionIdentifiers["2"], configuration.ResolveVersionIdentifier(2));
    }

    [Fact]
    public void KnownKeyTablesMustCoverEveryVersionWithDistinctPublishedKeys()
    {
        var (_, missing) = TestSetup.KnownKeys();
        missing.KnownTestKeyByVersion.Remove("2");
        Assert.Throws<ProbeConfigurationException>(() => missing.ValidateForManagedHsm(forImport: false));

        var (_, same) = TestSetup.KnownKeys();
        same.KnownTestKeyByVersion["2"] = TestKeys.TestKey1Id;
        Assert.Throws<ProbeConfigurationException>(() => same.ValidateForManagedHsm(forImport: false));

        var (_, unknown) = TestSetup.KnownKeys();
        unknown.KnownTestKeyByVersion["2"] = "production-key";
        Assert.Throws<ProbeConfigurationException>(() => unknown.ValidateForManagedHsm(forImport: false));

        var (_, generated) = TestSetup.KnownKeys(KeyProvenance.HsmGenerated);
        generated.KnownTestKeyByVersion["1"] = TestKeys.TestKey1Id;
        Assert.Throws<ProbeConfigurationException>(() => generated.ValidateForManagedHsm(forImport: false));
    }

    [Fact]
    public void ImportNeedsItsOwnOptInAndKnownTestKeys()
    {
        var (_, configuration) = TestSetup.KnownKeys();
        var error = Assert.Throws<ProbeConfigurationException>(() => configuration.ValidateForManagedHsm(forImport: true));
        Assert.Contains(error.Errors, e => e.StartsWith("AllowTestKeyImport", StringComparison.Ordinal));
        configuration.AllowTestKeyImport = true;
        configuration.ValidateForManagedHsm(forImport: true);

        var (_, generated) = TestSetup.KnownKeys(KeyProvenance.HsmGenerated);
        generated.AllowTestKeyImport = true;
        Assert.Throws<ProbeConfigurationException>(() => generated.ValidateForManagedHsm(forImport: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void TimeoutsOutsideOneToSixtySecondsAreRejected(int seconds)
    {
        var (_, configuration) = TestSetup.KnownKeys();
        configuration.OperationTimeoutSeconds = seconds;
        Assert.Throws<ProbeConfigurationException>(() => configuration.ValidateForManagedHsm(forImport: false));
    }

    [Theory]
    [InlineData("{\"Mode\":\"ManagedHsm\",\"AllowAzureCall\":true}")]
    [InlineData("{\"Mode\":\"Hsm\"}")]
    [InlineData("{\"Mode\":1}")]
    [InlineData("not json")]
    [InlineData("{\"ApiVersion\":\"7.6\",\"ClientSecret\":\"x\"}")]
    public void UnknownPropertiesBadEnumValuesAndSecretsAreRejectedAtParse(string json) =>
        Assert.Throws<ProbeConfigurationException>(() => ProbeConfiguration.Parse(json));

    [Fact]
    public void UnsupportedApiVersionsAreRejected()
    {
        var (_, configuration) = TestSetup.KnownKeys();
        configuration.ApiVersion = "7.4";
        Assert.Throws<ProbeConfigurationException>(() => configuration.ValidateForManagedHsm(forImport: false));
    }

    [Fact]
    public void TheCommittedTemplateParsesButIsDeliberatelyUnusableUntilEdited()
    {
        var template = ProbeConfiguration.Parse(File.ReadAllText(Path.Combine(TestSetup.ProbeRoot(), "probe.settings.template.json")));
        Assert.False(template.AllowAzureCalls);
        var error = Assert.Throws<ProbeConfigurationException>(() => template.ValidateForManagedHsm(forImport: false));
        Assert.Contains(error.Errors, e => e.StartsWith("AllowAzureCalls", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("HsmEndpoint", StringComparison.Ordinal));
    }
}
