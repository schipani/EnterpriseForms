using AuthorizationAuthority.Domain.Features.Sessions;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Disabling a member revokes their live sessions with <see cref="SessionRevocationReasons.MembershipDisabled"/>. The in-memory
/// unit tests and bearer-token integration tests never write an authenticated_sessions row, so a reason missing from the
/// database check constraint only failed for a signed-in browser user (503 "authority store unavailable"). Every defined
/// revocation reason must therefore be accepted by ck_session_revocation, asserted here from the EF model.
/// </summary>
public sealed class SessionRevocationConstraintTests
{
    [Fact]
    public void EveryDefinedRevocationReasonIsAllowedByTheSessionRevocationConstraint()
    {
        var options = new DbContextOptionsBuilder<AuthorityDbContext>()
            .UseNpgsql("Host=unit-test.invalid;Database=unused;Username=unused;Password=unused", n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName))
            .Options;
        using var context = new AuthorityDbContext(options);
        var entity = context.GetService<IDesignTimeModel>().Model.GetEntityTypes().Single(e => e.ClrType == typeof(AuthenticatedSession));
        var constraint = entity.GetCheckConstraints().Single(c => c.Name == "ck_session_revocation").Sql;
        var reasons = typeof(SessionRevocationReasons).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.NotEmpty(reasons);
        foreach (var reason in reasons)
        {
            Assert.Contains($"'{reason}'", constraint, StringComparison.Ordinal);
        }
    }
}
