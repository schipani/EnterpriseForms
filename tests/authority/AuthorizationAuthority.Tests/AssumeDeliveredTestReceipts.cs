using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// TEST DOUBLE ONLY (SEC-BL-01 E-6/E-7): for in-memory stores that model no archive delivery at all (they report nothing pending), every row
/// counts as delivered, which is exactly what those stores modelled before E-6.
/// Never used by any production composition; the D+ and receipt tests compose <c>TrustedJournalReceipts</c> instead.
/// </summary>
public sealed class AssumeDeliveredTestReceipts : IJournalReceiptVerifier
{
    public static AssumeDeliveredTestReceipts Instance { get; } = new();

    public Task<JournalReceiptVerdict> VerifyAsync(SecurityJournalEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(new JournalReceiptVerdict(JournalReceiptStatus.Verified, JournalReceiptCodes.Verified));

    public Task<JournalReceiptVerdict> VerifyStreamHeadAsync(SecurityJournalEntry entry, CancellationToken cancellationToken) => VerifyAsync(entry, cancellationToken);
}
