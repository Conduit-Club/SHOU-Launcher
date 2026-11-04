using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace SHOU.Launcher.Core.Java;

/// <summary>Reads only authenticated public-certificate bags in memory, without importing a PFX into an OS key store.</summary>
public static class Pkcs12TrustReader
{
    private const string Data = "1.2.840.113549.1.7.1";
    private const string EncryptedData = "1.2.840.113549.1.7.6";
    private const string CertificateBag = "1.2.840.113549.1.12.10.1.3";
    private static readonly Asn1Tag Explicit = new(TagClass.ContextSpecific, 0, true);

    public static IReadOnlyList<string> ReadFingerprints(byte[] data)
    {
        if (data.Length is < 1 or > 33554432) throw new InvalidDataException("Invalid PKCS12 trust-store size.");
        ValidateWorkBudget(data);
        var info = Pkcs12Info.Decode(data, out var consumed);
        if (consumed != data.Length || info.IntegrityMode != Pkcs12IntegrityMode.Password || !info.VerifyMac("changeit"))
            throw new InvalidDataException("PKCS12 integrity or default password could not be verified.");
        var fingerprints = new List<string>();
        foreach (var safe in info.AuthenticatedSafe)
        {
            if (safe.ConfidentialityMode == Pkcs12ConfidentialityMode.Password) safe.Decrypt("changeit");
            if (safe.ConfidentialityMode != Pkcs12ConfidentialityMode.None)
                throw new InvalidDataException("Unsupported PKCS12 confidentiality mode.");
            foreach (var bag in safe.GetBags())
            {
                if (fingerprints.Count >= 10000 || bag is not Pkcs12CertBag certificate || !certificate.IsX509Certificate)
                    throw new InvalidDataException("PKCS12 private keys, unknown/nested bags or excessive certificates are refused.");
                // A CertBag wraps the DER certificate in an ASN.1 OCTET STRING.
                var encoded = Reader(certificate.EncodedCertificate);
                var der = encoded.ReadOctetString(); encoded.ThrowIfNotEmpty();
                using var parsed = X509CertificateLoader.LoadCertificate(der);
                fingerprints.Add(parsed.GetCertHashString(HashAlgorithmName.SHA256));
            }
        }
        return fingerprints;
    }

    // Bound declared KDF work before invoking the official crypto library. This parses structure/limits only;
    // all MAC, password derivation and decryption remain System.Security.Cryptography.Pkcs operations.
    private static void ValidateWorkBudget(byte[] data)
    {
        long work = 0;
        var outer = Reader(data); var pfx = outer.ReadSequence(); outer.ThrowIfNotEmpty();
        if (pfx.ReadInteger() != 3) throw new InvalidDataException("Unsupported PKCS12 version.");
        var content = pfx.ReadSequence();
        if (content.ReadObjectIdentifier() != Data) throw new InvalidDataException("Password-authenticated PKCS12 is required.");
        var wrapper = content.ReadSequence(Explicit); var safeBytes = wrapper.ReadOctetString();
        wrapper.ThrowIfNotEmpty(); content.ThrowIfNotEmpty();
        if (!pfx.HasData) throw new InvalidDataException("PKCS12 MAC is required.");
        var mac = pfx.ReadSequence(); _ = mac.ReadSequence();
        Salt(mac); Iterations(mac.HasData ? mac.ReadInteger() : 1); mac.ThrowIfNotEmpty(); pfx.ThrowIfNotEmpty();
        var authenticated = Reader(safeBytes); var safes = authenticated.ReadSequence(); authenticated.ThrowIfNotEmpty();
        var safeCount = 0;
        while (safes.HasData)
        {
            if (++safeCount > 4096) throw new InvalidDataException("PKCS12 safe-content count exceeds its budget.");
            var item = safes.ReadSequence(); var oid = item.ReadObjectIdentifier();
            var value = item.ReadSequence(Explicit); item.ThrowIfNotEmpty();
            if (oid == Data)
            {
                var encoded = value.ReadOctetString(); value.ThrowIfNotEmpty();
                var bagReader = Reader(encoded); var bags = bagReader.ReadSequence(); bagReader.ThrowIfNotEmpty();
                var count = 0;
                while (bags.HasData)
                {
                    if (++count > 10000) throw new InvalidDataException("PKCS12 bag count exceeds its budget.");
                    var bag = bags.ReadSequence();
                    if (bag.ReadObjectIdentifier() != CertificateBag)
                        throw new InvalidDataException("PKCS12 non-certificate bag is refused before import.");
                    // The official decoder validates the remaining certificate/attribute structure.
                }
            }
            else if (oid == EncryptedData)
            {
                var encrypted = value.ReadSequence(); value.ThrowIfNotEmpty();
                var version = encrypted.ReadInteger();
                if (version != 0) throw new InvalidDataException("Unsupported PKCS12 encrypted content version.");
                var encryptedContent = encrypted.ReadSequence();
                if (encryptedContent.ReadObjectIdentifier() != Data) throw new InvalidDataException("Unsupported PKCS12 encrypted payload.");
                Algorithm(encryptedContent.ReadSequence());
            }
            else throw new InvalidDataException("Unsupported PKCS12 safe-content type.");
        }

        void Iterations(System.Numerics.BigInteger count)
        {
            if (count < 1 || count > 600000 || (work += (long)count) > 1000000)
                throw new InvalidDataException("PKCS12 declared KDF work exceeds its budget.");
        }
        void Salt(AsnReader reader)
        {
            if (reader.ReadOctetString().Length > 1024) throw new InvalidDataException("PKCS12 salt exceeds its budget.");
        }
        void Algorithm(AsnReader algorithm)
        {
            var oid = algorithm.ReadObjectIdentifier(); var parameters = algorithm.ReadSequence(); algorithm.ThrowIfNotEmpty();
            if (oid == "1.2.840.113549.1.5.13") // PBES2; the library validates the selected cipher and PRF.
            {
                var kdf = parameters.ReadSequence();
                if (kdf.ReadObjectIdentifier() != "1.2.840.113549.1.5.12") throw new InvalidDataException("Unsupported PKCS12 KDF.");
                var pbkdf = kdf.ReadSequence(); kdf.ThrowIfNotEmpty(); Salt(pbkdf); Iterations(pbkdf.ReadInteger());
                if (pbkdf.HasData && pbkdf.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
                {
                    var length = pbkdf.ReadInteger();
                    if (length < 1 || length > 64) throw new InvalidDataException("PKCS12 derived-key length exceeds its budget.");
                }
                if (pbkdf.HasData) _ = pbkdf.ReadSequence();
                pbkdf.ThrowIfNotEmpty(); _ = parameters.ReadSequence(); parameters.ThrowIfNotEmpty();
            }
            else if (oid.StartsWith("1.2.840.113549.1.12.1.", StringComparison.Ordinal)
                     && oid[^1] is >= '1' and <= '6' && oid.Length == "1.2.840.113549.1.12.1.1".Length)
            {
                Salt(parameters); Iterations(parameters.ReadInteger()); parameters.ThrowIfNotEmpty();
            }
            else throw new InvalidDataException("Unsupported PKCS12 password encryption.");
        }
    }
    private static AsnReader Reader(ReadOnlyMemory<byte> data) => new(data, AsnEncodingRules.BER);
}
