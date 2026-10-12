package com.rsxdevelops.extendspace.decodertest

import android.app.Application
import android.net.Uri
import android.provider.OpenableColumns
import android.util.Log
import android.view.Surface
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.io.File
import java.io.FileInputStream
import java.io.FileNotFoundException
import java.io.IOException

internal data class DecoderValidationUiState(
    val status: DecodeStatus = DecodeStatus(),
    val options: DecodeOptions = DecodeOptions(),
    val selectedSample: String? = null,
    val samples: List<String> = emptyList(),
    val decoderNames: List<String> = emptyList(),
    val capabilityReport: String = "",
    val samplesDirectory: String = "",
)

/**
 * Holds the validation screen's state and owns the one decoder that may exist at a time. The
 * decoder is tied to the surface: it is started only once there is one and stopped before it goes.
 */
internal class DecoderValidationViewModel(application: Application) : AndroidViewModel(application) {
    private val samplesDirectory: File? = application.getExternalFilesDir(null)?.let { File(it, SAMPLES_FOLDER) }

    private val _uiState = MutableStateFlow(DecoderValidationUiState(samplesDirectory = samplesDirectory?.path.orEmpty()))
    val uiState: StateFlow<DecoderValidationUiState> = _uiState.asStateFlow()

    private var source: SampleSource? = null
    private var surface: Surface? = null
    private var decoder: H264FileDecoder? = null
    private var playWhenSurfaceArrives = false

    init {
        samplesDirectory?.mkdirs()
        refreshSamples()
        viewModelScope.launch(Dispatchers.Default) {
            val report = DecoderCatalog.report()
            val names = DecoderCatalog.h264Decoders().map { it.name }
            Log.i(LOG_TAG, "Codec capabilities:\n$report")
            writeToSamplesDirectory(CAPABILITIES_FILE, report)
            _uiState.update { it.copy(capabilityReport = report, decoderNames = names) }
        }
    }

    /** Looks again for `.h264` files pushed into the app's samples folder. */
    fun refreshSamples() {
        val names = samplesDirectory
            ?.listFiles { file -> file.isFile && file.name.endsWith(".h264", ignoreCase = true) }
            ?.map { it.name }
            ?.sorted()
            .orEmpty()
        _uiState.update { it.copy(samples = names) }
    }

    /** Selects a file by name from the samples folder, or by absolute path. */
    fun selectFile(nameOrPath: String) {
        val file = File(nameOrPath).takeIf { it.isAbsolute } ?: File(samplesDirectory, nameOrPath)
        select(SampleSource(file.name, file.length().takeIf { file.isFile }) { FileInputStream(file) })
    }

    /** Selects a document the user picked with the system file picker. */
    fun selectDocument(uri: Uri) {
        val resolver = getApplication<Application>().contentResolver
        var name = uri.lastPathSegment ?: uri.toString()
        var size: Long? = null
        resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                if (!cursor.isNull(0)) name = cursor.getString(0)
                if (!cursor.isNull(1)) size = cursor.getLong(1)
            }
        }
        select(
            SampleSource(name, size) {
                resolver.openInputStream(uri) ?: throw FileNotFoundException("The document provider returned nothing for $uri.")
            },
        )
    }

    /** Options apply to the next run; they cannot be changed under a decoder that is running. */
    fun setOptions(options: DecodeOptions) {
        _uiState.update { if (it.status.isRunning) it else it.copy(options = options) }
    }

    /** Starts decoding the selected file, now or as soon as the surface exists. */
    fun play() {
        val source = source ?: return
        val surface = surface
        if (surface == null) {
            playWhenSurfaceArrives = true
            return
        }

        stop()
        val options = _uiState.value.options
        val started = H264FileDecoder(source, surface, options, ::onDecoderStatus)
        decoder = started
        _uiState.update {
            it.copy(status = DecodeStatus(DecodeState.Preparing, source.displayName, source.sizeBytes, options))
        }
        started.start()
    }

    /** Stops the decoder, if one is running, and waits for it to be released. */
    fun stop() {
        playWhenSurfaceArrives = false
        decoder?.stop()
        decoder = null
    }

    fun onSurfaceAvailable(surface: Surface) {
        this.surface = surface
        if (playWhenSurfaceArrives) play()
    }

    /** Called before the surface is destroyed. The decoder is gone by the time this returns. */
    fun onSurfaceDestroyed() {
        stop()
        surface = null
    }

    override fun onCleared() {
        stop()
    }

    private fun select(newSource: SampleSource) {
        stop()
        source = newSource
        _uiState.update {
            it.copy(
                selectedSample = newSource.displayName,
                status = DecodeStatus(sampleName = newSource.displayName, sampleBytes = newSource.sizeBytes, options = it.options),
            )
        }
    }

    /** Runs on the decoder's thread. */
    private fun onDecoderStatus(status: DecodeStatus) {
        _uiState.update { it.copy(status = status) }
        if (!status.isRunning) {
            val summary = DecodeSummary.format(status)
            Log.i(LOG_TAG, summary)
            writeToSamplesDirectory(RESULT_FILE, summary)
        }
    }

    private fun writeToSamplesDirectory(name: String, text: String) {
        val directory = samplesDirectory ?: return
        try {
            File(directory, name).writeText(text)
        } catch (exception: IOException) {
            // The copy on storage is a convenience for `adb pull`; the log line above is the record.
            Log.w(LOG_TAG, "Could not write $name: ${exception.message}")
        }
    }

    companion object {
        const val LOG_TAG = "ExtendSpaceDecoderTest"
        const val SAMPLES_FOLDER = "decoder-samples"
        const val RESULT_FILE = "last-result.txt"
        const val CAPABILITIES_FILE = "capabilities.txt"
    }
}
