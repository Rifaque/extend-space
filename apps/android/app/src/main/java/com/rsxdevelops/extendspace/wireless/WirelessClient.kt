package com.rsxdevelops.extendspace.wireless

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Where the "allow Wi-Fi" choice is kept. SharedPreferences in the app; memory in tests. */
internal interface WirelessPreference {
    var enabled: Boolean
}

/**
 * What the screens need of Wi-Fi (D74–D79): whether the tablet also waits on Wi-Fi, the pairing
 * window, and the paired PCs. The session itself never sees this; it sees only the listener.
 */
class WirelessClient internal constructor(
    private val endpoint: WifiEndpoint,
    private val trust: TrustedHostStore,
    private val preference: WirelessPreference,
    private val identity: () -> DeviceIdentity,
) {
    private val enabledFlow = MutableStateFlow(preference.enabled)

    /** Whether Wait for PC also listens on Wi-Fi. Takes effect at the next Wait for PC. */
    val wifiEnabled: StateFlow<Boolean> = enabledFlow.asStateFlow()

    val pairing: StateFlow<PairingState> get() = endpoint.pairing

    val pairedPcs: StateFlow<List<TrustedHost>> get() = trust.hosts

    fun setWifiEnabled(enabled: Boolean) {
        preference.enabled = enabled
        enabledFlow.value = enabled
    }

    fun openPairing() = endpoint.openPairing()

    fun closePairing() = endpoint.closePairing()

    fun confirmPairing(accepted: Boolean) = endpoint.decide(accepted)

    fun acknowledgePairing() = endpoint.acknowledgePairing()

    fun forget(deviceId: String): Boolean = trust.remove(deviceId)

    /**
     * This tablet's IPv4 addresses on local networks, for typing on the PC where discovery does not
     * work (ES-126). Read from the network interfaces; no permission is needed.
     */
    fun localAddresses(): List<String> = try {
        java.net.NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback && !it.isVirtual }
            .flatMap { it.inetAddresses.toList() }
            .filterIsInstance<java.net.Inet4Address>()
            .filter { it.isSiteLocalAddress }
            .map { it.hostAddress.orEmpty() }
            .filter { it.isNotEmpty() }
            .distinct()
    } catch (exception: java.net.SocketException) {
        emptyList()
    }

    /** This tablet's short ID, as the PC lists it; null if the keystore cannot provide one. */
    fun shortId(): String? = try {
        DeviceIds.short(identity().deviceId)
    } catch (exception: Exception) {
        null
    }
}
