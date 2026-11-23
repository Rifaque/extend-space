package com.rsxdevelops.extendspace.ui.session

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.util.Log
import android.view.MotionEvent
import android.view.View
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.mutableIntStateOf
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.layout
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.rsxdevelops.extendspace.ExtendSpaceApplication
import com.rsxdevelops.extendspace.R
import com.rsxdevelops.extendspace.connection.ConnectionController
import com.rsxdevelops.extendspace.connection.ConnectionState
import com.rsxdevelops.extendspace.connection.StreamInfo
import com.rsxdevelops.extendspace.connection.TransportKind
import com.rsxdevelops.extendspace.ui.theme.ExtendSpaceTheme
import com.rsxdevelops.extendspace.video.SurfaceOutput
import kotlinx.coroutines.delay

/**
 * The release session screen (D66): the PC's picture, full screen, and what to do when there is
 * none. It lends its surface to the application's session controller and withdraws it when the
 * surface goes, exactly as the debug harness (`sessiontest/SessionDisplayActivity`) that was run on
 * the reference tablet does; it parses, decodes and draws nothing itself.
 *
 * Lifecycle, as architecture section 7.2 and D4 say: rotating recreates the screen and only
 * withdraws and lends the surface again; Home and screen-off withdraw it, and the controller ends a
 * session left without a surface for its grace period (D6); leaving the screen (Back, or Close)
 * ends the session, so a session is never left running with no screen to return to.
 */
class SessionActivity : ComponentActivity() {
    private val controller: ConnectionController get() = (application as ExtendSpaceApplication).connectionController
    private var lent: SurfaceOutput? = null

    // D81-D84: touches on the picture, sent only on a stream whose PC offered input.
    private val touch = TouchCapture { sequence, contacts -> controller.sendTouch(sequence, contacts) }
    private var touchStream: StreamInfo? = null
    private var controlsVisible = false
    private val backSignal = mutableIntStateOf(0)

    private val touchEnabled: Boolean
        get() = controller.state.value is ConnectionState.Streaming && controller.stream.value?.touchInput == true

    private fun onSurfaceTouch(view: View, event: MotionEvent): Boolean {
        val stream = controller.stream.value
        if (stream !== touchStream) {
            // A new stream numbers its frames from zero; nothing of the last one is held.
            touch.reset()
            touchStream = stream
        }
        if (!touchEnabled) {
            touch.cancelAll()
            return false
        }
        val action = when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> TouchAction.DOWN
            MotionEvent.ACTION_POINTER_DOWN -> TouchAction.POINTER_DOWN
            MotionEvent.ACTION_MOVE -> TouchAction.MOVE
            MotionEvent.ACTION_POINTER_UP -> TouchAction.POINTER_UP
            MotionEvent.ACTION_UP -> TouchAction.UP
            MotionEvent.ACTION_CANCEL -> TouchAction.CANCEL
            else -> return true
        }
        val pointers = (0 until event.pointerCount).map { PointerSample(event.getPointerId(it), event.getX(it), event.getY(it)) }
        touch.onEvent(action, event.getPointerId(event.actionIndex), pointers, view.width, view.height)
        // The tap is the PC's; accessibility services still learn that the surface was clicked.
        if (action == TouchAction.UP) view.performClick()
        return true
    }

    private val surfaceCallback = object : SurfaceHolder.Callback {
        override fun surfaceCreated(holder: SurfaceHolder) {
            val output = SurfaceOutput(holder.surface)
            lent = output
            controller.lendOutput(output)
        }

        override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit

        override fun surfaceDestroyed(holder: SurfaceHolder) {
            // A finger on a surface that is going cannot be lifted normally (D84).
            touch.cancelAll()
            // Returns once nothing draws to the surface any more.
            lent?.let(controller::withdrawOutput)
            lent = null
        }
    }

    // The surface's touch listener calls performClick() on each tap (onSurfaceTouch); lint asks for the
    // view class to override it, which a stock SurfaceView cannot.
    @SuppressLint("ClickableViewAccessibility")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowCompat.getInsetsController(window, window.decorView).apply {
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            hide(WindowInsetsCompat.Type.systemBars())
        }

        onBackPressedDispatcher.addCallback(
            this,
            object : OnBackPressedCallback(true) {
                override fun handleOnBackPressed() {
                    // With touch on, an edge swipe is easily made by accident: Back first shows the
                    // controls, and only Back with the controls showing leaves (D84).
                    if (touchEnabled && !controlsVisible) backSignal.intValue++ else leave()
                }
            },
        )

        setContent {
            ExtendSpaceTheme {
                val state by controller.state.collectAsStateWithLifecycle()
                val stream by controller.stream.collectAsStateWithLifecycle()
                SessionScreen(
                    state = state,
                    stream = stream,
                    surface = { modifier ->
                        AndroidView(
                            factory = { context ->
                                SurfaceView(context).apply {
                                    holder.addCallback(surfaceCallback)
                                    setOnTouchListener(::onSurfaceTouch)
                                    addOnAttachStateChangeListener(object : View.OnAttachStateChangeListener {
                                        override fun onViewAttachedToWindow(view: View) = Unit
                                        override fun onViewDetachedFromWindow(view: View) = touch.cancelAll()
                                    })
                                }
                            },
                            modifier = modifier,
                        )
                    },
                    onAction = ::act,
                    touchEnabled = state is ConnectionState.Streaming && stream?.touchInput == true,
                    showControlsSignal = backSignal.intValue,
                    onControlsVisibleChanged = { controlsVisible = it },
                )
            }
        }
    }

    override fun onPause() {
        // Home, the recent-apps screen or a dialog over this one: no gesture survives it (D84).
        touch.cancelAll()
        super.onPause()
    }

    override fun onDestroy() {
        // Left for good: the session ends. Recreated (rotation): it carries on.
        if (isFinishing && controller.state.value.isActive) {
            Log.i(ExtendSpaceApplication.LOG_TAG, "session screen closed: the session is stopped")
            controller.stop()
        }
        super.onDestroy()
    }

    private fun act(action: SessionAction) {
        when (action) {
            SessionAction.CANCEL -> controller.stop()
            SessionAction.WAIT_AGAIN -> controller.waitForHost(waitTransport())
            SessionAction.TRY_AGAIN -> {
                controller.acknowledgeError()
                controller.waitForHost(waitTransport())
            }
            SessionAction.CLOSE -> leave()
        }
    }

    /** As the Connect screen waits: over Wi-Fi too when the user allowed it (D74). */
    private fun waitTransport(): TransportKind =
        if ((application as ExtendSpaceApplication).wireless.wifiEnabled.value) TransportKind.WIRELESS else TransportKind.USB

    private fun leave() {
        val state = controller.state.value
        if (state is ConnectionState.Failed) controller.acknowledgeError() else if (state.isActive) controller.stop()
        finish()
    }

    companion object {
        fun intent(context: Context): Intent = Intent(context, SessionActivity::class.java)
    }
}

/** How long the controls stay after a tap on the picture. */
private const val CONTROLS_VISIBLE_MILLIS = 4_000L

@Composable
fun SessionScreen(
    state: ConnectionState,
    stream: StreamInfo?,
    surface: @Composable (Modifier) -> Unit,
    onAction: (SessionAction) -> Unit,
    /** The PC takes touches from the picture (D81): taps go to it, and the controls open from the handle. */
    touchEnabled: Boolean = false,
    /** Changes when Back asks for the controls to be shown instead of leaving (D84). */
    showControlsSignal: Int = 0,
    onControlsVisibleChanged: (Boolean) -> Unit = {},
) {
    var controlsVisible by remember { mutableStateOf(false) }
    val overlay = sessionOverlay(state)

    LaunchedEffect(showControlsSignal) {
        if (showControlsSignal > 0) controlsVisible = true
    }
    LaunchedEffect(controlsVisible) { onControlsVisibleChanged(controlsVisible) }

    // When the picture first appears the controls show for a moment, so that the way to end the
    // session (and that a tap brings it back) is seen once without covering the picture for long.
    val streaming = state is ConnectionState.Streaming
    LaunchedEffect(streaming) {
        if (streaming) controlsVisible = true
    }

    LaunchedEffect(controlsVisible) {
        if (controlsVisible) {
            delay(CONTROLS_VISIBLE_MILLIS)
            controlsVisible = false
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black)
            .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) {
                // With touch on, taps belong to the PC (even one that reached here through the
                // picture's bars): the handle opens the controls instead.
                if (overlay == null && !touchEnabled) controlsVisible = !controlsVisible
            },
        contentAlignment = Alignment.Center,
    ) {
        // The surface exists for as long as the screen does, so that a stream starting finds it lent.
        // It is sized to the stream's own shape, fitted inside the screen and centred (D69).
        surface(
            Modifier.layout { measurable, constraints ->
                val size = fitPicture(constraints.maxWidth, constraints.maxHeight, stream?.width ?: 0, stream?.height ?: 0)
                val placeable = measurable.measure(Constraints.fixed(size.width, size.height))
                layout(size.width, size.height) { placeable.place(0, 0) }
            },
        )

        if (overlay == null && touchEnabled && !controlsVisible) {
            // Touches on the picture belong to the PC, so the controls have a handle of their own:
            // small, translucent, in a corner, 48 dp to touch.
            Surface(
                onClick = { controlsVisible = true },
                modifier = Modifier.align(Alignment.TopEnd).padding(12.dp).size(48.dp),
                shape = CircleShape,
                color = MaterialTheme.colorScheme.surfaceContainerHigh.copy(alpha = 0.55f),
            ) {
                Box(contentAlignment = Alignment.Center) {
                    Text(text = "⋯", style = MaterialTheme.typography.titleLarge)
                }
            }
        }

        if (overlay != null) {
            OverlayCard(overlay, onAction)
        } else if (controlsVisible) {
            Surface(
                modifier = Modifier.align(Alignment.TopCenter).padding(24.dp),
                shape = MaterialTheme.shapes.large,
                color = MaterialTheme.colorScheme.surfaceContainerHigh.copy(alpha = 0.92f),
            ) {
                Row(
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    Text(
                        text = if (stream != null) {
                            stringResource(
                                if (stream.extended) R.string.session_extended_detail else R.string.session_streaming_detail,
                                stream.hostName,
                                stream.width,
                                stream.height,
                                stream.frameRate,
                            )
                        } else {
                            stringResource(R.string.session_streaming_hint)
                        },
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    FilledTonalButton(onClick = { onAction(SessionAction.CANCEL) }) {
                        Text(stringResource(R.string.session_action_end))
                    }
                }
            }
        }
    }
}

@Composable
private fun OverlayCard(overlay: SessionOverlay, onAction: (SessionAction) -> Unit) {
    Surface(
        modifier = Modifier.padding(24.dp).widthIn(max = 520.dp),
        shape = MaterialTheme.shapes.extraLarge,
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
    ) {
        Column(
            modifier = Modifier.padding(28.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            if (overlay.showsProgress) CircularProgressIndicator()
            Text(text = stringResource(overlay.title), style = MaterialTheme.typography.headlineSmall, textAlign = TextAlign.Center)
            Text(
                text = stringResource(overlay.detail),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
            )
            if (overlay.actions.isNotEmpty()) {
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    overlay.actions.forEachIndexed { index, action ->
                        val label = stringResource(action.label())
                        if (index == 0 && action != SessionAction.CANCEL) {
                            Button(onClick = { onAction(action) }) { Text(label) }
                        } else {
                            OutlinedButton(onClick = { onAction(action) }) { Text(label) }
                        }
                    }
                }
            }
        }
    }
}

private fun SessionAction.label(): Int = when (this) {
    SessionAction.CANCEL -> R.string.session_action_cancel
    SessionAction.WAIT_AGAIN -> R.string.session_action_wait_again
    SessionAction.TRY_AGAIN -> R.string.session_action_try_again
    SessionAction.CLOSE -> R.string.session_action_close
}
