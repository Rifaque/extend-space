package com.rsxdevelops.extendspace.livestream

import android.os.Bundle
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.rsxdevelops.extendspace.R
import com.rsxdevelops.extendspace.ui.theme.ExtendSpaceTheme

/**
 * Shows live video sent by the ExtendSpace Windows experiment over USB. The app listens on a local
 * socket; the host reaches it with `adb forward tcp:<port> localabstract:extendspace`. Like the
 * decoder validation screen, this exists only in debug builds.
 *
 * `adb shell am start -S -n com.rsxdevelops.extendspace/.livestream.LiveStreamActivity` opens it
 * listening. Optional: `--es decoder <name>`, `--ez lowLatency false`, `--ez listen false`.
 */
class LiveStreamActivity : ComponentActivity() {
    private val viewModel: LiveStreamViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        val options = LiveOptions(
            decoderName = intent.getStringExtra(EXTRA_DECODER),
            lowLatency = intent.getBooleanExtra(EXTRA_LOW_LATENCY, LiveOptions().lowLatency),
        )
        if (savedInstanceState == null && intent.getBooleanExtra(EXTRA_LISTEN, true)) viewModel.listen(options)

        setContent {
            ExtendSpaceTheme {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    LiveStreamScreen(viewModel, options)
                }
            }
        }
    }

    private companion object {
        const val EXTRA_DECODER = "decoder"
        const val EXTRA_LOW_LATENCY = "lowLatency"
        const val EXTRA_LISTEN = "listen"
    }
}

@Composable
private fun LiveStreamScreen(viewModel: LiveStreamViewModel, options: LiveOptions) {
    val status by viewModel.status.collectAsStateWithLifecycle()
    val listening = status.phase != LivePhase.Stopped && status.phase != LivePhase.Failed

    Column(
        modifier = Modifier
            .fillMaxSize()
            .safeDrawingPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            Text(stringResource(R.string.live_title), style = MaterialTheme.typography.headlineSmall)
            Text(
                stringResource(R.string.live_subtitle),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        AndroidView(
            modifier = Modifier
                .fillMaxWidth()
                .aspectRatio(status.config?.let { it.width.toFloat() / it.height } ?: DEFAULT_ASPECT_RATIO)
                .background(Color.Black),
            factory = { context ->
                SurfaceView(context).apply {
                    holder.addCallback(
                        object : SurfaceHolder.Callback {
                            override fun surfaceCreated(holder: SurfaceHolder) = viewModel.onSurfaceAvailable(holder.surface)

                            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit

                            override fun surfaceDestroyed(holder: SurfaceHolder) = viewModel.onSurfaceDestroyed()
                        },
                    )
                }
            },
        )

        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(onClick = { viewModel.listen(options) }, enabled = !listening) {
                Text(stringResource(R.string.live_listen))
            }
            OutlinedButton(onClick = viewModel::stop, enabled = listening) {
                Text(stringResource(R.string.live_stop))
            }
        }

        Diagnostics(status)
    }
}

@Composable
private fun Diagnostics(status: LiveStatus) {
    val none = stringResource(R.string.live_value_none)
    val stats = status.stats
    val config = status.config

    OutlinedCard(modifier = Modifier.fillMaxWidth()) {
        Row(R.string.live_row_state, "${status.phase.name}. ${status.detail}")
        status.problems.lastOrNull()?.let { problem ->
            HorizontalDivider()
            Row(R.string.live_row_problem, problem, isError = true)
        }
        HorizontalDivider()
        Row(R.string.live_row_host, status.hostName?.let { "$it (${status.hostVersion})" } ?: none)
        HorizontalDivider()
        Row(
            R.string.live_row_stream,
            config?.let { "${it.width} x ${it.height}, ${it.frameRate} fps and ${it.bitrate / 1000} kbit/s requested, ${it.encoder ?: "unknown encoder"} (${it.encoderAcceleration ?: "?"})" } ?: none,
        )
        HorizontalDivider()
        Row(R.string.live_row_picture, status.pictureSize ?: none)
        HorizontalDivider()
        Row(
            R.string.live_row_decoder,
            status.decoderName?.let { "$it [${status.decoderKind ?: "?"}], low latency ${status.lowLatency ?: none}" } ?: none,
        )
        HorizontalDivider()
        Row(
            R.string.live_row_rates,
            if (status.receivedFps == null) {
                none
            } else {
                "received ${rate(status.receivedFps)}, decoded ${rate(status.decodedFps)}, rendered ${rate(status.renderedFps)} fps; " +
                    "${LiveSummary.number((status.bitsPerSecond ?: 0.0) / 1e6)} Mbit/s"
            },
        )
        HorizontalDivider()
        Row(
            R.string.live_row_frames,
            "received ${stats.framesReceived}, submitted ${stats.framesSubmitted}, rendered ${stats.framesRendered}, displayed (system) ${stats.framesDisplayed}",
        )
        HorizontalDivider()
        Row(
            R.string.live_row_drops,
            "missing ${stats.framesMissing}, discarded ${stats.framesDiscarded}, decoder errors ${stats.decoderErrors}, keyframe requests ${status.keyframeRequests}",
        )
        HorizontalDivider()
        Row(R.string.live_row_latency_receive, LiveSummary.latency(status.receiveLatency))
        HorizontalDivider()
        Row(R.string.live_row_latency_render, LiveSummary.latency(status.renderLatency))
        HorizontalDivider()
        Row(
            R.string.live_row_clock,
            status.roundTripMicros?.let { stringResource(R.string.live_clock_known, LiveSummary.number(it / 1000.0), LiveSummary.number(it / 2000.0)) }
                ?: stringResource(R.string.live_clock_unknown),
        )
        HorizontalDivider()
        Row(
            R.string.live_row_connections,
            "accepted ${status.connections}, ended ${status.disconnects}, refused ${status.rejectedPeers}",
        )
    }
}

@Composable
private fun Row(@StringRes label: Int, value: String, isError: Boolean = false) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Text(
            text = stringResource(label),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.weight(0.3f),
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = if (isError) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface,
            modifier = Modifier.weight(0.7f),
        )
    }
}

private fun rate(value: Double?): String = value?.let(LiveSummary::number) ?: "-"

private const val DEFAULT_ASPECT_RATIO = 16f / 9f
