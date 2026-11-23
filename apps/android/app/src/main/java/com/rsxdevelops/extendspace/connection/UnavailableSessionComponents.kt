package com.rsxdevelops.extendspace.connection

import kotlinx.coroutines.awaitCancellation

// What the application composes its session controller with until the real link and receiver are
// moved out of the debug experiment (migration stage 5). Nothing in the UI asks the controller to
// wait for a host yet, so in practice nothing reaches them; if something did, the session would
// fail at its first step with a fault that says why, and nothing would listen.

internal class UnavailableSessionLink : SessionLink {
    override suspend fun awaitHost(): AttemptOutcome<Unit> = AttemptOutcome.Failed(
        SessionFault(FaultCategory.TRANSPORT, "transport_not_built", "The USB link is not part of this build yet.", FaultTreatment.TERMINAL),
    )

    override suspend fun exchangeCapabilities(decoders: List<DecoderCapability>): AttemptOutcome<StreamDescription> =
        error("No host can connect: awaitHost never succeeds.")

    override suspend fun acceptStream(decoder: DecoderChoice, video: VideoSink): AttemptOutcome<Unit> =
        error("No host can connect: awaitHost never succeeds.")

    override suspend fun awaitEnd(): LinkEnd = error("No stream has been accepted.")

    override fun requestKeyframe(reason: String) = Unit

    override suspend fun endLink(reason: String, fault: SessionFault?) = Unit

    override suspend fun stopListening() = Unit
}

internal class UnavailableVideoReceiver : VideoReceiver {
    override val sink = VideoSink { _, _, _ -> }

    override suspend fun decoders(): List<DecoderCapability> = emptyList()

    override suspend fun prepare(stream: StreamDescription): AttemptOutcome<DecoderChoice> = AttemptOutcome.Failed(
        SessionFault(FaultCategory.DECODER, "decoder_not_built", "Video decoding is not part of this build yet.", FaultTreatment.TERMINAL),
    )

    override fun start(stream: StreamDescription, decoder: DecoderChoice, onKeyframeNeeded: (reason: String) -> Unit) = Unit

    override fun setOutput(output: VideoOutput?) = Unit

    override suspend fun awaitFault(): SessionFault? = awaitCancellation()

    override suspend fun stop() = Unit
}
