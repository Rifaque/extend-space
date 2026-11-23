package com.rsxdevelops.extendspace

import android.app.Application
import android.hardware.display.DisplayManager
import android.os.Build
import android.util.Log
import android.view.Display
import android.view.WindowManager
import com.rsxdevelops.extendspace.connection.ClientSessionController
import com.rsxdevelops.extendspace.connection.ConnectionController
import com.rsxdevelops.extendspace.connection.SystemSessionClock
import com.rsxdevelops.extendspace.link.CompositeHostListenerFactory
import com.rsxdevelops.extendspace.link.DisplayInfo
import com.rsxdevelops.extendspace.link.LocalSocketListenerFactory
import com.rsxdevelops.extendspace.link.UsbHostLink
import com.rsxdevelops.extendspace.video.HostClockEstimate
import com.rsxdevelops.extendspace.video.MediaCodecPlatform
import com.rsxdevelops.extendspace.video.MediaCodecVideoReceiver
import com.rsxdevelops.extendspace.wireless.KeystoreDeviceIdentity
import com.rsxdevelops.extendspace.wireless.NsdAdvertiser
import com.rsxdevelops.extendspace.wireless.TrustedHostStore
import com.rsxdevelops.extendspace.wireless.WifiEndpoint
import com.rsxdevelops.extendspace.wireless.WirelessClient
import com.rsxdevelops.extendspace.wireless.WirelessPreference
import androidx.core.content.edit
import java.io.File
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob

class ExtendSpaceApplication : Application() {
    private val hostClock = HostClockEstimate()
    private val receiver = MediaCodecVideoReceiver(MediaCodecPlatform(), hostClock, log = { Log.i(LOG_TAG, it) })

    /**
     * Process-wide so a session can outlive any single screen. Its link is the real USB one (a
     * local socket that adb forwards to) and its receiver the real `MediaCodec` decoder (migration
     * stage 5). It listens only when asked to wait for a PC, and nothing in this build's own screens
     * asks yet: the Connect screen's button stays disabled until the production path has passed its
     * device acceptance. Its decisions run one at a time, off the UI thread.
     */
    private val deviceName = "${Build.MANUFACTURER} ${Build.MODEL}"
    private val identity = KeystoreDeviceIdentity()
    private val trust by lazy { TrustedHostStore(File(noBackupFilesDir, "trusted-hosts.json")) { Log.i(LOG_TAG, it) } }
    private val wifiPreference by lazy {
        object : WirelessPreference {
            private val preferences = getSharedPreferences("wireless", MODE_PRIVATE)
            override var enabled: Boolean
                get() = preferences.getBoolean("wifi_enabled", false)
                set(value) = preferences.edit { putBoolean("wifi_enabled", value) }
        }
    }

    /**
     * The Wi-Fi endpoint (D74-D79): mutual TLS with the keystore identity, the trusted-PC list in the
     * no-backup directory, advertised by DNS-SD. It runs only while the session waits with Wi-Fi
     * allowed, or while the pairing window is open.
     */
    private val wifi by lazy {
        WifiEndpoint(
            identity = { identity },
            trust = trust,
            deviceName = { deviceName },
            appVersion = ::appVersion,
            advertiser = NsdAdvertiser(this) { Log.i(LOG_TAG, it) },
            log = { Log.i(LOG_TAG, it) },
        )
    }

    /** Wi-Fi settings, pairing and paired PCs, for the screens. */
    val wireless: WirelessClient by lazy { WirelessClient(wifi, trust, wifiPreference) { identity } }

    private fun appVersion(): String = packageManager.getPackageInfo(packageName, 0).versionName.orEmpty()

    val connectionController: ConnectionController = ClientSessionController(
        link = UsbHostLink(
            // USB always; Wi-Fi too when the user allowed it (read each time listening starts).
            listenerFactory = CompositeHostListenerFactory(
                usb = LocalSocketListenerFactory(),
                wifi = { wifi.sessionListenerFactory.listen() },
                includeWifi = { wifiPreference.enabled },
                log = { Log.i(LOG_TAG, it) },
            ),
            deviceName = deviceName,
            appVersion = ::appVersion,
            display = ::displayInfo,
            stats = receiver::stats,
            hostClock = hostClock,
            log = { Log.i(LOG_TAG, it) },
        ),
        receiver = receiver,
        clock = SystemSessionClock(),
        scope = CoroutineScope(SupervisorJob() + Dispatchers.Default.limitedParallelism(1)),
        onStateChanged = { Log.i(LOG_TAG, "state $it") },
    )

    private fun displayInfo(): DisplayInfo {
        val display = getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY)
        val refresh = display?.refreshRate?.toDouble() ?: 0.0
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val bounds = getSystemService(WindowManager::class.java).maximumWindowMetrics.bounds
            DisplayInfo(bounds.width(), bounds.height(), refresh)
        } else {
            val metrics = resources.displayMetrics
            DisplayInfo(metrics.widthPixels, metrics.heightPixels, refresh)
        }
    }

    companion object {
        /** The production session's log tag; the debug experiment's screen logs as `ExtendSpaceLive`. */
        const val LOG_TAG = "ExtendSpaceSession"
    }
}
