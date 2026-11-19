using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthorizationAuthority.Application.Features.Journal.Recovery;

/// <summary>
/// SEC-BL-01 G14-R2 (A-4 §24.4): configuration section <c>JournalRecovery</c>. Public keys only, from protected deployment configuration; never
/// from AuthorityDb, the Platform Operator registry or any record store. The key set is separate from the DR-C7-3 <c>OperatorApprovals</c> set.
/// </summary>
public sealed class JournalRecoveryOptions
{
    public const string SectionName = "JournalRecovery";

    /// <summary><c>Unavailable</c> (default) or <c>SyntheticDevelopment</c> (Development/Testing only).</summary>
    public string? Mode { get; set; }

    /// <summary>Upper bound of an approval's lifetime (exp − iat); 60..900 seconds, default 900 (A-4 §24.4).</summary>
    public int MaxApprovalLifetimeSeconds { get; set; } = 900;

    public List<JournalRecoveryApproverOptions> Approvers { get; set; } = [];
}

public sealed class JournalRecoveryApproverOptions
{
    public string? ApproverId { get; set; }

    /// <summary>Base64 of the approver's P-256 public key (SubjectPublicKeyInfo, DER).</summary>
    public string? PublicKeyBase64 { get; set; }

    /// <summary>
    /// Historical verification (A-4 §24.4): a retired key still verifies decisions issued at or before this instant (so recoveries it approved
    /// stay lifted), and verifies nothing issued later. A key removed from the configuration verifies nothing (its lifts disappear: fail closed).
    /// </summary>
    public DateTimeOffset? RetiredOnUtc { get; set; }
}

/// <summary>Claims of a recovery decision (compact JWS, ES256, <c>kid</c> = approver id).</summary>
public static class DPlusRecoveryClaims
{
    public const string Issuer = "enterpriseforms-journal-recovery-approver";
    public const string Audience = "authorization-authority-dplus-stream-recovery";
    public const string RecoveryKind = "DPlusStreamRecovery";
    public const string CancellationKind = "DPlusStreamRecoveryCancellation";
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Cancel = "cancel";

    /// <summary>The complete claim set; anything else is refused (no extension members).</summary>
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "iss", "aud", "sub", "jti", "iat", "nbf", "exp", "approval_kind", "decision", "statement_sha256", "recovery_id", "organization_id", "stream_name", "failed_revision",
        "stop_generation", "justification_sha256",
    };
}

/// <summary>What a decision must bind besides its signature.</summary>
public sealed record DPlusRecoveryBinding(Guid RecoveryId, Guid OrganizationId, string StreamName, long FailedRevision, int StopGeneration, string StatementSha256Hex);

/// <summary>One decision whose signature, key and binding verified. Freshness (exp against the current time) is checked only at execution.</summary>
public sealed record DPlusVerifiedDecision(string ApproverId, Guid Jti, string ApprovalKind, string Decision, DateTimeOffset IssuedOn, DateTimeOffset NotBefore, DateTimeOffset ExpiresOn, string TokenSha256Hex);

/// <summary>
/// G14-R2 (A-4 §24.4): verification of recovery decisions (approve, reject, cancel) by two distinct recovery approver keys. The Authority
/// holds public keys only: no runtime identity, record store, operator credential or the recovery tool can produce a decision. Modes as
/// DR-C7-3: <c>Unavailable</c> (default, and the only mode outside Development/Testing) verifies nothing, so nothing is ever lifted;
/// <c>SyntheticDevelopment</c> uses development-only keys that one person typically holds, so it is <b>not two-human approval evidence</b>.
/// </summary>
public sealed class DPlusRecoveryApprovalVerifier
{
    public const string UnavailableMode = "Unavailable";
    public const string SyntheticMode = "SyntheticDevelopment";
    public const string SyntheticAssurance = "SyntheticDevelopment (development-only recovery approver keys; not two-human approval evidence)";
    public const int RequiredApprovals = 2;
    public static TimeSpan ClockSkew { get; } = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, (byte[] Spki, DateTimeOffset? RetiredOnUtc)> approvers;

    private DPlusRecoveryApprovalVerifier(string mode, int maxLifetimeSeconds, Dictionary<string, (byte[] Spki, DateTimeOffset? RetiredOnUtc)> approvers)
    {
        Mode = mode;
        MaxApprovalLifetimeSeconds = maxLifetimeSeconds;
        this.approvers = approvers;
    }

    public static DPlusRecoveryApprovalVerifier Unavailable { get; } = new(UnavailableMode, 900, new Dictionary<string, (byte[], DateTimeOffset?)>(StringComparer.Ordinal));

    public string Mode { get; }

    public bool Operational => Mode == SyntheticMode;

    public int MaxApprovalLifetimeSeconds { get; }

    public IReadOnlyCollection<string> ApproverIds => approvers.Keys;

    /// <summary>
    /// Builds the verifier. Refuses (startup failure) any mode other than the two, the synthetic mode outside Development/Testing, fewer than two
    /// approvers, non-P-256 keys, duplicate ids or keys, and any key that is also a DR-C7-3 operator approver key (<paramref name="operatorApproverKeys"/>).
    /// </summary>
    public static DPlusRecoveryApprovalVerifier Create(JournalRecoveryOptions? options, string environmentName, IEnumerable<string>? operatorApproverKeys = null)
    {
        options ??= new JournalRecoveryOptions();
        if (string.IsNullOrWhiteSpace(options.Mode) || string.Equals(options.Mode, UnavailableMode, StringComparison.Ordinal))
        {
            return Unavailable;
        }

        if (!string.Equals(options.Mode, SyntheticMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{JournalRecoveryOptions.SectionName}:Mode must be {UnavailableMode} or {SyntheticMode}; production recovery approvals require the privileged identity model (not implemented). Refusing to start.");
        }

        if (environmentName is not ("Development" or "Testing"))
        {
            throw new InvalidOperationException($"{JournalRecoveryOptions.SectionName}:Mode={SyntheticMode} is development-only and must not be set in the {environmentName} environment. Refusing to start.");
        }

        if (options.MaxApprovalLifetimeSeconds is < 60 or > 900)
        {
            throw new InvalidOperationException($"{JournalRecoveryOptions.SectionName}:MaxApprovalLifetimeSeconds must be 60..900.");
        }

        var operatorKeys = new HashSet<string>(operatorApproverKeys ?? [], StringComparer.Ordinal);
        var registry = new Dictionary<string, (byte[], DateTimeOffset?)>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var approver in options.Approvers)
        {
            if (string.IsNullOrWhiteSpace(approver.ApproverId) || approver.ApproverId.Length > 64 || string.IsNullOrWhiteSpace(approver.PublicKeyBase64))
            {
                throw new InvalidOperationException($"{JournalRecoveryOptions.SectionName}: every approver needs an id (≤ 64 characters) and a public key.");
            }

            if (operatorKeys.Contains(approver.PublicKeyBase64))
            {
                throw new InvalidOperationException($"{JournalRecoveryOptions.SectionName}: approver {approver.ApproverId} reuses an OperatorApprovals key; the recovery key set must be separate.");
            }

            var spki = Convert.FromBase64String(approver.PublicKeyBase64);
            using (var key = ECDsa.Create())
            {
                key.ImportSubjectPublicKeyInfo(spki, out _);
                if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                {
                    throw new InvalidOperationException($"{JournalRecoveryOptions.SectionName}: approver {approver.ApproverId} must use a P-256 key.");
                }
            }

            if (approver.RetiredOnUtc is { } retired && retired.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException($"{JournalRecoveryOptions.SectionName}: approver {approver.ApproverId} RetiredOnUtc must be UTC.");
            }

            if (!registry.TryAdd(approver.ApproverId, (spki, approver.RetiredOnUtc)) || !keys.Add(approver.PublicKeyBase64))
            {
                throw new InvalidOperationException($"{JournalRecoveryOptions.SectionName}: approver ids and keys must be distinct.");
            }
        }

        if (registry.Count < RequiredApprovals)
        {
            throw new InvalidOperationException($"{JournalRecoveryOptions.SectionName}: at least {RequiredApprovals} approvers with distinct keys are required.");
        }

        return new DPlusRecoveryApprovalVerifier(SyntheticMode, options.MaxApprovalLifetimeSeconds, registry);
    }

    /// <summary>
    /// Verifies one decision token against <paramref name="binding"/>: compact JWS, ES256, <c>kid</c> configured, signature, exact claim set,
    /// issuer/audience/subject, binding, lifetime bound and the key's retirement time. Returns null with <paramref name="reason"/> otherwise.
    /// Never checks <c>exp</c> against the current time (the executor does; D+ re-verifies an executed lift long after approval).
    /// </summary>
    public DPlusVerifiedDecision? Verify(string token, DPlusRecoveryBinding binding, out string reason)
    {
        ArgumentNullException.ThrowIfNull(binding);
        reason = string.Empty;
        if (!Operational)
        {
            reason = "recovery approval is not available in this environment";
            return null;
        }

        var parts = (token ?? string.Empty).Split('.');
        if (parts.Length != 3 || token!.Length > 8192)
        {
            reason = "not a compact JWS";
            return null;
        }

        JsonElement header;
        JsonElement claims;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement;
            claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement;
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            reason = "malformed";
            return null;
        }

        if (header.ValueKind != JsonValueKind.Object || Text(header, "alg") != "ES256" || Text(header, "kid") is not { } approverId || !approvers.TryGetValue(approverId, out var approver))
        {
            reason = "not signed by a configured recovery approver with ES256";
            return null;
        }

        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(approver.Spki, out _);
            if (signature.Length != 64 || !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256))
            {
                reason = "signature not valid";
                return null;
            }
        }

        if (claims.ValueKind != JsonValueKind.Object || claims.EnumerateObject().Any(p => !DPlusRecoveryClaims.Names.Contains(p.Name)))
        {
            reason = "unexpected claim set";
            return null;
        }

        if (Text(claims, "iss") != DPlusRecoveryClaims.Issuer || Text(claims, "aud") != DPlusRecoveryClaims.Audience || Text(claims, "sub") != approverId
            || !Guid.TryParseExact(Text(claims, "jti"), "D", out var jti))
        {
            reason = "wrong issuer, audience, subject or id";
            return null;
        }

        var kind = Text(claims, "approval_kind");
        var decision = Text(claims, "decision");
        var decisionValid = (kind, decision) is (DPlusRecoveryClaims.RecoveryKind, DPlusRecoveryClaims.Approve or DPlusRecoveryClaims.Reject) or (DPlusRecoveryClaims.CancellationKind, DPlusRecoveryClaims.Cancel);
        if (!decisionValid || !DPlusRecoveryCodec.IsSha256Hex(Text(claims, "justification_sha256")))
        {
            reason = "unknown decision or missing justification digest";
            return null;
        }

        if (!string.Equals(Text(claims, "statement_sha256"), binding.StatementSha256Hex, StringComparison.Ordinal)
            || Text(claims, "recovery_id") != binding.RecoveryId.ToString("D")
            || Text(claims, "organization_id") != binding.OrganizationId.ToString("D")
            || Text(claims, "stream_name") != binding.StreamName
            || Number(claims, "failed_revision") != binding.FailedRevision
            || Number(claims, "stop_generation") != binding.StopGeneration)
        {
            reason = "bound to a different statement, recovery, scope, revision or generation";
            return null;
        }

        if (Number(claims, "iat") is not { } iat || Number(claims, "exp") is not { } exp || Number(claims, "nbf") is not { } nbf)
        {
            reason = "lacks iat, nbf or exp";
            return null;
        }

        if (exp <= iat || exp - iat > MaxApprovalLifetimeSeconds || nbf > exp)
        {
            reason = "lifetime not acceptable";
            return null;
        }

        var issued = DateTimeOffset.FromUnixTimeSeconds(iat);
        if (approver.RetiredOnUtc is { } retired && issued > retired)
        {
            reason = "issued after the approver key was retired";
            return null;
        }

        return new DPlusVerifiedDecision(approverId, jti, kind!, decision!, issued, DateTimeOffset.FromUnixTimeSeconds(nbf), DateTimeOffset.FromUnixTimeSeconds(exp),
            DPlusRecoveryCodec.Sha256Hex(Encoding.ASCII.GetBytes(token)));
    }

    /// <summary>The jti claim of a token, without any verification (used only to name its create-only object).</summary>
    public static Guid? PeekJti(string token)
    {
        var parts = (token ?? string.Empty).Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement;
            return claims.ValueKind == JsonValueKind.Object && Guid.TryParseExact(Text(claims, "jti"), "D", out var jti) ? jti : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}

/// <summary>
/// The authorization of one statement derived from its verified decisions (A-4 §24.4): two approvals from distinct approvers, none of whom is
/// the requester or the analyst; no valid rejection or cancellation. <see cref="Approvals"/> holds the approvals that count.
/// </summary>
public sealed record DPlusRecoveryAuthorization(bool Authorized, string Reason, IReadOnlyList<DPlusVerifiedDecision> Approvals, bool Rejected, bool Cancelled)
{
    public static DPlusRecoveryAuthorization Evaluate(IReadOnlyList<DPlusVerifiedDecision> decisions, string requesterLabel, string analystLabel)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var rejected = decisions.Any(d => d.ApprovalKind == DPlusRecoveryClaims.RecoveryKind && d.Decision == DPlusRecoveryClaims.Reject);
        var cancelled = decisions.Any(d => d.ApprovalKind == DPlusRecoveryClaims.CancellationKind);
        var approvals = decisions.Where(d => d.ApprovalKind == DPlusRecoveryClaims.RecoveryKind && d.Decision == DPlusRecoveryClaims.Approve)
            .Where(d => !string.Equals(d.ApproverId, requesterLabel, StringComparison.Ordinal) && !string.Equals(d.ApproverId, analystLabel, StringComparison.Ordinal))
            .GroupBy(d => d.ApproverId, StringComparer.Ordinal).Select(g => g.OrderBy(d => d.IssuedOn).First()).OrderBy(d => d.ApproverId, StringComparer.Ordinal).ToList();
        if (rejected)
        {
            return new DPlusRecoveryAuthorization(false, "a valid rejection exists (final)", approvals, true, cancelled);
        }

        if (cancelled)
        {
            return new DPlusRecoveryAuthorization(false, "a valid cancellation exists (final)", approvals, false, true);
        }

        return approvals.Count >= DPlusRecoveryApprovalVerifier.RequiredApprovals
            ? new DPlusRecoveryAuthorization(true, "authorized by two distinct approvers", approvals, false, false)
            : new DPlusRecoveryAuthorization(false, string.Create(CultureInfo.InvariantCulture, $"{approvals.Count} qualifying approval(s); two distinct approvers other than the requester and analyst are required"), approvals, false, false);
    }
}
