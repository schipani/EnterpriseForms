using System.Net;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>Organization logo upload (ADR-PB-002 Stage B item 6; B-08): PNG only, owner only, served back byte-exact.</summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class BrandingTests(StageBFixture fixture)
{
    /// <summary>Smallest valid PNG (1×1 transparent pixel).</summary>
    public static readonly byte[] OnePixelPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Fact]
    public async Task OwnerUploadsAPngLogoAndItIsServedBack()
    {
        var s = new StageBScenario(fixture, "branding-logo");
        await s.BootstrapAsync();

        // The browser sends image/png (the executor's binaryContentType); the endpoint accepts nothing else.
        var uploaded = await fixture.SendBytesAsync(HttpMethod.Post, "/api/v1/organization/branding/logo", s.Token(s.Designer), OnePixelPng, "image/png");
        Assert.True(uploaded.IsSuccess, uploaded.Body);
        Assert.True(uploaded.Root.GetProperty("hasLogo").GetBoolean());

        var served = await fixture.DownloadAsync("/api/v1/organization/branding/logo", s.Token(s.Clerk));
        Assert.Equal(HttpStatusCode.OK, served.Status);
        Assert.Equal(OnePixelPng, served.Body);

        // Wrong media type is refused before any bytes are interpreted (this was the HTTP 415 seen in the browser before the client declared image/png).
        var octet = await fixture.SendBytesAsync(HttpMethod.Post, "/api/v1/organization/branding/logo", s.Token(s.Designer), OnePixelPng, "application/octet-stream");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, octet.Status);

        // A non-PNG body with the PNG media type is refused by the signature check.
        var fake = await fixture.SendBytesAsync(HttpMethod.Post, "/api/v1/organization/branding/logo", s.Token(s.Designer), "<svg onload=alert(1)></svg>"u8.ToArray(), "image/png");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fake.Status);

        // Only the organization owner may change branding.
        var clerk = await fixture.SendBytesAsync(HttpMethod.Post, "/api/v1/organization/branding/logo", s.Token(s.Clerk), OnePixelPng, "image/png");
        Assert.True(clerk.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, clerk.Body);
    }
}
