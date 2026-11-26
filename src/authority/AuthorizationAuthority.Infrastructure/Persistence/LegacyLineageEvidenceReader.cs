using System.Data;
using AuthorizationAuthority.Application.Features.Lineage;
using AuthorizationAuthority.Domain.Features.Organizations;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// C10 M1-WP10 (Section 5.1): reads the lineage evidence of one organization in a single <c>REPEATABLE READ, READ ONLY</c>
/// transaction and always rolls it back. It never writes, never submits a command, never takes the organization lock and never
/// reads command payloads, reasons or request digests. The isolation and read-only state of the transaction are verified on the
/// server before the first evidence query; any other state is refused.
/// </summary>
public sealed class LegacyLineageEvidenceReader(AuthorityDbContext context, TimeProvider clock)
{
    /// <summary>Issued as the first statement of the transaction: PostgreSQL then refuses every write in it.</summary>
    public const string ReadOnlySql = "SET TRANSACTION READ ONLY";

    public const string VerifySql = "SELECT current_setting('transaction_read_only') = 'on' AND current_setting('transaction_isolation') = 'repeatable read' AS \"Value\"";

    private static readonly string[] EvidenceRelations = ["reader", "contributor", "manager", "shared_via", "owner", "member"];

    /// <summary>Null when the organization does not exist; throws <see cref="LineageReadOnlyTransactionException"/> if the transaction is not read-only.</summary>
    public async Task<LineageStoreRows?> ReadAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Database.ExecuteSqlRawAsync(ReadOnlySql, cancellationToken).ConfigureAwait(false);
            var verified = await context.Database.SqlQueryRaw<bool>(VerifySql).SingleAsync(cancellationToken).ConfigureAwait(false);
            if (!verified)
            {
                throw new LineageReadOnlyTransactionException();
            }

            var observedOn = clock.GetUtcNow();
            var revision = await context.OrganizationStates.Where(s => s.OrganizationId == organizationId)
                .Select(s => (long?)s.AuthorizationRevision).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (revision is null)
            {
                return null;
            }

            var shares = await context.Resources.Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.RecordShare &&
                                                            r.ResourceLifecycleState == ResourceLifecycleStates.Active)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var metadata = await context.ShareResourceMetadatas.Where(m => m.OrganizationId == organizationId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var documents = await context.ShareDocumentBindings.Where(d => d.OrganizationId == organizationId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var edges = await context.RelationshipEdges.Where(e => e.OrganizationId == organizationId &&
                                                                   (e.ResourceTypeName == ResourceTypeNames.RecordShare ||
                                                                    e.ResourceTypeName == ResourceTypeNames.FieldItem ||
                                                                    e.ResourceTypeName == ResourceTypeNames.FormRecord ||
                                                                    e.ResourceTypeName == ResourceTypeNames.Team) &&
                                                                   EvidenceRelations.Contains(e.RelationshipName))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var referenced = shares.Select(s => s.RegisteredBySecurityCommandId)
                .Concat(edges.Select(e => e.CreatedBySecurityCommandId))
                .Concat(edges.Where(e => e.RemovedBySecurityCommandId is not null).Select(e => e.RemovedBySecurityCommandId!.Value))
                .Distinct().ToList();
            var commands = await context.SecurityCommands.Where(c => c.OrganizationId == organizationId && referenced.Contains(c.SecurityCommandId))
                .Select(c => new LineageCommandFact(c.SecurityCommandId, c.OrganizationId, c.ActorIdentityBindingId, c.CommandKindName,
                    c.CommandStateName, c.CommittedAuthorizationRevision))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var bindings = await context.IdentityBindings.Where(b => b.OrganizationId == organizationId)
                .Select(b => new { b.IdentityBindingId, b.AuthorityUserId }).ToListAsync(cancellationToken).ConfigureAwait(false);

            return new LineageStoreRows
            {
                OrganizationId = organizationId,
                AuthorizationRevision = revision.Value,
                ObservedOn = observedOn,
                ActiveShares = shares,
                ShareMetadata = metadata,
                DocumentBindings = documents,
                Edges = edges,
                Commands = commands,
                IdentityBindingUsers = bindings.ToDictionary(b => b.IdentityBindingId, b => b.AuthorityUserId),
            };
        }
        finally
        {
            // Never commit: the evidence transaction is read-only and leaves nothing behind.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}

/// <summary>The server did not report a <c>REPEATABLE READ, READ ONLY</c> transaction; no evidence was read.</summary>
public sealed class LineageReadOnlyTransactionException()
    : InvalidOperationException("the lineage evidence transaction is not REPEATABLE READ, READ ONLY; nothing was read");
