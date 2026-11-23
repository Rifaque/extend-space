package com.rsxdevelops.extendspace.wireless

import java.net.Socket
import java.security.KeyStore
import java.security.MessageDigest
import java.security.Principal
import java.security.PrivateKey
import java.security.cert.X509Certificate
import javax.net.ssl.SSLEngine
import javax.net.ssl.X509ExtendedKeyManager

/**
 * Device IDs (D76): the SHA-256 of a certificate's SubjectPublicKeyInfo, as 64 lowercase hex digits.
 * The same definition as the PC's `DeviceIds`. They identify a key, not a name.
 */
internal object DeviceIds {
    const val LENGTH = 64

    fun of(certificate: X509Certificate): String = of(certificate.publicKey.encoded)

    /** [subjectPublicKeyInfo] is the DER encoding, which is what `PublicKey.getEncoded` returns for EC and RSA keys. */
    fun of(subjectPublicKeyInfo: ByteArray): String = hex(MessageDigest.getInstance("SHA-256").digest(subjectPublicKeyInfo))

    fun isValid(id: String?): Boolean = id != null && id.length == LENGTH && id.all { it in '0'..'9' || it in 'a'..'f' }

    fun toBytes(id: String): ByteArray {
        require(isValid(id)) { "Not a device ID." }
        return ByteArray(32) { index -> id.substring(index * 2, index * 2 + 2).toInt(16).toByte() }
    }

    /** What people and logs are shown: the first eight digits, grouped (`3f2a-91c0`). */
    fun short(id: String): String = if (id.length >= 8) "${id.substring(0, 4)}-${id.substring(4, 8)}" else id

    fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
}

/** This device's long-term key and self-signed certificate (D79). */
internal interface DeviceIdentity {
    val certificate: X509Certificate
    val privateKey: PrivateKey
    val deviceId: String get() = DeviceIds.of(certificate)
}

/**
 * The identity in AndroidKeyStore: a P-256 key that never leaves the keystore, created on first use,
 * with the self-signed certificate the keystore makes for it. Deleting the app's data (or
 * reinstalling) makes a new one, and paired PCs must pair again.
 */
internal class KeystoreDeviceIdentity(private val alias: String = ALIAS) : DeviceIdentity {
    private val entry: KeyStore.PrivateKeyEntry by lazy { loadOrCreate() }

    override val certificate: X509Certificate get() = entry.certificate as X509Certificate
    override val privateKey: PrivateKey get() = entry.privateKey

    private fun loadOrCreate(): KeyStore.PrivateKeyEntry {
        val store = KeyStore.getInstance(PROVIDER).apply { load(null) }
        (store.getEntry(alias, null) as? KeyStore.PrivateKeyEntry)?.let { return it }

        val generator = java.security.KeyPairGenerator.getInstance(android.security.keystore.KeyProperties.KEY_ALGORITHM_EC, PROVIDER)
        generator.initialize(
            android.security.keystore.KeyGenParameterSpec.Builder(alias, android.security.keystore.KeyProperties.PURPOSE_SIGN)
                .setAlgorithmParameterSpec(java.security.spec.ECGenParameterSpec("secp256r1"))
                // TLS signs with SHA-256 (ecdsa_secp256r1_sha256); NONE lets a provider hash first.
                .setDigests(
                    android.security.keystore.KeyProperties.DIGEST_NONE,
                    android.security.keystore.KeyProperties.DIGEST_SHA256,
                    android.security.keystore.KeyProperties.DIGEST_SHA384,
                    android.security.keystore.KeyProperties.DIGEST_SHA512,
                )
                .setCertificateSubject(javax.security.auth.x500.X500Principal("CN=ExtendSpace tablet"))
                .setCertificateNotBefore(java.util.Date(System.currentTimeMillis() - 86_400_000L))
                .setCertificateNotAfter(java.util.Date(System.currentTimeMillis() + 20L * 365 * 86_400_000L))
                .build(),
        )
        generator.generateKeyPair()
        return store.getEntry(alias, null) as KeyStore.PrivateKeyEntry
    }

    private companion object {
        const val PROVIDER = "AndroidKeyStore"
        const val ALIAS = "extendspace-identity-v1"
    }
}

/** Presents [identity] as the TLS server's certificate. One key, one alias. */
internal class IdentityKeyManager(private val identity: () -> DeviceIdentity) : X509ExtendedKeyManager() {
    override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?): String = ALIAS
    override fun chooseEngineServerAlias(keyType: String?, issuers: Array<out Principal>?, engine: SSLEngine?): String = ALIAS
    override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?): String = ALIAS
    override fun chooseEngineClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, engine: SSLEngine?): String = ALIAS
    override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?): Array<String> = arrayOf(ALIAS)
    override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?): Array<String> = arrayOf(ALIAS)
    override fun getCertificateChain(alias: String?): Array<X509Certificate> = arrayOf(identity().certificate)
    override fun getPrivateKey(alias: String?): PrivateKey = identity().privateKey

    private companion object {
        const val ALIAS = "extendspace"
    }
}
