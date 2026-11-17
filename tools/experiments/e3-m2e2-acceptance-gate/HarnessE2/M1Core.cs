// E3-M2e-2 adapter to the REAL E3-M1 Envelope v2 core. The core (EnterpriseForms.Infrastructure ...EnvelopeV2) is internal by design: no
// product path composes it, and only the contract-test assembly reaches it through InternalsVisibleTo. Production visibility is not
// changed (no production-source change is authorized) and the harness does not impersonate the contract-test assembly name. Instead it
// invokes the internal API by reflection: FieldSealBatchV2.CreateAsync / Seal / Dispose and DocumentEnvelopeV2Cipher.EncryptAsync run
// unmodified, with the real key provider. Members exposed only as ReadOnlySpan<byte> (which reflection cannot box) are read from their
// private readonly byte[] backing fields; every length is checked, so a renamed field or a changed shape fails loudly, never silently.
using System.Reflection;
using EnterpriseForms.Application.Features.Documents.Ports;

internal static class M1Core
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Ns = "EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.";
    private static readonly Assembly Infrastructure = typeof(EnterpriseForms.Infrastructure.Features.Documents.LocalDevelopmentKeyProvider).Assembly;
    private static readonly Type BatchType = Infrastructure.GetType(Ns + "FieldSealBatchV2", throwOnError: true)!;
    private static readonly Type CipherType = Infrastructure.GetType(Ns + "DocumentEnvelopeV2Cipher", throwOnError: true)!;
    private static readonly Type DocumentIdentityType = Infrastructure.GetType(Ns + "DocumentVersionIdentity", throwOnError: true)!;

    /// <summary>Only the PUBLIC, provider-seeded overloads are invoked; the internal deterministic known-answer cores are never used.</summary>
    private static readonly MethodInfo PublicSeal = BatchType.GetMethod("Seal", BindingFlags.Public | BindingFlags.Instance, [typeof(Guid), typeof(Guid), typeof(string), typeof(string)])
        ?? throw new MissingMethodException("FieldSealBatchV2.Seal (public overload)");

    internal sealed record SealedField(Guid OrganizationId, string KekReference, Guid DataKeyIdentifier, int WrapCode, byte[] Commitment, Guid FormRecordId, Guid FieldDefinitionId,
        Guid FieldValueItemId, string LogicalType, byte[] WrappedSeed, byte[] Nonce, byte[] Tag, byte[] Ciphertext);

    internal sealed record EncryptedDocument(Guid OrganizationId, string KekReference, Guid DataKeyIdentifier, int WrapCode, byte[] Commitment, byte[] WrappedSeed, byte[] NoncePrefix,
        int SegmentSizeBytes, long PlaintextSizeBytes, byte[] PlaintextSha256, long CiphertextSizeBytes, byte[] CiphertextSha256);

    /// <summary>One real FieldSealBatchV2 (one data key, one record); disposed by the harness at the end of the run.</summary>
    internal sealed class Batch : IDisposable
    {
        private readonly object inner;

        private Batch(object inner) => this.inner = inner;

        public static async Task<Batch> CreateAsync(IEncryptionKeyProvider keys, Guid organizationId, Guid formRecordId, EncryptionKeyPurpose purpose)
        {
            var create = BatchType.GetMethod("CreateAsync", BindingFlags.Public | BindingFlags.Static, [typeof(IEncryptionKeyProvider), typeof(Guid), typeof(Guid), typeof(EncryptionKeyPurpose), typeof(CancellationToken)])
                ?? throw new MissingMethodException("FieldSealBatchV2.CreateAsync");
            var task = (Task)create.Invoke(null, [keys, organizationId, formRecordId, purpose, CancellationToken.None])!;
            await task;
            return new Batch(task.GetType().GetProperty("Result")!.GetValue(task)!);
        }

        public SealedField Seal(Guid fieldDefinitionId, Guid fieldValueItemId, string logicalType, string plaintext)
        {
            object envelope;
            try
            {
                envelope = PublicSeal.Invoke(inner, [fieldDefinitionId, fieldValueItemId, logicalType, plaintext])!; // the public overload: a fresh random nonce per item
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }

            var header = Get(envelope, "Header");
            var prefix = Get(header, "Prefix");
            var identity = Get(envelope, "Identity");
            return new SealedField(
                (Guid)Get(prefix, "OrganizationId"), (string)Get(prefix, "KekReference"), (Guid)Get(prefix, "DataKeyIdentifier"), Convert.ToInt32(Get(prefix, "WrapAlgorithm"), System.Globalization.CultureInfo.InvariantCulture),
                Bytes(header, "commitment", 32), (Guid)Get(identity, "FormRecordId"), (Guid)Get(identity, "FieldDefinitionId"), (Guid)Get(identity, "FieldValueItemId"), (string)Get(identity, "LogicalType"),
                Bytes(envelope, "wrappedSeed", -1), Bytes(envelope, "nonce", 12), Bytes(envelope, "tag", 16), Bytes(envelope, "ciphertext", -1));
        }

        public void Dispose() => ((IDisposable)inner).Dispose();
    }

    public static async Task<EncryptedDocument> EncryptDocumentAsync(Stream plaintext, Stream sink, IEncryptionKeyProvider keys, Guid organizationId, Guid formRecordId, Guid fileAttachmentId, int versionNumber)
    {
        var identity = Activator.CreateInstance(DocumentIdentityType, formRecordId, fileAttachmentId, versionNumber)!;
        var encrypt = CipherType.GetMethod("EncryptAsync", BindingFlags.Public | BindingFlags.Static, [typeof(Stream), typeof(Stream), typeof(IEncryptionKeyProvider), typeof(Guid), DocumentIdentityType, typeof(CancellationToken)])
            ?? throw new MissingMethodException("DocumentEnvelopeV2Cipher.EncryptAsync (public, provider-seeded overload)");
        var task = (Task)encrypt.Invoke(null, [plaintext, sink, keys, organizationId, identity, CancellationToken.None])!;
        await task;
        var outcome = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var envelope = Get(outcome, "Envelope");
        var header = Get(envelope, "Header");
        var prefix = Get(header, "Prefix");
        var context = Get(envelope, "Context");
        return new EncryptedDocument(
            (Guid)Get(prefix, "OrganizationId"), (string)Get(prefix, "KekReference"), (Guid)Get(prefix, "DataKeyIdentifier"), Convert.ToInt32(Get(prefix, "WrapAlgorithm"), System.Globalization.CultureInfo.InvariantCulture),
            Bytes(header, "commitment", 32), Bytes(envelope, "wrappedSeed", -1), Bytes(context, "noncePrefix", -1), (int)Get(context, "SegmentSizeBytes"),
            (long)Get(outcome, "PlaintextSizeBytes"), (byte[])Get(outcome, "PlaintextSha256"), (long)Get(outcome, "CiphertextSizeBytes"), (byte[])Get(outcome, "CiphertextSha256"));
    }

    private static object Get(object target, string property) =>
        target.GetType().GetProperty(property, Instance)?.GetValue(target) ?? throw new MissingMemberException(target.GetType().Name, property);

    private static byte[] Bytes(object target, string field, int expectedLength)
    {
        var value = target.GetType().GetField(field, Instance)?.GetValue(target) as byte[] ?? throw new MissingFieldException(target.GetType().Name, field);
        if (expectedLength >= 0 && value.Length != expectedLength)
        {
            throw new InvalidOperationException($"{target.GetType().Name}.{field} has length {value.Length}, expected {expectedLength}");
        }

        return [.. value];
    }
}
