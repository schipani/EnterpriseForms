using Azure.Core;
using Azure.Identity;

namespace EnterpriseForms.Experiments.ManagedHsmProbe;

/// <summary>Creates the HSM transport. Called only after an HSM command passed both explicit opt-ins and full validation.</summary>
internal interface IHsmClientFactory
{
    (IManagedHsmClient Client, IDigestSigner? SdkSigner) Create(ProbeConfiguration configuration);
}

/// <summary>Real factory: the explicitly selected Azure Identity credential (no DefaultAzureCredential chain), no retries.</summary>
internal sealed class AzureHsmClientFactory : IHsmClientFactory
{
    public (IManagedHsmClient Client, IDigestSigner? SdkSigner) Create(ProbeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        TokenCredential credential = configuration.Credential switch
        {
            ProbeCredentialKind.AzureCli => new AzureCliCredential(new AzureCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(configuration.OperationTimeoutSeconds) }),
            ProbeCredentialKind.ManagedIdentity => configuration.ManagedIdentityClientId is { Length: > 0 } clientId
                ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
                : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned),
            _ => throw new ProbeConfigurationException(["Credential must be set explicitly"]),
        };
#pragma warning disable CA2000 // the HttpClient lives for the single probe run (process lifetime)
        var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
#pragma warning restore CA2000
        return (new ManagedHsmRestClient(httpClient, credential, configuration), new SdkDigestSigner(configuration, credential));
    }
}

/// <summary>
/// Command-line application. <c>local</c> is the default; HSM commands need <c>Mode=ManagedHsm</c> and
/// <c>AllowAzureCalls=true</c> in the configuration file AND the <c>--allow-azure-calls</c> flag.
/// Exit codes: 0 Pass, 1 Fail, 2 usage/configuration error (nothing contacted), 3 Inconclusive, 130 cancelled.
/// </summary>
internal static class ProbeApplication
{
    public const int UsageError = 2;
    public const int Cancelled = 130;

    public const string Usage = """
        E-6a Managed HSM HMAC compatibility probe (Development/Testing experiment; default local-only)

          ManagedHsmProbe [local]                               local self-checks only (no network)
          ManagedHsmProbe generate-vectors --row-vectors <trust_authority/row-integrity-v1.test-vectors.json> --out <file>
          ManagedHsmProbe validate-config --config <file>       validate only (no network)
          ManagedHsmProbe hsm-run --config <file> --allow-azure-calls [--report <file>]
          ManagedHsmProbe hsm-import-test-keys --config <file> --allow-azure-calls [--report <file>]

        HSM commands are for a separately authorized run against a throwaway NON-PRODUCTION Managed HSM only.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, IHsmClientFactory factory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(factory);
        var command = args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal) ? "local" : args[0];
        var options = ParseOptions(args.Skip(command == "local" && (args.Length == 0 || args[0] != "local") ? 0 : 1).ToArray(), error);
        if (options is null)
        {
            return UsageError;
        }

        try
        {
            switch (command)
            {
                case "local":
                    if (options.Count > 0)
                    {
                        error.WriteLine("local takes no options");
                        return UsageError;
                    }

                    return Finish(LocalSelfChecks.Run(ReferenceVectorGenerator.LoadEmbedded()), null, output);
                case "generate-vectors":
                    return GenerateVectors(options, output, error);
                case "validate-config":
                    {
                        var configuration = LoadConfiguration(options);
                        if (configuration.Mode == ProbeMode.ManagedHsm)
                        {
                            configuration.ValidateForManagedHsm(forImport: options.ContainsKey("--import"));
                        }

                        output.WriteLine($"configuration valid (Mode={configuration.Mode}); nothing was contacted");
                        return 0;
                    }

                case "hsm-run":
                case "hsm-import-test-keys":
                    return await RunHsmAsync(command, options, output, error, factory, cancellationToken).ConfigureAwait(false);
                default:
                    error.WriteLine(Usage);
                    return UsageError;
            }
        }
        catch (ProbeConfigurationException ex)
        {
            foreach (var problem in ex.Errors)
            {
                error.WriteLine("configuration error: " + problem);
            }

            error.WriteLine("nothing was contacted");
            return UsageError;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("cancelled; partial results were not written");
            return Cancelled;
        }
    }

    private static async Task<int> RunHsmAsync(string command, Dictionary<string, string?> options, TextWriter output, TextWriter error, IHsmClientFactory factory, CancellationToken cancellationToken)
    {
        if (!options.ContainsKey("--allow-azure-calls"))
        {
            error.WriteLine($"{command} contacts Azure Managed HSM and requires the explicit --allow-azure-calls flag; nothing was contacted");
            return UsageError;
        }

        var configuration = LoadConfiguration(options);
        var forImport = command == "hsm-import-test-keys";
        configuration.ValidateForManagedHsm(forImport);
        var vectors = ReferenceVectorGenerator.LoadEmbedded();
        var local = LocalSelfChecks.Run(vectors);
        if (local.OverallVerdict != Verdict.Pass)
        {
            error.WriteLine("local self-checks failed; refusing to contact the HSM");
            local.WriteSummary(error);
            return 1;
        }

        output.WriteLine($"{command}: contacting {configuration.EndpointUri.Host} (key {configuration.KeyName}, api-version {configuration.ApiVersion}, credential {configuration.Credential})");
        var (client, sdk) = factory.Create(configuration);
        var experiments = new HsmExperiments(client, sdk, configuration, vectors);
        var report = forImport ? await experiments.ImportTestKeysAsync(cancellationToken).ConfigureAwait(false) : await experiments.RunAsync(cancellationToken).ConfigureAwait(false);
        var path = options.TryGetValue("--report", out var reportPath) && reportPath is { Length: > 0 }
            ? reportPath
            : $"e6a-{command}-report-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json";
        return Finish(report, path, output);
    }

    private static int GenerateVectors(Dictionary<string, string?> options, TextWriter output, TextWriter error)
    {
        if (!options.TryGetValue("--row-vectors", out var source) || source is null || !options.TryGetValue("--out", out var target) || target is null)
        {
            error.WriteLine(Usage);
            return UsageError;
        }

        var set = ReferenceVectorGenerator.Generate(File.ReadAllText(source), "trust_authority/row-integrity-v1.test-vectors.json");
        var check = new ProbeReport { Command = "generate-vectors" };
        LocalSelfChecks.CheckVectorSet(check, set);
        if (check.OverallVerdict != Verdict.Pass)
        {
            check.WriteSummary(error);
            return 1;
        }

        File.WriteAllText(target, ReferenceVectorGenerator.Serialize(set), new System.Text.UTF8Encoding(false));
        output.WriteLine($"wrote {set.Vectors.Count} vectors to {target}");
        return 0;
    }

    private static ProbeConfiguration LoadConfiguration(Dictionary<string, string?> options) =>
        options.TryGetValue("--config", out var path) && path is { Length: > 0 }
            ? ProbeConfiguration.Parse(File.ReadAllText(path))
            : throw new ProbeConfigurationException(["--config <file> is required"]);

    private static int Finish(ProbeReport report, string? reportPath, TextWriter output)
    {
        report.WriteSummary(output);
        if (reportPath is not null)
        {
            File.WriteAllText(reportPath, report.ToJson(), new System.Text.UTF8Encoding(false));
            output.WriteLine("report: " + reportPath);
        }

        return report.ExitCode;
    }

    private static Dictionary<string, string?>? ParseOptions(string[] args, TextWriter error)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal) { "--allow-azure-calls", "--import" };
        var valued = new HashSet<string>(StringComparer.Ordinal) { "--config", "--report", "--row-vectors", "--out" };
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (flags.Contains(args[i]))
            {
                options[args[i]] = null;
            }
            else if (valued.Contains(args[i]) && i + 1 < args.Length)
            {
                options[args[i]] = args[++i];
            }
            else
            {
                error.WriteLine($"unknown or incomplete option '{args[i]}'");
                error.WriteLine(Usage);
                return null;
            }
        }

        return options;
    }
}
