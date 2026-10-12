package com.rsxdevelops.extendspace.decodertest

import android.content.Intent
import android.os.Bundle
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilterChip
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
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.rsxdevelops.extendspace.R
import com.rsxdevelops.extendspace.ui.theme.ExtendSpaceTheme

/**
 * Decodes a `.h264` file captured by the Windows experiment and shows it, to find out whether this
 * device's decoder accepts what ExtendSpace produces. It exists only in debug builds: the class and
 * its manifest entry live in the `debug` source set, so a release build contains neither.
 *
 * It can be driven without touching the screen:
 * `adb shell am start -S -n com.rsxdevelops.extendspace/.decodertest.DecoderValidationActivity
 * --es file sample.h264 --ez autostart true`, with optional `--ez paced false`,
 * `--ez lowLatency true` and `--es decoder <name>`. `-S` restarts the app first. Without it Android
 * only brings an already open screen forward and drops the arguments, unless
 * `--activity-single-top` is given, which hands them to the open screen.
 */
class DecoderValidationActivity : ComponentActivity() {
    private val viewModel: DecoderValidationViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        // Only on a fresh start: after a rotation the view model already holds the selection.
        if (savedInstanceState == null) applyLaunchArguments()

        setContent {
            ExtendSpaceTheme {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    DecoderValidationScreen(viewModel)
                }
            }
        }
    }

    /** A second `am start` while the screen is already open: take its arguments instead of ignoring them. */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        viewModel.stop()
        applyLaunchArguments()
    }

    private fun applyLaunchArguments() {
        val defaults = DecodeOptions()
        viewModel.setOptions(
            DecodeOptions(
                decoderName = intent.getStringExtra(EXTRA_DECODER),
                paced = intent.getBooleanExtra(EXTRA_PACED, defaults.paced),
                lowLatency = intent.getBooleanExtra(EXTRA_LOW_LATENCY, defaults.lowLatency),
            ),
        )
        intent.getStringExtra(EXTRA_FILE)?.let { file ->
            viewModel.selectFile(file)
            if (intent.getBooleanExtra(EXTRA_AUTOSTART, false)) viewModel.play()
        }
    }

    private companion object {
        const val EXTRA_FILE = "file"
        const val EXTRA_AUTOSTART = "autostart"
        const val EXTRA_DECODER = "decoder"
        const val EXTRA_PACED = "paced"
        const val EXTRA_LOW_LATENCY = "lowLatency"
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun DecoderValidationScreen(viewModel: DecoderValidationViewModel) {
    val state by viewModel.uiState.collectAsStateWithLifecycle()
    val status = state.status
    val pickDocument = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) viewModel.selectDocument(uri)
    }

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
            Text(stringResource(R.string.decoder_test_title), style = MaterialTheme.typography.headlineSmall)
            Text(
                stringResource(R.string.decoder_test_subtitle),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        VideoSurface(
            aspectRatio = status.stream?.let { it.width.toFloat() / it.height } ?: DEFAULT_ASPECT_RATIO,
            onSurfaceAvailable = viewModel::onSurfaceAvailable,
            onSurfaceDestroyed = viewModel::onSurfaceDestroyed,
        )

        SectionTitle(R.string.decoder_test_section_sample)
        if (state.samples.isEmpty()) {
            Text(
                stringResource(R.string.decoder_test_no_samples, state.samplesDirectory),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.fillMaxWidth(),
            )
        }
        FlowRow(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            state.samples.forEach { name ->
                FilterChip(
                    selected = name == state.selectedSample,
                    onClick = { viewModel.selectFile(name) },
                    label = { Text(name) },
                    enabled = !status.isRunning,
                )
            }
        }
        FlowRow(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = viewModel::refreshSamples, enabled = !status.isRunning) {
                Text(stringResource(R.string.decoder_test_refresh))
            }
            OutlinedButton(onClick = { pickDocument.launch(arrayOf("*/*")) }, enabled = !status.isRunning) {
                Text(stringResource(R.string.decoder_test_choose_file))
            }
        }

        SectionTitle(R.string.decoder_test_section_decoder)
        FlowRow(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(
                selected = state.options.decoderName == null,
                onClick = { viewModel.setOptions(state.options.copy(decoderName = null)) },
                label = { Text(stringResource(R.string.decoder_test_decoder_automatic)) },
                enabled = !status.isRunning,
            )
            state.decoderNames.forEach { name ->
                FilterChip(
                    selected = state.options.decoderName == name,
                    onClick = { viewModel.setOptions(state.options.copy(decoderName = name)) },
                    label = { Text(name) },
                    enabled = !status.isRunning,
                )
            }
        }
        OptionRow(R.string.decoder_test_option_paced, state.options.paced, !status.isRunning) {
            viewModel.setOptions(state.options.copy(paced = it))
        }
        OptionRow(R.string.decoder_test_option_low_latency, state.options.lowLatency, !status.isRunning) {
            viewModel.setOptions(state.options.copy(lowLatency = it))
        }

        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(onClick = viewModel::play, enabled = state.selectedSample != null && !status.isRunning) {
                Text(stringResource(R.string.decoder_test_play))
            }
            OutlinedButton(onClick = viewModel::stop, enabled = status.isRunning) {
                Text(stringResource(R.string.decoder_test_stop))
            }
        }

        SectionTitle(R.string.decoder_test_section_result)
        Diagnostics(status)

        SectionTitle(R.string.decoder_test_section_capabilities)
        Text(
            stringResource(R.string.decoder_test_capabilities_note),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.fillMaxWidth(),
        )
        Text(
            text = state.capabilityReport,
            style = MaterialTheme.typography.bodySmall,
            fontFamily = FontFamily.Monospace,
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
        )
    }
}

/** The surface the decoder renders into. It tells the view model the moment it exists or is about to go. */
@Composable
private fun VideoSurface(
    aspectRatio: Float,
    onSurfaceAvailable: (android.view.Surface) -> Unit,
    onSurfaceDestroyed: () -> Unit,
) {
    AndroidView(
        modifier = Modifier
            .widthIn(max = 960.dp)
            .fillMaxWidth()
            .aspectRatio(aspectRatio)
            .background(Color.Black),
        factory = { context ->
            SurfaceView(context).apply {
                holder.addCallback(
                    object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) = onSurfaceAvailable(holder.surface)

                        override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit

                        override fun surfaceDestroyed(holder: SurfaceHolder) = onSurfaceDestroyed()
                    },
                )
            }
        },
    )
}

@Composable
private fun Diagnostics(status: DecodeStatus) {
    val stream = status.stream
    val none = stringResource(R.string.decoder_test_value_none)

    OutlinedCard(modifier = Modifier.fillMaxWidth()) {
        DiagnosticRow(R.string.decoder_test_row_state, status.state.name)
        status.error?.let { error ->
            HorizontalDivider()
            DiagnosticRow(R.string.decoder_test_row_error, error, isError = true)
        }
        HorizontalDivider()
        DiagnosticRow(
            R.string.decoder_test_row_file,
            status.sampleName?.let { name -> status.sampleBytes?.let { "$name ($it bytes)" } ?: name } ?: none,
        )
        HorizontalDivider()
        DiagnosticRow(
            R.string.decoder_test_row_input,
            stream?.let {
                "${it.width} x ${it.height}, ${it.profileName} profile, level ${it.levelName}" +
                    (it.framesPerSecond?.let { rate -> ", ${DecodeSummary.number(rate)} fps stated" } ?: ", no frame rate stated")
            } ?: none,
        )
        HorizontalDivider()
        DiagnosticRow(
            R.string.decoder_test_row_decoder,
            status.decoderName?.let { "$it [${status.decoderKind}], chosen by ${status.decoderChosenBy}" } ?: none,
        )
        HorizontalDivider()
        DiagnosticRow(R.string.decoder_test_row_capability, status.capabilityCheck ?: none)
        HorizontalDivider()
        DiagnosticRow(R.string.decoder_test_row_low_latency, status.lowLatency ?: none)
        HorizontalDivider()
        DiagnosticRow(R.string.decoder_test_row_output, status.outputFormat ?: none)
        HorizontalDivider()
        DiagnosticRow(
            R.string.decoder_test_row_queued,
            "${status.accessUnitsQueued} (${status.keyFramesQueued} keyframes, ${status.bytesQueued} bytes)",
        )
        HorizontalDivider()
        DiagnosticRow(R.string.decoder_test_row_decoded, status.framesDecoded.toString())
        HorizontalDivider()
        DiagnosticRow(R.string.decoder_test_row_displayed, status.framesReportedDisplayed.toString())
        HorizontalDivider()
        DiagnosticRow(R.string.decoder_test_row_elapsed, "${DecodeSummary.milliseconds(status.elapsedNanos)} ms")
        HorizontalDivider()
        DiagnosticRow(
            R.string.decoder_test_row_first_frame,
            status.timeToFirstFrameNanos?.let { "${DecodeSummary.milliseconds(it)} ms" } ?: none,
        )
        HorizontalDivider()
        DiagnosticRow(
            R.string.decoder_test_row_fps,
            status.framesPerSecond?.let {
                DecodeSummary.number(it) + stringResource(
                    if (status.options.paced) R.string.decoder_test_fps_paced else R.string.decoder_test_fps_unpaced,
                )
            } ?: none,
        )
    }
}

@Composable
private fun DiagnosticRow(@StringRes label: Int, value: String, isError: Boolean = false) {
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
            modifier = Modifier.weight(0.35f),
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = if (isError) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface,
            modifier = Modifier.weight(0.65f),
        )
    }
}

@Composable
private fun OptionRow(@StringRes label: Int, checked: Boolean, enabled: Boolean, onChange: (Boolean) -> Unit) {
    Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked = checked, onCheckedChange = onChange, enabled = enabled)
        Text(stringResource(label), style = MaterialTheme.typography.bodyLarge)
    }
}

@Composable
private fun SectionTitle(@StringRes title: Int) {
    Text(
        text = stringResource(title),
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier
            .fillMaxWidth()
            .padding(top = 8.dp),
    )
}

private const val DEFAULT_ASPECT_RATIO = 16f / 9f
