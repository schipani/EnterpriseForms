using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>An effective protection decision of a field: the command, its form and field, the direction and the deciding actor.</summary>
public sealed record ProtectionCommandScope(Guid CommandId, Guid FormDefinitionId, Guid FieldDefinitionId, ProtectionDirection Direction, IdentityBinding ActorBinding);

/// <summary>
/// The one rule for "this workload operation is scoped by an accepted protection command" (reprotection writes, Phase C6, and
/// the Phase C7 migration catalogue): the command exists in this organization, is an Effective PublishFormSecurityRevision,
/// and is still the field's current protection decision (a later command in either direction supersedes it).
/// </summary>
public static class ProtectionCommandResolver
{
    public static async Task<ProtectionCommandScope> ResolveAsync(IAuthorityScope scope, Guid organizationId, Guid commandId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var command = await scope.FindCommandAsync(commandId, cancellationToken).ConfigureAwait(false);
        if (command is null || command.OrganizationId != organizationId || command.CommandKindName != SecurityCommandKinds.PublishFormSecurityRevision || command.CommandStateName != SecurityCommandStates.Effective)
        {
            throw AuthorityException.Forbidden("requires an effective PublishFormSecurityRevision command of this organization");
        }

        PublishFormSecurityRevisionPayload payload;
        using (var document = System.Text.Json.JsonDocument.Parse(command.CommandPayloadJson))
        {
            payload = CanonicalJson.Deserialize<PublishFormSecurityRevisionPayload>(document.RootElement);
        }

        var binding = await scope.FindIdentityBindingByIdAsync(command.ActorIdentityBindingId, cancellationToken).ConfigureAwait(false)
                      ?? throw AuthorityException.Forbidden("the command's actor binding no longer exists");
        var direction = payload.RequiredProtectionMode == "Standard" ? ProtectionDirection.Decrypt : ProtectionDirection.Encrypt;

        var currentType = direction == ProtectionDirection.Encrypt ? ResourceTypeNames.FieldProtectionRequirement : ResourceTypeNames.FieldProtectionRelease;
        var current = await scope.FindResourceAsync(organizationId, currentType, payload.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.ResourceLifecycleState != ResourceLifecycleStates.Active || current.RegisteredBySecurityCommandId != command.SecurityCommandId)
        {
            throw AuthorityException.Forbidden("the command no longer describes the field's current protection (superseded or cancelled)");
        }

        return new ProtectionCommandScope(command.SecurityCommandId, payload.FormDefinitionId, payload.FieldDefinitionId, direction, binding);
    }
}
