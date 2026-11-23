package com.rsxdevelops.extendspace.wireless

import com.rsxdevelops.extendspace.connection.TransportKind
import com.rsxdevelops.extendspace.link.HostConnection
import com.rsxdevelops.extendspace.link.HostListener
import com.rsxdevelops.extendspace.protocol.Bye
import com.rsxdevelops.extendspace.protocol.ErrorCode
import com.rsxdevelops.extendspace.protocol.ErrorMessage
import com.rsxdevelops.extendspace.protocol.Hello
import com.rsxdevelops.extendspace.protocol.PairCommit
import com.rsxdevelops.extendspace.protocol.PairNonce
import com.rsxdevelops.extendspace.protocol.PairResult
import com.rsxdevelops.extendspace.protocol.PairReveal
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.security.SecureRandom
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException

class WifiEndpointTest {
    private class RecordingAdvertiser : ServiceAdvertiser {
        @Volatile var last: Map<String, String>? = null
        @Volatile var withdrawn = 0

        override fun advertise(name: String, port: Int, attributes: Map<String, String>) {
            last = attributes
        }

        override fun withdraw() {
            last = null
            withdrawn++
        }
    }

    private val tablet = SoftwareIdentity("CN=tablet")
    private val pc = FakePc()
    private val trust = TrustedHostStore(tempFile("trusted.json"))
    private val advertiser = RecordingAdvertiser()
    private val executor = Executors.newCachedThreadPool()
    private var listener: HostListener? = null
    private val endpoint = WifiEndpoint(
        identity = { tablet },
        trust = trust,
        deviceName = { "Test Pad" },
        appVersion = { "1.0" },
        advertiser = advertiser,
        options = WifiEndpointOptions(port = 0, bindAddress = InetAddress.getLoopbackAddress(), handshakeMillis = 3_000, decisionMillis = 5_000),
    )

    @After
    fun tearDown() {
        listener?.close()
        endpoint.closePairing()
        executor.shutdownNow()
    }

    private fun listen(): HostListener = endpoint.sessionListenerFactory.listen().also { listener = it }

    private fun acceptWithin(listening: HostListener, millis: Long): HostConnection? = try {
        executor.submit<HostConnection> { listening.accept() }.get(millis, TimeUnit.MILLISECONDS)
    } catch (timeout: TimeoutException) {
        null
    }

    private fun waitFor(condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + 5_000
        while (!condition()) {
            check(System.currentTimeMillis() < deadline) { "condition not met in time" }
            Thread.sleep(10)
        }
    }

    @Test
    fun `a trusted PC's session connection reaches the session listener already authenticated`() {
        trust.add(TrustedHost(pc.identity.deviceId, "Test PC", 0))
        val listening = listen()
        pc.connect(endpoint.port!!, WifiTls.SESSION_PROTOCOL)

        val accepted = acceptWithin(listening, 5_000) as TlsHostConnection
        assertTrue(accepted.preauthorized)
        assertEquals(TransportKind.WIRELESS, accepted.transport)
        assertEquals(pc.identity.deviceId, accepted.peerId)
        assertEquals(tablet.deviceId, pc.tabletId)

        pc.socket.outputStream.apply { write(byteArrayOf(1, 2, 3)); flush() }
        val received = ByteArray(3)
        var filled = 0
        while (filled < 3) filled += accepted.input.read(received, filled, 3 - filled)
        assertEquals(listOf<Byte>(1, 2, 3), received.toList())
        accepted.close()
    }

    @Test
    fun `an unpaired PC is told not_paired and nothing reaches the session`() {
        val listening = listen()
        pc.connect(endpoint.port!!, WifiTls.SESSION_PROTOCOL)
        pc.sendHello()

        val refusal = pc.read() as ErrorMessage
        assertEquals(ErrorCode.NOT_PAIRED, refusal.code)
        assertTrue(refusal.fatal)
        assertNull(acceptWithin(listening, 300))
        assertEquals(1, endpoint.refusedPeers)
    }

    @Test
    fun `the endpoint is advertised while it runs, flags the pairing window, and is withdrawn when nothing uses it`() {
        val listening = listen()
        assertEquals(mapOf("id" to tablet.deviceId.take(16), "v" to "0", "p" to "0"), advertiser.last)

        endpoint.openPairing()
        assertEquals("1", advertiser.last!!["p"])
        endpoint.closePairing()
        assertEquals("0", advertiser.last!!["p"])

        listening.close()
        assertNull(advertiser.last)
        assertNull(endpoint.port)
    }

    @Test
    fun `pairing outside the pairing window is refused`() {
        listen()
        pc.connect(endpoint.port!!, WifiTls.PAIRING_PROTOCOL)
        pc.sendHello("pair")

        assertEquals(ErrorCode.PAIRING_NOT_OPEN, (pc.read() as ErrorMessage).code)
        assertFalse(trust.isTrusted(pc.identity.deviceId))
    }

    /** The PC's half of the ceremony up to the reveal; returns the code the PC shows. */
    private fun pcPairsUntilReveal(revealOverride: ByteArray? = null): String {
        pc.connect(endpoint.port!!, WifiTls.PAIRING_PROTOCOL)
        pc.sendHello("pair")
        val hello = pc.read() as Hello
        assertEquals("client", hello.role)
        assertEquals("pair", hello.purpose)
        val hostNonce = ByteArray(32).also(SecureRandom()::nextBytes)
        pc.send(PairCommit(PairingCode.commit(hostNonce)))
        val tabletNonce = (pc.read() as PairNonce).nonce
        pc.send(PairReveal(revealOverride ?: hostNonce))
        return PairingCode.compute(hostNonce, tabletNonce, DeviceIds.toBytes(pc.identity.deviceId), DeviceIds.toBytes(pc.tabletId!!))
    }

    @Test
    fun `when both accept both show the same code and the PC is trusted, then the window closes`() {
        endpoint.openPairing()
        val pcCode = pcPairsUntilReveal()

        waitFor { endpoint.pairing.value is PairingState.Confirm }
        assertEquals(PairingState.Confirm(pcCode, "Test PC"), endpoint.pairing.value)
        endpoint.decide(true)
        pc.send(PairResult(true))

        assertEquals(PairResult(true), pc.read())
        assertTrue(pc.read() is Bye)
        waitFor { endpoint.pairing.value is PairingState.Finished }
        assertTrue((endpoint.pairing.value as PairingState.Finished).paired)
        assertTrue(trust.isTrusted(pc.identity.deviceId))
        assertEquals("Test PC", trust.find(pc.identity.deviceId)!!.name)

        // One attempt per window.
        val again = FakePc(pc.identity).connect(endpoint.port ?: return, WifiTls.PAIRING_PROTOCOL)
        again.sendHello("pair")
        assertEquals(ErrorCode.PAIRING_NOT_OPEN, (again.read() as ErrorMessage).code)
    }

    @Test
    fun `a reveal that does not match the commitment stops pairing`() {
        endpoint.openPairing()
        pcPairsUntilReveal(revealOverride = ByteArray(32))

        assertEquals(ErrorCode.PAIRING_FAILED, (pc.read() as ErrorMessage).code)
        waitFor { endpoint.pairing.value is PairingState.Finished }
        assertFalse((endpoint.pairing.value as PairingState.Finished).paired)
        assertFalse(trust.isTrusted(pc.identity.deviceId))
    }

    @Test
    fun `when the tablet's user declines the PC is told and nothing is stored`() {
        endpoint.openPairing()
        pcPairsUntilReveal()
        waitFor { endpoint.pairing.value is PairingState.Confirm }

        endpoint.decide(false)

        assertEquals(PairResult(false), pc.read())
        waitFor { endpoint.pairing.value is PairingState.Finished }
        assertFalse(trust.isTrusted(pc.identity.deviceId))
    }

    @Test
    fun `when the PC declines first the tablet's question ends and nothing is stored`() {
        endpoint.openPairing()
        pcPairsUntilReveal()
        waitFor { endpoint.pairing.value is PairingState.Confirm }

        pc.send(PairResult(false))

        waitFor { endpoint.pairing.value is PairingState.Finished }
        assertTrue((endpoint.pairing.value as PairingState.Finished).message.contains("declined"))
        assertFalse(trust.isTrusted(pc.identity.deviceId))
    }

    @Test
    fun `closing the window during the question cancels the attempt`() {
        endpoint.openPairing()
        pcPairsUntilReveal()
        waitFor { endpoint.pairing.value is PairingState.Confirm }

        endpoint.closePairing()

        assertEquals(PairResult(false), pc.read())
        assertFalse(trust.isTrusted(pc.identity.deviceId))
    }

    @Test
    fun `connections beyond the handshake limit are closed at once rather than queued`() {
        val limited = WifiEndpoint(
            { tablet }, trust, { "Test Pad" }, { "1.0" },
            options = WifiEndpointOptions(port = 0, bindAddress = InetAddress.getLoopbackAddress(), handshakeMillis = 3_000, maxHandshakes = 1),
        )
        val listening = limited.sessionListenerFactory.listen()
        try {
            val silent = Socket().apply { connect(InetSocketAddress(InetAddress.getLoopbackAddress(), limited.port!!), 2_000) }
            Thread.sleep(200)
            val extra = Socket().apply {
                connect(InetSocketAddress(InetAddress.getLoopbackAddress(), limited.port!!), 2_000)
                soTimeout = 2_000
            }
            val started = System.currentTimeMillis()
            assertEquals(-1, extra.getInputStream().read())
            assertTrue(System.currentTimeMillis() - started < 1_500)
            assertTrue(limited.refusedPeers >= 1)
            silent.close()
            extra.close()
        } finally {
            listening.close()
        }
    }
}
