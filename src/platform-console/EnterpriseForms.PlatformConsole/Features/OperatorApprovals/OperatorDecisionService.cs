using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.PlatformConsole.Composition;
using EnterpriseForms.PlatformConsole.Features.Session;
using EnterpriseForms.PlatformConsole.Infrastructure;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.PlatformConsole.Features.OperatorApprovals;

/// <summary>What the browser may send for a decision: the statement fingerprint the operator reviewed and their justification. Nothing else (unknown members are refused).</summary>
public sealed record OperatorDecisionInput(string ExpectedStatementSha256, string Justification);

/// <summary>
/// One operator decision (approve, withdraw, reject) for the signed-in operator only. The console does not decide anything: it
/// (1) re-reads the operator's registry entry and requires it to be the session's operator with the session's own signing key,
/// (2) asks the Authority for the current statement of this exact request (dry run, changes nothing) and refuses to sign unless it
/// equals the fingerprint the operator reviewed, (3) signs evidence over that statement, the decision and the justification with the
/// operator's own Vault Transit key, and (4) submits it to the Authority, which verifies the evidence against its registry, applies
/// every rule (first approval pending, second approval by a different operator executes, refusals journaled) and answers.
/// <para>
/// Not a signing service: the claims are fixed (issuer, audience, operation kind, decision from the route, operation and organization
/// from the route, statement digest from the Authority, justification digest computed here), the key comes from the session, and
/// the evidence is never returned to the browser or logged. Each submission has a fresh evidence id (single use at the Authority).
/// </para>
/// </summary>
public sealed partial class OperatorDecisionService(
    AuthorityPlatformClient authority,
    IOperatorEvidenceSigner signer,
    OperatorSessionStore store,
    IOptions<PlatformConsoleOptions> options,
    IHostEnvironment environment,
    TimeProvider clock,
    ILogger<OperatorDecisionService> logger)
{
    public const string EvidenceKind = "SyntheticDevelopmentJws";
    public const string StatusCheckJustification = "Platform Console status check (dry run; nothing is recorded)";
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(5);

    public async Task<PlatformMembershipDecisionResponse> DecideAsync(HttpContext http, OperatorSession session, Guid organizationId, Guid requestId, string decision, OperatorDecisionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);
        var expected = input.ExpectedStatementSha256?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!Sha256Hex().IsMatch(expected))
        {
            throw new ConsoleException(ConsoleErrorCodes.InvalidRequest, 400, "expectedStatementSha256 must be the 64-character hex fingerprint of the statement you reviewed");
        }

        var justification = input.Justification?.Trim() ?? string.Empty;
        if (justification.Length is < 10 or > 500 || justification.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            throw new ConsoleException(ConsoleErrorCodes.InvalidRequest, 400, "a justification of 10 to 500 characters is required");
        }

        var correlation = SessionEndpoints.Correlation(http);
        return await OperatorSessionGuard.RunAsync(http, session, store, environment, async () =>
        {
            try
            {
                // (1) The registry entry is still this session's operator, active, with this session's own key.
                var profile = await OperatorProfiles.CurrentAsync(http, session, authority, store, options.Value, environment, cancellationToken).ConfigureAwait(false);

                // (2) The Authority's current statement for this exact request; nothing is signed for a statement the operator did not review.
                var preview = await authority.DecideAsync(session.SubjectIdentifier, organizationId, requestId, decision, new PlatformMembershipDecisionRequest(expected, justification, DryRun: true), correlation, cancellationToken).ConfigureAwait(false);
                if (preview.RequestId != requestId || preview.OrganizationId != organizationId || !Sha256Hex().IsMatch(preview.StatementSha256))
                {
                    throw new ConsoleException(ConsoleErrorCodes.AuthorityUnavailable, 502, "the Authority's preview does not describe this request", correlationId: correlation);
                }

                if (!string.Equals(preview.StatementSha256, expected, StringComparison.Ordinal))
                {
                    throw new ConsoleException(ConsoleErrorCodes.ApprovalStatementChanged, 409, "the request changed since it was reviewed; review it again", correlationId: correlation);
                }

                // (3) Evidence of this operator's decision, signed with this operator's key only.
                var evidence = await SignEvidenceAsync(session, profile.Operator, PlatformOperationKinds.MembershipApproval, decision, organizationId, requestId, preview.StatementSha256, justification, cancellationToken).ConfigureAwait(false);

                // (4) The Authority verifies and decides.
                var outcome = await authority.DecideAsync(session.SubjectIdentifier, organizationId, requestId, decision,
                    new PlatformMembershipDecisionRequest(preview.StatementSha256, justification, DryRun: false, EvidenceKind, evidence), correlation, cancellationToken).ConfigureAwait(false);
                ConsoleLog.Decided(logger, session.PlatformOperatorId, decision, requestId, outcome.Outcome, correlation);
                return outcome;
            }
            catch (ConsoleException ex)
            {
                ConsoleLog.DecisionRefused(logger, session.PlatformOperatorId, decision, requestId, ex.ErrorCode, correlation);
                throw;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// C8 onboarding (C-104): the same four steps for one onboarding request (approve, withdraw, reject, cancel or initialize). The
    /// organization id bound into the evidence is the one the Authority reports for this request in its dry run, never a browser value,
    /// and nothing is signed for a decision the Authority's dry run already refuses.
    /// </summary>
    public async Task<OrganizationOnboardingDecisionResponse> DecideOnboardingAsync(HttpContext http, OperatorSession session, Guid requestId, string decision, OperatorDecisionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);
        var (expected, justification) = ValidateInput(input);
        var correlation = SessionEndpoints.Correlation(http);
        return await OperatorSessionGuard.RunAsync(http, session, store, environment, async () =>
        {
            try
            {
                var profile = await OperatorProfiles.CurrentAsync(http, session, authority, store, options.Value, environment, cancellationToken).ConfigureAwait(false);
                var preview = await authority.DecideOnboardingAsync(session.SubjectIdentifier, requestId, decision, new OrganizationOnboardingDecisionRequest(expected, justification, null, null, DryRun: true), correlation, cancellationToken).ConfigureAwait(false);
                if (preview.RequestId != requestId || preview.OrganizationId == Guid.Empty || !Sha256Hex().IsMatch(preview.StatementSha256))
                {
                    throw new ConsoleException(ConsoleErrorCodes.AuthorityUnavailable, 502, "the Authority's preview does not describe this request", correlationId: correlation);
                }

                if (!string.Equals(preview.StatementSha256, expected, StringComparison.Ordinal))
                {
                    throw new ConsoleException(ConsoleErrorCodes.ApprovalStatementChanged, 409, "the request changed since it was reviewed; review it again", correlationId: correlation);
                }

                if (!preview.Eligible)
                {
                    throw new ConsoleException(preview.RefusalCode ?? ConsoleErrorCodes.InvalidRequest, preview.RefusalCode == "SIGNING_UNAVAILABLE" ? 503 : 409, preview.RefusalDetail ?? "the Authority refuses this decision", correlationId: correlation);
                }

                var evidence = await SignEvidenceAsync(session, profile.Operator, PlatformOperationKinds.OrganizationOnboarding, decision, preview.OrganizationId, requestId, preview.StatementSha256, justification, cancellationToken).ConfigureAwait(false);
                var outcome = await authority.DecideOnboardingAsync(session.SubjectIdentifier, requestId, decision,
                    new OrganizationOnboardingDecisionRequest(preview.StatementSha256, justification, EvidenceKind, evidence, DryRun: false), correlation, cancellationToken).ConfigureAwait(false);
                ConsoleLog.Decided(logger, session.PlatformOperatorId, decision, requestId, outcome.Outcome, correlation);
                return outcome;
            }
            catch (ConsoleException ex)
            {
                ConsoleLog.DecisionRefused(logger, session.PlatformOperatorId, decision, requestId, ex.ErrorCode, correlation);
                throw;
            }
        }).ConfigureAwait(false);
    }

    private static (string Expected, string Justification) ValidateInput(OperatorDecisionInput input)
    {
        var expected = input.ExpectedStatementSha256?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!Sha256Hex().IsMatch(expected))
        {
            throw new ConsoleException(ConsoleErrorCodes.InvalidRequest, 400, "expectedStatementSha256 must be the 64-character hex fingerprint of the statement you reviewed");
        }

        var justification = input.Justification?.Trim() ?? string.Empty;
        if (justification.Length is < 10 or > 500 || justification.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            throw new ConsoleException(ConsoleErrorCodes.InvalidRequest, 400, "a justification of 10 to 500 characters is required");
        }

        return (expected, justification);
    }

    /// <summary>Approval status without disclosure audit: the Authority's dry run (no evidence, nothing recorded) reports the valid approvals and this operator's eligibility.</summary>
    public async Task<PlatformMembershipDecisionResponse> PreviewAsync(HttpContext http, OperatorSession session, Guid organizationId, Guid requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await OperatorSessionGuard.RunAsync(http, session, store, environment, () =>
            authority.DecideAsync(session.SubjectIdentifier, organizationId, requestId, OperatorDecisions.Approve, new PlatformMembershipDecisionRequest(string.Empty, StatusCheckJustification, DryRun: true), SessionEndpoints.Correlation(http), cancellationToken)).ConfigureAwait(false);
    }

    private async Task<string> SignEvidenceAsync(OperatorSession session, PlatformOperatorSummary registered, string operationKind, string decision, Guid organizationId, Guid requestId, string statementSha256, string justification, CancellationToken cancellationToken)
    {
        // The key is the session's (fixed at sign-in from the configuration for this identity) and must still be the registered one.
        if (!string.Equals(registered.SyntheticKeyIdentifier, session.SigningKeyName, StringComparison.Ordinal) || registered.PlatformOperatorId != session.PlatformOperatorId)
        {
            throw new ConsoleException(ConsoleErrorCodes.SigningKeyMismatch, 403, "the operator's registered signing key is not this session's key; signing is refused");
        }

        var now = clock.GetUtcNow();
        var header = new JsonObject { ["alg"] = "ES256", ["typ"] = "JWT", ["kid"] = session.SigningKeyName };
        var claims = new JsonObject
        {
            ["iss"] = PlatformOperatorEvidenceClaims.Issuer,
            ["aud"] = PlatformOperatorEvidenceClaims.Audience,
            ["sub"] = session.SigningKeyName,
            ["jti"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-5).ToUnixTimeSeconds(),
            ["exp"] = now.Add(EvidenceLifetime).ToUnixTimeSeconds(),
            [PlatformOperatorEvidenceClaims.PlatformOperatorId] = session.PlatformOperatorId.ToString("D"),
            [PlatformOperatorEvidenceClaims.Decision] = decision,
            [PlatformOperatorEvidenceClaims.OperationKind] = operationKind,
            [PlatformOperatorEvidenceClaims.OperationId] = requestId.ToString("D"),
            [PlatformOperatorEvidenceClaims.OrganizationId] = organizationId.ToString("D"),
            [PlatformOperatorEvidenceClaims.StatementDigest] = statementSha256,
            [PlatformOperatorEvidenceClaims.JustificationDigest] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(justification))),
        };
        var signingInput = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signature = await signer.SignAsync(session.SigningKeyName, Encoding.ASCII.GetBytes(signingInput), cancellationToken).ConfigureAwait(false);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();
}
