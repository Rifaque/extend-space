package com.rsxdevelops.extendspace.wireless

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager

/**
 * Advertises the Wi-Fi endpoint as `_extendspace._tcp` with Android's DNS-SD (D78), only while the
 * endpoint runs. NsdManager cannot change a TXT record in place, so a change re-registers. A
 * multicast lock is held meanwhile so that the Wi-Fi driver does not filter the PC's queries.
 */
internal class NsdAdvertiser(context: Context, private val log: (String) -> Unit = {}) : ServiceAdvertiser {
    private val nsd = context.getSystemService(NsdManager::class.java)
    private val multicast = context.applicationContext.getSystemService(WifiManager::class.java)
        ?.createMulticastLock("extendspace-discovery")?.apply { setReferenceCounted(false) }
    private val lock = Any()
    private var registered: Pair<Registration, NsdManager.RegistrationListener>? = null

    private data class Registration(val name: String, val port: Int, val attributes: Map<String, String>)

    override fun advertise(name: String, port: Int, attributes: Map<String, String>) {
        val wanted = Registration(name, port, attributes)
        synchronized(lock) {
            if (registered?.first == wanted) return
            unregisterLocked()
            val info = NsdServiceInfo().apply {
                serviceName = name
                serviceType = SERVICE_TYPE
                setPort(port)
                attributes.forEach { (key, value) -> setAttribute(key, value) }
            }
            val listener = object : NsdManager.RegistrationListener {
                override fun onServiceRegistered(info: NsdServiceInfo) = log("advertised on Wi-Fi as '${info.serviceName}'")
                override fun onRegistrationFailed(info: NsdServiceInfo, errorCode: Int) = log("could not advertise on Wi-Fi (error $errorCode); the PC can still connect by address")
                override fun onServiceUnregistered(info: NsdServiceInfo) = Unit
                override fun onUnregistrationFailed(info: NsdServiceInfo, errorCode: Int) = Unit
            }
            multicast?.acquire()
            try {
                nsd?.registerService(info, NsdManager.PROTOCOL_DNS_SD, listener)
                registered = wanted to listener
            } catch (exception: IllegalArgumentException) {
                log("could not advertise on Wi-Fi: ${exception.message}")
            }
        }
    }

    override fun withdraw() {
        synchronized(lock) {
            unregisterLocked()
            if (multicast?.isHeld == true) multicast.release()
        }
    }

    private fun unregisterLocked() {
        val (_, listener) = registered ?: return
        registered = null
        try {
            nsd?.unregisterService(listener)
        } catch (exception: IllegalArgumentException) {
            // Never registered, or already gone.
        }
    }

    private companion object {
        const val SERVICE_TYPE = "_extendspace._tcp"
    }
}
