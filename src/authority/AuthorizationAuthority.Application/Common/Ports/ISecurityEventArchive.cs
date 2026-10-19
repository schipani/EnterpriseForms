namespace AuthorizationAuthority.Application.Common.Ports;

public sealed record ArchiveReceipt(string ReceiptReference, byte[] ReceiptDigest);

/// <summary>
/// Separately administered append-only archive of full semantic security/content event bodies (G14). The Docker
/// implementation is an Azurite blob container on the authority network with create-only writes — a test double
/// for an immutable WORM store, explicitly not Azure immutable storage or Confidential Ledger evidence.
/// </summary>
public interface ISecurityEventArchive
{
    string ArchiveName { get; }

    /// <summary>Appends the body at a deterministic key. If the key already exists with identical bytes the call is idempotent; different bytes ⇒ VerificationFailed.</summary>
    Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken);

    Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken);
}

public sealed class ArchiveUnavailableException(string detail, Exception? inner = null) : Exception(detail, inner);

public sealed class ArchiveConflictException(string detail) : Exception(detail);
