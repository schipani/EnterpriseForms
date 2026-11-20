using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Infrastructure.Archive;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 G14-R2 (A-4 §24): the in-memory D+ durability of a test rig: journal-verification record store (recovery records and W2-S
/// commitments), provisioning store (instance pin) and checkpoint store, plus the recovery approver verifier (Unavailable unless a rig sets
/// synthetic approvers).
/// </summary>
public sealed class DPlusTestStores
{
    public InMemoryJournalVerificationRecordStore Records { get; } = new();

    public InMemoryArchiveProvisioningStore Provisioning { get; } = new();

    public InMemoryVerificationCheckpointStore Checkpoints { get; } = new();

    public DPlusRecoveryApprovalVerifier Approvals { get; set; } = DPlusRecoveryApprovalVerifier.Unavailable;

    public DPlusDurability Durability => new(Records, Provisioning, Checkpoints, Approvals);

    /// <summary>The sentinel (created if absent) and a provisioning record naming the same instance: the archive is pinned.</summary>
    public async Task<string> ProvisionAsync(ISecurityEventArchiveInventory archive, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var existing = await archive.ReadIdentityAsync(CancellationToken.None).ConfigureAwait(false);
        var instance = existing.Identity?.ArchiveInstanceId;
        if (instance is null)
        {
            var identity = ArchiveIdentityCodec.NewIdentity(now);
            instance = (await archive.CreateIdentityAsync(identity, CancellationToken.None).ConfigureAwait(false)).Existing?.ArchiveInstanceId ?? identity.ArchiveInstanceId;
        }

        await PinAsync(instance, now).ConfigureAwait(false);
        return instance;
    }

    /// <summary>A provisioning record for <paramref name="instance"/> (the archive itself is not touched).</summary>
    public Task PinAsync(string instance, DateTimeOffset now) =>
        Provisioning.CreateRecordAsync(ArchiveProvisioningRecords.Serialize(new ArchiveProvisioningRecord(ArchiveProvisioningRecord.SchemaV1, instance, now.ToUniversalTime(), "test")), CancellationToken.None);
}

/// <summary>
/// Synthetic recovery approvers for tests: P-256 key pairs held by the test (never by the Authority), and a verifier configured with their
/// public keys in the Testing environment. Signing helpers produce compact ES256 decisions with any claims a negative test needs.
/// </summary>
public sealed class SyntheticRecoveryApprovers : IDisposable
{
    private readonly Dictionary<string, ECDsa> keys = new(StringComparer.Ordinal);

    public SyntheticRecoveryApprovers(params string[] approverIds) : this(approverIds, null)
    {
    }

    public SyntheticRecoveryApprovers(IReadOnlyList<string> approverIds, IReadOnlyDictionary<string, DateTimeOffset>? retired)
    {
        ArgumentNullException.ThrowIfNull(approverIds);
        var options = new JournalRecoveryOptions { Mode = DPlusRecoveryApprovalVerifier.SyntheticMode };
        foreach (var id in approverIds)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            keys[id] = key;
            options.Approvers.Add(new JournalRecoveryApproverOptions
            {
                ApproverId = id, PublicKeyBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), RetiredOnUtc = retired?.GetValueOrDefault(id) is { } r && r != default ? r : null,
            });
        }

        Options = options;
        Verifier = DPlusRecoveryApprovalVerifier.Create(options, "Testing");
    }

    public JournalRecoveryOptions Options { get; }

    public DPlusRecoveryApprovalVerifier Verifier { get; }

    /// <summary>A verifier over a subset of the keys (a key removed from configuration), or with retirements applied.</summary>
    public DPlusRecoveryApprovalVerifier VerifierWith(IEnumerable<string> approverIds, IReadOnlyDictionary<string, DateTimeOffset>? retired = null)
    {
        var options = new JournalRecoveryOptions { Mode = DPlusRecoveryApprovalVerifier.SyntheticMode };
        foreach (var id in approverIds)
        {
            options.Approvers.Add(new JournalRecoveryApproverOptions
            {
                ApproverId = id, PublicKeyBase64 = Convert.ToBase64String(keys[id].ExportSubjectPublicKeyInfo()), RetiredOnUtc = retired is not null && retired.TryGetValue(id, out var r) ? r : null,
            });
        }

        return DPlusRecoveryApprovalVerifier.Create(options, "Testing");
    }

    public static DPlusRecoveryBinding Binding(DPlusRecoveryStatement statement, string statementSha256Hex) =>
        new(statement.RecoveryId, statement.OrganizationId, statement.StreamName, statement.FailedRevision, statement.StopGeneration, statementSha256Hex);

    /// <summary>A decision token. <paramref name="overrides"/> replaces or adds claims (null value removes one) for negative tests.</summary>
    public string Sign(string approverId, DPlusRecoveryBinding binding, DateTimeOffset issuedAt, string decision = DPlusRecoveryClaims.Approve, string kind = DPlusRecoveryClaims.RecoveryKind,
        TimeSpan? lifetime = null, Guid? jti = null, IReadOnlyDictionary<string, object?>? overrides = null, string? signWithKeyOf = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var iat = issuedAt.ToUnixTimeSeconds();
        var claims = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iss"] = DPlusRecoveryClaims.Issuer,
            ["aud"] = DPlusRecoveryClaims.Audience,
            ["sub"] = approverId,
            ["jti"] = (jti ?? Guid.CreateVersion7()).ToString("D"),
            ["iat"] = iat,
            ["nbf"] = iat,
            ["exp"] = iat + (long)(lifetime ?? TimeSpan.FromSeconds(600)).TotalSeconds,
            ["approval_kind"] = kind,
            ["decision"] = decision,
            ["statement_sha256"] = binding.StatementSha256Hex,
            ["recovery_id"] = binding.RecoveryId.ToString("D"),
            ["organization_id"] = binding.OrganizationId.ToString("D"),
            ["stream_name"] = binding.StreamName,
            ["failed_revision"] = binding.FailedRevision,
            ["stop_generation"] = binding.StopGeneration,
            ["justification_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("justification of " + approverId))),
        };
        foreach (var (name, value) in overrides ?? new Dictionary<string, object?>())
        {
            if (value is null)
            {
                claims.Remove(name);
            }
            else
            {
                claims[name] = value;
            }
        }

        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"kid\":\"" + approverId + "\"}"));
        var payload = Base64Url.EncodeToString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = keys[signWithKeyOf ?? approverId].SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256);
        return header + "." + payload + "." + Base64Url.EncodeToString(signature);
    }

    public void Dispose()
    {
        foreach (var key in keys.Values)
        {
            key.Dispose();
        }
    }
}

/// <summary>Small helpers over the in-memory stores for G14-R2 tests.</summary>
public static class DPlusRecordInspection
{
    public static IEnumerable<string> ArchiveNames(InMemorySecurityEventArchive archive, string prefix) => archive.MetaForTesting.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal);

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    public static T? Parse<T>(byte[] bytes) where T : class => DPlusRecoveryCodec.TryParse<T>(bytes);

    public static byte[] Canonical<T>(T value) => CanonicalJson.SerializeBytes(value);

    public static JournalVerificationCheckpoint FirstCheckpoint(Guid organization, string stream, string instance, long verifiedRevision, byte[]? verifiedDigest, DateTimeOffset at) =>
        new(organization, stream, 1, verifiedRevision, verifiedDigest, null, instance, CheckpointWalkKind.Baseline, CheckpointAssuranceLevel.Full, [], "test-verifier", at.ToUniversalTime());
}
