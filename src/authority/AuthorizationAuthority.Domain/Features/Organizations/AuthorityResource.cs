namespace AuthorizationAuthority.Domain.Features.Organizations;

/// <summary>Independent resource registry (invariant A7): a business row that is not registered here is never a valid protected resource.</summary>
public sealed class AuthorityResource
{
    public Guid AuthorityResourceId { get; set; }

    public Guid OrganizationId { get; set; }

    public string ResourceTypeName { get; set; } = string.Empty;

    public Guid ResourceEntityId { get; set; }

    public string ResourceLifecycleState { get; set; } = ResourceLifecycleStates.Active;

    /// <summary>For form_record resources the registered form definition; for record_share resources the record. Otherwise null.</summary>
    public Guid? ParentResourceEntityId { get; set; }

    public Guid RegisteredBySecurityCommandId { get; set; }

    public DateTimeOffset ResourceRegisteredOn { get; set; }
}

public static class ResourceLifecycleStates
{
    public const string Registered = "Registered";
    public const string Active = "Active";
    public const string Revoked = "Revoked";
    public const string Quarantined = "Quarantined";
}

/// <summary>SpiceDB object type names (spicedb.schema.zed) doubling as registry type names.</summary>
public static class ResourceTypeNames
{
    public const string Organization = "organization";
    public const string Team = "team";
    public const string FormDefinition = "form_definition";
    public const string FormRecord = "form_record";
    public const string RecordShare = "record_share";
    public const string FieldItem = "field_item";
    public const string DocumentItem = "document_item";
    public const string User = "user";
    public const string ProjectionStep = "projection_step";

    /// <summary>A field that must be application-encrypted (entity id = field definition id, parent = form definition id).</summary>
    public const string FieldProtectionRequirement = "field_protection_requirement";
}
