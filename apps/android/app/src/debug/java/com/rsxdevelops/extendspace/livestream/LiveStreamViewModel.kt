package com.rsxdevelops.extendspace.livestream

import android.app.Application
import android.content.Context
import android.hardware.display.DisplayManager
import android.os.Build
import android.util.Log
import android.view.Display
import android.view.Surface
import android.view.WindowManager
import androidx.lifecycle.AndroidViewModel
import com.rsxdevelops.extendspace.livestream.protocol.LatencySummary
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.io.IOException
import java.util.Locale

/** Holds the live screen's state and the one session that may exist at a time. */
internal class LiveStreamViewModel(application: Application) : AndroidViewModel(application) {
    private val _status = MutableStateFlow(LiveStatus())
    val status: StateFlow<LiveStatus> = _status.asStateFlow()

    private var session: LiveSession? = null
    private var surface: Surface? = null

    /** Starts listening for the host with these options. Does nothing if already listening. */
    fun listen(options: LiveOptions) {
        if (session != null) return
        val application = getApplication<Application>()
        val started = LiveSession(
            deviceName = "${Build.MANUFACTURER} ${Build.MODEL}",
            appVersion = application.packageManager.getPackageInfo(application.packageName, 0).versionName.orEmpty(),
            display = displayInfo(application),
            options = options,
            onStatus = ::onStatus,
        )
        session = started
        started.setSurface(surface)
        started.start()
    }

    /** Ends any session and stops listening. */
    fun stop() {
        session?.stop()
        session = null
    }

    fun onSurfaceAvailable(newSurface: Surface) {
        surface = newSurface
        session?.setSurface(newSurface)
    }

    /** Called before the surface is destroyed; the decoder is gone by the time this returns. */
    fun onSurfaceDestroyed() {
        surface = null
        session?.setSurface(null)
    }

    override fun onCleared() = stop()

    /** Runs on the session's threads. */
    private fun onStatus(status: LiveStatus) {
        val previous = _status.value
        _status.value = status
        if (previous.phase == LivePhase.Streaming && status.phase != LivePhase.Streaming) {
            // The record of the connection that just ended, where adb can read it back.
            writeResult(LiveSummary.format(previous.copy(detail = status.detail)))
        }
    }

    private fun writeResult(text: String) {
        val directory = getApplication<Application>().getExternalFilesDir(null) ?: return
        try {
            File(directory, RESULT_FILE).writeText(text)
        } catch (exception: IOException) {
            Log.w(LiveSession.LOG_TAG, "Could not write $RESULT_FILE: ${exception.message}")
        }
    }

    private fun displayInfo(context: Context): DisplayInfo {
        val display = context.getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY)
        val refresh = display?.refreshRate?.toDouble() ?: 0.0
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val bounds = context.getSystemService(WindowManager::class.java).maximumWindowMetrics.bounds
            DisplayInfo(bounds.width(), bounds.height(), refresh)
        } else {
            val metrics = context.resources.displayMetrics
            DisplayInfo(metrics.widthPixels, metrics.heightPixels, refresh)
        }
    }

    companion object {
        const val RESULT_FILE = "live-last-result.txt"
    }
}

/** The plain-text record of one connection, for the log and for reading back over ADB. */
internal object LiveSummary {
    fun format(status: LiveStatus): String = buildString {
        val stats = status.stats
        appendLine("ExtendSpace live stream, client side")
        appendLine("ended: ${status.detail}")
        appendLine("host: ${status.hostName ?: "unknown"} (${status.hostVersion ?: "?"})")
        appendLine(
            "stream announced: " + (status.config?.let { "${it.width} x ${it.height} at ${it.frameRate} fps, ${it.bitrate} bit/s, encoder ${it.encoder ?: "?"} (${it.encoderAcceleration ?: "?"})" } ?: "none"),
        )
        appendLine("picture: ${status.pictureSize ?: "none decoded"}")
        appendLine("decoder: ${status.decoderName ?: "none"}${status.decoderKind?.let { " [$it]" }.orEmpty()}; low latency: ${status.lowLatency ?: "n/a"}")
        appendLine("frames received: ${stats.framesReceived} (${stats.bytesReceived} bytes)")
        appendLine("frames missing (sequence gaps): ${stats.framesMissing}")
        appendLine("frames discarded before the decoder: ${stats.framesDiscarded}")
        appendLine("frames submitted to the decoder: ${stats.framesSubmitted}")
        appendLine("frames decoded and released to the surface: ${stats.framesRendered}")
        appendLine("frames the system reported as displayed: ${stats.framesDisplayed}")
        appendLine("decoder errors: ${stats.decoderErrors}")
        appendLine("keyframe requests sent: ${status.keyframeRequests}")
        appendLine("last second, capture to receive: ${latency(status.receiveLatency)}")
        appendLine("last second, capture to release to the surface: ${latency(status.renderLatency)}")
        appendLine("clock relation round trip: ${status.roundTripMicros?.let { "${number(it / 1000.0)} ms" } ?: "n/a"}")
        append("problems: ${if (status.problems.isEmpty()) "none" else status.problems.joinToString(" | ")}")
    }

    fun latency(summary: LatencySummary?): String = summary?.let {
        "mean ${number(it.meanMs)} ms, p95 ${number(it.p95Ms)}, max ${number(it.maxMs)} (n = ${it.count})"
    } ?: "n/a"

    fun number(value: Double): String = String.format(Locale.ROOT, "%.1f", value)
}
