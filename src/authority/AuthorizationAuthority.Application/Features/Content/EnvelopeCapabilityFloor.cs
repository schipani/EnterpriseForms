using System.Globalization;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md §3): the business envelope capability one Authority request declares in the
/// header <see cref="HeaderName"/>. A trusted workload-build compatibility signal, asserted by the authenticated workload (CAP-S7): it is
/// not an organization entitlement, not part of any request digest or idempotency identity, and not proof of software identity.
/// </summary>
public sealed class ClientEnvelopeCapability
{
    /// <summary>CAP-S1. The business declarer (<c>EnterpriseForms.Infrastructure.Authority.EnvelopeCapability</c>) uses the same name; an architecture test cross-checks them.</summary>
    public const string HeaderName = "EnterpriseForms-Envelope-Capability";

    private ClientEnvelopeCapability(int level) => Level = level;

    /// <summary>Level 1: the build cannot represent signed schema 2 and Envelope v2 faithfully. Every request without a valid declaration (CAP-S5, CAP-S6).</summary>
    public static ClientEnvelopeCapability Legacy { get; } = new(1);

    /// <summary>Level 2, the highest level this Authority recognizes (CAP-S4): signed schema-2 fidelity and verified Envelope v2 reads.</summary>
    public static ClientEnvelopeCapability Current { get; } = new(2);

    public int Level { get; }

    /// <summary>
    /// CAP-S3/S5/S6: exactly one header instance whose value is exactly the ASCII string <c>1</c> or <c>2</c>. Absent, empty, repeated,
    /// list-valued, padded, signed, zero-prefixed, unknown or non-digit values are level 1, never higher.
    /// </summary>
    public static ClientEnvelopeCapability FromHeaderValues(IReadOnlyList<string?>? values) =>
        values is { Count: 1 } && string.Equals(values[0], "2", StringComparison.Ordinal) ? Current : Legacy;

    public override string ToString() => Level.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The outcome of one capability evaluation (CAP-E4 operational signal; not durable refusal evidence, OD-M2f-09).</summary>
public static class EnvelopeCapabilityOutcomes
{
    public const string Admitted = "admitted";
    public const string Refused = "refused";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// CAP-E4: a structured log event and metric per evaluation, carrying the outcome, the parsed client level and the organization
/// identifier only (no record, user or binding data). Implemented by the host; optional (unit tests and tools pass none).
/// </summary>
public interface IEnvelopeCapabilityObserver
{
    void Evaluated(Guid organizationId, int clientLevel, string outcome);
}

/// <summary>
/// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md §4–§7, AM-F1): the one shared capability check of the seven content entry points
/// (Prepare, Commit, Verify, batch Verify, protection census, search-index backfill and deep-sweep verify). It runs in the entry point's
/// transaction, right after the organization lock (and, for Prepare and Commit, after the Release B fence), before actor resolution,
/// the organization gate, replay handling, the protection barrier, snapshot binding, pinning and every verification, so an honest older
/// business build is refused before its re-encoded bytes can quarantine a head or journal an incident (finding G-2).
/// <para>
/// Effective floor <c>E = max(D, O)</c> (CAP-F1): <c>D</c> is the deployment floor (<see cref="ConfigurationKey"/>, default 1);
/// <c>O</c> is 2 when the organization has any data-key binding in any state, else 1. The client level <c>C</c> is refused when
/// <c>C &lt; E</c>. Short-circuit (CAP-F3): <c>C &lt; D</c> refuses and <c>C ≥ max(D, 2)</c> admits without reading the ledger; only
/// <c>D ≤ C &lt; 2</c> runs the one boolean existence query AM-F1 permits. A query failure refuses (CAP-F5).
/// </para>
/// <para>
/// The check can only refuse (CAP-F6): it writes nothing, caches nothing, admits nothing a later check would refuse, and never
/// replaces CARV, the floors, the Release B fence, the ledger rules, authorization, quarantine or integrity verification.
/// </para>
/// </summary>
public sealed class EnvelopeCapabilityFloor
{
    /// <summary>§9: Authority deployment configuration. Absent ⇒ 1; present ⇒ exactly <c>1</c> or <c>2</c>, anything else refuses the start.</summary>
    public const string ConfigurationKey = "Content:MinimumBusinessEnvelopeCapability";

    /// <summary>CAP-E1/E2: the one fixed detail of every refusal (no identifiers, levels, floor or binding information).</summary>
    public const string RefusalDetail = "the calling workload's envelope capability is insufficient for this request; a current business build is required";

    /// <summary>CAP-F5: the fixed detail when the organization floor cannot be evaluated.</summary>
    public const string UnavailableDetail = "the envelope capability floor could not be evaluated";

    private readonly IEnvelopeCapabilityObserver? observer;

    private EnvelopeCapabilityFloor(int deploymentFloor, IEnvelopeCapabilityObserver? observer)
    {
        DeploymentFloor = deploymentFloor;
        this.observer = observer;
    }

    /// <summary>The floor of an Authority composed without configuration (unit tests, tools): deployment floor 1.</summary>
    public static EnvelopeCapabilityFloor Default { get; } = new(1, null);

    /// <summary><c>D</c>, 1 or 2.</summary>
    public int DeploymentFloor { get; }

    /// <summary>Readiness and startup text (INV-4, supporting evidence only; no identifiers).</summary>
    public string Description => "envelope capability floor enforced; deployment floor " + DeploymentFloor.ToString(CultureInfo.InvariantCulture);

    /// <summary>Builds the floor from the deployment's configured value. Throws (the host refuses to start) on an invalid value.</summary>
    public static EnvelopeCapabilityFloor Create(string? configuredValue, IEnvelopeCapabilityObserver? observer = null) =>
        new(ParseDeploymentFloor(configuredValue), observer);

    /// <summary>§9: absent ⇒ 1; exactly <c>1</c> or <c>2</c>; anything else (empty, padded, signed, zero-prefixed, other numbers) throws.</summary>
    public static int ParseDeploymentFloor(string? configuredValue) => configuredValue switch
    {
        null => 1,
        "1" => 1,
        "2" => 2,
        _ => throw new InvalidOperationException(ConfigurationKey + " must be absent, \"1\" or \"2\"; the Authorization Authority refuses to start."),
    };

    /// <summary>
    /// Refuses <paramref name="client"/> (null ⇒ level 1) with <c>409 CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT</c> when it is below the
    /// effective floor of <paramref name="lockedOrganization"/>. The caller holds that organization's row lock in <paramref name="scope"/>'s
    /// transaction (CAP-F4), which serializes this decision with every binding insert of the organization (FB-1).
    /// </summary>
    public async Task RequireAsync(IAuthorityScope scope, OrganizationAuthorizationState lockedOrganization, ClientEnvelopeCapability? client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(lockedOrganization);
        var organizationId = lockedOrganization.OrganizationId;
        var level = (client ?? ClientEnvelopeCapability.Legacy).Level;
        if (level < DeploymentFloor)
        {
            throw Refuse(organizationId, level);
        }

        if (level >= Math.Max(DeploymentFloor, ClientEnvelopeCapability.Current.Level))
        {
            observer?.Evaluated(organizationId, level, EnvelopeCapabilityOutcomes.Admitted);
            return;
        }

        bool bound;
        try
        {
            // AM-F1: the only ledger read outside the content write rules; a boolean, nothing else is materialized or kept.
            bound = await scope.OrganizationHasDataKeyBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            observer?.Evaluated(organizationId, level, EnvelopeCapabilityOutcomes.Unavailable);
            throw AuthorityException.NotReady(UnavailableDetail);
        }

        if (bound)
        {
            throw Refuse(organizationId, level);
        }

        observer?.Evaluated(organizationId, level, EnvelopeCapabilityOutcomes.Admitted);
    }

    private AuthorityException Refuse(Guid organizationId, int level)
    {
        observer?.Evaluated(organizationId, level, EnvelopeCapabilityOutcomes.Refused);

        // E3-M2f-3b: the organization and parsed level travel to the evidence handler in Exception.Data (never serialized); the
        // exception type, code, status, retryable flag and detail, and so the 409 on the wire, are exactly those of E3-M2f-2. The
        // same context is attached whichever floor refused (CAP-E2: no cause is carried).
        return CapabilityRefusalContext.Attach(new AuthorityException(AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient, 409, false, RefusalDetail), organizationId, level);
    }
}
