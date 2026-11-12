using System.Globalization;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Keys;
using AuthorizationAuthority.Domain.Features.Keys;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// E-5 operator command (Development/Testing only), run on the fully composed Authority host so it uses exactly the Authority's
/// own store, signing provider and credentials:
/// <code>dotnet AuthorizationAuthority.Api.dll --activate-integrity-key-version --organization &lt;uuid&gt; --expected-current-version &lt;n&gt; --signing-key-version &lt;m&gt; --reason "&lt;text&gt;"</code>
/// It calls <see cref="IntegrityKeyActivationService"/> once, prints one JSON result and exits: 0 activated, 1 refused (error code on
/// stderr, nothing changed), 2 invalid arguments, 3 refused in this environment. It is not an endpoint and adds no role or
/// permission: whoever can run the Authority host with its configuration can already act as the Authority.
/// </summary>
public static class IntegrityKeyActivationCommand
{
    public const string Flag = "--activate-integrity-key-version";
    private const string OperatorName = "authority-key-activation-cli";
    private static readonly string[] ValueOptions = ["--organization", "--expected-current-version", "--signing-key-version", "--reason", "--key-purpose"];
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public sealed record Arguments(Guid OrganizationId, int ExpectedCurrentVersion, int SigningKeyVersion, string Reason, string KeyPurposeName);

    /// <summary>Whether the command was requested; removes its arguments so the host's command-line configuration never sees them.</summary>
    public static bool Requested(string[] args, out Arguments? arguments, out string[] hostArguments)
    {
        ArgumentNullException.ThrowIfNull(args);
        arguments = null;
        hostArguments = args;
        if (Array.IndexOf(args, Flag) < 0)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == Flag)
            {
                continue;
            }

            if (ValueOptions.Contains(args[i]))
            {
                if (i + 1 < args.Length)
                {
                    values[args[i]] = args[i + 1];
                }

                i++;
                continue;
            }

            remaining.Add(args[i]);
        }

        hostArguments = remaining.ToArray();
        if (Guid.TryParseExact(values.GetValueOrDefault("--organization"), "D", out var organization)
            && int.TryParse(values.GetValueOrDefault("--expected-current-version"), NumberStyles.None, CultureInfo.InvariantCulture, out var expected)
            && int.TryParse(values.GetValueOrDefault("--signing-key-version"), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            && values.GetValueOrDefault("--reason") is { Length: > 0 } reason)
        {
            arguments = new Arguments(organization, expected, version, reason, values.GetValueOrDefault("--key-purpose") ?? IntegrityKeyPurposes.RowIntegrityV1);
        }

        return true;
    }

    public static int Run(IServiceProvider services, IHostEnvironment environment, Arguments? arguments)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        if (!(environment.IsDevelopment() || environment.IsEnvironment("Testing")))
        {
            Console.Error.WriteLine(Flag + " is a Development/Testing operator command; refusing in " + environment.EnvironmentName);
            return 3;
        }

        if (arguments is null)
        {
            Console.Error.WriteLine("usage: " + Flag + " --organization <uuid> --expected-current-version <n> --signing-key-version <m> --reason \"<text>\" [--key-purpose row-integrity-v1]");
            return 2;
        }

        var service = new IntegrityKeyActivationService(services.GetRequiredService<IAuthorityStore>(), services.GetRequiredService<IIntegrityMacProvider>(), services.GetService<TimeProvider>() ?? TimeProvider.System);
        try
        {
            var result = service.ActivateAsync(arguments.OrganizationId, arguments.KeyPurposeName, arguments.ExpectedCurrentVersion, arguments.SigningKeyVersion, OperatorName, arguments.Reason, Guid.CreateVersion7(), CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(result, Indented));
            return 0;
        }
        catch (AuthorityException ex)
        {
            Console.Error.WriteLine("refused: " + ex.ErrorCode + " (" + ex.HttpStatusCode.ToString(CultureInfo.InvariantCulture) + "): " + ex.Message + " — nothing changed");
            return 1;
        }
    }
}
