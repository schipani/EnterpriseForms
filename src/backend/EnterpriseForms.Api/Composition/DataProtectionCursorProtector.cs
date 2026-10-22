using System.Security.Cryptography;
using EnterpriseForms.Application.Common;
using Microsoft.AspNetCore.DataProtection;

namespace EnterpriseForms.Api.Composition;

/// <summary>
/// List cursors (C-29) protected with ASP.NET Core Data Protection under their own purpose, time-limited. With the
/// host's key ring, a cursor issued by one API instance is valid only where that key ring is shared (as for the session
/// cookie); elsewhere it is rejected as invalid and the client reloads the first page — never widened.
/// </summary>
public sealed class DataProtectionCursorProtector(IDataProtectionProvider provider) : IOpaqueCursorProtector
{
    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector("EnterpriseForms.RecordListCursor.v1").ToTimeLimitedDataProtector();

    public string Protect(string payload, TimeSpan lifetime) => protector.Protect(payload, lifetime);

    public string? Unprotect(string token)
    {
        try
        {
            return protector.Unprotect(token, out _);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
