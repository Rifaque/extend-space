package com.rsxdevelops.extendspace.wireless

import com.rsxdevelops.extendspace.protocol.ControlCodec
import com.rsxdevelops.extendspace.protocol.ControlMessage
import com.rsxdevelops.extendspace.protocol.Hello
import com.rsxdevelops.extendspace.protocol.Protocol
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import java.io.File
import java.math.BigInteger
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.security.KeyPairGenerator
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Date
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/** A software P-256 identity with a self-signed certificate: what AndroidKeyStore gives the app, for the desktop JVM. */
internal class SoftwareIdentity(subject: String = "CN=test") : DeviceIdentity {
    private val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    override val privateKey: PrivateKey = pair.private
    override val certificate: X509Certificate = JcaX509CertificateConverter().getCertificate(
        JcaX509v3CertificateBuilder(
            X500Name(subject),
            BigInteger.valueOf(SecureRandom().nextLong() and Long.MAX_VALUE),
            Date(System.currentTimeMillis() - 86_400_000L),
            Date(System.currentTimeMillis() + 86_400_000L),
            X500Name(subject),
            pair.public,
        ).build(JcaContentSignerBuilder("SHA256withECDSA").build(pair.private)),
    )
}

internal fun tempFile(name: String): File = File.createTempFile("extendspace-", "-$name").apply { delete(); deleteOnExit() }

/**
 * The PC's side of the Wi-Fi binding for these tests: a TLS 1.3 client presenting [identity] and
 * offering one ALPN name, as the Windows host does. It accepts whatever certificate the tablet shows
 * and reports it, so that tests can check what a PC would pin.
 */
internal class FakePc(val identity: DeviceIdentity = SoftwareIdentity("CN=fake pc")) {
    lateinit var socket: SSLSocket
        private set
    lateinit var frames: BlockingFrames
        private set
    var tabletId: String? = null
        private set

    fun connect(port: Int, protocol: String): FakePc {
        val context = SSLContext.getInstance("TLS").apply {
            init(
                arrayOf(IdentityKeyManager { identity }),
                arrayOf(object : X509TrustManager {
                    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
                    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                        tabletId = DeviceIds.of(chain!![0])
                    }
                    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
                }),
                SecureRandom(),
            )
        }
        val plain = Socket().apply { connect(InetSocketAddress(InetAddress.getLoopbackAddress(), port), 2_000) }
        socket = (context.socketFactory.createSocket(plain, "extendspace", port, true) as SSLSocket).apply {
            enabledProtocols = arrayOf("TLSv1.3")
            sslParameters = sslParameters.apply { applicationProtocols = arrayOf(protocol) }
            soTimeout = 5_000
            startHandshake()
        }
        frames = BlockingFrames(socket.inputStream, socket.outputStream)
        return this
    }

    fun sendHello(purpose: String? = null) =
        frames.write(Protocol.PREFACE + ControlCodec.encode(Hello(0, 0, "host", "Test PC", "9.9", purpose)))

    fun send(message: ControlMessage) = frames.write(ControlCodec.encode(message))

    fun read(): ControlMessage = frames.read()

    fun close() = socket.close()
}
