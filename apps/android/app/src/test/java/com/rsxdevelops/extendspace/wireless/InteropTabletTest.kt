package com.rsxdevelops.extendspace.wireless

import com.rsxdevelops.extendspace.connection.AttemptOutcome
import com.rsxdevelops.extendspace.connection.DecoderCapability
import com.rsxdevelops.extendspace.connection.DecoderChoice
import com.rsxdevelops.extendspace.connection.VideoSink
import com.rsxdevelops.extendspace.link.DisplayInfo
import com.rsxdevelops.extendspace.link.UsbHostLink
import com.rsxdevelops.extendspace.link.UsbHostLinkOptions
import com.rsxdevelops.extendspace.protocol.InputFrames
import com.rsxdevelops.extendspace.protocol.Protocol
import com.rsxdevelops.extendspace.protocol.TouchContact
import com.rsxdevelops.extendspace.protocol.TouchPhase
import com.rsxdevelops.extendspace.video.HostClockEstimate
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.net.InetAddress

/**
 * The tablet's half of a cross-platform run on one PC (Phase 10/11 interop): the real Wi-Fi endpoint,
 * pairing and session link of this app, on the desktop JVM's TLS, against the real Windows host code
 * in `ExtendSpace.Core.Tests` (`InteropHostTests`). Gated: set `EXTENDSPACE_INTEROP_DIR` to an empty
 * folder for both. Listens on loopback only. What it cannot show: Conscrypt, AndroidKeyStore, Wi-Fi.
 */
class InteropTabletTest {
    @Test
    fun `serve one PC through pairing and a session`() {
        val folder = System.getenv("EXTENDSPACE_INTEROP_DIR")?.let(::File)
        assumeTrue("set EXTENDSPACE_INTEROP_DIR to run the cross-platform interop", folder != null)
        folder!!.mkdirs()
        val log = File(folder, "tablet.log").apply { writeText("") }
        fun note(line: String) = log.appendText(line + "\n")

        val identity = SoftwareIdentity("CN=interop tablet")
        val trust = TrustedHostStore(File(folder, "tablet-trust.json"))
        val endpoint = WifiEndpoint(
            { identity }, trust, { "Interop Pad" }, { "0.2.0-interop" },
            options = WifiEndpointOptions(port = 0, bindAddress = InetAddress.getLoopbackAddress(), decisionMillis = 60_000),
            log = ::note,
        )

        // The session link listens first, as when the user has tapped Wait for PC with Wi-Fi allowed,
        // so that the endpoint (and its port) stays up across pairing.
        val video = mutableListOf<Int>()
        val link = UsbHostLink(
            listenerFactory = endpoint.sessionListenerFactory,
            deviceName = "Interop Pad",
            appVersion = { "0.2.0-interop" },
            display = { DisplayInfo(2560, 1600, 60.0) },
            stats = { null },
            hostClock = HostClockEstimate(),
            options = UsbHostLinkOptions(allowedPeerUids = setOf(0, 2000)),
            log = ::note,
        )
        runBlocking {
            val accepting = async(Dispatchers.Default) { withTimeout(120_000) { link.awaitHost() } }
            while (endpoint.port == null) Thread.sleep(10)

            // 1. Pairing: the window opens, the PC connects, the code is published for the PC side to compare.
            endpoint.openPairing()
            File(folder, "tablet-id").writeText(identity.deviceId)
            File(folder, "port").writeText(endpoint.port.toString())
            val deadline = System.currentTimeMillis() + 60_000
            while (endpoint.pairing.value !is PairingState.Confirm) {
                check(System.currentTimeMillis() < deadline) { "no PC started pairing" }
                Thread.sleep(20)
            }
            val code = (endpoint.pairing.value as PairingState.Confirm).code
            File(folder, "tablet-code").writeText(code)
            endpoint.decide(true)
            while (endpoint.pairing.value !is PairingState.Finished) Thread.sleep(20)
            val paired = endpoint.pairing.value as PairingState.Finished
            note("pairing: ${paired.message}")
            check(paired.paired) { paired.message }

            // 2. A session over Wi-Fi TLS, through the same link the app uses.
            File(folder, "session-ready").writeText("1")
            check(accepting.await() is AttemptOutcome.Succeeded)
            val stream = (link.exchangeCapabilities(listOf(DecoderCapability("c2.interop.avc", true, true, 4096, 4096))) as AttemptOutcome.Succeeded).value
            note("stream: ${stream.width}x${stream.height}@${stream.frameRate} input=${stream.inputOffered} over ${link.connectedTransport}")
            check(link.acceptStream(DecoderChoice("c2.interop.avc", true), VideoSink { flags, _, _ -> synchronized(video) { video += flags } }) is AttemptOutcome.Succeeded)

            val videoDeadline = System.currentTimeMillis() + 20_000
            while (synchronized(video) { video.none { it and Protocol.FLAG_KEYFRAME != 0 } }) {
                check(System.currentTimeMillis() < videoDeadline) { "no keyframe arrived" }
                Thread.sleep(10)
            }
            note("video: ${synchronized(video) { video.size }} frame(s), first keyframe received")

            if (stream.inputOffered) {
                check(link.sendInput(InputFrames.touchFrame(0, listOf(TouchContact(0, TouchPhase.DOWN, 32768, 32768)))))
                check(link.sendInput(InputFrames.touchFrame(1, listOf(TouchContact(0, TouchPhase.UP, 32768, 32768)))))
                note("touch: tap sent")
            }

            val transport = link.connectedTransport
            val end = withTimeout(30_000) { link.awaitEnd() }
            note("end: ${end.kind} ${end.peerReason.orEmpty()}")
            File(folder, "tablet-result").writeText("paired=${paired.paired} code=$code transport=$transport end=${end.kind}")
            link.endLink("interop over")
            link.stopListening()
        }
    }
}
