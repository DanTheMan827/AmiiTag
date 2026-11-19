using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;

namespace AmiiTag.Tags;

/// <summary>
/// Verifies the NXP originality signature (ECDSA over secp128r1) returned by READ_SIG.
/// </summary>
public static class NtagOriginality
{
    private static readonly byte[] PublicKey =
    [
        0x04, 0x49, 0x4E, 0x1A, 0x38, 0x6D, 0x3D, 0x3C,
        0xFE, 0x3D, 0xC1, 0x0E, 0x5D, 0xE6, 0x8A, 0x49,
        0x9B, 0x1C, 0x20, 0x2D, 0xB5, 0xB1, 0x32, 0x39,
        0x3E, 0x89, 0xED, 0x19, 0xFE, 0x5B, 0xE8, 0xBC, 0x61,
    ];

    private static readonly Lazy<ECPublicKeyParameters> Key = new(() =>
    {
        var curve = SecNamedCurves.GetByName("secp128r1");
        var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        var point = curve.Curve.DecodePoint(PublicKey);
        return new ECPublicKeyParameters(point, domain);
    });

    /// <summary>
    /// Validates a signature for a 7 or 9 byte UID.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> uid, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 32 || (uid.Length != 7 && uid.Length != 9))
        {
            return false;
        }

        if (signature.IndexOfAnyExcept((byte)0) < 0)
        {
            return false;
        }

        var uid7 = uid.Length == 9 ? NtagUid.ToSevenByteUid(uid) : uid.ToArray();

        // The Swift code pads the UID to 16 bytes and treats it as the hash, which is numerically the UID itself.
        var message = new byte[16];
        uid7.CopyTo(message, 9);

        try
        {
            var signer = new ECDsaSigner();
            signer.Init(false, Key.Value);
            var r = new BigInteger(1, signature[..16].ToArray());
            var s = new BigInteger(1, signature[16..].ToArray());
            return signer.VerifySignature(message, r, s);
        }
        catch
        {
            return false;
        }
    }
}
