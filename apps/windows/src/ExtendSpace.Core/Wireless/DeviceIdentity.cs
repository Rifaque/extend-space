using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ExtendSpace.Core.Wireless;

/// <summary>
/// Device IDs (D76): the SHA-256 of a certificate's SubjectPublicKeyInfo, as 64 lowercase hex digits.
/// They identify a key, not a name; a reinstalled app has a new one.
/// </summary>
public static class DeviceIds
{
    public const int Length = 64;

    public static string Of(X509Certificate2 certificate) =>
        Of(certificate.PublicKey.ExportSubjectPublicKeyInfo());

    public static string Of(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));

    public static bool IsValid(string? id) =>
        id is { Length: Length } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>The 32 bytes a device ID stands for, as the pairing code uses them.</summary>
    public static byte[] ToBytes(string id) =>
        IsValid(id) ? Convert.FromHexString(id) : throw new ArgumentException("Not a device ID.", nameof(id));

    /// <summary>What people and logs are shown: the first eight digits, grouped (<c>3f2a-91c0</c>).</summary>
    public static string Short(string id) => id.Length >= 8 ? $"{id[..4]}-{id[4..8]}" : id;
}

/// <summary>Protects a secret at rest for the current user. DPAPI on Windows (ExtendSpace.Media); a fake in tests.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] secret);

    /// <exception cref="CryptographicException">The data was not protected for this user, or was altered.</exception>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>This PC's long-term key and self-signed certificate (D79). The certificate carries its private key.</summary>
public sealed class HostIdentity(X509Certificate2 certificate, byte[] pkcs12)
{
    public X509Certificate2 Certificate { get; } = certificate;

    /// <summary>The identity as it is stored: a key loaded for TLS cannot be exported again, so the original bytes are kept.</summary>
    internal byte[] Pkcs12 { get; } = pkcs12;

    public string DeviceId { get; } = DeviceIds.Of(certificate);

    /// <summary>A new P-256 identity, valid for 20 years, usable as a TLS client and server certificate.</summary>
    public static HostIdentity Create(string subject = "CN=ExtendSpace host")
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));

        // Through PKCS#12 so that the key is one SChannel can use for TLS client authentication.
        byte[] pkcs12 = created.Export(X509ContentType.Pkcs12);
        return new HostIdentity(X509CertificateLoader.LoadPkcs12(pkcs12, password: null), pkcs12);
    }
}

/// <summary>
/// Keeps <see cref="HostIdentity"/> in one file: PKCS#12 protected by <see cref="ISecretProtector"/>
/// (DPAPI, current user). Created on first use; a file that cannot be read is set aside and replaced,
/// which costs re-pairing and is logged, rather than leaving wireless unusable.
/// </summary>
public sealed class HostIdentityStore(string path, ISecretProtector protector, Action<string>? log = null)
{
    private readonly Lock _gate = new();
    private HostIdentity? _loaded;

    public string Path { get; } = path;

    public HostIdentity LoadOrCreate()
    {
        lock (_gate)
        {
            if (_loaded is not null)
            {
                return _loaded;
            }

            if (File.Exists(Path))
            {
                try
                {
                    byte[] pkcs12 = protector.Unprotect(File.ReadAllBytes(Path));
                    _loaded = new HostIdentity(X509CertificateLoader.LoadPkcs12(pkcs12, password: null), pkcs12);
                    return _loaded;
                }
                catch (Exception exception) when (exception is CryptographicException or IOException)
                {
                    string aside = Path + ".unreadable";
                    File.Move(Path, aside, overwrite: true);
                    log?.Invoke($"this PC's wireless identity could not be read ({exception.GetType().Name}); a new one is made and tablets must be paired again");
                }
            }

            HostIdentity created = HostIdentity.Create();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            AtomicFile.Write(Path, protector.Protect(created.Pkcs12));
            log?.Invoke($"created this PC's wireless identity {DeviceIds.Short(created.DeviceId)}");
            _loaded = created;
            return created;
        }
    }
}

internal static class AtomicFile
{
    /// <summary>Writes beside the target, then replaces it, so that a crash leaves the old file or the new one.</summary>
    public static void Write(string path, byte[] contents)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }
}
