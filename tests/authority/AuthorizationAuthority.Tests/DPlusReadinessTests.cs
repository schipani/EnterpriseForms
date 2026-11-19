using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Journal;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-5b (C-157 SC-5, A-6 §26.6): a host that cannot evaluate D+ is reported not ready with a configuration code, distinct from archive
/// outages (the stream waits with the outage message) and integrity failures (durable stop records). The wiring into
/// <see cref="AuthorityReadiness"/> is by source inspection; this test covers the decision.
/// </summary>
public sealed class DPlusReadinessTests
{
    [Fact]
    public void AMissingDPlusCompositionIsAConfigurationErrorAndAComposedOneIsReady()
    {
        Assert.Equal((false, ArchiveProcessor.DurabilityNotComposedCode), DPlusReadiness.Check(null));
        Assert.Equal((true, (string?)null), DPlusReadiness.Check(new DPlusTestStores().Durability));
        Assert.Equal("DPLUS_DURABILITY_NOT_COMPOSED", ArchiveProcessor.DurabilityNotComposedCode);
    }
}
