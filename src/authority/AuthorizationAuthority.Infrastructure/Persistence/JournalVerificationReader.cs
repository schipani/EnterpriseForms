using System.Data;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Journal;
using Npgsql;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// SEC-BL-01 WP-3/WP-4 (C-155): the PostgreSQL implementation of <see cref="IJournalVerificationReader"/>. Each snapshot is one
/// connection in a <c>REPEATABLE READ</c> transaction set <c>READ ONLY</c> before any read, so PostgreSQL itself refuses every write
/// for the snapshot's lifetime; it always rolls back. Every statement is a parameterized <c>SELECT</c> over
/// <c>authority.security_journal_entries</c> and <c>authority.organization_authorization_states</c> (stream column names come from a fixed
/// map, never from input). The sweep lock is a transaction-scoped advisory lock, which writes no data.
/// <para>
/// Identity: the connection string <c>ConnectionStrings:AuthorityDbJournalVerification</c> should name a dedicated SELECT-only login;
/// creating one is a G08 privilege change that is not authorized, so Development/Testing falls back to the Authority runtime
/// credential (which can write) and relies on the read-only transaction and this class's statement set (contract §8.5, OI-DR-2).
/// </para>
/// </summary>
public sealed class JournalVerificationReader : IJournalVerificationReader, IAsyncDisposable
{
    /// <summary>Advisory-lock class for journal-verification sweeps (distinct from the index command's (1161055538, 1177774385)).</summary>
    internal const int SweepLockClass = 0x5EC_B101;

    private readonly NpgsqlDataSource dataSource;
    private readonly Action<string>? commandObserver;

    public JournalVerificationReader(string connectionString) : this(connectionString, null)
    {
    }

    /// <summary>Test seam: <paramref name="commandObserver"/> receives every SQL text sent (statement-trace tests).</summary>
    internal JournalVerificationReader(string connectionString, Action<string>? commandObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = "enterpriseforms-journal-verifier" };
        dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        this.commandObserver = commandObserver;
    }

    public async Task<IJournalVerificationSnapshot> OpenSnapshotAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            var snapshot = new Snapshot(connection, transaction, commandObserver);
            await snapshot.ExecuteAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    /// <summary>Fixed, closed map of stream → head columns (never built from input).</summary>
    private static (string Revision, string Digest) HeadColumns(string streamName) => streamName switch
    {
        JournalStreamNames.Authorization => ("authorization_revision", "latest_security_journal_digest"),
        JournalStreamNames.Content => ("content_integrity_revision", "latest_content_journal_digest"),
        JournalStreamNames.Audit => ("audit_journal_revision", "latest_audit_journal_digest"),
        _ => throw new ArgumentException("unknown stream", nameof(streamName)),
    };

    internal sealed class Snapshot(NpgsqlConnection connection, NpgsqlTransaction transaction, Action<string>? observer) : IJournalVerificationSnapshot
    {
        internal async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            await using var command = Command(sql);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Test seam: PostgreSQL's view of the snapshot's read-only state.</summary>
        internal async Task<string?> TransactionReadOnlyAsync(CancellationToken cancellationToken)
        {
            await using var command = Command("SHOW transaction_read_only");
            return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Test seam: lets a test prove that PostgreSQL refuses a write inside the snapshot.</summary>
        internal async Task ExecuteForTestingAsync(string sql, CancellationToken cancellationToken) => await ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);

        public async Task<IReadOnlyList<Guid>> ListOrganizationsAsync(Guid? after, int limit, CancellationToken cancellationToken)
        {
            await using var command = Command(after is null
                ? "SELECT organization_id FROM authority.organization_authorization_states ORDER BY organization_id LIMIT @limit"
                : "SELECT organization_id FROM authority.organization_authorization_states WHERE organization_id > @after ORDER BY organization_id LIMIT @limit");
            command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 10_000));
            if (after is { } a)
            {
                command.Parameters.AddWithValue("after", a);
            }

            var result = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(reader.GetGuid(0));
            }

            return result;
        }

        /// <summary>WP-5a.1: provisioning evidence, the number of security-journal and platform-journal rows (any organization).</summary>
        public async Task<long> CountJournalRowsAsync(CancellationToken cancellationToken)
        {
            await using var command = Command("SELECT (SELECT count(*) FROM authority.security_journal_entries) + (SELECT count(*) FROM authority.platform_journal_entries)");
            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        public async Task<JournalStreamHead?> ReadStreamHeadAsync(Guid organizationId, string streamName, CancellationToken cancellationToken)
        {
            var (revisionColumn, digestColumn) = HeadColumns(streamName);
            await using var command = Command(
                $"SELECT s.{revisionColumn}, s.{digestColumn}, " +
                "(SELECT coalesce(max(j.journal_stream_revision), 0) FROM authority.security_journal_entries j WHERE j.organization_id = @org AND j.journal_stream_name = @stream) " +
                "FROM authority.organization_authorization_states s WHERE s.organization_id = @org");
            command.Parameters.AddWithValue("org", organizationId);
            command.Parameters.AddWithValue("stream", streamName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new JournalStreamHead(organizationId, streamName, reader.GetInt64(0), reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1), reader.GetInt64(2));
        }

        public async Task<IReadOnlyList<JournalRowSnapshot>> ReadRowsAsync(Guid organizationId, string streamName, long fromRevision, long toRevision, int limit, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "SELECT security_journal_entry_id, organization_id, journal_stream_name, journal_stream_revision, journal_event_kind, journal_event_body_json, journal_event_digest, " +
                "journal_previous_digest, archive_delivery_state, (archive_receipt_reference IS NOT NULL OR archive_receipt_digest IS NOT NULL), journal_created_on " +
                "FROM authority.security_journal_entries WHERE organization_id = @org AND journal_stream_name = @stream AND journal_stream_revision BETWEEN @from AND @to " +
                "ORDER BY journal_stream_revision LIMIT @limit");
            command.Parameters.AddWithValue("org", organizationId);
            command.Parameters.AddWithValue("stream", streamName);
            command.Parameters.AddWithValue("from", fromRevision);
            command.Parameters.AddWithValue("to", toRevision);
            command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100_000));
            var rows = new List<JournalRowSnapshot>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new JournalRowSnapshot(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
                    (byte[])reader.GetValue(6), reader.IsDBNull(7) ? null : (byte[])reader.GetValue(7), reader.GetString(8), reader.GetBoolean(9),
                    reader.GetFieldValue<DateTimeOffset>(10)));
            }

            return rows;
        }

        public async Task<bool> TryAcquireSweepLockAsync(Guid organizationId, string streamName, CancellationToken cancellationToken)
        {
            await using var command = Command("SELECT pg_try_advisory_xact_lock(@class, @key)");
            command.Parameters.AddWithValue("class", SweepLockClass);
            command.Parameters.AddWithValue("key", LockKey(organizationId, streamName));
            return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        internal static int LockKey(Guid organizationId, string streamName) =>
            BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(organizationId.ToString("D") + "|" + streamName)), 0);

        private NpgsqlCommand Command(string sql)
        {
            observer?.Invoke(sql);
            return new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.RollbackAsync().ConfigureAwait(false); // read-only: nothing to keep; releases the advisory lock
            }
            catch (NpgsqlException)
            {
                // The connection is being discarded anyway.
            }
            catch (InvalidOperationException)
            {
                // The transaction already ended (for example the connection broke).
            }
            finally
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
