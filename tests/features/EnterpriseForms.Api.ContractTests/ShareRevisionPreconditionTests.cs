using EnterpriseForms.Api.Features.Sharing;
using EnterpriseForms.Application.Common;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

public sealed class ShareRevisionPreconditionTests
{
    private const string ShareIdText = "ad35d02e-ecfd-49ec-ac38-03b0d49fb25e";
    private static readonly Guid ShareId = Guid.Parse(ShareIdText);

    [Theory]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void StrongTagRoundTripsToTheAuthorityRevision(long revision)
    {
        var tag = ShareRevisionPrecondition.Format(ShareId, revision);
        Assert.StartsWith($"\"{ShareIdText}:", tag, StringComparison.Ordinal);
        Assert.Equal(revision, ShareRevisionPrecondition.Require($" \t{tag} ", ShareId));
    }

    [Fact]
    public void MissingHeaderRequiresAPrecondition()
    {
        var error = Assert.Throws<BusinessException>(() => ShareRevisionPrecondition.Require(StringValues.Empty, ShareId));
        Assert.Equal(428, error.HttpStatusCode);
        Assert.Equal("PRECONDITION_REQUIRED", error.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("W/\"" + ShareIdText + ":1\"")]
    [InlineData(ShareIdText + ":1")]
    [InlineData("\"" + ShareIdText + ":0\"")]
    [InlineData("\"" + ShareIdText + ":01\"")]
    [InlineData("\"" + ShareIdText + ":9223372036854775808\"")]
    [InlineData("\"00000000-ecfd-49ec-ac38-03b0d49fb25e:1\"")]
    [InlineData("\"" + ShareIdText + ":1\", \"" + ShareIdText + ":2\"")]
    public void InvalidOrInapplicableTagFailsThePrecondition(string tag)
    {
        var error = Assert.Throws<BusinessException>(() => ShareRevisionPrecondition.Require(tag, ShareId));
        Assert.Equal(412, error.HttpStatusCode);
        Assert.Equal("PRECONDITION_FAILED", error.ErrorCode);
    }

    [Fact]
    public void MultipleHeaderValuesFailThePrecondition()
    {
        var tag = ShareRevisionPrecondition.Format(ShareId, 1);
        var tags = new StringValues([tag, tag]);
        var error = Assert.Throws<BusinessException>(() => ShareRevisionPrecondition.Require(tags, ShareId));
        Assert.Equal(412, error.HttpStatusCode);
    }
}
