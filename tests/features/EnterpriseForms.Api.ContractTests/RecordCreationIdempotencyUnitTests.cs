using System.Security.Cryptography;
using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>F-C7-01 (C-65): the creation-request fingerprint and the Testing-only fault seam (unit level; the retry protocol runs against Docker).</summary>
public sealed class RecordCreationIdempotencyUnitTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();

    private readonly AesGcmFieldEncryptionService encryption = new(new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) })));

    private static CreationRequestFingerprint.Request Request(IReadOnlyDictionary<string, string>? values = null, IReadOnlyList<RecordGroupValuesRequest>? groups = null, string subject = "alice", Guid? ownerTeam = null, Guid? form = null) =>
        new("https://issuer.test", subject, form ?? Form, ownerTeam, null, null, values ?? new Dictionary<string, string> { ["given_name"] = "Avery", ["family_name"] = "Synthetic" }, groups);

    private static byte[] Canonical(CreationRequestFingerprint.Request request) => CreationRequestFingerprint.Canonicalize(request);

    [Fact]
    public void TheSameRequestMatchesIncludingADifferentKeyOrder()
    {
        var attempt = CreationRequestFingerprint.Create(encryption, Tenant, Record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Canonical(Request()));
        var reordered = Request(new Dictionary<string, string> { ["family_name"] = "Synthetic", ["given_name"] = "Avery" });
        Assert.True(CreationRequestFingerprint.Matches(encryption, attempt, Canonical(reordered)));
    }

    [Fact]
    public void AnyDifferenceInTheRequestIsAConflict()
    {
        var attempt = CreationRequestFingerprint.Create(encryption, Tenant, Record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Canonical(Request()));
        var group = new RecordGroupValuesRequest("witnesses", [new RecordOccurrenceValuesRequest(null, new Dictionary<string, string> { ["witness_name"] = "A" })]);
        var variants = new[]
        {
            Request(new Dictionary<string, string> { ["given_name"] = "Avery", ["family_name"] = "Other" }),
            Request(new Dictionary<string, string> { ["given_name"] = "Avery" }),
            Request(new Dictionary<string, string> { ["given_name"] = "Avery", ["family_name"] = "Synthetic", ["extra"] = "" }),
            Request(groups: [group]),
            Request(subject: "bob"),
            Request(ownerTeam: Guid.CreateVersion7()),
            Request(form: Guid.CreateVersion7()),
        };
        Assert.All(variants, v => Assert.False(CreationRequestFingerprint.Matches(encryption, attempt, Canonical(v))));

        // Repeating entries keep their order: the same entries in another order are a different request.
        var two = new RecordGroupValuesRequest("witnesses", [new RecordOccurrenceValuesRequest(null, new Dictionary<string, string> { ["witness_name"] = "A" }), new RecordOccurrenceValuesRequest(null, new Dictionary<string, string> { ["witness_name"] = "B" })]);
        var swapped = new RecordGroupValuesRequest("witnesses", [two.Occurrences[1], two.Occurrences[0]]);
        var withGroups = CreationRequestFingerprint.Create(encryption, Tenant, Record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Canonical(Request(groups: [two])));
        Assert.True(CreationRequestFingerprint.Matches(encryption, withGroups, Canonical(Request(groups: [two]))));
        Assert.False(CreationRequestFingerprint.Matches(encryption, withGroups, Canonical(Request(groups: [swapped]))));
    }

    [Fact]
    public void TheStoredRowRevealsNoValueAndTamperingOrMovingItFailsClosed()
    {
        var canonical = Canonical(Request());
        var attempt = CreationRequestFingerprint.Create(encryption, Tenant, Record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, canonical);
        Assert.DoesNotContain("Avery", attempt.FingerprintKeyEnvelopeJson, StringComparison.Ordinal);
        Assert.NotEqual(SHA256.HashData(canonical), attempt.CreationRequestFingerprint); // keyed, not a plain digest a reader could test guesses against
        Assert.Equal(32, attempt.CreationRequestFingerprint.Length);

        var flipped = attempt.CreationRequestFingerprint.ToArray();
        flipped[0] ^= 1;
        Assert.False(CreationRequestFingerprint.Matches(encryption, Clone(attempt, fingerprint: flipped), canonical));
        Assert.False(CreationRequestFingerprint.Matches(encryption, Clone(attempt, record: Guid.CreateVersion7()), canonical)); // moved to another record: the envelope does not open
        Assert.False(CreationRequestFingerprint.Matches(encryption, Clone(attempt, envelope: attempt.FingerprintKeyEnvelopeJson.Replace("\"Nonce\":\"", "\"Nonce\":\"AA", StringComparison.Ordinal)), canonical));
        Assert.False(CreationRequestFingerprint.Matches(encryption, Clone(attempt, envelope: "not json"), canonical));
        var otherKek = new AesGcmFieldEncryptionService(new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) })));
        Assert.False(CreationRequestFingerprint.Matches(otherKek, attempt, canonical));
    }

    [Fact]
    public void TheFaultSeamIsTestingOnlyAndOffByDefault()
    {
        Assert.IsType<NoWriteFaults>(Resolve("Testing", configured: false));
        Assert.IsNotType<NoWriteFaults>(Resolve("Testing", configured: true));
        foreach (var environment in new[] { "Development", "Staging", "Production" })
        {
            Assert.IsType<NoWriteFaults>(Resolve(environment, configured: false));
            Assert.Throws<InvalidOperationException>(() => Resolve(environment, configured: true));
        }
    }

    private static IWriteFaultInjector Resolve(string environment, bool configured)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [WriteFaultCheckpoints.ConfigurationKey] = configured ? "true" : null }).Build();
        var services = new ServiceCollection().AddWriteFaultInjection(configuration, new TestEnvironment(environment));
        return services.BuildServiceProvider().GetRequiredService<IWriteFaultInjector>();
    }

    private static Domain.Features.FormRecords.FormRecordCreationAttempt Clone(Domain.Features.FormRecords.FormRecordCreationAttempt a, byte[]? fingerprint = null, Guid? record = null, string? envelope = null) => new()
    {
        FormRecordCreationAttemptId = a.FormRecordCreationAttemptId,
        TenantId = a.TenantId,
        FormRecordId = record ?? a.FormRecordId,
        CreationRequestFingerprint = fingerprint ?? a.CreationRequestFingerprint,
        FingerprintKeyEnvelopeJson = envelope ?? a.FingerprintKeyEnvelopeJson,
        CreatedOn = a.CreatedOn,
        CreatedByUserId = a.CreatedByUserId,
    };

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
