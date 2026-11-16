namespace EnterpriseForms.Infrastructure.Authority;

/// <summary>
/// E3-M2f-1 (<c>docs/security/E3_M2F_CONTRACT_CLOSURE.md</c> §3, CAP-S1..S7): the business-build envelope capability declaration.
/// <para>
/// Level 2 means this compiled build encodes signed schema 2 faithfully (E3-M2b-2) and opens Envelope v2 through the verified-read
/// path (E3-M2c). It says nothing about Envelope v2 writing (E3-M4).
/// </para>
/// <para>
/// The value is a compile-time constant of the build. It is not configurable, and no user, organization, request or deployment
/// setting can change it. The three content workload clients (<see cref="AuthorityHttpClient"/>,
/// <see cref="ProtectionVerifierHttpClient"/>, <see cref="SearchIndexBuilderHttpClient"/>) declare it on every Authority request
/// from their single private send path.
/// </para>
/// <para>
/// The header is outside the request body, so it is outside the Authority's request digest and every idempotency identity. It is
/// self-asserted by an authenticated workload and protects only against honest older builds (CAP-S7). Authority-side enforcement is
/// E3-M2f-2; Authorities before it ignore the header.
/// </para>
/// </summary>
public static class EnvelopeCapability
{
    /// <summary>The contract-defined header name (CAP-S1).</summary>
    public const string HeaderName = "EnterpriseForms-Envelope-Capability";

    /// <summary>The capability level this build declares (CAP-S3, CAP-S4): exactly the ASCII string <c>2</c>.</summary>
    public const string DeclaredLevel = "2";

    /// <summary>
    /// Sets exactly one <see cref="HeaderName"/> value, <see cref="DeclaredLevel"/>, on an outgoing Authority request, replacing any
    /// value already present. Only the three clients' send paths call this; callers of those clients never see the request message.
    /// </summary>
    internal static void Declare(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Remove(HeaderName);
        request.Headers.Add(HeaderName, DeclaredLevel);
    }
}
