using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace IsotopeProbe.Web.Scanning;

// Signed, user-scoped nonce: tampering or moving a form between users is rejected.
public sealed class SubmissionTokens(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("WebScanSubmission.v1");
    public string Create(Guid user) => protector.Protect($"{user:D}:{Guid.NewGuid():D}");
    public Guid Validate(string? value, Guid user)
    {
        try
        {
            var parts = protector.Unprotect(value ?? "").Split(':');
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var owner) && owner == user &&
                Guid.TryParse(parts[1], out var nonce) && nonce != Guid.Empty) return nonce;
        }
        catch (CryptographicException) { }
        throw new ArgumentException("Invalid submission token. Open New scan again.");
    }
}
