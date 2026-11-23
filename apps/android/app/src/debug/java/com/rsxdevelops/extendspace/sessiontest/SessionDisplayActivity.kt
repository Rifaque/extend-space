package com.rsxdevelops.extendspace.sessiontest

import android.content.Context
import android.graphics.Color
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import android.view.Gravity
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.rsxdevelops.extendspace.ExtendSpaceApplication
import com.rsxdevelops.extendspace.R
import com.rsxdevelops.extendspace.connection.ConnectionController
import com.rsxdevelops.extendspace.connection.ConnectionState
import com.rsxdevelops.extendspace.connection.TransportKind
import com.rsxdevelops.extendspace.video.SurfaceOutput
import kotlinx.coroutines.launch

/**
 * Drives the **production** session from a debug build: the application's own
 * `ClientSessionController` with its USB link and `MediaCodec` receiver, exactly as composed in
 * `ExtendSpaceApplication`. It asks the controller to wait for a PC and lends it this screen's
 * surface; nothing here parses, decodes or draws. It exists because the release build's Connect
 * screen stays disabled until the production path has passed its device acceptance, and it is not
 * the debug live-stream screen, which runs the experiment's own code.
 *
 * `adb shell am start -n com.rsxdevelops.extendspace/.sessiontest.SessionDisplayActivity` opens it
 * waiting for the PC; `--ez listen false` opens it without. When a session ends without error it
 * waits for the next PC session, so that acceptance runs can follow one another; `--ez rewait
 * false` leaves it disconnected instead, as the product's own screen would. Leaving it (Back) stops the session;
 * rotating it or going Home only withdraws and lends the surface again, as the architecture says.
 */
class SessionDisplayActivity : ComponentActivity() {
    private val controller: ConnectionController get() = (application as ExtendSpaceApplication).connectionController
    private var lent: SurfaceOutput? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Log.i(ExtendSpaceApplication.LOG_TAG, "harness: screen created${if (savedInstanceState != null) " again (configuration change)" else ""}")
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        val picture = AspectSurfaceView(this)
        picture.holder.addCallback(
            object : SurfaceHolder.Callback {
                override fun surfaceCreated(holder: SurfaceHolder) {
                    val output = SurfaceOutput(holder.surface)
                    lent = output
                    controller.lendOutput(output)
                    Log.i(ExtendSpaceApplication.LOG_TAG, "harness: surface created and lent")
                }

                override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit

                override fun surfaceDestroyed(holder: SurfaceHolder) {
                    // Returns once nothing draws to the surface any more. Timed for device test V15.
                    val startedNanos = SystemClock.elapsedRealtimeNanos()
                    lent?.let(controller::withdrawOutput)
                    lent = null
                    Log.i(ExtendSpaceApplication.LOG_TAG, "harness: surface destroyed; withdrawing the output took ${(SystemClock.elapsedRealtimeNanos() - startedNanos) / 1_000_000} ms on the UI thread")
                }
            },
        )
        val banner = TextView(this).apply {
            setTextColor(Color.WHITE)
            setBackgroundColor(BANNER_BACKGROUND)
            setPadding(BANNER_PADDING, BANNER_PADDING, BANNER_PADDING, BANNER_PADDING)
        }
        setContentView(
            FrameLayout(this).apply {
                setBackgroundColor(Color.BLACK)
                addView(picture, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER))
                addView(banner, FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.START))
            },
        )

        val rewait = intent.getBooleanExtra(EXTRA_REWAIT, true)
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                var previous: ConnectionState? = null
                controller.state.collect { state ->
                    banner.text = getString(R.string.session_banner, state.toString())
                    // A session the PC ended (its goodbye) is over, as designed; for repeated
                    // acceptance runs the harness then waits for the next one. Never after a failure.
                    if (rewait && state is ConnectionState.Disconnected && previous is ConnectionState.Disconnecting) {
                        controller.waitForHost(TransportKind.USB)
                    }
                    previous = state
                }
            }
        }

        if (savedInstanceState == null && intent.getBooleanExtra(EXTRA_LISTEN, true)) controller.waitForHost(TransportKind.USB)
    }

    override fun onDestroy() {
        // Left for good: the session ends. Recreated (rotation): it carries on, and only the surface was withdrawn.
        Log.i(ExtendSpaceApplication.LOG_TAG, "harness: screen destroyed${if (isFinishing) ", finishing: the session is stopped" else ", to be recreated"}")
        if (isFinishing) controller.stop()
        super.onDestroy()
    }

    /** A surface kept at the stream's 16:9 shape, centred, so the picture is not stretched. */
    private class AspectSurfaceView(context: Context) : SurfaceView(context) {
        override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
            val width = MeasureSpec.getSize(widthMeasureSpec)
            val height = MeasureSpec.getSize(heightMeasureSpec)
            if (width * ASPECT_HEIGHT > height * ASPECT_WIDTH) {
                setMeasuredDimension(height * ASPECT_WIDTH / ASPECT_HEIGHT, height)
            } else {
                setMeasuredDimension(width, width * ASPECT_HEIGHT / ASPECT_WIDTH)
            }
        }
    }

    private companion object {
        const val EXTRA_LISTEN = "listen"
        const val EXTRA_REWAIT = "rewait"
        const val ASPECT_WIDTH = 16
        const val ASPECT_HEIGHT = 9
        const val BANNER_PADDING = 16
        const val BANNER_BACKGROUND = 0x99000000.toInt()
    }
}
