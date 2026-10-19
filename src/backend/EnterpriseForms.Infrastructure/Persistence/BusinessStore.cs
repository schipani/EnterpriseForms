using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Sharing;
using EnterpriseForms.Domain.Features.Teams;
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

            if (!await context.Users.AnyAsync(u => u.UserId == userId, cancellationToken).ConfigureAwait(false))
            {
                context.Users.Add(new User { UserId = userId, TenantId = tenantId, ExternalIssuerUri = issuerUri, ExternalSubjectIdentifier = subjectIdentifier, DisplayName = displayName, EmailAddress = string.Empty, IsActive = true, CreatedOn = now, ModifiedOn = now });
            }
        }

        public void AddFormDefinition(FormDefinition definition, FormVersion version, IEnumerable<FieldDefinition> fields)
        {
            // The definition's current-version pointer references a version row of the same insert; EF orders the inserts by FK.
            context.FormDefinitions.Add(definition);
            context.FormVersions.Add(version);
            context.FieldDefinitions.AddRange(fields);
        }

        public Task<FormDefinition?> FindFormDefinitionAsync(Guid tenantId, Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.FormDefinitions.FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FormDefinitionId == formDefinitionId, cancellationToken);

        public Task<List<FieldDefinition>> GetFieldDefinitionsAsync(Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.FieldDefinitions.AsNoTracking().Where(f => f.FormDefinitionId == formDefinitionId).OrderBy(f => f.FieldKey).ToListAsync(cancellationToken);

        public Task<FormRecord?> FindFormRecordAsync(Guid tenantId, Guid formRecordId, CancellationToken cancellationToken) =>
            context.FormRecords.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.FormRecordId == formRecordId, cancellationToken);

        public void AddFormRecord(FormRecord record) => context.FormRecords.Add(record);

        public void StageCandidate(FormRecordRevision headerRevision, IEnumerable<FieldStringValue> values)
        {
            context.FormRecordRevisions.Add(headerRevision);
            context.FieldStringValues.AddRange(values);
        }

        public Task<FormRecordRevision?> FindStagedHeaderAsync(Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.FormRecordRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.FormRecordId == formRecordId && r.ContentChangeOperationId == contentChangeOperationId, cancellationToken);

        public Task<List<FieldStringValue>> GetStagedValuesAsync(Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.FieldStringValues.AsNoTracking().Where(v => v.FormRecordId == formRecordId && v.ContentChangeOperationId == contentChangeOperationId).OrderBy(v => v.FieldValueItemId).ToListAsync(cancellationToken);

        public void AddShareProjection(RecordShare share, IEnumerable<RecordShareField> fields)
        {
            context.RecordShares.Add(share);
            context.RecordShareFields.AddRange(fields);
        }

        public Task<RecordShare?> FindShareProjectionAsync(Guid tenantId, Guid recordShareId, CancellationToken cancellationToken) =>
            context.RecordShares.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.RecordShareId == recordShareId, cancellationToken);

        public void AddTeamProjection(Team team) => context.Teams.Add(team);

        public Task<Team?> FindTeamProjectionAsync(Guid tenantId, Guid teamId, CancellationToken cancellationToken) =>
            context.Teams.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.TeamId == teamId, cancellationToken);

        public void AddTeamMembershipProjection(TeamMembership membership) => context.TeamMemberships.Add(membership);

        public Task<TeamMembership?> FindActiveTeamMembershipProjectionAsync(Guid tenantId, Guid teamId, Guid userId, CancellationToken cancellationToken) =>
            context.TeamMemberships.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.TeamId == teamId && m.UserId == userId && m.RemovedOn == null, cancellationToken);

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
