using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.FormDefinitions;

namespace EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;

/// <summary>
/// Stage A form registration: the authority registers the form resource (designer role required) and the business
/// store records the definition, stable field identities and an immutable published v1 layout. Local rows are
/// written only after the authority command is Effective (A7: no phantom resources).
/// </summary>
public sealed partial class CreateFormDefinitionHandler(IBusinessStore store, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex FieldKeyPattern();

    public async Task<FormDefinitionResponse> HandleAsync(RequestContext context, CreateFormDefinitionRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FormDefinitionName) || request.FormDefinitionName.Length > 200)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "form_definition_name is required (max 200)");
        }

        if (request.Fields.Count is 0 or > 250 || request.Fields.Select(f => f.FieldKey).Distinct(StringComparer.Ordinal).Count() != request.Fields.Count
            || request.Fields.Any(f => !FieldKeyPattern().IsMatch(f.FieldKey) || string.IsNullOrWhiteSpace(f.FieldDisplayName) || f.FieldDisplayName.Length > 200))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "1..250 fields with unique snake_case keys and display names are required");
        }

        var formDefinitionId = DeterministicId(idempotencyKey, "form");
        var payload = JsonSerializer.SerializeToElement(new { form_definition_id = formDefinitionId, form_user_user_ids = request.FormUserUserIds, form_user_team_ids = request.FormUserTeamIds });
        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(
            context.OrganizationId,
            SecurityCommandKind.RegisterFormDefinition,
            new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId),
            await CurrentAuthorizationRevisionAsync(context, cancellationToken).ConfigureAwait(false),
            payload,
            "register form " + request.FormDefinitionName), "form:" + idempotencyKey, cancellationToken).ConfigureAwait(false);

        var outcome = new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, command.CommandStatus == SecurityCommandStatus.Effective);
        if (!outcome.Effective)
        {
            throw new PendingCommandException(outcome);
        }

        var now = clock.GetUtcNow();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var existing = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var existingFields = await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
            return ToResponse(existing, existingFields, outcome);
        }

        var versionId = Guid.CreateVersion7();
        var fields = request.Fields.Select(f => new FieldDefinition
        {
            FieldDefinitionId = Guid.CreateVersion7(),
            FormDefinitionId = formDefinitionId,
            FieldKey = f.FieldKey,
            FieldDisplayName = f.FieldDisplayName,
            FieldDescription = string.Empty,
            FieldDataType = "String",
            FieldClassification = "Internal",
            IsSearchable = false,
            LifecycleStatus = "Available",
            CreatedOn = now,
            CreatedByUserId = context.LocalUserId,
        }).ToList();

        var layout = new
        {
            layoutSchemaVersion = 1,
            title = request.FormDefinitionName,
            sections = new[]
            {
                new
                {
                    sectionKey = "main",
                    title = request.FormDefinitionName,
                    gridColumns = 4,
                    elements = fields.Select(f => new { kind = "field", fieldDefinitionId = f.FieldDefinitionId, control = "text", label = f.FieldDisplayName, layout = new { columnSpan = 4, startOnNewRow = false } }).ToArray(),
                },
            },
        };

        var definition = new FormDefinition
        {
            FormDefinitionId = formDefinitionId,
            TenantId = context.OrganizationId,
            FormDefinitionName = request.FormDefinitionName,
            FormDefinitionDescription = request.FormDefinitionDescription ?? string.Empty,
            CurrentFormVersionId = versionId,
            VersionBehavior = "Latest",
            LifecycleStatus = "Active",
            CreatedOn = now,
            CreatedByUserId = context.LocalUserId,
            ModifiedOn = now,
            ModifiedByUserId = context.LocalUserId,
            AuthorityCommandId = command.CommandId,
        };
        var version = new FormVersion
        {
            FormVersionId = versionId,
            FormDefinitionId = formDefinitionId,
            VersionNumber = 1,
            LayoutSchemaVersion = 1,
            LayoutJson = JsonSerializer.Serialize(layout),
            CreatedOn = now,
            CreatedByUserId = context.LocalUserId,
            ChangeReason = "Stage A initial publication",
            PublishedOn = now,
            PublishedByUserId = context.LocalUserId,
        };
        scope.AddFormDefinition(definition, version, fields);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(definition, fields, outcome);
    }

    private static FormDefinitionResponse ToResponse(FormDefinition definition, IReadOnlyList<FieldDefinition> fields, CommandOutcome outcome) =>
        new(definition.FormDefinitionId, definition.TenantId, definition.FormDefinitionName, definition.CurrentFormVersionId ?? Guid.Empty,
            fields.OrderBy(f => f.FieldKey, StringComparer.Ordinal).Select(f => new FieldResponse(f.FieldDefinitionId, f.FieldKey, f.FieldDisplayName, f.FieldDataType)).ToList(),
            outcome.AuthorizationRevision, outcome);

    private async Task<long> CurrentAuthorizationRevisionAsync(RequestContext context, CancellationToken cancellationToken)
    {
        // The organization resource check returns the current authorization revision without granting anything.
        var decision = await awaiter.Authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.DesignForm), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "designer role required");
        }

        return decision.AuthorizationRevision;
    }

    /// <summary>Deterministic UUID derived from the idempotency key so a retried create registers the same resource id.</summary>
    internal static Guid DeterministicId(string idempotencyKey, string purpose)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(purpose + ":" + idempotencyKey));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // version 8 (custom) so it cannot be mistaken for a v7 issued id
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}

/// <summary>Raised when the authority committed a command that did not become Effective within the budget; mapped to 202 with the command id.</summary>
public sealed class PendingCommandException(CommandOutcome outcome) : Exception("security command pending: " + outcome.CommandStatus)
{
    public CommandOutcome Outcome { get; } = outcome;
}
