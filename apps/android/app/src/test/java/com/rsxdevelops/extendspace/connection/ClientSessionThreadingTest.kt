package com.rsxdevelops.extendspace.connection

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.atomic.AtomicInteger
import kotlin.concurrent.thread
import kotlin.random.Random

/**
 * The one place real threads are used: the controller on the dispatcher the app gives it, seams
 * that suspend on other threads, and intents from several threads at once. It checks what the
 * single-threaded tests cannot: that decisions really are made one at a time, so that no component
 * sees two calls at once, and that every session reaches a terminal phase. It polls with a
 * failsafe, which nothing else does.
 */
class ClientSessionThreadingTest {
    /** Counts calls into one component that overlap. */
    private class Overlaps {
        private val active = AtomicInteger()
        val seen = AtomicInteger()

        suspend fun <T> call(block: suspend () -> T): T {
            if (active.incrementAndGet() > 1) seen.incrementAndGet()
            try {
                return block()
            } finally {
                active.decrementAndGet()
            }
        }
    }

    private class ThreadedLink : SessionLink {
        val overlaps = Overlaps()

        override suspend fun awaitHost(): AttemptOutcome<Unit> = overlaps.call {
            elsewhere()
            if (Random.nextInt(5) == 0) AttemptOutcome.Failed(ClientFaults.refusedPeer) else AttemptOutcome.Succeeded(Unit)
        }

        override suspend fun exchangeCapabilities(decoders: List<DecoderCapability>) = overlaps.call {
            elsewhere()
            AttemptOutcome.Succeeded(FakeLink.stream)
        }

        override suspend fun acceptStream(decoder: DecoderChoice, video: VideoSink) = overlaps.call {
            elsewhere()
            AttemptOutcome.Succeeded(Unit)
        }

        override suspend fun awaitEnd(): LinkEnd = overlaps.call {
            delay(Random.nextLong(1, 20))
            LinkEnd(LinkEndKind.LOST, ClientFaults.hostClosed)
        }

        override fun requestKeyframe(reason: String) = Unit

        override suspend fun endLink(reason: String, fault: SessionFault?) = overlaps.call { elsewhere() }

        override suspend fun stopListening() = overlaps.call { elsewhere() }
    }

    private class ThreadedReceiver : VideoReceiver {
        val overlaps = Overlaps()

        override val sink = VideoSink { _, _, _ -> }

        override suspend fun decoders() = overlaps.call { listOf(DecoderCapability("c2.hw.avc", true, true, 4096, 4096)) }

        override suspend fun prepare(stream: StreamDescription) = overlaps.call {
            elsewhere()
            AttemptOutcome.Succeeded(DecoderChoice("c2.hw.avc", true))
        }

        override fun start(stream: StreamDescription, decoder: DecoderChoice, onKeyframeNeeded: (reason: String) -> Unit) = Unit

        override fun setOutput(output: VideoOutput?) = Unit

        // A wait, not a step: it runs beside the link's, by design.
        override suspend fun awaitFault(): SessionFault? = kotlinx.coroutines.awaitCancellation()

        override suspend fun stop() = overlaps.call { elsewhere() }
    }

    @Test
    fun `sessions started and stopped from many threads never overlap a component's calls and always end`() {
        val link = ThreadedLink()
        val receiver = ThreadedReceiver()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default.limitedParallelism(1))
        val controller = ClientSessionController(
            link,
            receiver,
            SystemSessionClock(),
            scope,
            ClientSessionOptions(retryPauseMillis = 1, hostReturnMillis = 200, outputGraceMillis = 60_000),
        )
        val failsafe = System.nanoTime() + 60_000_000_000L

        try {
            repeat(60) {
                controller.waitForHost(TransportKind.USB)
                val workers = List(4) {
                    thread {
                        Thread.sleep(Random.nextLong(0, 5))
                        controller.stop()
                        controller.waitForHost(TransportKind.USB)
                        controller.lendOutput(TestOutput("surface"))
                    }
                }
                workers.forEach { it.join() }

                while (controller.state.value !is ConnectionState.Disconnected && controller.state.value !is ConnectionState.Failed) {
                    assertTrue("a session did not end within the failsafe: ${controller.state.value}", System.nanoTime() < failsafe)
                    controller.stop()
                    Thread.sleep(1)
                }
                controller.acknowledgeError()
            }

            while (!controller.isReleased) {
                assertTrue("never released", System.nanoTime() < failsafe)
                Thread.sleep(1)
            }
            assertEquals("overlapping link calls", 0, link.overlaps.seen.get())
            assertEquals("overlapping receiver calls", 0, receiver.overlaps.seen.get())
        } finally {
            scope.cancel()
        }
    }

    private companion object {
        /** Suspends the way a real seam does: the work happens on another thread and the caller resumes on its own dispatcher. */
        suspend fun elsewhere() = withContext(Dispatchers.IO) { Thread.sleep(Random.nextLong(0, 2)) }
    }
}
