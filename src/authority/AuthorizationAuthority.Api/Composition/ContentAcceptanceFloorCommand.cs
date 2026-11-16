using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// E3-M2d (D-3): <c>--apply-content-acceptance-floor 2</c> raises floor 1 to floor 2 the same way (migration identity, floor context
/// migrated to exactly the floor-2 migration, only after floor 1, never lowering a recorded floor). Floor 2 is exercised only in
/// disposable PostgreSQL in E3-M2d.
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

        // E3-M2d (D-3): the floor number is stated explicitly and must be a floor this build recognizes, at most its own rule version.
        var requested = index + 1 < args.Length ? args[index + 1] : null;
        var target = ContentAcceptanceFloors.Recognized.FirstOrDefault(f => f.Version <= ContentAcceptanceRules.CurrentVersion
            && string.Equals(requested, f.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        if (target is null)
        {
            var known = string.Join("|", ContentAcceptanceFloors.Recognized.Where(f => f.Version <= ContentAcceptanceRules.CurrentVersion).Select(f => f.Version));
            Console.Error.WriteLine($"usage: {Flag} {known} [{ReinstateFlag}]  (the floor number must be stated explicitly; this build installs only the floors it recognizes)");
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

            // E3-M2d: monotonic, one step at a time. A floor above the requested one already recorded is never undone by applying a
            // lower one; a floor is applied only after the floor below it (no skipping). Both are refusals, nothing is changed.
            var recorded = RecordedFloors(context);
            if (recorded.Count > 0 && recorded.Max() > target.Version)
            {
                Console.Error.WriteLine($"content acceptance rule floor {recorded.Max()} is already recorded; floors are never lowered, so floor {target.Version} is not applied");
                exitCode = 2;
                return true;
            }

            if (ContentAcceptanceFloors.Recognized.Any(f => f.Version < target.Version && !recorded.Contains(f.Version)))
            {
                Console.Error.WriteLine($"content acceptance rule floor {target.Version} requires every lower floor to be applied first (floors are raised one step at a time)");
                exitCode = 2;
                return true;
            }

            // Exactly up to the requested floor's migration: a later floor migration of this build is never applied implicitly.
            context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate(target.MigrationId);
            var installed = InstalledFloor(context);
            if (installed < target.Version)
            {
                if (Array.IndexOf(args, ReinstateFlag) < 0)
                {
                    Console.Error.WriteLine($"content acceptance rule floor {target.Version} is recorded in the migration history but its constraint is missing; investigate (privileged change or partial restore), then re-run with {ReinstateFlag}");
                    exitCode = 4;
                    return true;
                }

                using (var transaction = context.Database.BeginTransaction())
                {
                    context.Database.ExecuteSqlRaw(target.ReinstateSql);
                    transaction.Commit();
                }

                installed = InstalledFloor(context);
                Console.WriteLine($"content acceptance rule floor {installed} re-installed (recorded floor reinstated)");
            }

            Console.WriteLine($"content acceptance rule floor {installed} installed ({target.ConstraintName}, NOT VALID); ordinary --migrate never applies or removes it");
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

    /// <summary>The recognized floors whose migration the history records (an absent history table means none).</summary>
    private static IReadOnlySet<int> RecordedFloors(ContentAcceptanceFloorContext context)
    {
        var historyExists = context.Database.SqlQueryRaw<bool>("SELECT to_regclass('authority.ef_migrations_history') IS NOT NULL AS \"Value\"").AsEnumerable().Single();
        return historyExists
            ? ContentAcceptanceFloors.RecordedFloors(context.Database.SqlQueryRaw<string>("SELECT \"MigrationId\" AS \"Value\" FROM authority.ef_migrations_history").AsEnumerable().ToList())
            : new HashSet<int>();
    }
}
