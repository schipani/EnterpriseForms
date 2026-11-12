using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Tests;

/// <summary>C8 M4 S1 test helpers: a verifier over a fake projection, and the marker a consistent projection reports.</summary>
internal static class TestProjection
{
    public static ProjectionIntegrityVerifier Verifier(IAuthorityStore store, ISpiceDbProjection spiceDb, ISecurityAlertSink? alerts = null, bool transitionCompleted = true)
    {
        var transition = new ProjectionMarkerTransitionState();
        if (transitionCompleted)
        {
            transition.MarkCompleted();
        }

        return new ProjectionIntegrityVerifier(store, spiceDb, transition, alerts ?? new RecordingAlerts(), new ProjectionIntegrityMetrics(), TimeProvider.System);
    }

    /// <summary>Gives a fixture state the journal digest every projected revision has in production (the expected marker needs it).</summary>
    public static OrganizationAuthorizationState WithJournalDigest(this OrganizationAuthorizationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ProjectionAppliedRevision > 0 && state.LatestSecurityJournalDigest is null)
        {
            state.LatestSecurityJournalDigest = System.Security.Cryptography.SHA256.HashData(state.OrganizationId.ToByteArray());
        }

        return state;
    }

    /// <summary>What a SpiceDB holding exactly this state's projection returns for a marker read (pin token derived from the revision).</summary>
    public static MarkerObservation MarkerFor(OrganizationAuthorizationState state) =>
        new([ProjectionMarker.ExpectedFor(state.WithJournalDigest())], "pin-" + state.ProjectionAppliedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public sealed class RecordingAlerts : ISecurityAlertSink
    {
        public List<SecurityAlert> Raised { get; } = [];

        public void Raise(SecurityAlert alert) => Raised.Add(alert);
    }
}
