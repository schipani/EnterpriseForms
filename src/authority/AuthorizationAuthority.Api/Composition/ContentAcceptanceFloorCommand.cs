using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// E3-M2b-1F (Release B, contract §21): <c>dotnet AuthorizationAuthority.Api.dll --apply-content-acceptance-floor 1</c>. The ONLY
/// path that applies the content acceptance rule floor. It migrates <see cref="ContentAcceptanceFloorContext"/>, whose migrations
/// the ordinary <c>--migrate</c> (<see cref="AuthorityDbContext"/>) can never see, with the migration identity
/// (<c>ConnectionStrings__AuthorityDbMigration</c>; the runtime credential is never used). The migration itself refuses unless
/// the E3-M2b-1 fence is complete, no floor exists, no unexpired pre-CARV operation is in flight and no session without a CARV
/// build marker can write operations; a refusal or a lock timeout applies nothing. Re-running after success is a no-op.
/// After the migration it verifies that the floor constraint really exists. If the history records the floor but the
/// constraint is missing (a privileged drop or a partial restore), it stops with exit code 4 unless the operator, after
/// investigation, passes <c>--reinstate-recorded-floor</c>, which re-installs the constraint under the same preconditions.
/// There is no lowering or removal path.
/// </summary>
public static class ContentAcceptanceFloorCommand
{
    public const string Flag = "--apply-content-acceptance-floor";
    public const string ReinstateFlag = "--reinstate-recorded-floor";

    private const string InstalledFloorSql = "SELECT coalesce(max(substring(conname FROM '^ck_content_operation_rule_floor_v([0-9]+)$')::integer), 0) AS \"Value\" FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass AND contype = 'c'";

    public static bool TryRun(string[] args, out int exitCode) => TryRun(args, null, out exitCode);

    /// <summary>As <see cref="TryRun(string[], out int)"/>; <paramref name="configuration"/> replaces the environment and command line (tests).</summary>
    public static bool TryRun(string[] args, IConfiguration? configuration, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        exitCode = 0;
        var index = Array.IndexOf(args, Flag);
        if (index < 0)
        {
            return false;
        }

        if (Array.IndexOf(args, "--migrate") >= 0)
        {
            Console.Error.WriteLine($"{Flag} cannot be combined with --migrate: the floor is a separate release (Release B), applied only after every Authority instance runs a CARV build");
            exitCode = 2;
            return true;
        }

        var requested = index + 1 < args.Length ? args[index + 1] : null;
        if (!string.Equals(requested, ContentAcceptanceFloorSql.FloorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"usage: {Flag} {ContentAcceptanceFloorSql.FloorVersion} [{ReinstateFlag}]  (this build installs floor {ContentAcceptanceFloorSql.FloorVersion} only; the floor number must be stated explicitly)");
            exitCode = 2;
            return true;
        }

        configuration ??= new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args.Where(a => a != Flag && a != ReinstateFlag && a != requested).ToArray()).Build();
        var connectionString = configuration.GetConnectionString("AuthorityDbMigration");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine($"ConnectionStrings__AuthorityDbMigration (the migration identity) is required for {Flag}");
            exitCode = 2;
            return true;
        }

        try
        {
            using var context = new ContentAcceptanceFloorContext(ContentAcceptanceFloorContext.OptionsFor(connectionString));
            context.Database.Migrate(); // floor migrations only
            var installed = InstalledFloor(context);
            if (installed < ContentAcceptanceFloorSql.FloorVersion)
            {
                if (Array.IndexOf(args, ReinstateFlag) < 0)
                {
                    Console.Error.WriteLine($"content acceptance rule floor {ContentAcceptanceFloorSql.FloorVersion} is recorded in the migration history but its constraint is missing; investigate (privileged change or partial restore), then re-run with {ReinstateFlag}");
                    exitCode = 4;
                    return true;
                }

                using (var transaction = context.Database.BeginTransaction())
                {
                    context.Database.ExecuteSqlRaw(ContentAcceptanceFloorSql.ReinstateSql);
                    transaction.Commit();
                }

                installed = InstalledFloor(context);
                Console.WriteLine($"content acceptance rule floor {installed} re-installed (recorded floor reinstated)");
            }

            Console.WriteLine($"content acceptance rule floor {installed} installed ({ContentAcceptanceFloorSql.ConstraintName}, NOT VALID); ordinary --migrate never applies or removes it");
        }
        catch (Exception ex) when (DatabaseFailure(ex) is { } failure)
        {
            // EF reports transient failures (for example the 55P03 lock timeout) wrapped in InvalidOperationException. Either way
            // the migration transaction rolled back: nothing was applied, not even the history row. The operator re-runs it.
            Console.Error.WriteLine(failure is Npgsql.PostgresException pg
                ? $"content acceptance rule floor NOT applied: {pg.SqlState}: {pg.MessageText}"
                : $"content acceptance rule floor NOT applied: {failure.GetType().Name}: {failure.Message}");
            exitCode = 3;
        }

        return true;
    }

    private static Npgsql.NpgsqlException? DatabaseFailure(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is Npgsql.NpgsqlException npgsql)
            {
                return npgsql;
            }
        }

        return null;
    }

    private static int InstalledFloor(ContentAcceptanceFloorContext context) =>
        context.Database.SqlQueryRaw<int>(InstalledFloorSql).AsEnumerable().Single();
}
