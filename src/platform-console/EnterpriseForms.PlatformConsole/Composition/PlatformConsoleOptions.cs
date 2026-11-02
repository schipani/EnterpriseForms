using System.Text.RegularExpressions;

namespace EnterpriseForms.PlatformConsole.Composition;

/// <summary>Configuration section <c>PlatformConsole</c>. Secrets come from deployment configuration only; never committed.</summary>
public sealed class PlatformConsoleOptions
{
    public const string SectionName = "PlatformConsole";
    public const string SyntheticDevelopmentMode = "SyntheticDevelopment";

    /// <summary>Only <c>SyntheticDevelopment</c> exists (Development/Testing). Production needs PB-08 Entra sign-in and transaction-bound step-up.</summary>
    public string? Mode { get; set; }

    /// <summary>Browser origins allowed to call the console (exact <c>scheme://host:port</c>). State-changing requests must present one of them.</summary>
    public List<string> AllowedOrigins { get; set; } = [];

    public AuthorityOptions Authority { get; set; } = new();

    public VaultOptions Vault { get; set; } = new();

    public SessionOptions Session { get; set; } = new();

    /// <summary>The synthetic Platform Operators this console may sign in, each with its own Vault Transit signing key.</summary>
    public List<SyntheticOperatorOptions> Operators { get; set; } = [];
}

public sealed class AuthorityOptions
{
    public string? BaseAddress { get; set; }

    /// <summary>The Platform Console workload credential (HS256; own key and issuer, role <c>platform-console</c>).</summary>
    public CredentialOptions ConsoleCredential { get; set; } = new();

    /// <summary>The operator-assertion key; its issuer is the synthetic operators' issuer URI in the Authority registry.</summary>
    public CredentialOptions OperatorAssertion { get; set; } = new();

    public string WorkloadName { get; set; } = "platform-console";
}

public sealed class CredentialOptions
{
    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    /// <summary>Base64 of a ≥32-byte random secret, from deployment secret configuration.</summary>
    public string? SharedKeyBase64 { get; set; }

    public bool IsConfigured
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience) || string.IsNullOrWhiteSpace(SharedKeyBase64))
            {
                return false;
            }

            try
            {
                return Convert.FromBase64String(SharedKeyBase64).Length >= 32;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }

    public byte[] Key() => Convert.FromBase64String(SharedKeyBase64 ?? string.Empty);
}

public sealed class VaultOptions
{
    /// <summary>Local Vault Transit (Development/Testing). The console's token may only sign with the configured operator keys.</summary>
    public string? Address { get; set; }

    public string? Token { get; set; }
}

public sealed class SessionOptions
{
    /// <summary>Approved maximum (C-99): 15 minutes. Configuration may shorten it, never lengthen it.</summary>
    public const int MaximumIdleMinutes = 15;

    /// <summary>Approved maximum (C-99): 2 hours. Configuration may shorten it, never lengthen it.</summary>
    public const int MaximumAbsoluteMinutes = 120;

    public int IdleTimeoutMinutes { get; set; } = MaximumIdleMinutes;

    public int AbsoluteLifetimeMinutes { get; set; } = MaximumAbsoluteMinutes;

    /// <summary>Upper bound of concurrently held sessions (memory bound; the oldest expired are swept first).</summary>
    public int MaximumSessions { get; set; } = 200;
}

public sealed class SyntheticOperatorOptions
{
    public string? SubjectIdentifier { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>Vault Transit key name; equal to the operator's registered synthetic key identifier in the Authority.</summary>
    public string? VaultKeyName { get; set; }
}

/// <summary>Fail closed at startup: wrong environment, missing or reused credentials, or an invalid operator list stop the host.</summary>
public static partial class PlatformConsoleOptionsValidation
{
    public static void Validate(PlatformConsoleOptions options, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (environmentName is not ("Development" or "Testing"))
        {
            throw new InvalidOperationException($"The Platform Console runs only in Development/Testing until PB-08 (Entra operator sign-in, transaction-bound FIDO2 step-up, PIM) exists; refusing to start in {environmentName}.");
        }

        if (!string.Equals(options.Mode, PlatformConsoleOptions.SyntheticDevelopmentMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{PlatformConsoleOptions.SectionName}:Mode must be {PlatformConsoleOptions.SyntheticDevelopmentMode}; no production authentication mode is implemented. Refusing to start.");
        }

        if (!Uri.TryCreate(options.Authority.BaseAddress, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException($"{PlatformConsoleOptions.SectionName}:Authority:BaseAddress is required.");
        }

        var console = options.Authority.ConsoleCredential;
        var assertion = options.Authority.OperatorAssertion;
        if (!console.IsConfigured || !assertion.IsConfigured)
        {
            throw new InvalidOperationException("The Platform Console credential and the operator-assertion key must both be configured completely (issuer, audience, >=256-bit key).");
        }

        if (console.SharedKeyBase64 == assertion.SharedKeyBase64 || console.Issuer == assertion.Issuer)
        {
            throw new InvalidOperationException("The Platform Console credential and the operator-assertion key must have different keys and issuers.");
        }

        if (!Uri.TryCreate(assertion.Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The operator-assertion issuer is the operators' issuer URI and must be an absolute https URI.");
        }

        if (!Uri.TryCreate(options.Vault.Address, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(options.Vault.Token))
        {
            throw new InvalidOperationException($"{PlatformConsoleOptions.SectionName}:Vault:Address and :Token are required (operator evidence is signed in Vault Transit; the console holds no private key).");
        }

        if (options.Session.IdleTimeoutMinutes is < 1 or > SessionOptions.MaximumIdleMinutes || options.Session.AbsoluteLifetimeMinutes is < 1 or > SessionOptions.MaximumAbsoluteMinutes
            || options.Session.IdleTimeoutMinutes > options.Session.AbsoluteLifetimeMinutes || options.Session.MaximumSessions is < 1 or > 10_000)
        {
            throw new InvalidOperationException($"Session limits: idle 1..{SessionOptions.MaximumIdleMinutes} minutes, absolute 1..{SessionOptions.MaximumAbsoluteMinutes} minutes (idle <= absolute), 1..10000 sessions.");
        }

        if (options.AllowedOrigins.Count == 0 || options.AllowedOrigins.Any(o => !IsOrigin(o)))
        {
            throw new InvalidOperationException($"{PlatformConsoleOptions.SectionName}:AllowedOrigins must list exact origins (scheme://host[:port], no path).");
        }

        if (options.Operators.Count is 0 or > 10)
        {
            throw new InvalidOperationException($"{PlatformConsoleOptions.SectionName}:Operators must list 1..10 synthetic operators.");
        }

        foreach (var o in options.Operators)
        {
            var subject = o.SubjectIdentifier ?? string.Empty;
            if (subject.Length is 0 or > 512 || subject.Any(char.IsWhiteSpace) || subject.Contains('@', StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Each synthetic operator needs a subject identifier of 1..512 characters, without spaces, never an email address.");
            }

            if (string.IsNullOrWhiteSpace(o.DisplayName) || o.DisplayName.Length > 200 || o.DisplayName.Any(char.IsControl))
            {
                throw new InvalidOperationException("Each synthetic operator needs a display name of 1..200 characters.");
            }

            if (!KeyName().IsMatch(o.VaultKeyName ?? string.Empty))
            {
                throw new InvalidOperationException("Each synthetic operator needs a Vault key name of 1..64 characters (letters, digits, '.', '_', '-').");
            }
        }

        if (options.Operators.Select(o => o.SubjectIdentifier).Distinct(StringComparer.Ordinal).Count() != options.Operators.Count
            || options.Operators.Select(o => o.VaultKeyName).Distinct(StringComparer.Ordinal).Count() != options.Operators.Count)
        {
            throw new InvalidOperationException("Synthetic operator subjects and Vault keys must be unique: one key per operator, never shared.");
        }
    }

    public static bool IsKeyName(string? value) => KeyName().IsMatch(value ?? string.Empty);

    private static bool IsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && !value.EndsWith('/');

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex KeyName();
}
