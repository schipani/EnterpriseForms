using AuthorizationAuthority.Infrastructure.SpiceDb;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>C-149 (P-2): the measurement-only SpiceDB call counter.</summary>
public sealed class SpiceDbCallCounterTests
{
    [Fact]
    public async Task CallsAreAttributedToTheirFlowAndBackgroundCallsAreSeparate()
    {
        var counter = new SpiceDbCallCounter(enabled: true);
        counter.Record("ReadRelationships", 0);
        using (counter.Attribute("POST /internal/v1/commands"))
        {
            counter.Record("CheckBulkPermissions", 40);
            await Task.Run(() => counter.Record("CheckBulkPermissions", 60), TestContext.Current.CancellationToken); // flows into awaited work
            counter.Record("WriteRelationships", 0);
        }

        counter.Record("ReadRelationships", 0);
        var snapshot = counter.Snapshot();
        Assert.Equal(2, snapshot.CallsByAttribution[SpiceDbCallCounter.Background]["ReadRelationships"]);
        Assert.Equal(2, snapshot.CallsByAttribution["POST /internal/v1/commands"]["CheckBulkPermissions"]);
        Assert.Equal(100, snapshot.BulkCheckItemsByAttribution["POST /internal/v1/commands"]);
        Assert.Equal(1, snapshot.OperationsByAttribution["POST /internal/v1/commands"]);
        counter.Reset();
        Assert.Empty(counter.Snapshot().CallsByAttribution);
    }

    [Fact]
    public void ADisabledCounterRecordsNothing()
    {
        var counter = new SpiceDbCallCounter(enabled: false);
        using (counter.Attribute("x"))
        {
            counter.Record("CheckPermission", 0);
        }

        var snapshot = counter.Snapshot();
        Assert.False(snapshot.CountingEnabled);
        Assert.Empty(snapshot.CallsByAttribution);
        Assert.Empty(snapshot.OperationsByAttribution);
    }
}
