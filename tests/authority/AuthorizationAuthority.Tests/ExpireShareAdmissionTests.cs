using System.Text.Json;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP6: Focused tests for ExpireShare admission decision covering all 18 required checks.
/// </summary>
public sealed class ExpireShareAdmissionTests
{
    private static readonly Guid OrgId = Guid.CreateVersion7();
    private static readonly Guid ShareId = Guid.CreateVersion7();
    private static readonly Guid RecordId = Guid.CreateVersion7();
    private static readonly Guid ActorId = Guid.CreateVersion7();
    private static readonly Guid SourceCommandId = Guid.CreateVersion7();
    private static readonly DateTimeOffset BaseTime = new(2024, 1, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiryTime = new(2024, 1, 15, 9, 0, 0, TimeSpan.Zero); // 1h before base

    [Fact]
    public void EligibleWhenAllConditionsMet()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Eligible, result.Disposition);
        Assert.Equal("ELIGIBLE", result.ReasonCode);
        Assert.Equal(ActorId, result.DerivedActorIdentityBindingId);
        Assert.Equal($"share-expiry:{ShareId:D}:1", result.IdempotencyKey);
    }

    [Fact]
    public void EligibleAtExpiryEqualsEvaluatedOn()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(BaseTime);
        var sourceCommand = CreateSourceCommand(BaseTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Eligible, result.Disposition);
    }

    [Fact]
    public void EffectiveScopeUpdateCanBeTheExpirySource()
    {
        var source = CreateSourceCommand(ExpiryTime);
        source.CommandKindName = "UpdateShareScope";
        source.CommandPayloadJson = JsonSerializer.Serialize(new
        {
            record_share_id = ShareId, form_record_id = RecordId, share_expires_on = ExpiryTime,
            field_item_ids = new[] { Guid.NewGuid() }, recipient_role = "Reader",
            expected_share_revision = 3, share_revision = 4,
        });
        var metadata = CreateShareMetadata(ExpiryTime);
        metadata.ShareRevision = 4;
        var result = ExpireShareAdmission.Evaluate(CreateOrganization(), CreateShareResource(), metadata, 4,
            source, BaseTime, true, BaseTime, hasDelegatedAssertion: false);
        Assert.Equal(ExpireShareAdmission.Disposition.Eligible, result.Disposition);
        Assert.Equal($"share-expiry:{ShareId:D}:4", result.IdempotencyKey);
    }

    [Fact]
    public void RefusesARepeatedBindingInTheSourceObject()
    {
        var source = CreateSourceCommand(ExpiryTime);
        source.CommandPayloadJson = source.CommandPayloadJson.Replace("\"form_record_id\":",
            $"\"record_share_id\":\"{ShareId:D}\",\"form_record_id\":", StringComparison.Ordinal);
        var result = ExpireShareAdmission.Evaluate(CreateOrganization(), CreateShareResource(), CreateShareMetadata(ExpiryTime), 1,
            source, BaseTime, true, BaseTime, hasDelegatedAssertion: false);
        Assert.Equal("PAYLOAD_MALFORMED", result.ReasonCode);
        Assert.Null(result.DerivedActorIdentityBindingId);
    }

    [Fact]
    public void ClockUnsafeWhenEvaluatedOnBeforeLatestJournal()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.ClockUnsafe, result.Disposition);
        Assert.Equal("CLOCK_BACKWARDS", result.ReasonCode);
        Assert.Null(result.DerivedActorIdentityBindingId);
        Assert.Null(result.IdempotencyKey);
    }

    [Fact]
    public void IneligibleWhenGateClosed()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, false, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("GATE_CLOSED", result.ReasonCode);
    }

    [Theory]
    [InlineData(true, "DELEGATED_ASSERTION_REFUSED")]
    [InlineData(false, "GATE_CLOSED")]
    public void RefusesDelegatedOrQuarantinedRequests(bool delegated, string expectedReason)
    {
        var org = CreateOrganization();
        org.IntegrityQuarantineEnabled = !delegated;
        var result = ExpireShareAdmission.Evaluate(org, CreateShareResource(), CreateShareMetadata(ExpiryTime),
            1L, CreateSourceCommand(ExpiryTime), BaseTime, true, BaseTime, delegated);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Null(result.DerivedActorIdentityBindingId);
        Assert.Null(result.IdempotencyKey);
    }

    [Fact]
    public void IneligibleWhenResourceTypeNotRecordShare()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        resource.ResourceTypeName = ResourceTypeNames.FormRecord;
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("RESOURCE_TYPE_NOT_RECORD_SHARE", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenResourceMetadataIdMismatch()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        metadata.RecordShareId = Guid.CreateVersion7();
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("RESOURCE_METADATA_ID_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenResourceMetadataOrgMismatch()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        resource.OrganizationId = Guid.CreateVersion7();
        var metadata = CreateShareMetadata(ExpiryTime);
        metadata.OrganizationId = Guid.CreateVersion7();
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("RESOURCE_METADATA_ORG_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenShareWrongTenant()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        metadata.OrganizationId = Guid.CreateVersion7();
        resource.OrganizationId = metadata.OrganizationId;
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("SHARE_WRONG_TENANT", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenRecordIdMissing()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        resource.ParentResourceEntityId = null;
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("RECORD_ID_MISSING", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenShareNotActive()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        resource.ResourceLifecycleState = ResourceLifecycleStates.Expired;
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("SHARE_NOT_ACTIVE", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenShareRevisionMismatch()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        metadata.ShareRevision = 2;
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("SHARE_REVISION_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenNoExpirySet()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(null);
        var sourceCommand = CreateSourceCommand(null);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("NO_EXPIRY_SET", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenNotYetExpired()
    {
        var futureExpiry = BaseTime.AddHours(1);
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(futureExpiry);
        var sourceCommand = CreateSourceCommand(futureExpiry);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("NOT_YET_EXPIRED", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenNoExpirySourceCommand()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        metadata.ExpirySourceSecurityCommandId = null;
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, null, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("NO_EXPIRY_SOURCE_COMMAND", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenExpirySourceCommandNotFound()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, null, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("EXPIRY_SOURCE_COMMAND_NOT_FOUND", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenSourceCommandIdMismatch()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        sourceCommand.SecurityCommandId = Guid.CreateVersion7();
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("SOURCE_COMMAND_ID_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenSourceCommandNotEffective()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        sourceCommand.CommandStateName = SecurityCommandStates.Pending;
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("SOURCE_COMMAND_NOT_EFFECTIVE", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenSourceCommandWrongOrg()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        sourceCommand.OrganizationId = Guid.CreateVersion7();
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("SOURCE_COMMAND_WRONG_ORG", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenSourceCommandWrongKind()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        sourceCommand.CommandKindName = SecurityCommandKinds.RevokeShare;
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("SOURCE_COMMAND_WRONG_KIND", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenActorIdEmpty()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        sourceCommand.ActorIdentityBindingId = Guid.Empty;
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("ACTOR_ID_EMPTY", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenPayloadMalformed()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var sourceCommand = CreateSourceCommand(ExpiryTime);
        sourceCommand.CommandPayloadJson = "not valid json";
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("PAYLOAD_MALFORMED", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenPayloadShareIdMismatch()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var wrongShareId = Guid.CreateVersion7();
        var sourceCommand = CreateSourceCommand(ExpiryTime, wrongShareId);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("PAYLOAD_SHARE_ID_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenPayloadRecordIdMismatch()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var wrongRecordId = Guid.CreateVersion7();
        var sourceCommand = CreateSourceCommand(ExpiryTime, ShareId, wrongRecordId);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("PAYLOAD_RECORD_ID_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void IneligibleWhenPayloadExpiryMismatch()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var metadata = CreateShareMetadata(ExpiryTime);
        var wrongExpiry = ExpiryTime.AddHours(1);
        var sourceCommand = CreateSourceCommand(wrongExpiry);
        var latestJournal = BaseTime.AddMinutes(-5);

        var result = ExpireShareAdmission.Evaluate(org, resource, metadata, 1L, sourceCommand, BaseTime, true, latestJournal, hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Ineligible, result.Disposition);
        Assert.Equal("PAYLOAD_EXPIRY_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void EligibleWhenTheJournalledExpiryCarriesSubMicrosecondTicksTheStoreDropped()
    {
        // C-201 real-elapsed-time Stage B: the request's expiry keeps 100 ns ticks in the journalled payload, AuthorityDb stores microseconds.
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var journalled = ExpiryTime.AddTicks(6_805_932);
        var stored = ExpiryTime.AddTicks(6_805_930);
        var result = ExpireShareAdmission.Evaluate(org, resource, CreateShareMetadata(stored), 1L, CreateSourceCommand(journalled), BaseTime, true, BaseTime.AddMinutes(-5), hasDelegatedAssertion: false);

        Assert.Equal(ExpireShareAdmission.Disposition.Eligible, result.Disposition);
    }

    [Fact]
    public void IneligibleWhenTheCommittedExpiryMovedByOneMicrosecond()
    {
        var org = CreateOrganization();
        var resource = CreateShareResource();
        var result = ExpireShareAdmission.Evaluate(org, resource, CreateShareMetadata(ExpiryTime.AddMicroseconds(1)), 1L, CreateSourceCommand(ExpiryTime), BaseTime, true, BaseTime.AddMinutes(-5), hasDelegatedAssertion: false);

        Assert.Equal("PAYLOAD_EXPIRY_MISMATCH", result.ReasonCode);
    }

    private static OrganizationAuthorizationState CreateOrganization() => new()
    {
        OrganizationId = OrgId,
        AuthorizationRevision = 100,
    };

    private static AuthorityResource CreateShareResource() => new()
    {
        ResourceEntityId = ShareId,
        OrganizationId = OrgId,
        ResourceTypeName = ResourceTypeNames.RecordShare,
        ResourceLifecycleState = ResourceLifecycleStates.Active,
        ParentResourceEntityId = RecordId,
        RegisteredBySecurityCommandId = SourceCommandId,
    };

    private static ShareResourceMetadata CreateShareMetadata(DateTimeOffset? expiresOn) => new()
    {
        RecordShareId = ShareId,
        OrganizationId = OrgId,
        ShareExpiresOn = expiresOn,
        ShareRevision = 1,
        ExpirySourceSecurityCommandId = SourceCommandId,
        ShareMetadataCreatedOn = BaseTime.AddDays(-1),
        ShareMetadataModifiedOn = BaseTime.AddDays(-1),
    };

    private static SecurityCommand CreateSourceCommand(DateTimeOffset? expiresOn, Guid? shareId = null, Guid? recordId = null)
    {
        var sid = shareId ?? ShareId;
        var rid = recordId ?? RecordId;
        var payload = JsonSerializer.Serialize(new
        {
            record_share_id = sid,
            form_record_id = rid,
            recipient_user_id = ActorId,
            recipient_team_id = (Guid?)null,
            recipient_role = "Reader",
            field_item_ids = new[] { Guid.NewGuid() },
            parent_record_share_id = (Guid?)null,
            delegation_depth = 1,
            share_expires_on = expiresOn,
        });

        return new SecurityCommand
        {
            SecurityCommandId = SourceCommandId,
            OrganizationId = OrgId,
            ActorIdentityBindingId = ActorId,
            CommandKindName = SecurityCommandKinds.CreateShare,
            CommandStateName = SecurityCommandStates.Effective,
            CommandPayloadJson = payload,
            CommandCreatedOn = BaseTime.AddDays(-1),
            CommandModifiedOn = BaseTime.AddDays(-1),
            CommandEffectiveOn = BaseTime.AddDays(-1),
        };
    }
}
