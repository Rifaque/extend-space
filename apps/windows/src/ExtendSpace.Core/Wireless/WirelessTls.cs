using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ExtendSpace.Core.Wireless;

/// <summary>The tablet's key is not the one this PC paired with (D76). Never worth retrying.</summary>
public sealed class PinMismatchException(string expected, string actual)
    : AuthenticationException($"The tablet presented key {DeviceIds.Short(actual)}, not the paired {DeviceIds.Short(expected)}.")
{
    public string ActualDeviceId { get; } = actual;
}

/// <summary>
/// The PC's side of the Wi-Fi binding's TLS (transport architecture section 4): TLS 1.3 only, this
/// PC's certificate presented, the tablet's certificate accepted only by its key's hash. No CA, no
/// name check, no revocation lookup (there is nothing to look up; revocation is "forget").
/// </summary>
public static class WirelessTls
{
    public const string SessionProtocol = "exsp/0";
    public const string PairingProtocol = "exsp-pair/0";

    /// <summary>Not checked; TLS needs a name for its SNI field and this one says nothing about anyone.</summary>
    private const string TargetHost = "extendspace";

    /// <summary>
    /// Runs the handshake on <paramref name="inner"/>. With <paramref name="expectedPeerId"/> the
    /// tablet must hold that key; without it (pairing) any key is accepted and returned.
    /// </summary>
    /// <exception cref="PinMismatchException">The tablet's key is not the expected one.</exception>
    /// <exception cref="AuthenticationException">TLS failed for another reason.</exception>
    public static async Task<(SslStream Stream, string PeerDeviceId)> ConnectAsync(
        Stream inner, HostIdentity identity, string applicationProtocol, string? expectedPeerId, CancellationToken cancellation)
    {
        string? peerId = null;
        PinMismatchException? mismatch = null;
        var tls = new SslStream(inner, leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            string id = DeviceIds.Of(ToCertificate2(certificate));
            if (expectedPeerId is not null && id != expectedPeerId)
            {
                mismatch = new PinMismatchException(expectedPeerId, id);
                return false;
            }

            peerId = id;
            return true;
        });

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = TargetHost,
            EnabledSslProtocols = SslProtocols.Tls13,
            ApplicationProtocols = [new SslApplicationProtocol(applicationProtocol)],
            ClientCertificateContext = SslStreamCertificateContext.Create(identity.Certificate, additionalCertificates: null, offline: true),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };

        try
        {
            await tls.AuthenticateAsClientAsync(options, cancellation).ConfigureAwait(false);
        }
        catch (AuthenticationException) when (mismatch is not null)
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw mismatch;
        }
        catch
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (tls.NegotiatedApplicationProtocol.ToString() != applicationProtocol || peerId is null)
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw new AuthenticationException($"The tablet did not agree to '{applicationProtocol}'; it may be another program, or an incompatible version of ExtendSpace.");
        }

        return (tls, peerId);
    }

    private static X509Certificate2 ToCertificate2(X509Certificate certificate) =>
        certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
}
