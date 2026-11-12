using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace EnterpriseForms.Experiments.ManagedHsmProbe;

/// <summary>
/// The future Managed HSM experiments. A check passes only when observed bytes or behaviour equal what a locally computed
/// reference requires. With an HSM-generated (unknown) key no byte-for-byte check can pass: those checks are Inconclusive.
/// Results are per input length and per version; a pass at one length is never generalized to another.
/// </summary>
internal sealed class HsmExperiments(IManagedHsmClient client, IDigestSigner? sdkSigner, ProbeConfiguration configuration, ReferenceVectorSet vectors)
{
    public const string KeyInspection = "KeyInspection";
    public const string DigestPath = "A-DigestPath";
    public const string RawInput = "B-RawInput";
    public const string Versions = "V-Versions";
    public const string Import = "I-ImportUndocumented";

    private readonly Dictionary<string, byte[]> digestPathMacs = new(StringComparer.Ordinal);

    public ProbeReport NewReport(string command) => new()
    {
        Command = command,
        HsmHost = configuration.EndpointUri.Host,
        KeyName = configuration.KeyName,
        ApiVersion = configuration.ApiVersion,
        KeyProvenance = configuration.KeyProvenance?.ToString(),
        RequestShapes = ManagedHsmRestClient.RequestShapes(configuration.ApiVersion),
    };

    public async Task<ProbeReport> RunAsync(CancellationToken cancellationToken)
    {
        var report = NewReport("hsm-run");
        if (configuration.KeyProvenance == ManagedHsmProbe.KeyProvenance.HsmGenerated)
        {
            report.Notes.Add("KeyProvenance=HsmGenerated: the key material is unknown, so byte-for-byte compatibility with the local vectors cannot be concluded (those checks are Inconclusive by design).");
        }

        foreach (var experiment in configuration.Experiments.OrderBy(e => e))
        {
            switch (experiment)
            {
                case ProbeExperiment.KeyInspection:
                    await InspectKeysAsync(report, cancellationToken).ConfigureAwait(false);
                    break;
                case ProbeExperiment.DigestPath:
                    await DigestPathAsync(report, cancellationToken).ConfigureAwait(false);
                    break;
                case ProbeExperiment.RawInput:
                    await RawInputAsync(report, cancellationToken).ConfigureAwait(false);
                    break;
                case ProbeExperiment.Versions:
                    await VersionsAsync(report, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        return report;
    }

    /// <summary>Each mapped version: oct-HSM, sign+verify permitted, enabled, not exportable, no release policy.</summary>
    internal async Task InspectKeysAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        foreach (var (version, identifier) in configuration.MappedVersions)
        {
            var id = "key-v" + version.ToString(CultureInfo.InvariantCulture);
            var result = await client.GetKeyAsync(identifier, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != HsmOutcome.Success || result.Body is not { } body)
            {
                report.Add(id, KeyInspection, Classify(result), "get key: " + result.Describe());
                continue;
            }

            var key = body.GetProperty("key");
            var kty = key.TryGetProperty("kty", out var t) ? t.GetString() : null;
            var operations = key.TryGetProperty("key_ops", out var o) && o.ValueKind == JsonValueKind.Array ? o.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : [];
            var attributes = body.TryGetProperty("attributes", out var a) ? a : default;
            var exportable = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("exportable", out var e) && e.ValueKind == JsonValueKind.True;
            var disabled = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False;
            var releasePolicy = body.TryGetProperty("release_policy", out var rp) && rp.ValueKind == JsonValueKind.Object;
            var problems = new List<string>();
            if (kty != "oct-HSM")
            {
                problems.Add($"kty is {kty ?? "missing"}, not oct-HSM");
            }

            if (!operations.Contains("sign") || !operations.Contains("verify"))
            {
                problems.Add("key_ops does not permit both sign and verify");
            }

            if (exportable || releasePolicy)
            {
                problems.Add("the key is exportable or has a release policy");
            }

            if (disabled)
            {
                problems.Add("the key version is disabled");
            }

            var evidence = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["versionIdentifier"] = identifier,
                ["kty"] = kty ?? "(missing)",
                ["keyOps"] = string.Join(",", operations),
                ["keySize"] = key.TryGetProperty("key_size", out var size) ? size.ToString() : "(not returned)",
                ["exportable"] = exportable ? "true" : "false/absent",
            };
            report.Add(id, KeyInspection, problems.Count == 0 ? Verdict.Pass : Verdict.Fail, problems.Count == 0 ? "oct-HSM, sign+verify, enabled, non-exportable" : string.Join("; ", problems), evidence);
        }
    }

    /// <summary>
    /// Experiment A (documented "sign hash" path): SHA-256 locally, sign the 32-byte digest, compare with
    /// HMAC-SHA-256(key, SHA-256(input)). Also verifies through the service and cross-checks the Azure SDK.
    /// </summary>
    internal async Task DigestPathAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        var (version, identifier) = Primary();
        var knownKey = configuration.KnownTestKeyFor(version);
        foreach (var vector in vectors.Vectors)
        {
            var digest = SHA256.HashData(vector.Input());
            var result = await client.SignAsync(identifier, digest, cancellationToken).ConfigureAwait(false);
            var evidence = Evidence(vector, version, identifier, result);
            if (result.Outcome != HsmOutcome.Success || result.Bytes is null)
            {
                report.Add("A-sign-" + vector.VectorId, DigestPath, Classify(result), "documented digest path: " + result.Describe(), evidence);
                continue;
            }

            digestPathMacs[vector.VectorId] = result.Bytes;
            Verdict verdict;
            string detail;
            if (result.Bytes.Length != 32)
            {
                (verdict, detail) = (Verdict.Fail, $"HS256 returned {result.Bytes.Length} bytes, not 32");
            }
            else if (knownKey is null)
            {
                (verdict, detail) = (Verdict.Inconclusive, "HSM-generated key: the expected MAC is unknowable");
            }
            else
            {
                var expected = vector.MacFor(knownKey);
                var observed = Codec.ToHex(result.Bytes);
                (verdict, detail) = observed == expected.DigestMessageHmacSha256Hex
                    ? (Verdict.Pass, "equals HMAC-SHA-256(key, SHA-256(input))")
                    : (Verdict.Fail, observed == expected.RawMessageHmacSha256Hex ? "unexpectedly equals the raw-message MAC of the original input" : "matches neither reference MAC");
            }

            report.Add("A-sign-" + vector.VectorId, DigestPath, verdict, detail, evidence);
            await VerifyPairAsync(report, DigestPath, "A-verify-" + vector.VectorId, identifier, digest, result.Bytes, cancellationToken).ConfigureAwait(false);
            if (sdkSigner is not null)
            {
                var sdk = await sdkSigner.SignDigestAsync(identifier, digest, cancellationToken).ConfigureAwait(false);
                report.Add("A-sdk-" + vector.VectorId, DigestPath,
                    sdk.Outcome != HsmOutcome.Success ? Classify(sdk) : sdk.Bytes is { } b && b.AsSpan().SequenceEqual(result.Bytes) ? Verdict.Pass : Verdict.Fail,
                    sdk.Outcome != HsmOutcome.Success ? "Azure SDK SignAsync(HS256, digest): " + sdk.Describe() : "Azure SDK SignAsync(HS256, digest) " + (sdk.Bytes!.AsSpan().SequenceEqual(result.Bytes) ? "matches the REST result" : "differs from the REST result"));
            }
        }
    }

    /// <summary>
    /// Experiment B (raw-input compatibility, NOT a documented contract): send the original bytes as the sign value. Each
    /// length is its own check: accepted/rejected, transformation, and equality with HMAC-SHA-256(key, input).
    /// </summary>
    internal async Task RawInputAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        var (version, identifier) = Primary();
        var knownKey = configuration.KnownTestKeyFor(version);
        var accepted = new List<int>();
        var rejected = new List<int>();
        foreach (var vector in vectors.Vectors)
        {
            var input = vector.Input();
            var result = await client.SignAsync(identifier, input, cancellationToken).ConfigureAwait(false);
            var evidence = Evidence(vector, version, identifier, result);
            if (result.Outcome is HsmOutcome.Rejected or HsmOutcome.NotFound)
            {
                rejected.Add(vector.InputLength);
                report.Add("B-sign-" + vector.VectorId, RawInput, Verdict.Fail, $"service rejected {vector.InputLength} raw bytes: {result.Describe()}", evidence);
                continue;
            }

            if (result.Outcome != HsmOutcome.Success || result.Bytes is null)
            {
                report.Add("B-sign-" + vector.VectorId, RawInput, Classify(result), "raw input: " + result.Describe(), evidence);
                continue;
            }

            accepted.Add(vector.InputLength);
            var observed = Codec.ToHex(result.Bytes);
            Verdict verdict;
            string detail;
            if (result.Bytes.Length != 32)
            {
                (verdict, detail) = (Verdict.Fail, $"accepted, but returned {result.Bytes.Length} bytes, not 32");
            }
            else if (knownKey is not null)
            {
                var expected = vector.MacFor(knownKey);
                (verdict, detail) = observed == expected.RawMessageHmacSha256Hex
                    ? (Verdict.Pass, $"accepted {vector.InputLength} bytes; equals HMAC-SHA-256(key, raw input) — the existing EnterpriseForms MAC")
                    : observed == expected.DigestMessageHmacSha256Hex
                        ? (Verdict.Fail, "accepted, but the service hashed the input first: equals HMAC-SHA-256(key, SHA-256(input))")
                        : (Verdict.Fail, "accepted, but matches neither reference: unknown transformation");
            }
            else if (digestPathMacs.TryGetValue(vector.VectorId, out var overDigest))
            {
                (verdict, detail) = overDigest.AsSpan().SequenceEqual(result.Bytes)
                    ? (Verdict.Fail, "accepted, but equals the digest-path MAC of SHA-256(input): the service hashed the input first")
                    : (Verdict.Inconclusive, "accepted and differs from the digest path; byte equality with HMAC-SHA-256(key, input) is unprovable with an HSM-generated key");
            }
            else
            {
                (verdict, detail) = (Verdict.Inconclusive, "accepted; HSM-generated key and no digest-path result to compare (run DigestPath first)");
            }

            report.Add("B-sign-" + vector.VectorId, RawInput, verdict, detail, evidence);
            if (verdict != Verdict.Fail || knownKey is null)
            {
                // Verify through the service with the raw bytes as "digest" and the expected (known key) or observed MAC.
                var expectedMac = knownKey is null ? result.Bytes : Codec.FromHex(vector.MacFor(knownKey).RawMessageHmacSha256Hex);
                await VerifyPairAsync(report, RawInput, "B-verify-" + vector.VectorId, identifier, input, expectedMac, cancellationToken).ConfigureAwait(false);
            }
        }

        report.Notes.Add($"B raw-input lengths accepted by the service: [{string.Join(", ", accepted)}]; rejected: [{string.Join(", ", rejected)}]. A pass at some lengths is not evidence for others.");
    }

    /// <summary>Experiment V: explicit opaque versions, cross-version isolation, a nonexistent version, and no silent "latest".</summary>
    internal async Task VersionsAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        var mapped = configuration.MappedVersions;
        var listing = await client.ListKeyVersionsAsync(cancellationToken).ConfigureAwait(false);
        string? latest = null;
        if (listing.Outcome == HsmOutcome.Success)
        {
            var missing = mapped.Where(m => !listing.VersionIdentifiers.Contains(m.Identifier, StringComparer.Ordinal)).Select(m => m.Version).ToList();
            latest = listing.LatestVersionIdentifier;
            report.Add("V-list", Versions, missing.Count == 0 ? Verdict.Pass : Verdict.Fail,
                missing.Count == 0 ? $"all {mapped.Count} mapped identifiers exist; service lists {listing.VersionIdentifiers.Count}" : "mapped versions missing in the HSM: " + string.Join(", ", missing),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["latestByCreated"] = latest ?? "(unknown)", ["listed"] = string.Join(",", listing.VersionIdentifiers) });
        }
        else
        {
            report.Add("V-list", Versions, Classify(listing), "list versions: " + listing.Describe());
        }

        var probe = vectors.Vectors.First(v => v.InputLength == 33);
        var digest = SHA256.HashData(probe.Input());
        var macs = new Dictionary<int, byte[]>();
        foreach (var (version, identifier) in mapped)
        {
            var result = await client.SignAsync(identifier, digest, cancellationToken).ConfigureAwait(false);
            var id = "V-explicit-v" + version.ToString(CultureInfo.InvariantCulture);
            if (result.Outcome != HsmOutcome.Success || result.Bytes is null)
            {
                report.Add(id, Versions, Classify(result), "explicit-version sign: " + result.Describe());
                continue;
            }

            macs[version] = result.Bytes;
            var known = configuration.KnownTestKeyFor(version);
            var own = known is null ? null : probe.MacFor(known).DigestMessageHmacSha256Hex;
            report.Add(id, Versions, own is null || Codec.ToHex(result.Bytes) == own ? Verdict.Pass : Verdict.Fail,
                own is null ? "answered for exactly the requested version (kid checked); key material unknown" : Codec.ToHex(result.Bytes) == own ? "answered for the requested version with that version's key" : "the MAC is not this version's key",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["requestedVersion"] = identifier, ["responseKid"] = result.KeyIdentifier ?? string.Empty });
        }

        var signed = macs.OrderBy(m => m.Key).ToList();
        for (var i = 0; i < signed.Count; i++)
        {
            for (var j = i + 1; j < signed.Count; j++)
            {
                var pair = $"v{signed[i].Key.ToString(CultureInfo.InvariantCulture)}-v{signed[j].Key.ToString(CultureInfo.InvariantCulture)}";
                report.Add("V-distinct-" + pair, Versions, signed[i].Value.AsSpan().SequenceEqual(signed[j].Value) ? Verdict.Fail : Verdict.Pass,
                    signed[i].Value.AsSpan().SequenceEqual(signed[j].Value) ? "two versions produced the same MAC (same key, or a version was ignored)" : "different versions give different MACs");
                var idI = configuration.ResolveVersionIdentifier(signed[i].Key);
                var idJ = configuration.ResolveVersionIdentifier(signed[j].Key);
                var ownI = await client.VerifyAsync(idI, digest, signed[i].Value, cancellationToken).ConfigureAwait(false);
                var ownJ = await client.VerifyAsync(idJ, digest, signed[j].Value, cancellationToken).ConfigureAwait(false);
                var crossIJ = await client.VerifyAsync(idI, digest, signed[j].Value, cancellationToken).ConfigureAwait(false);
                var crossJI = await client.VerifyAsync(idJ, digest, signed[i].Value, cancellationToken).ConfigureAwait(false);
                // Positive controls: a service that rejects EVERYTHING must not look version-isolated.
                var (controlI, detailI) = InterpretVerify(ownI, expectAccepted: true);
                var (controlJ, detailJ) = InterpretVerify(ownJ, expectAccepted: true);
                var (crossVerdictIJ, crossDetailIJ) = InterpretVerify(crossIJ, expectAccepted: false);
                var (crossVerdictJI, crossDetailJI) = InterpretVerify(crossJI, expectAccepted: false);
                report.Add("V-own-verify-" + pair, Versions, Worse(controlI, controlJ),
                    $"genuine MAC under its own version: first {detailI}; second {detailJ}");
                var controlsPass = controlI == Verdict.Pass && controlJ == Verdict.Pass;
                var crossVerdict = Worse(crossVerdictIJ, crossVerdictJI);
                report.Add("V-cross-verify-" + pair, Versions, controlsPass ? crossVerdict : Worse(crossVerdict, Verdict.Inconclusive),
                    $"other version's MAC: {crossDetailIJ}; reverse: {crossDetailJI}{(controlsPass ? string.Empty : " (not conclusive: a genuine MAC was not accepted under its own version)")}");
            }
        }

        // No silent latest: every older mapped version must keep its own MAC, never the latest version's.
        var latestVersion = latest is null ? mapped.Max(m => m.Version) : mapped.FirstOrDefault(m => m.Identifier == latest).Version;
        if (latestVersion > 0 && macs.TryGetValue(latestVersion, out var latestMac))
        {
            foreach (var (version, mac) in signed.Where(m => m.Key != latestVersion))
            {
                report.Add("V-no-silent-latest-v" + version.ToString(CultureInfo.InvariantCulture), Versions, mac.AsSpan().SequenceEqual(latestMac) ? Verdict.Fail : Verdict.Pass,
                    mac.AsSpan().SequenceEqual(latestMac) ? "an older version produced the latest version's MAC" : "an older explicit version did not use the latest version",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["latestSource"] = latest is null ? "highest mapped integer (listing unavailable)" : "service listing (created)" });
            }
        }
        else
        {
            report.Add("V-no-silent-latest", Versions, Verdict.Inconclusive, "the latest version is not among the mapped versions or could not be signed with");
        }

        var nonexistent = NonexistentIdentifier(mapped.Select(m => m.Identifier).Concat(listing.VersionIdentifiers));
        var missingVersion = await client.SignAsync(nonexistent, digest, cancellationToken).ConfigureAwait(false);
        report.Add("V-nonexistent", Versions,
            missingVersion.Outcome is HsmOutcome.NotFound or HsmOutcome.Rejected ? Verdict.Pass : missingVersion.Outcome == HsmOutcome.Success ? Verdict.Fail : Classify(missingVersion),
            missingVersion.Outcome == HsmOutcome.Success ? "a nonexistent version was accepted" : "nonexistent version: " + missingVersion.Describe(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["requestedVersion"] = nonexistent });
    }

    /// <summary>
    /// UNDOCUMENTED-BEHAVIOUR EXPERIMENT (separate command, explicit opt-in): plaintext JWK import of each published test key
    /// as oct-HSM. Microsoft documents oct-HSM import only through BYOK from a supported vendor HSM; a refusal here is an
    /// expected, reportable result. The key bytes are never written to the console or the report.
    /// </summary>
    public async Task<ProbeReport> ImportTestKeysAsync(CancellationToken cancellationToken)
    {
        var report = NewReport("hsm-import-test-keys");
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (versionText, keyId) in configuration.KnownTestKeyByVersion.OrderBy(p => int.Parse(p.Key, CultureInfo.InvariantCulture)))
        {
            var result = await client.ImportOctKeyAsync(TestKeys.Get(keyId), cancellationToken).ConfigureAwait(false);
            if (result.Outcome != HsmOutcome.Success || result.LatestVersionIdentifier is null)
            {
                report.Add("I-import-v" + versionText, Import, result.Outcome is HsmOutcome.Rejected or HsmOutcome.NotFound ? Verdict.Fail : Classify(result),
                    $"plaintext oct-HSM import of {keyId}: {result.Describe()}");
                report.Notes.Add("Import was not accepted; byte-for-byte experiments need BYOK from a supported vendor HSM, or remain Inconclusive with an HSM-generated key.");
                break;
            }

            mapping[versionText] = result.LatestVersionIdentifier;
            var inspected = await client.GetKeyAsync(result.LatestVersionIdentifier, cancellationToken).ConfigureAwait(false);
            var kty = inspected.Body is { } b && b.GetProperty("key").TryGetProperty("kty", out var t) ? t.GetString() : null;
            var exportable = inspected.Body is { } b2 && b2.TryGetProperty("attributes", out var a) && a.TryGetProperty("exportable", out var e) && e.ValueKind == JsonValueKind.True;
            report.Add("I-import-v" + versionText, Import, inspected.Outcome == HsmOutcome.Success && kty == "oct-HSM" && !exportable ? Verdict.Pass : Verdict.Fail,
                $"imported {keyId} as version {result.LatestVersionIdentifier} (kty {kty ?? "?"}, exportable {exportable})");
        }

        if (mapping.Count > 0)
        {
            report.Notes.Add("Suggested test-only KeyVersionIdentifiers: " + JsonSerializer.Serialize(mapping));
        }

        return report;
    }

    internal static Verdict Classify(HsmCallResult result) => result.Outcome switch
    {
        HsmOutcome.Success => Verdict.Pass,
        HsmOutcome.WrongKeyIdentifier => Verdict.Fail,
        HsmOutcome.Rejected or HsmOutcome.NotFound => Verdict.Fail,
        // Outside InterpretVerify (which knows whether rejection was expected) a verification failure is never a pass.
        HsmOutcome.VerificationFailed => Verdict.Fail,
        _ => Verdict.Inconclusive,
    };

    /// <summary>
    /// What a verify answer means when the experiment expects acceptance (a genuine MAC) or rejection (a tampered or other-version MAC).
    /// A valid MAC that is not accepted is ALWAYS a Fail. A rejection is a pass only when the service said so specifically
    /// (200 value=false, or HTTP 400 RET_SIGNATURE_VERIFICATION_FAILED) AND rejection was expected. Anything else is classified as before.
    /// </summary>
    internal static (Verdict Verdict, string Detail) InterpretVerify(HsmCallResult result, bool expectAccepted)
    {
        ArgumentNullException.ThrowIfNull(result);
        var accepted = result.Outcome == HsmOutcome.Success && result.Verified == true;
        var rejected = result.Outcome == HsmOutcome.VerificationFailed || (result.Outcome == HsmOutcome.Success && result.Verified == false);
        if (accepted)
        {
            return expectAccepted ? (Verdict.Pass, "accepted, as expected") : (Verdict.Fail, "a signature that must be rejected was ACCEPTED");
        }

        if (rejected)
        {
            var how = result.Outcome == HsmOutcome.VerificationFailed ? "HTTP 400 " + ManagedHsmRestClient.SignatureVerificationFailedCode : "value=false";
            return expectAccepted
                ? (Verdict.Fail, "a signature that must verify was REJECTED (" + how + ")")
                : (Verdict.Pass, "rejected, as expected (" + how + ")");
        }

        return (Classify(result), "verify: " + result.Describe());
    }

    private static Verdict Worse(Verdict first, Verdict second) =>
        first == Verdict.Fail || second == Verdict.Fail ? Verdict.Fail : first == Verdict.Inconclusive || second == Verdict.Inconclusive ? Verdict.Inconclusive : Verdict.Pass;

    /// <summary>Verifies the genuine MAC (must be accepted) and a one-bit-tampered MAC (must be rejected); each is judged on its own.</summary>
    private async Task VerifyPairAsync(ProbeReport report, string experiment, string checkId, string identifier, byte[] digest, byte[] mac, CancellationToken cancellationToken)
    {
        var good = await client.VerifyAsync(identifier, digest, mac, cancellationToken).ConfigureAwait(false);
        var tampered = (byte[])mac.Clone();
        tampered[^1] ^= 0x01;
        var bad = await client.VerifyAsync(identifier, digest, tampered, cancellationToken).ConfigureAwait(false);
        var (goodVerdict, goodDetail) = InterpretVerify(good, expectAccepted: true);
        var (badVerdict, badDetail) = InterpretVerify(bad, expectAccepted: false);
        var note = goodVerdict == Verdict.Pass ? string.Empty : " (the tampered result is no evidence while the genuine MAC is not accepted)";
        report.Add(checkId, experiment, Worse(goodVerdict, badVerdict), $"genuine: {goodDetail}; tampered: {badDetail}{note}");
    }

    private (int Version, string Identifier) Primary() => (configuration.PrimaryKeyVersion, configuration.ResolveVersionIdentifier(configuration.PrimaryKeyVersion));

    private static Dictionary<string, string> Evidence(ReferenceVector vector, int version, string identifier, HsmCallResult result)
    {
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["inputLength"] = vector.InputLength.ToString(CultureInfo.InvariantCulture),
            ["inputSha256"] = vector.InputSha256Hex,
            ["keyVersion"] = version.ToString(CultureInfo.InvariantCulture),
            ["versionIdentifier"] = identifier,
            ["outcome"] = result.Describe(),
        };
        if (result.Bytes is { } bytes)
        {
            evidence["observedMac"] = Codec.ToHex(bytes);
        }

        return evidence;
    }

    private static string NonexistentIdentifier(IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string candidate;
        do
        {
            candidate = Codec.ToHex(RandomNumberGenerator.GetBytes(16));
        }
        while (taken.Contains(candidate));

        return candidate;
    }
}
