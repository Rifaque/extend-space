package com.rsxdevelops.extendspace.connection

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * The stream the host is sending, as its `stream_config` described it (D69). The session screen fits
 * the picture to [width] by [height]; the rest is shown as information.
 */
data class StreamInfo(
    val width: Int,
    val height: Int,
    val frameRate: Int,
    val hostName: String,
    /** The PC accepts touch on this stream (D81): the session screen sends touches only then. */
    val touchInput: Boolean = false,
    /** This device is an extra display of the PC, not a mirror (D90). */
    val extended: Boolean = false,
)

private val noStream: StateFlow<StreamInfo?> = MutableStateFlow<StreamInfo?>(null).asStateFlow()

private val noRoute: StateFlow<TransportKind?> = MutableStateFlow<TransportKind?>(null).asStateFlow()

/** A Windows host this device has paired with or discovered. */
data class HostDevice(val id: String, val displayName: String, val transport: TransportKind)

/** The answer to a request to wait for a host. */
enum class WaitResult {
    /** A session was begun: the app is listening for a host. */
    STARTED,

    /** A session is already in progress, or a failure has not been acknowledged. */
    ALREADY_ACTIVE,

    /** A previous session's teardown overran its deadline and has not let go of everything yet. */
    PREVIOUS_SESSION_NOT_RELEASED,
}

/**
 * The only surface the UI uses for connections. Transports, pairing and the video pipeline live
 * behind it. Every method returns at once and may be called on the UI thread; nothing that waits
 * for the other device runs on the caller's thread.
 */
interface ConnectionController {
    val state: StateFlow<ConnectionState>
    val hosts: StateFlow<List<HostDevice>>

    /** The current or most recent stream of the session in progress; null when there is none (D69). */
    val stream: StateFlow<StreamInfo?> get() = noStream

    /** The route the PC is connected by while negotiating or streaming; null otherwise. */
    val connectedOver: StateFlow<TransportKind?> get() = noRoute

    /** Begins a session: listen for a host on [transport] and serve it. */
    fun waitForHost(transport: TransportKind): WaitResult

    /** Ends the session in progress, if any. False if there is nothing to stop. */
    fun stop(): Boolean

    /** Clears a failure the user has seen. False if there is no failure. */
    fun acknowledgeError(): Boolean

    /**
     * Lends [output] to the session, for as long as it exists. Called on the UI thread when the
     * output becomes usable (for a surface: `surfaceCreated`). Replaces any output lent before.
     */
    fun lendOutput(output: VideoOutput)

    /**
     * Takes [output] back. Called on the UI thread before the output is destroyed (for a surface:
     * in `surfaceDestroyed`); returns once nothing draws to it. Does nothing if [output] is not the
     * one currently lent, so that a screen being replaced cannot take away its successor's output.
     */
    fun withdrawOutput(output: VideoOutput)

    /** Sends touch contacts to the PC (D81). False when the stream in progress did not offer input. Any thread. */
    fun sendTouch(sequence: Long, contacts: List<TouchContactSample>): Boolean = false
}

/** One contact as the session screen saw it: [x] and [y] already 0..65535 over the picture. */
data class TouchContactSample(val id: Int, val phase: Int, val x: Int, val y: Int)
