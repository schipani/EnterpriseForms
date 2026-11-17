using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Infrastructure;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2c Worker field-reader integration: the Worker host composes the business infrastructure exactly as the API does
/// (<c>Program.cs</c>: <c>AddBusinessInfrastructure</c>), so its field-protection migration and reconciliation reads
/// (<c>FieldValueCodec.FormatAsync</c>, pinned to verified mode by DecryptionCallSiteTests) and any document read reach the same
/// S-8 dispatchers. Here because this suite is the one that references the Worker host.
/// </summary>
public sealed class WorkerFieldReaderCompositionTests
{
    private static ServiceProvider Compose()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("Encryption:KeyProvider", "LocalDevelopment"),
            new KeyValuePair<string, string?>("Encryption:LocalDevelopmentKekBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
        ]).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBusinessInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheWorkerProgramComposesTheBusinessInfrastructure()
    {
        var program = File.ReadAllText(RepositoryPaths.Combine("src", "backend", "EnterpriseForms.Worker", "Program.cs"));
        Assert.Contains("builder.Services.AddBusinessInfrastructure(builder.Configuration);", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheComposedFieldReaderIsTheS8DispatcherAndRefusesUnstagedContent()
    {
        await using var provider = Compose();
        var fields = provider.GetRequiredService<IFieldEncryptionService>();
        Assert.IsType<AesGcmFieldEncryptionService>(fields);
        Assert.IsType<AesGcmFileEncryptionService>(provider.GetRequiredService<IFileEncryptionService>());

        var organization = Guid.CreateVersion7();
        var row = await fields.EncryptAsync(new FieldEncryptionContext(organization, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String"), "worker read", EncryptionKeyPurpose.FieldValue, CancellationToken.None);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => fields.DecryptVerifiedAsync(organization, [row], CancellationToken.None)); // schema 0: never verified content
        row.RowSignatureSchemaVersion = 1;
        Assert.Equal(["worker read"], await fields.DecryptVerifiedAsync(organization, [row], CancellationToken.None));
    }
}
