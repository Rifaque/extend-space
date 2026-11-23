package com.rsxdevelops.extendspace.wireless

import java.security.MessageDigest

/**
 * The arithmetic of the pairing ceremony (D77, transport architecture section 5). Byte-exact with the
 * PC's `PairingCode`; both are checked against `tests/pairing-vectors/sas-v1.json`.
 */
internal object PairingCode {
    const val NONCE_LENGTH = 32

    private val COMMIT_LABEL = "ExtendSpace pair commit v1\u0000".toByteArray(Charsets.US_ASCII)
    private val CODE_LABEL = "ExtendSpace pair sas v1\u0000".toByteArray(Charsets.US_ASCII)

    /** SHA-256(label ‖ host nonce): what the PC committed to before it saw this device's nonce. */
    fun commit(hostNonce: ByteArray): ByteArray {
        require(hostNonce.size == NONCE_LENGTH) { "The host nonce must be $NONCE_LENGTH bytes." }
        return sha256(COMMIT_LABEL, hostNonce)
    }

    /** The six digits both screens show, from both nonces and both device IDs as this side saw them. */
    fun compute(hostNonce: ByteArray, clientNonce: ByteArray, hostId: ByteArray, clientId: ByteArray): String {
        for (value in listOf(hostNonce, clientNonce, hostId, clientId)) require(value.size == NONCE_LENGTH) { "Pairing inputs are $NONCE_LENGTH bytes." }
        val digest = sha256(CODE_LABEL, hostNonce, clientNonce, hostId, clientId)
        val value = ((digest[0].toLong() and 0xFF) shl 24) or ((digest[1].toLong() and 0xFF) shl 16) or
            ((digest[2].toLong() and 0xFF) shl 8) or (digest[3].toLong() and 0xFF)
        return "%06d".format(value % 1_000_000)
    }

    /** "123 456", as both apps display it. */
    fun format(code: String): String = if (code.length == 6) "${code.substring(0, 3)} ${code.substring(3)}" else code

    private fun sha256(vararg parts: ByteArray): ByteArray =
        MessageDigest.getInstance("SHA-256").apply { parts.forEach(::update) }.digest()
}
