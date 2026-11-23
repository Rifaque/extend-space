package com.rsxdevelops.extendspace.wireless

import com.rsxdevelops.extendspace.link.CompositeHostListenerFactory
import com.rsxdevelops.extendspace.link.HostListener
import com.rsxdevelops.extendspace.link.HostListenerFactory
import com.rsxdevelops.extendspace.link.MemoryConnection
import com.rsxdevelops.extendspace.link.MemoryListenerFactory
import com.rsxdevelops.extendspace.protocol.ControlCodec
import com.rsxdevelops.extendspace.protocol.ControlType
import com.rsxdevelops.extendspace.protocol.ErrorCode
import com.rsxdevelops.extendspace.protocol.Hello
import com.rsxdevelops.extendspace.protocol.PairCommit
import com.rsxdevelops.extendspace.protocol.PairNonce
import com.rsxdevelops.extendspace.protocol.PairResult
import com.rsxdevelops.extendspace.protocol.PairReveal
import com.rsxdevelops.extendspace.protocol.Protocol
import com.rsxdevelops.extendspace.protocol.ProtocolException
import com.rsxdevelops.extendspace.protocol.StreamConfig
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.IOException
import java.security.MessageDigest
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class PairingCodeTest {
    @Test
    fun `the commitment and code match the shared vectors`() {
        val cases = JSONObject(locate("tests/pairing-vectors/sas-v1.json").readText()).getJSONArray("cases")
        assertTrue(cases.length() >= 5)
        for (index in 0 until cases.length()) {
            val case = cases.getJSONObject(index)
            fun bytes(field: String) = hexToBytes(case.getString(field))
            assertEquals(case.getString("name"), case.getString("commitment"), DeviceIds.hex(PairingCode.commit(bytes("host_nonce"))))
            assertEquals(case.getString("name"), case.getString("code"), PairingCode.compute(bytes("host_nonce"), bytes("client_nonce"), bytes("host_id"), bytes("client_id")))
        }
    }

    @Test
    fun `codes keep leading zeros, are formatted in two groups and refuse inputs of the wrong length`() {
        assertEquals("014 363", PairingCode.format("014363"))
        assertThrows(IllegalArgumentException::class.java) { PairingCode.commit(ByteArray(31)) }
        assertThrows(IllegalArgumentException::class.java) { PairingCode.compute(ByteArray(32), ByteArray(32), ByteArray(32), ByteArray(1)) }
    }

    @Test
    fun `device IDs are the SHA-256 of the SubjectPublicKeyInfo, as on the PC`() {
        val identity = SoftwareIdentity()
        assertEquals(DeviceIds.hex(MessageDigest.getInstance("SHA-256").digest(identity.certificate.publicKey.encoded)), identity.deviceId)
        assertTrue(DeviceIds.isValid(identity.deviceId))
        assertFalse(DeviceIds.isValid(identity.deviceId.uppercase()))
        assertEquals(identity.deviceId, DeviceIds.hex(DeviceIds.toBytes(identity.deviceId)))
        assertEquals("${identity.deviceId.take(4)}-${identity.deviceId.substring(4, 8)}", DeviceIds.short(identity.deviceId))
    }

    private fun hexToBytes(hex: String) = ByteArray(hex.length / 2) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    private fun locate(relative: String): File {
        var directory: File? = File("").absoluteFile
        while (directory != null) {
            File(directory, relative).takeIf { it.isFile }?.let { return it }
            directory = directory.parentFile
        }
        error("'$relative' was not found above ${File("").absolutePath}.")
    }
}

class WirelessCodecTest {
    private fun roundTrip(message: com.rsxdevelops.extendspace.protocol.ControlMessage) =
        ControlCodec.encode(message).let { ControlCodec.decode(it[1].toInt(), it.copyOfRange(Protocol.HEADER_LENGTH, it.size)) }

    @Test
    fun `pairing messages round trip`() {
        val value = ByteArray(32) { it.toByte() }
        assertArrayEquals(value, (roundTrip(PairCommit(value)) as PairCommit).commitment)
        assertArrayEquals(value, (roundTrip(PairNonce(value)) as PairNonce).nonce)
        assertArrayEquals(value, (roundTrip(PairReveal(value)) as PairReveal).nonce)
        assertEquals(PairResult(true), roundTrip(PairResult(true)))
    }

    @Test
    fun `a pairing value that is not 32 bytes of base64 is malformed`() {
        for (json in listOf("""{"nonce":"AAAA"}""", """{"nonce":"not base64!"}""", """{"nonce":5}""", "{}")) {
            val error = assertThrows(ProtocolException::class.java) { ControlCodec.decode(ControlType.PAIR_NONCE, json.toByteArray()) }
            assertEquals(ErrorCode.MALFORMED_MESSAGE, error.code)
        }
    }

    @Test
    fun `hello carries an optional purpose and stream_config an optional input offer`() {
        assertEquals("pair", (roundTrip(Hello(0, 0, "client", "Pad", "1", "pair")) as Hello).purpose)
        assertNull((roundTrip(Hello(0, 0, "client", "Pad", "1")) as Hello).purpose)

        val config = StreamConfig("h264", "high", 1920, 1080, 60, 12_000_000, 1 shl 20, null, null)
        assertFalse((roundTrip(config) as StreamConfig).inputTouch)
        assertTrue((roundTrip(config.copy(inputTouch = true)) as StreamConfig).inputTouch)
        assertThrows(ProtocolException::class.java) {
            ControlCodec.decode(ControlType.STREAM_CONFIG, """{"codec":"h264","profile":"high","width":1,"height":1,"frame_rate":1,"bitrate":1,"max_video_payload":1,"input":true}""".toByteArray())
        }
    }

    @Test
    fun `types after the pairing messages are still unknown and ignored`() {
        assertNull(ControlCodec.decode(15, "{}".toByteArray()))
    }
}

class TrustedHostStoreTest {
    private val idA = "a".repeat(64)
    private val idB = "b".repeat(64)

    @Test
    fun `paired PCs persist, are replaced by ID, and can be forgotten`() {
        val file = tempFile("trusted.json")
        val store = TrustedHostStore(file)
        store.add(TrustedHost(idA, "Desk", 1))
        store.add(TrustedHost(idB, "Laptop", 2))
        store.add(TrustedHost(idA, "Desk renamed", 3))

        val reopened = TrustedHostStore(file)
        assertEquals(listOf("Laptop", "Desk renamed"), reopened.hosts.value.map { it.name })
        assertTrue(reopened.remove(idA))
        assertFalse(reopened.remove(idA))
        assertFalse(TrustedHostStore(file).isTrusted(idA))
        assertFalse(File(file.path + ".tmp").exists())
    }

    @Test
    fun `a damaged file fails closed and is set aside`() {
        val file = tempFile("trusted.json").apply { writeText("{ not json") }
        val store = TrustedHostStore(file)
        assertTrue(store.hosts.value.isEmpty())
        assertTrue(File(file.path + ".unreadable").exists())
    }

    @Test
    fun `entries that are not device IDs are refused`() {
        assertThrows(IllegalArgumentException::class.java) { TrustedHostStore(tempFile("t.json")).add(TrustedHost("ABC", "x", 0)) }
    }
}

class CompositeHostListenerTest {
    private val executor = Executors.newCachedThreadPool()

    private fun acceptWithin(listener: HostListener) = executor.submit<com.rsxdevelops.extendspace.link.HostConnection> { listener.accept() }.get(5, TimeUnit.SECONDS)

    @Test
    fun `connections from either route are handed on, and Wi-Fi is left out when not allowed`() {
        val usb = MemoryListenerFactory()
        val wifi = MemoryListenerFactory()
        var allowed = true
        val factory = CompositeHostListenerFactory(usb, wifi, { allowed })

        val listener = factory.listen()
        val overWifi = MemoryConnection()
        wifi.listener.connect(overWifi)
        assertSame(overWifi, acceptWithin(listener))
        val overUsb = MemoryConnection()
        usb.listener.connect(overUsb)
        assertSame(overUsb, acceptWithin(listener))
        listener.close()

        allowed = false
        val usbOnly = factory.listen()
        assertSame(usb.listener, usbOnly)
        usbOnly.close()
        executor.shutdownNow()
    }

    @Test
    fun `a route that cannot listen is left out, and with none listening fails`() {
        val usb = MemoryListenerFactory()
        val broken = HostListenerFactory { throw IOException("port taken") }
        val lines = mutableListOf<String>()

        val listener = CompositeHostListenerFactory(usb, broken, { true }, lines::add).listen()
        assertSame(usb.listener, listener)
        assertEquals(1, lines.size)
        listener.close()

        assertThrows(IOException::class.java) { CompositeHostListenerFactory(broken, broken, { true }).listen() }
    }
}
