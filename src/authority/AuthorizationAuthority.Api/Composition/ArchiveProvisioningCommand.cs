using AuthorizationAuthority.Application.Features.Journal.Reconciliation;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// SEC-BL-01 WP-5a.1 (amendment A-2 correction, §22.8): the explicit provisioning step of a <b>disposable</b> Development/Testing
/// security-event archive (<see cref="ArchiveProvisioningService"/>), run on the composed host before the Authority starts, then exit.
/// <code>--journal-archive-provision</code>
/// Refused outside Development/Testing, and unless the stack declares itself disposable (<c>JournalArchiveProvisioning:DisposableStack=true</c>,
/// set only by the security-test and integration overlays). A persistent Development archive and production archives are never initialized
/// here: they need an operator baseline (<c>--journal-baseline --create-sentinel</c>). Exit codes: 0 provisioned or already provisioned,
/// 2 refused environment or not a disposable stack, 3 not composed (journal-verification store not configured), 4 refused state, 5 unavailable.
/// </summary>
public static class ArchiveProvisioningCommand
{
    public const string Flag = "--journal-archive-provision";

    public const string DisposableStackKey = "JournalArchiveProvisioning:DisposableStack";

    public static bool Requested(string[] args, out string[] hostArguments)
    {
        ArgumentNullException.ThrowIfNull(args);
        hostArguments = [.. args.Where(a => !string.Equals(a, Flag, StringComparison.Ordinal))];
        return hostArguments.Length != args.Length;
    }

    public static int Run(IServiceProvider services, IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            Console.Error.WriteLine($"{Flag} is a Development/Testing command; refused in {environment.EnvironmentName}.");
            return 2;
        }

        if (!configuration.GetValue<bool>(DisposableStackKey))
        {
            Console.Error.WriteLine($"{Flag} initializes only disposable stacks ({DisposableStackKey}=true); persistent archives need an operator baseline.");
            return 2;
        }

        if (services.GetService<ArchiveProvisioningService>() is not { } provisioning)
        {
            Console.Error.WriteLine("journal verification is not composed (configure JournalVerificationCheckpointStore).");
            return 3;
        }

        var result = provisioning.ProvisionAsync(typeof(ArchiveProvisioningService).Assembly.GetName().Version?.ToString() ?? "unknown", CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"archive provisioning {result.Outcome}{(result.ReasonCode is null ? string.Empty : " " + result.ReasonCode)}: {result.Detail}");
        return result.Outcome switch
        {
            ArchiveProvisioningOutcome.Provisioned or ArchiveProvisioningOutcome.AlreadyProvisioned => 0,
            ArchiveProvisioningOutcome.Refused => 4,
            _ => 5,
        };
    }
}
