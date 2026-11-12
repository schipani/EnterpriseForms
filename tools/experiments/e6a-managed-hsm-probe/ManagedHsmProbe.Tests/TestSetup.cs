namespace EnterpriseForms.Experiments.ManagedHsmProbe.Tests;

internal static class TestSetup
{
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "trust_authority")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    public static string ProbeRoot() => Path.Combine(RepositoryRoot(), "tools", "experiments", "e6a-managed-hsm-probe");

    /// <summary>A fully valid HSM configuration against the fake host, with both test keys imported as versions 1 and 2.</summary>
    public static (FakeManagedHsm Hsm, ProbeConfiguration Configuration) KnownKeys(KeyProvenance provenance = KeyProvenance.ImportedKnownTestKeys)
    {
        var hsm = new FakeManagedHsm();
        var v1 = hsm.AddVersion(TestKeys.Get(TestKeys.TestKey1Id));
        var v2 = hsm.AddVersion(TestKeys.Get(TestKeys.TestKey2Id));
        var configuration = new ProbeConfiguration
        {
            Mode = ProbeMode.ManagedHsm,
            AllowAzureCalls = true,
            HsmEndpoint = FakeManagedHsm.Endpoint.AbsoluteUri,
            KeyName = FakeManagedHsm.KeyName,
            Credential = ProbeCredentialKind.AzureCli,
            KeyProvenance = provenance,
            KeyVersionIdentifiers = new(StringComparer.Ordinal) { ["1"] = v1, ["2"] = v2 },
            KnownTestKeyByVersion = provenance == KeyProvenance.ImportedKnownTestKeys
                ? new(StringComparer.Ordinal) { ["1"] = TestKeys.TestKey1Id, ["2"] = TestKeys.TestKey2Id }
                : new(StringComparer.Ordinal),
            OperationTimeoutSeconds = 5,
        };
        configuration.ValidateForManagedHsm(forImport: false);
        return (hsm, configuration);
    }

#pragma warning disable CA2000 // test-scoped HttpClient over the fake handler
    public static ManagedHsmRestClient Client(FakeManagedHsm hsm, ProbeConfiguration configuration, FakeCredential? credential = null) =>
        new(new HttpClient(hsm, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }, credential ?? new FakeCredential(), configuration);
#pragma warning restore CA2000

    public static HsmExperiments Experiments(FakeManagedHsm hsm, ProbeConfiguration configuration) =>
        new(Client(hsm, configuration), null, configuration, ReferenceVectorGenerator.LoadEmbedded());

    /// <summary>Factory for application-level tests over the fake; records whether it was ever asked for a client.</summary>
    internal sealed class FakeFactory(FakeManagedHsm hsm, FakeCredential? credential = null) : IHsmClientFactory
    {
        public int Created { get; private set; }

        public (IManagedHsmClient Client, IDigestSigner? SdkSigner) Create(ProbeConfiguration configuration)
        {
            Created++;
            return (Client(hsm, configuration, credential), null);
        }
    }

    /// <summary>Fails the test if any code path tries to create an HSM client (local mode, refused commands).</summary>
    internal sealed class TripwireFactory : IHsmClientFactory
    {
        public (IManagedHsmClient Client, IDigestSigner? SdkSigner) Create(ProbeConfiguration configuration) =>
            throw new InvalidOperationException("TRIPWIRE: an HSM client was requested");
    }
}
