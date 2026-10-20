using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Organizations;
using EnterpriseForms.Domain.Features.RelatedForms;
using EnterpriseForms.Domain.Features.Sharing;
using EnterpriseForms.Domain.Features.Teams;
using EnterpriseForms.Domain.Features.UserPreferences;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EnterpriseForms.Infrastructure.Persistence;

public sealed class BusinessStore(IDbContextFactory<BusinessDbContext> contextFactory, TimeProvider clock) : IBusinessStore
{
    public async Task<IBusinessScope> BeginScopeAsync(CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return new BusinessScope(context, transaction, clock);
    }

    private sealed class BusinessScope(BusinessDbContext context, IDbContextTransaction transaction, TimeProvider clock) : IBusinessScope
    {
        private bool committed;

        public async Task EnsureTenantAndUserProjectionAsync(Guid tenantId, Guid userId, string issuerUri, string subjectIdentifier, string displayName, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            if (!await context.Tenants.AnyAsync(t => t.TenantId == tenantId, cancellationToken).ConfigureAwait(false))
            {
                context.Tenants.Add(new Tenant { TenantId = tenantId, TenantName = "organization " + tenantId.ToString("N")[..8], TenantLifecycleStatus = "Active", CreatedOn = now, ModifiedOn = now });
            }

            var user = await context.Users.FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                context.Users.Add(new User { UserId = userId, TenantId = tenantId, ExternalIssuerUri = issuerUri, ExternalSubjectIdentifier = subjectIdentifier, DisplayName = displayName, EmailAddress = string.Empty, IsActive = true, CreatedOn = now, ModifiedOn = now });
            }
            else if (!string.IsNullOrWhiteSpace(displayName) && user.DisplayName != displayName)
            {
                user.DisplayName = displayName;
                user.ModifiedOn = now;
            }
        }

        public Task<List<User>> GetUsersAsync(Guid tenantId, CancellationToken cancellationToken) =>
            context.Users.AsNoTracking().Where(u => u.TenantId == tenantId && u.IsActive).OrderBy(u => u.DisplayName).ToListAsync(cancellationToken);

        public void AddFormDefinition(FormDefinition definition, FormVersion? version, IEnumerable<FieldDefinition> fields)
        {
            context.FormDefinitions.Add(definition);
            if (version is not null)
            {
                context.FormVersions.Add(version);
            }

            context.FieldDefinitions.AddRange(fields);
        }

        public Task<FormDefinition?> FindFormDefinitionAsync(Guid tenantId, Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.FormDefinitions.FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FormDefinitionId == formDefinitionId, cancellationToken);

        public Task<List<FormDefinition>> GetFormDefinitionsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            context.FormDefinitions.AsNoTracking().Where(f => f.TenantId == tenantId && f.LifecycleStatus != "Deleted").OrderBy(f => f.FormDefinitionName).ToListAsync(cancellationToken);

        public Task<FieldDefinition?> FindFieldDefinitionAsync(Guid formDefinitionId, Guid fieldDefinitionId, CancellationToken cancellationToken) =>
            context.FieldDefinitions.FirstOrDefaultAsync(f => f.FormDefinitionId == formDefinitionId && f.FieldDefinitionId == fieldDefinitionId, cancellationToken);

        public Task<List<FieldDefinition>> GetFieldDefinitionsAsync(Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.FieldDefinitions.AsNoTracking().Where(f => f.FormDefinitionId == formDefinitionId).OrderBy(f => f.FieldKey).ToListAsync(cancellationToken);

        public void AddFieldDefinition(FieldDefinition field) => context.FieldDefinitions.Add(field);

        public Task<List<FieldOption>> GetFieldOptionsAsync(Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.FieldOptions.Where(o => context.FieldDefinitions.Any(f => f.FormDefinitionId == formDefinitionId && f.FieldDefinitionId == o.FieldDefinitionId)).OrderBy(o => o.OptionSortOrder).ThenBy(o => o.OptionKey).ToListAsync(cancellationToken);

        public void AddFieldOption(FieldOption fieldOption) => context.FieldOptions.Add(fieldOption);

        public Task<FormVersion?> FindFormVersionAsync(Guid formVersionId, CancellationToken cancellationToken) =>
            context.FormVersions.AsNoTracking().FirstOrDefaultAsync(v => v.FormVersionId == formVersionId, cancellationToken);

        public Task<List<FormVersion>> GetFormVersionsAsync(Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.FormVersions.AsNoTracking().Where(v => v.FormDefinitionId == formDefinitionId).OrderByDescending(v => v.VersionNumber).ToListAsync(cancellationToken);

        public void AddFormVersion(FormVersion version) => context.FormVersions.Add(version);

        public Task<FormDraft?> FindDraftAsync(Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.FormDrafts.FirstOrDefaultAsync(d => d.FormDefinitionId == formDefinitionId, cancellationToken);

        public void AddDraft(FormDraft draft) => context.FormDrafts.Add(draft);

        public Task<List<FormRelationshipDefinition>> GetRelationshipDefinitionsAsync(Guid tenantId, Guid? parentFormDefinitionId, CancellationToken cancellationToken) =>
            context.FormRelationshipDefinitions.AsNoTracking().Where(r => r.TenantId == tenantId && (parentFormDefinitionId == null || r.ParentFormDefinitionId == parentFormDefinitionId)).OrderBy(r => r.RelationshipDefinitionKey).ToListAsync(cancellationToken);

        public Task<FormRelationshipDefinition?> FindRelationshipDefinitionAsync(Guid tenantId, Guid formRelationshipDefinitionId, CancellationToken cancellationToken) =>
            context.FormRelationshipDefinitions.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.FormRelationshipDefinitionId == formRelationshipDefinitionId, cancellationToken);

        public void AddRelationshipDefinition(FormRelationshipDefinition definition) => context.FormRelationshipDefinitions.Add(definition);

        public Task<List<FormRecordRelationship>> GetActiveChildLinksAsync(Guid parentFormRecordId, Guid formRelationshipDefinitionId, CancellationToken cancellationToken) =>
            context.FormRecordRelationships.Where(l => l.ParentFormRecordId == parentFormRecordId && l.FormRelationshipDefinitionId == formRelationshipDefinitionId && l.LinkLifecycleState == "Active").OrderBy(l => l.ListSortOrder).ThenBy(l => l.LinkCreatedOn).ToListAsync(cancellationToken);

        public Task<FormRecordRelationship?> FindActiveChildLinkAsync(Guid childFormRecordId, CancellationToken cancellationToken) =>
            context.FormRecordRelationships.FirstOrDefaultAsync(l => l.ChildFormRecordId == childFormRecordId && l.LinkLifecycleState == "Active", cancellationToken);

        public void AddChildLink(FormRecordRelationship link) => context.FormRecordRelationships.Add(link);

        public Task<FormRecord?> FindFormRecordAsync(Guid tenantId, Guid formRecordId, CancellationToken cancellationToken) =>
            context.FormRecords.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.FormRecordId == formRecordId, cancellationToken);

        public Task<List<FormRecord>> GetFormRecordsAsync(Guid tenantId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            context.FormRecords.AsNoTracking().Where(r => r.TenantId == tenantId && formRecordIds.Contains(r.FormRecordId)).ToListAsync(cancellationToken);

        public void AddFormRecord(FormRecord record) => context.FormRecords.Add(record);

        public void StageCandidate(FormRecordRevision headerRevision, IEnumerable<ISignedContentRow> rows)
        {
            context.FormRecordRevisions.Add(headerRevision);
            foreach (var row in rows)
            {
                switch (row)
                {
                    case FieldStringValue v: context.FieldStringValues.Add(v); break;
                    case FieldIntegerValue v: context.FieldIntegerValues.Add(v); break;
                    case FieldDecimalValue v: context.FieldDecimalValues.Add(v); break;
                    case FieldDateValue v: context.FieldDateValues.Add(v); break;
                    case FieldDateTimeValue v: context.FieldDateTimeValues.Add(v); break;
                    case FieldGuidValue v: context.FieldGuidValues.Add(v); break;
                    case FieldEncryptedValue v: context.FieldEncryptedValues.Add(v); break;
                    case FieldBooleanValue v: context.FieldBooleanValues.Add(v); break;
                    case FieldOptionValue v: context.FieldOptionValues.Add(v); break;
                    case FileAttachmentVersion v: context.FileAttachmentVersions.Add(v); break; // per-operation immutable copy (B-05)
                    default: throw new InvalidOperationException("unsupported signed row type " + row.GetType().Name);
                }
            }
        }

        public Task<FormRecordRevision?> FindStagedHeaderAsync(Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.FormRecordRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.FormRecordId == formRecordId && r.ContentChangeOperationId == contentChangeOperationId, cancellationToken);

        public async Task<List<ISignedContentRow>> GetStagedRowsAsync(Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken)
        {
            var rows = new List<ISignedContentRow>();
            rows.AddRange(await context.FieldStringValues.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken).ConfigureAwait(false));
            rows.AddRange(await context.FieldIntegerValues.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken).ConfigureAwait(false));
            rows.AddRange(await context.FieldDecimalValues.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken).ConfigureAwait(false));
            rows.AddRange(await context.FieldDateValues.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken).ConfigureAwait(false));
            rows.AddRange(await context.FieldDateTimeValues.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken).ConfigureAwait(false));
            rows.AddRange(await context.FieldGuidValues.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken).ConfigureAwait(false));
            rows.AddRange(await context.FieldEncryptedValues.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken).ConfigureAwait(false));
            rows.AddRange(await context.FieldBooleanValues.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken).ConfigureAwait(false));
            rows.AddRange(await context.FieldOptionValues.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken).ConfigureAwait(false));
            rows.AddRange(await context.FileAttachmentVersions.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken).ConfigureAwait(false));
            return rows.OrderBy(r => r.ItemKindCode).ThenBy(r => r.ItemId).ToList();
        }

        public Task<UploadSession?> FindUploadSessionAsync(Guid tenantId, Guid uploadSessionId, CancellationToken cancellationToken) =>
            context.UploadSessions.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.UploadSessionId == uploadSessionId, cancellationToken);

        public Task<UploadSession?> FindUploadSessionByIdempotencyAsync(Guid tenantId, Guid ownerUserId, string idempotencyKey, CancellationToken cancellationToken) =>
            context.UploadSessions.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.OwnerUserId == ownerUserId && s.SessionIdempotencyKey == idempotencyKey, cancellationToken);

        public Task<List<UploadSession>> GetUploadSessionsForUserAsync(Guid tenantId, Guid ownerUserId, IReadOnlyCollection<string> states, CancellationToken cancellationToken) =>
            context.UploadSessions.AsNoTracking().Where(s => s.TenantId == tenantId && s.OwnerUserId == ownerUserId && states.Contains(s.SessionState)).OrderByDescending(s => s.SessionCreatedOn).Take(100).ToListAsync(cancellationToken);

        public Task<List<UploadSession>> GetSessionsDueForStagingPurgeAsync(DateTimeOffset now, DateTimeOffset terminalBefore, int limit, CancellationToken cancellationToken) =>
            context.UploadSessions
                .Where(s => s.StagingObjectPurgedOn == null
                            && (((s.SessionState == UploadSessionStates.Open || s.SessionState == UploadSessionStates.Completed) && s.SessionExpiresOn < now)
                                || ((s.SessionState == UploadSessionStates.Processed || s.SessionState == UploadSessionStates.Committed || s.SessionState == UploadSessionStates.Rejected
                                     || s.SessionState == UploadSessionStates.Cancelled || s.SessionState == UploadSessionStates.Failed || s.SessionState == UploadSessionStates.Expired)
                                    && s.SessionModifiedOn < terminalBefore)))
                .OrderBy(s => s.SessionModifiedOn)
                .Take(limit)
                .ToListAsync(cancellationToken);

        public void AddUploadSession(UploadSession session) => context.UploadSessions.Add(session);

        public void AddProcessingJob(DocumentProcessingJob job) => context.DocumentProcessingJobs.Add(job);

        public async Task<DocumentProcessingJob?> LeaseNextProcessingJobAsync(string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var job = await context.DocumentProcessingJobs
                .FromSqlInterpolated($"SELECT * FROM document_processing_jobs WHERE (job_state = 'Queued' OR job_state = 'ScanPending' OR (job_state = 'Leased' AND lease_expires_on < {now})) AND next_attempt_on <= {now} ORDER BY next_attempt_on LIMIT 1 FOR UPDATE SKIP LOCKED")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return null;
            }

            job.JobState = ProcessingJobStates.Leased;
            job.LeaseOwnerName = leaseOwner;
            job.LeaseExpiresOn = now.Add(leaseDuration);
            job.AttemptCount++;
            job.JobModifiedOn = now;
            return job;
        }

        public Task<DocumentProcessingJob?> FindProcessingJobAsync(Guid uploadSessionId, CancellationToken cancellationToken) =>
            context.DocumentProcessingJobs.FirstOrDefaultAsync(j => j.UploadSessionId == uploadSessionId, cancellationToken);

        public Task<FileAttachment?> FindAttachmentAsync(Guid tenantId, Guid fileAttachmentId, CancellationToken cancellationToken) =>
            context.FileAttachments.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.FileAttachmentId == fileAttachmentId, cancellationToken);

        public Task<List<FileAttachment>> GetAttachmentsAsync(Guid formRecordId, CancellationToken cancellationToken) =>
            context.FileAttachments.Where(a => a.FormRecordId == formRecordId).OrderBy(a => a.AttachmentCreatedOn).ToListAsync(cancellationToken);

        public void AddAttachment(FileAttachment attachment) => context.FileAttachments.Add(attachment);

        public Task<FileAttachmentVersion?> FindAttachmentVersionAsync(Guid fileAttachmentVersionId, CancellationToken cancellationToken) =>
            context.FileAttachmentVersions.FirstOrDefaultAsync(v => v.FileAttachmentVersionId == fileAttachmentVersionId, cancellationToken);

        public Task<List<FileAttachmentVersion>> GetAttachmentVersionsAsync(Guid fileAttachmentId, CancellationToken cancellationToken) =>
            context.FileAttachmentVersions.AsNoTracking().Where(v => v.FileAttachmentId == fileAttachmentId).OrderByDescending(v => v.VersionNumber).ToListAsync(cancellationToken);

        public void AddAttachmentVersion(FileAttachmentVersion version) => context.FileAttachmentVersions.Add(version);

        public void AddShareProjection(RecordShare share, IEnumerable<RecordShareField> fields)
        {
            context.RecordShares.Add(share);
            context.RecordShareFields.AddRange(fields);
        }

        public Task<RecordShare?> FindShareProjectionAsync(Guid tenantId, Guid recordShareId, CancellationToken cancellationToken) =>
            context.RecordShares.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.RecordShareId == recordShareId, cancellationToken);

        public Task<List<RecordShare>> GetSharesOfRecordAsync(Guid tenantId, Guid formRecordId, CancellationToken cancellationToken) =>
            context.RecordShares.AsNoTracking().Where(s => s.TenantId == tenantId && s.FormRecordId == formRecordId).OrderBy(s => s.ShareCreatedOn).ToListAsync(cancellationToken);

        public Task<List<RecordShareField>> GetShareFieldsOfRecordAsync(Guid tenantId, Guid formRecordId, CancellationToken cancellationToken) =>
            context.RecordShareFields.AsNoTracking().Where(f => f.TenantId == tenantId && context.RecordShares.Any(s => s.RecordShareId == f.RecordShareId && s.FormRecordId == formRecordId)).ToListAsync(cancellationToken);

        public void AddTeamProjection(Team team) => context.Teams.Add(team);

        public Task<Team?> FindTeamProjectionAsync(Guid tenantId, Guid teamId, CancellationToken cancellationToken) =>
            context.Teams.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.TeamId == teamId, cancellationToken);

        public Task<List<Team>> GetTeamsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            context.Teams.AsNoTracking().Where(t => t.TenantId == tenantId).OrderBy(t => t.TeamName).ToListAsync(cancellationToken);

        public void AddTeamMembershipProjection(TeamMembership membership) => context.TeamMemberships.Add(membership);

        public Task<TeamMembership?> FindActiveTeamMembershipProjectionAsync(Guid tenantId, Guid teamId, Guid userId, CancellationToken cancellationToken) =>
            context.TeamMemberships.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.TeamId == teamId && m.UserId == userId && m.RemovedOn == null, cancellationToken);

        public void AddAuditEvent(AuditEvent auditEvent) => context.AuditEvents.Add(auditEvent);

        public Task<List<AuditEvent>> GetRecordAuditEventsAsync(Guid tenantId, Guid formRecordId, int limit, CancellationToken cancellationToken) =>
            context.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.FormRecordId == formRecordId).OrderByDescending(e => e.EventOccurredOn).ThenByDescending(e => e.AuditSequence).Take(limit).ToListAsync(cancellationToken);

        public Task<List<AuditEvent>> GetActorAuditEventsAsync(Guid tenantId, Guid actorUserId, int limit, CancellationToken cancellationToken) =>
            context.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.ActorUserId == actorUserId).OrderByDescending(e => e.EventOccurredOn).ThenByDescending(e => e.AuditSequence).Take(limit).ToListAsync(cancellationToken);

        public Task<OrganizationBranding?> FindBrandingAsync(Guid tenantId, CancellationToken cancellationToken) =>
            context.OrganizationBrandings.FirstOrDefaultAsync(b => b.TenantId == tenantId, cancellationToken);

        public void AddBranding(OrganizationBranding branding) => context.OrganizationBrandings.Add(branding);

        public Task<OrganizationSecurityPolicy?> FindSecurityPolicyAsync(Guid tenantId, CancellationToken cancellationToken) =>
            context.OrganizationSecurityPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);

        public void AddSecurityPolicy(OrganizationSecurityPolicy policy) => context.OrganizationSecurityPolicies.Add(policy);

        /// <summary>Records whose latest staged copy of this field is still a typed plaintext row (projection estimate for the migration status: the local store holds every staged revision, highest revision last; the authority head decides what is current).</summary>
        public async Task<int> CountLegacyPlaintextRecordsAsync(Guid fieldDefinitionId, CancellationToken cancellationToken)
        {
            var plaintext = context.FieldStringValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId })
                .Concat(context.FieldIntegerValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldDecimalValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldDateValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldDateTimeValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldGuidValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldBooleanValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldOptionValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }));
            var rows = await plaintext.ToListAsync(cancellationToken).ConfigureAwait(false);
            var latest = await context.FormRecordRevisions.Where(r => rows.Select(x => x.FormRecordId).Distinct().Contains(r.FormRecordId)).Select(r => new { r.FormRecordId, r.ContentChangeOperationId, r.ContentRevision }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var latestByRecord = latest.GroupBy(r => r.FormRecordId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ContentRevision).First().ContentChangeOperationId);
            return rows.Count(r => latestByRecord.TryGetValue(r.FormRecordId, out var op) && op == r.ContentChangeOperationId);
        }

        public async Task<List<LegacyPlaintextRecord>> GetLegacyPlaintextRecordsAsync(Guid fieldDefinitionId, CancellationToken cancellationToken)
        {
            var plaintext = context.FieldStringValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId })
                .Concat(context.FieldIntegerValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldDecimalValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldDateValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldDateTimeValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldGuidValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldBooleanValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }))
                .Concat(context.FieldOptionValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }));
            var rows = await plaintext.ToListAsync(cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                return [];
            }

            var latestByRecord = await CurrentOperationsAsync(rows.Select(r => r.FormRecordId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
            return rows.Where(r => latestByRecord.TryGetValue(r.FormRecordId, out var op) && op == r.ContentChangeOperationId)
                .GroupBy(r => r.FormRecordId)
                .Select(g => new LegacyPlaintextRecord(g.Key, g.Count()))
                .OrderBy(r => r.FormRecordId)
                .ToList();
        }

        /// <summary>
        /// The operation each record's local estimate reads: the projected authority-accepted head
        /// (form_records.accepted_content_change_operation_id), never merely the highest staged revision — a candidate the
        /// authority rejected stays in the insert-only tables and must not be mistaken for the record's state. Records
        /// without a projected head (written before the projection existed) fall back to the highest staged revision.
        /// </summary>
        private async Task<Dictionary<Guid, Guid>> CurrentOperationsAsync(List<Guid> recordIds, CancellationToken cancellationToken)
        {
            var accepted = await context.FormRecords.Where(r => recordIds.Contains(r.FormRecordId) && r.AcceptedContentChangeOperationId != null).Select(r => new { r.FormRecordId, r.AcceptedContentChangeOperationId }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var result = accepted.ToDictionary(r => r.FormRecordId, r => r.AcceptedContentChangeOperationId!.Value);
            var missing = recordIds.Where(id => !result.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                var latest = await context.FormRecordRevisions.Where(r => missing.Contains(r.FormRecordId)).Select(r => new { r.FormRecordId, r.ContentChangeOperationId, r.ContentRevision }).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var group in latest.GroupBy(r => r.FormRecordId))
                {
                    result[group.Key] = group.OrderByDescending(r => r.ContentRevision).First().ContentChangeOperationId;
                }
            }

            return result;
        }

        public async Task<int> CountPlaintextRowsInOperationAsync(Guid fieldDefinitionId, Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken)
        {
            var count = 0;
            count += await context.FieldStringValues.CountAsync(v => v.FieldDefinitionId == fieldDefinitionId && v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId, cancellationToken).ConfigureAwait(false);
            count += await context.FieldIntegerValues.CountAsync(v => v.FieldDefinitionId == fieldDefinitionId && v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId, cancellationToken).ConfigureAwait(false);
            count += await context.FieldDecimalValues.CountAsync(v => v.FieldDefinitionId == fieldDefinitionId && v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId, cancellationToken).ConfigureAwait(false);
            count += await context.FieldDateValues.CountAsync(v => v.FieldDefinitionId == fieldDefinitionId && v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId, cancellationToken).ConfigureAwait(false);
            count += await context.FieldDateTimeValues.CountAsync(v => v.FieldDefinitionId == fieldDefinitionId && v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId, cancellationToken).ConfigureAwait(false);
            count += await context.FieldGuidValues.CountAsync(v => v.FieldDefinitionId == fieldDefinitionId && v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId, cancellationToken).ConfigureAwait(false);
            count += await context.FieldBooleanValues.CountAsync(v => v.FieldDefinitionId == fieldDefinitionId && v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId, cancellationToken).ConfigureAwait(false);
            count += await context.FieldOptionValues.CountAsync(v => v.FieldDefinitionId == fieldDefinitionId && v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId, cancellationToken).ConfigureAwait(false);
            return count;
        }

        public async Task<List<LegacyPlaintextRecord>> GetLegacyEncryptedRecordsAsync(Guid fieldDefinitionId, CancellationToken cancellationToken)
        {
            var rows = await context.FieldEncryptedValues.Where(v => v.FieldDefinitionId == fieldDefinitionId).Select(v => new { v.FormRecordId, v.ContentChangeOperationId }).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                return [];
            }

            var latestByRecord = await CurrentOperationsAsync(rows.Select(r => r.FormRecordId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
            return rows.Where(r => latestByRecord.TryGetValue(r.FormRecordId, out var op) && op == r.ContentChangeOperationId)
                .GroupBy(r => r.FormRecordId)
                .Select(g => new LegacyPlaintextRecord(g.Key, g.Count()))
                .OrderBy(r => r.FormRecordId)
                .ToList();
        }

        public async Task<int> CountLegacyEncryptedRecordsAsync(Guid fieldDefinitionId, CancellationToken cancellationToken) =>
            (await GetLegacyEncryptedRecordsAsync(fieldDefinitionId, cancellationToken).ConfigureAwait(false)).Count;

        public Task<int> CountEncryptedRowsInOperationAsync(Guid fieldDefinitionId, Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.FieldEncryptedValues.CountAsync(v => v.FieldDefinitionId == fieldDefinitionId && v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId, cancellationToken);

        public void AddProtectionMigration(FieldProtectionMigration migration) => context.FieldProtectionMigrations.Add(migration);

        public void AddProtectionMigrationItem(FieldProtectionMigrationItem item) => context.FieldProtectionMigrationItems.Add(item);

        public Task<FieldProtectionMigration?> FindProtectionMigrationAsync(Guid fieldProtectionMigrationId, CancellationToken cancellationToken) =>
            context.FieldProtectionMigrations.FirstOrDefaultAsync(m => m.FieldProtectionMigrationId == fieldProtectionMigrationId, cancellationToken);

        public Task<FieldProtectionMigration?> FindLatestProtectionMigrationAsync(Guid fieldDefinitionId, CancellationToken cancellationToken) =>
            context.FieldProtectionMigrations.Where(m => m.FieldDefinitionId == fieldDefinitionId).OrderByDescending(m => m.ProtectionPolicyRevision).FirstOrDefaultAsync(cancellationToken);

        public async Task<FieldProtectionMigration?> LeaseNextProtectionMigrationAsync(string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var migration = await context.FieldProtectionMigrations
                .FromSqlInterpolated($"SELECT * FROM field_protection_migrations WHERE migration_state IN ('Queued','Running','Blocked') AND next_attempt_on <= {now} AND (lease_expires_on IS NULL OR lease_expires_on < {now}) ORDER BY next_attempt_on LIMIT 1 FOR UPDATE SKIP LOCKED")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (migration is null)
            {
                return null;
            }

            migration.LeaseOwnerName = leaseOwner;
            migration.LeaseExpiresOn = now.Add(leaseDuration);
            migration.MigrationModifiedOn = now;
            return migration;
        }

        public Task<List<FieldProtectionMigrationItem>> GetPendingProtectionMigrationItemsAsync(Guid fieldProtectionMigrationId, int limit, CancellationToken cancellationToken) =>
            context.FieldProtectionMigrationItems.AsNoTracking().Where(i => i.FieldProtectionMigrationId == fieldProtectionMigrationId && i.ItemState == FieldProtectionMigrationItemStates.Pending).OrderBy(i => i.LastAttemptOn ?? DateTimeOffset.MinValue).ThenBy(i => i.FormRecordId).Take(limit).ToListAsync(cancellationToken);

        public Task<FieldProtectionMigrationItem?> FindProtectionMigrationItemAsync(Guid fieldProtectionMigrationItemId, CancellationToken cancellationToken) =>
            context.FieldProtectionMigrationItems.FirstOrDefaultAsync(i => i.FieldProtectionMigrationItemId == fieldProtectionMigrationItemId, cancellationToken);

        public async Task<HashSet<Guid>> GetProtectionMigrationItemRecordIdsAsync(Guid fieldProtectionMigrationId, CancellationToken cancellationToken) =>
            (await context.FieldProtectionMigrationItems.AsNoTracking().Where(i => i.FieldProtectionMigrationId == fieldProtectionMigrationId).Select(i => i.FormRecordId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        public Task<List<FieldProtectionMigrationItem>> GetFailedProtectionMigrationItemsAsync(Guid fieldProtectionMigrationId, CancellationToken cancellationToken) =>
            context.FieldProtectionMigrationItems.Where(i => i.FieldProtectionMigrationId == fieldProtectionMigrationId && i.ItemState == FieldProtectionMigrationItemStates.Failed).ToListAsync(cancellationToken);

        public Task<int> CountPendingProtectionMigrationItemsAsync(Guid fieldProtectionMigrationId, CancellationToken cancellationToken) =>
            context.FieldProtectionMigrationItems.CountAsync(i => i.FieldProtectionMigrationId == fieldProtectionMigrationId && i.ItemState == FieldProtectionMigrationItemStates.Pending, cancellationToken);

        public Task<UserUiPreference?> FindUiPreferenceAsync(Guid userId, CancellationToken cancellationToken) =>
            context.UserUiPreferences.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        public void AddUiPreference(UserUiPreference preference) => context.UserUiPreferences.Add(preference);

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!committed)
            {
                try
                {
                    await transaction.RollbackAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }
}
