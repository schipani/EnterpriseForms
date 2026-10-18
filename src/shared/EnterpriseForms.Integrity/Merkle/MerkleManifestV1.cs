using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EnterpriseForms.Integrity.Merkle;

/// <summary>Leaf item kinds and their wire codes (protocol section 2.1). Code 7 is reserved for manifest v2.</summary>
public enum MerkleItemKind : byte
{
    RecordHeader = 1,
    RepeatedGroup = 2,
    FieldValue = 3,
    FieldSelection = 4,
    Attachment = 5,
    AttachmentLifecycle = 6,
}

/// <summary>One verified content item presented to the tree builder.</summary>
/// <remarks>
/// <paramref name="RowSignature"/> must already have been verified against the signing authority; this type
/// performs structural checks only and never treats a well-formed tag as proof of authenticity.
/// </remarks>
public sealed record MerkleLeafInput(
    MerkleItemKind Kind,
    Guid ItemId,
    ReadOnlyMemory<byte> RowSignature,
    ReadOnlyMemory<byte> CanonicalPayloadDigest,
    ulong CommittedContentRevision,
    Guid CommittedOperationId)
{
    public byte[] ItemKey => MerkleManifestV1.ItemKey(Kind, ItemId);
}

public sealed record MerkleManifestResult(
    IReadOnlyList<MerkleLeafInput> SortedLeaves,
    IReadOnlyList<byte[]> LeafHashes,
    IReadOnlyList<IReadOnlyList<byte[]>> LevelsBottomUp,
    byte[] MerkleRoot,
    ulong LeafCount,
    byte[] LeafIndexDigest);

/// <summary>
/// Deterministic Merkle record manifest, version 1. Normative text:
/// trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md sections 2.1 and 2.2.
/// Conformance vectors: trust_authority/merkle-v1.test-vectors.json.
/// </summary>
public static class MerkleManifestV1
{
    public const int ItemKeyLength = 17;
    public const int HashLength = 32;

    private static readonly byte[] LeafDomain = Encoding.ASCII.GetBytes("DynamicForms/MerkleLeaf/v1");
    private static readonly byte[] NodeDomain = Encoding.ASCII.GetBytes("DynamicForms/MerkleNode/v1");
    private static readonly byte[] EmptyDomain = Encoding.ASCII.GetBytes("DynamicForms/MerkleEmpty/v1");
    private static readonly byte[] IndexDomain = Encoding.ASCII.GetBytes("DynamicForms/ManifestIndex/v1");

    public static byte[] ItemKey(MerkleItemKind kind, Guid itemId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.UnknownItemKind, $"kind code {(byte)kind}");
        }

        var key = new byte[ItemKeyLength];
        key[0] = (byte)kind;
        WriteUuid(itemId, key.AsSpan(1));
        return key;
    }

    /// <summary>Exact leaf framing: 0x00 || domain || UInt16BE(17) || key || sig || digest || kind || UInt64BE(rev) || op uuid.</summary>
    public static byte[] LeafBytes(MerkleLeafInput leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        if (leaf.RowSignature.Length != HashLength)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.InvalidSignatureLength, "row_signature must be 32 bytes");
        }

        if (leaf.CanonicalPayloadDigest.Length != HashLength)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.InvalidDigestLength, "canonical_payload_digest must be 32 bytes");
        }

        var key = ItemKey(leaf.Kind, leaf.ItemId);
        if (key[0] != (byte)leaf.Kind)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.KindCodeMismatch, "item_key kind must equal trailing kind code");
        }

        var length = 1 + LeafDomain.Length + 2 + ItemKeyLength + HashLength + HashLength + 1 + 8 + 16;
        var buffer = new byte[length];
        var offset = 0;
        buffer[offset++] = 0x00;
        LeafDomain.CopyTo(buffer, offset);
        offset += LeafDomain.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), ItemKeyLength);
        offset += 2;
        key.CopyTo(buffer, offset);
        offset += ItemKeyLength;
        leaf.RowSignature.Span.CopyTo(buffer.AsSpan(offset));
        offset += HashLength;
        leaf.CanonicalPayloadDigest.Span.CopyTo(buffer.AsSpan(offset));
        offset += HashLength;
        buffer[offset++] = (byte)leaf.Kind;
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), leaf.CommittedContentRevision);
        offset += 8;
        WriteUuid(leaf.CommittedOperationId, buffer.AsSpan(offset, 16));
        return buffer;
    }

    public static byte[] LeafHash(MerkleLeafInput leaf) => SHA256.HashData(LeafBytes(leaf));

    public static byte[] NodeHash(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != HashLength || right.Length != HashLength)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.InvalidDigestLength, "node children must be 32 bytes");
        }

        var buffer = new byte[1 + NodeDomain.Length + HashLength + HashLength];
        buffer[0] = 0x01;
        NodeDomain.CopyTo(buffer, 1);
        left.CopyTo(buffer.AsSpan(1 + NodeDomain.Length));
        right.CopyTo(buffer.AsSpan(1 + NodeDomain.Length + HashLength));
        return SHA256.HashData(buffer);
    }

    public static byte[] EmptyRoot()
    {
        var buffer = new byte[1 + EmptyDomain.Length];
        buffer[0] = 0x02;
        EmptyDomain.CopyTo(buffer, 1);
        return SHA256.HashData(buffer);
    }

    /// <summary>Sort by unsigned item_key bytes; reject duplicates and mixed revision/operation bindings.</summary>
    public static IReadOnlyList<MerkleLeafInput> SortLeaves(IEnumerable<MerkleLeafInput> leaves)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        var ordered = leaves.OrderBy(static l => l.ItemKey, UnsignedByteComparer.Instance).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i - 1].ItemKey.AsSpan().SequenceEqual(ordered[i].ItemKey))
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.DuplicateItemKey, Convert.ToHexStringLower(ordered[i].ItemKey));
            }
        }

        if (ordered.Count > 0)
        {
            var revision = ordered[0].CommittedContentRevision;
            var operation = ordered[0].CommittedOperationId;
            if (ordered.Any(l => l.CommittedContentRevision != revision || l.CommittedOperationId != operation))
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.MixedRevisionOrOperation, "all leaves share one revision and operation");
            }
        }

        return ordered;
    }

    /// <summary>Builds sorted leaves, bottom-up levels (odd trailing node promoted, never duplicated), root and index digest.</summary>
    public static MerkleManifestResult Build(IEnumerable<MerkleLeafInput> leaves)
    {
        var sorted = SortLeaves(leaves);
        var leafHashes = sorted.Select(LeafHash).ToList();
        var levels = new List<IReadOnlyList<byte[]>>();

        if (sorted.Count == 0)
        {
            var empty = EmptyRoot();
            levels.Add([empty]);
            return new MerkleManifestResult(sorted, leafHashes, levels, empty, 0, LeafIndexDigest(sorted, leafHashes));
        }

        List<byte[]> level = leafHashes;
        levels.Add(level);
        while (level.Count > 1)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i + 1 < level.Count; i += 2)
            {
                next.Add(NodeHash(level[i], level[i + 1]));
            }

            if (level.Count % 2 == 1)
            {
                next.Add(level[^1]);
            }

            level = next;
            levels.Add(level);
        }

        return new MerkleManifestResult(sorted, leafHashes, levels, level[0], (ulong)sorted.Count, LeafIndexDigest(sorted, leafHashes));
    }

    public static byte[] LeafIndexDigest(IReadOnlyList<MerkleLeafInput> sortedLeaves, IReadOnlyList<byte[]> leafHashes)
    {
        ArgumentNullException.ThrowIfNull(sortedLeaves);
        ArgumentNullException.ThrowIfNull(leafHashes);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> scratch = stackalloc byte[8];
        sha.AppendData([0x03]);
        sha.AppendData(IndexDomain);
        BinaryPrimitives.WriteUInt64BigEndian(scratch, (ulong)sortedLeaves.Count);
        sha.AppendData(scratch);
        for (var i = 0; i < sortedLeaves.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(scratch[..2], ItemKeyLength);
            sha.AppendData(scratch[..2]);
            sha.AppendData(sortedLeaves[i].ItemKey);
            sha.AppendData(leafHashes[i]);
        }

        return sha.GetHashAndReset();
    }

    /// <summary>RFC 4122 network byte order. <see cref="Guid.ToByteArray()"/> without the bigEndian flag is mixed-endian and MUST NOT be used.</summary>
    internal static void WriteUuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out _))
        {
            throw new InvalidOperationException("UUID destination too small");
        }
    }

    private sealed class UnsignedByteComparer : IComparer<byte[]>
    {
        public static readonly UnsignedByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
