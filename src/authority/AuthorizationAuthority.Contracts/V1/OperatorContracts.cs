namespace AuthorizationAuthority.Contracts.V1;

// Operator-plane contract. Reachable only with the separate operator credential; never with the business workload credential.
// Stage A: the bootstrap replaces the PIM/dual-control provisioning workflow that production requires (documented gap).

public sealed record BootstrapIdentity(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, IReadOnlyList<string> OrganizationRoles);

public static class OrganizationRoleNames
{
    public const string OrganizationOwner = "organization_owner";
    public const string SystemAdministrator = "system_administrator";
    public const string Designer = "designer";
}

public sealed record BootstrapOrganizationRequest(Guid OrganizationId, string OrganizationDisplayName, IReadOnlyList<BootstrapIdentity> Identities);

public sealed record BootstrapOrganizationResponse(Guid OrganizationId, long AuthorizationRevision, Guid BootstrapCommandId, string IntegrityKeyName, int SigningKeyVersion, Guid CorrelationId);

public sealed record OrganizationGateStatus(Guid OrganizationId, long AuthorizationRevision, long ProjectionAppliedRevision, long ContentIntegrityRevision, ProjectionGateState ProjectionGateState, int PendingArchiveEntries, bool IntegrityQuarantineEnabled);

/// <summary>Testing-environment-only deterministic crash points (SEC-P09). Refused unless FaultInjection:Enabled.</summary>
public sealed record FaultInjectionRequest(string FaultName);

public static class FaultNames
{
    public const string None = "None";
    public const string CrashAfterJournalCommit = "CrashAfterJournalCommit";
    public const string CrashAfterProjectionWrite = "CrashAfterProjectionWrite";
    public const string CrashAfterContentHeadCommit = "CrashAfterContentHeadCommit";

    /// <summary>Testing only: terminate after a prepared content operation is durably stored, before the response is sent (the caller sees a transport failure and retries).</summary>
    public const string CrashAfterPrepareCommit = "CrashAfterPrepareCommit";

    /// <summary>Testing only: hold the next content commit before it takes the organization lock until released (fault "None") or 60 s pass (Phase C5 revocation races).</summary>
    public const string PauseBeforeContentCommit = "PauseBeforeContentCommit";
}

/// <summary>Testing only: state of the fault injector (armed fault and number of operations currently held at a pause point).</summary>
public sealed record FaultInjectionStatus(string ArmedFault, int PausedOperations);

/// <summary>Testing only: override of the contributor-write switch (null restores the deployment configuration).</summary>
public sealed record ContributorWriteOverrideRequest(bool? Enabled);

public sealed record ContributorWriteSwitchState(bool Enabled, bool ConfiguredEnabled);
