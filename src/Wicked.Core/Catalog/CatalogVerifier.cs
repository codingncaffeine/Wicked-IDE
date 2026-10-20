using System.Security.Cryptography;

namespace Wicked.Core.Catalog;

/// <summary>
/// Checks the catalog's signature: ECDSA over NIST P-256 of SHA-256 of the exact file bytes, the signature written as base64
/// of the 64-byte IEEE P1363 form (r followed by s). The public key is compiled in, so a changed CDN or a hijacked mirror
/// cannot make the app believe a file the maintainer did not sign. The private key is never in the repository.
/// </summary>
public static class CatalogVerifier
{
    /// <summary>Base64 of the DER SubjectPublicKeyInfo of the catalog signing key (printed by <c>tools/catalog/new-key.sh</c>).</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsPBXsNE9nDi7ZcuDFbRZB6VNqHrUm+fvH/MmEvmUftyhnSD/7MKyvfKowg1Fa7i3h0RJB4qVvdDrFRrdcWPWNA==";

    public static bool Verify(ReadOnlySpan<byte> catalog, string signatureBase64) => Verify(catalog, signatureBase64, PublicKey);

    public static bool Verify(ReadOnlySpan<byte> catalog, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            if (signature.Length != 64) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(catalog, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
