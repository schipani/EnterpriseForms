using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;

namespace EnterpriseForms.Application.Features.FormDesigner;

/// <summary>
/// C-188 BF-1: registration of published versions with the Authorization Authority, fail closed.
/// <para>
/// A publish asks the Authority to stage the version (<c>PrepareFormVersionPublication</c>, with the complete field manifest) <b>before</b>
/// the business commits it, holding the form's publication lock (<see cref="IBusinessScope.LockFormPublicationAsync"/>) from before the
/// prepare until the commit ends. From the prepare on, no field search approval of the form confers search in the Authority (capabilities,
/// both engines, open pages, catalogue snapshots), so a published layout the Authority has not registered can never leave a stale approval
/// usable - including one that removes an approved field. After the commit the publish resolves the staged version as registered
/// (<c>ResolveFormVersionPublication</c>; the Authority registers the manifest it staged, never one rebuilt later from business rows, OD-3).
/// If the Authority refuses the prepare, nothing is published; if the resolution does not complete, the version stays published and
/// the form stays unsearchable until recovery.
/// </para>
/// <para>
/// Recovery (<see cref="RecoverAuthorityRegistrationAsync"/>) needs no new publication: under the same lock, the staged version either was
/// committed here (it is resolved as registered, if its stored layout still has the staged digest) or never will be (it is withdrawn as
/// abandoned). Every attempt is authorized (design permission here and in the Authority), audited, and uses a fresh idempotency key: the
/// Authority keeps a refused command under its key, so a retry under the same key would only replay the refusal.
/// </para>
/// </summary>
public sealed partial class FormDesignerHandlers
{
    private const string RegisteredResolution = "Registered";
    private const string AbandonedResolution = "Abandoned";
    private const int PublicationCommandAttempts = 4;

    /// <summary>Refusals that guarantee nothing was committed and may pass: a security command committed meanwhile, or the gate it closed.</summary>
    private static readonly string[] TransientPublicationRefusals = ["STALE_AUTHORIZATION_REVISION", "AUTHORITY_NOT_READY"];

    /// <summary>
    /// Phase C7 M2/M3 field entries (unchanged): per field, the type, classification and protection the catalog holds now plus the label
    /// and option snapshot this version froze.
    /// </summary>
    private static List<object> RegistrationFields(FormLayout layout, Dictionary<Guid, FieldDefinition> catalogFields) =>
        layout.FieldElements
            .Where(e => e.FieldDefinitionId is { } id && catalogFields.ContainsKey(id))
            .GroupBy(e => e.FieldDefinitionId!.Value)
            .Select(g =>
            {
                var element = g.First();
                var field = catalogFields[g.Key];
                return (object)new
                {
                    field_definition_id = field.FieldDefinitionId,
                    field_data_type = field.FieldDataType,
                    field_classification = field.FieldClassification,
                    protection_mode = field.FieldProtectionMode,
                    field_label = element.Label.Length > 300 ? element.Label[..300] : element.Label,
                    options = field.FieldDataType == "Option" ? (element.Options ?? []).Select(o => new { field_option_id = o.FieldOptionId, option_label = o.Label.Length > 300 ? o.Label[..300] : o.Label }).ToList() : null,
                    in_repeating_group = field.GroupDefinitionId is not null,
                };
            })
            .ToList();

    /// <summary>SHA-256 of the canonical layout JSON (<c>layout-digest/v1</c>), as registered since C7 M2.</summary>
    private static string LayoutDigestHex(string canonicalLayout) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalLayout)));

    /// <summary>
    /// The digest of a stored version's layout. The business column is <c>jsonb</c>, which does not keep the published text byte for byte,
    /// so the layout is canonicalized again exactly as publishing canonicalizes it (the same round trip the publish path uses to detect an
    /// unchanged draft). Any change to its content still changes the digest; an unreadable layout yields no digest (never a match).
    /// </summary>
    private static string StoredLayoutDigestHex(string storedLayoutJson)
    {
        try
        {
            return LayoutDigestHex(FormLayoutCodec.Serialize(FormLayoutCodec.Parse(storedLayoutJson)));
        }
        catch (Exception ex) when (ex is BusinessException or JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Stages <paramref name="version"/> with the Authority before the business commit. Any refusal or failure throws, so the caller's
    /// transaction rolls back and nothing is published (a staged publication that was in fact recorded is withdrawn by recovery).
    /// </summary>
    private async Task PrepareVersionWithAuthorityAsync(RequestContext context, Guid formDefinitionId, FormVersion version, FormLayout layout, List<object> fields, string digestHex, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            form_definition_id = formDefinitionId,
            form_version_id = version.FormVersionId,
            form_version_number = version.VersionNumber,
            layout_digest_hex = digestHex,
            fields,
            form_title = string.IsNullOrWhiteSpace(layout.Title) ? null : (layout.Title.Length > 300 ? layout.Title[..300] : layout.Title),
        });
        try
        {
            await SubmitPublicationCommandAsync(context, SecurityCommandKind.PrepareFormVersionPublication, formDefinitionId, payload, $"form-version-prepare:{version.FormVersionId:N}", $"stage version {version.VersionNumber} before publishing", cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex)
        {
            throw ex.Retryable || ex.HttpStatusCode >= 500
                ? new BusinessException("AUTHORITY_UNAVAILABLE", 503, "the Authority could not record this version, so nothing was published; try again")
                : new BusinessException(ex.ErrorCode, ex.HttpStatusCode is >= 400 and < 500 ? ex.HttpStatusCode : 409, "the Authority refused this version, so nothing was published");
        }
    }

    /// <summary>
    /// Resolves the staged publication and audits the outcome (Registered/RegistrationFailed, or PublicationAbandoned). Never throws for an
    /// Authority refusal or an unreachable Authority: the version stays as it is and the form stays unsearchable until a later resolution.
    /// </summary>
    /// <returns><c>Committed</c>: the Authority recorded the resolution (the form is no longer pending); <c>Effective</c>: and it is
    /// effective (invariant A11: only then is a registration reported as registered); <c>Failure</c>: the reason otherwise.</returns>
    private async Task<(bool Committed, bool Effective, string? Failure)> ResolvePublicationAsync(RequestContext context, Guid formDefinitionId, Guid formVersionId, int versionNumber, string digestHex,
        string resolution, int fieldCount, string source, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            form_definition_id = formDefinitionId,
            form_version_id = formVersionId,
            form_version_number = versionNumber,
            layout_digest_hex = digestHex,
            resolution,
        });
        string? failure;
        var committed = false;
        try
        {
            var command = await SubmitPublicationCommandAsync(context, SecurityCommandKind.ResolveFormVersionPublication, formDefinitionId, payload,
                $"form-version-resolve:{formVersionId:N}:{Guid.NewGuid():N}", $"{resolution.ToLowerInvariant()} version {versionNumber}", cancellationToken).ConfigureAwait(false);
            committed = true;
            failure = command.CommandStatus == SecurityCommandStatus.Effective ? null : "COMMAND_" + command.CommandStatus.ToString().ToUpperInvariant();
        }
        catch (AuthorityClientException ex)
        {
            failure = ex.ErrorCode;
        }
        catch (HttpRequestException)
        {
            failure = "AUTHORITY_UNREACHABLE";
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        if (resolution == AbandonedResolution && committed)
        {
            AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormVersionAuthorityPublicationAbandoned, "a version staged with the Authority was never published and was withdrawn",
                formDefinitionId: formDefinitionId, targetEntityType: "form_version", targetEntityId: formVersionId, metadata: new { versionNumber, registrationSource = source, reason = failure });
        }
        else if (resolution == AbandonedResolution)
        {
            AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormVersionAuthorityRegistrationFailed, "a version staged with the Authority could not be withdrawn",
                formDefinitionId: formDefinitionId, targetEntityType: "form_version", targetEntityId: formVersionId, metadata: new { versionNumber, fields = fieldCount, registrationSource = source, reason = failure }, outcome: "Failed");
        }
        else
        {
            AuditRecorder.Record(scope, context, clock, AuditCategories.Form, failure is null ? AuditActions.FormVersionAuthorityRegistered : AuditActions.FormVersionAuthorityRegistrationFailed,
                failure is null ? "published version registered with the Authority" : "published version could not be registered with the Authority", formDefinitionId: formDefinitionId, targetEntityType: "form_version", targetEntityId: formVersionId,
                metadata: new { versionNumber, fields = fieldCount, registrationSource = source, reason = failure }, outcome: failure is null ? "Success" : "Failed"); // ck_audit_events_outcome: Success/Denied/Failed
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (committed, committed && failure is null, failure);
    }

    /// <summary>
    /// Submits a publication command with the caller's design permission re-checked at the Authority's current revision. Only refusals that
    /// guarantee nothing was committed are retried, each with a fresh idempotency key (the Authority replays a refused key's refusal);
    /// anything else - including an unknown outcome - propagates and fails closed.
    /// </summary>
    private async Task<SecurityCommandResult> SubmitPublicationCommandAsync(RequestContext context, SecurityCommandKind kind, Guid formDefinitionId, JsonElement payload, string keyPrefix, string reason, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), ProtectedAction.DesignForm), cancellationToken).ConfigureAwait(false);
                return await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(context.OrganizationId, kind, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), decision.AuthorizationRevision, payload, reason),
                    $"{keyPrefix}:{attempt}", cancellationToken).ConfigureAwait(false);
            }
            catch (AuthorityClientException ex) when (attempt < PublicationCommandAttempts && TransientPublicationRefusals.Contains(ex.ErrorCode, StringComparer.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (1 << (attempt - 1))), clock, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>C-188 BF-1: whether the form's current published version is registered with the Authority (designers of the form only).</summary>
    public async Task<FormAuthorityRegistrationResponse> GetAuthorityRegistrationAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        int? current;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false) ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
            var versions = await scope.GetFormVersionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
            current = versions.Count == 0 ? null : versions.Max(v => v.VersionNumber);
        }

        var view = await ReadAuthorityRegistrationAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        return Describe(formDefinitionId, current, view.Registered, view.Pending);
    }

    /// <summary>
    /// C-188 BF-1: completes or withdraws the form's pending publication without a new publication. Under the form's publication lock no
    /// publish of this form is between its prepare and its commit, so a staged version found in the business store was committed (it is
    /// registered from the Authority's staged manifest, provided its stored layout still has the staged digest) and one that is not found
    /// never will be (it is withdrawn). Without a pending publication nothing is sent: a current version the Authority never recorded (one
    /// published before this safeguard) is reported as not recoverable - the designer publishes again (OD-3).
    /// </summary>
    public async Task<FormAuthorityRegistrationResponse> RecoverAuthorityRegistrationAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        _ = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false) ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        await scope.LockFormPublicationAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        var versions = await scope.GetFormVersionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        int? current = versions.Count == 0 ? null : versions.Max(v => v.VersionNumber);
        var view = await ReadAuthorityRegistrationAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        string outcome;
        string? failure = null;
        if (view.Pending is { } pending)
        {
            var staged = versions.FirstOrDefault(v => v.FormVersionId == pending.FormVersionId);
            if (staged is null)
            {
                var (committed, _, reason) = await ResolvePublicationAsync(context, formDefinitionId, pending.FormVersionId, pending.FormVersionNumber, pending.LayoutDigestHex, AbandonedResolution, 0, "Recovery", cancellationToken).ConfigureAwait(false);
                (outcome, failure) = committed ? ("Abandoned", null) : ("Refused", reason);
            }
            else if (staged.VersionNumber != pending.FormVersionNumber || !string.Equals(StoredLayoutDigestHex(staged.LayoutJson), pending.LayoutDigestHex, StringComparison.Ordinal))
            {
                // The stored version no longer matches what the Authority staged: never register it (fail closed; the form stays unsearchable).
                (outcome, failure) = ("Refused", "STAGED_VERSION_MISMATCH");
                await ResolvePublicationAuditOnlyAsync(context, formDefinitionId, staged, failure, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var fields = 0;
                try
                {
                    fields = FormLayoutCodec.Parse(staged.LayoutJson).FieldElements.Select(e => e.FieldDefinitionId).Where(id => id is not null).Distinct().Count();
                }
                catch (BusinessException)
                {
                    // the count is informational only
                }

                var (committed, _, reason) = await ResolvePublicationAsync(context, formDefinitionId, staged.FormVersionId, staged.VersionNumber, pending.LayoutDigestHex, RegisteredResolution, fields, "Recovery", cancellationToken).ConfigureAwait(false);
                (outcome, failure) = committed ? ("Registered", reason) : ("Refused", reason);
            }
        }
        else
        {
            outcome = current is { } number && view.Registered == number ? "AlreadyRegistered" : "NotRecoverable";
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false); // nothing written here; ends the transaction and releases the lock
        var after = view.Pending is null ? view : await ReadAuthorityRegistrationAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        return Describe(formDefinitionId, current, after.Registered, after.Pending) with { Outcome = outcome, FailureReason = failure };
    }

    private async Task ResolvePublicationAuditOnlyAsync(RequestContext context, Guid formDefinitionId, FormVersion staged, string failure, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormVersionAuthorityRegistrationFailed, "published version could not be registered with the Authority",
            formDefinitionId: formDefinitionId, targetEntityType: "form_version", targetEntityId: staged.FormVersionId, metadata: new { versionNumber = staged.VersionNumber, fields = 0, registrationSource = "Recovery", reason = failure }, outcome: "Failed");
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FormAuthorityRegistrationResponse Describe(Guid formDefinitionId, int? current, int? registered, AuthorityPendingFormVersionPublication? pending)
    {
        var state = current is null ? "NotPublished" : pending is not null ? "Pending" : registered == current ? "Registered" : "NotRegistered";
        return new FormAuthorityRegistrationResponse(formDefinitionId, current, registered, pending?.FormVersionNumber, state);
    }

    /// <summary>The Authority's view of the form (configuration view: designers of the form and security administrators only).</summary>
    private async Task<(int? Registered, AuthorityPendingFormVersionPublication? Pending)> ReadAuthorityRegistrationAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        if (fieldSearchPolicy is null)
        {
            throw new BusinessException("AUTHORITY_UNAVAILABLE", 503, "the Authority's view of form registrations is not available in this host");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var capabilities = await fieldSearchPolicy.GetFieldSearchCapabilitiesAsync(context.ToDelegatedUser(), new FieldSearchCapabilitiesQuery(context.OrganizationId, formDefinitionId), cancellationToken).ConfigureAwait(false);
                var configuration = capabilities.Configuration ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
                return (configuration.RegisteredFormVersionNumber, configuration.PendingPublication);
            }
            catch (AuthorityClientException ex) when (attempt < PublicationCommandAttempts && TransientPublicationRefusals.Contains(ex.ErrorCode, StringComparer.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (1 << (attempt - 1))), clock, cancellationToken).ConfigureAwait(false);
            }
            catch (AuthorityClientException ex) when (ex.Retryable || ex.HttpStatusCode >= 500)
            {
                throw new BusinessException("AUTHORITY_UNAVAILABLE", 503, "the Authority is not available; try again");
            }
        }
    }
}
