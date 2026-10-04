// ServerSignature.cs
using System.Security.Cryptography;

namespace OVS.Rollback.Utils
{
    /// <summary>
    /// Checks the OpenVersus server's signature on a response (the X-OVS-Signature header): ECDSA P-256 over
    /// the exact body bytes with SHA-256, the signature as IEEE P1363 r||s in base64, the key as a base64
    /// SubjectPublicKeyInfo. Node.js signs this shape with crypto.sign("sha256", body, { key, dsaEncoding: "ieee-p1363" }).
    /// </summary>
    internal static class ServerSignature
    {
        /// <summary>Whether <paramref name="signature"/> is <paramref name="publicKey"/>'s owner's over <paramref name="body"/>; false for anything malformed.</summary>
        public static bool Verify(string publicKey, ReadOnlySpan<byte> body, string? signature)
        {
            if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
                return key.VerifyData(body, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256);
            }
            catch (Exception e) when (e is FormatException or CryptographicException)
            {
                return false;
            }
        }
    }
}
