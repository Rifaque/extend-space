package com.rsxdevelops.extendspace.wireless

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import java.io.File
import java.io.IOException

/** A PC this tablet has paired with. Holds no secret: only the hash of its public key. */
data class TrustedHost(val deviceId: String, val name: String, val pairedAtMillis: Long)

/**
 * The PCs this tablet trusts (D79): a JSON file in the app's no-backup directory, rewritten whole on
 * each change. A file that cannot be read is set aside and the list starts empty, so that damage fails
 * closed. Thread-safe.
 */
internal class TrustedHostStore(private val file: File, private val log: (String) -> Unit = {}) {
    private val lock = Any()
    private val current = MutableStateFlow(load())

    val hosts: StateFlow<List<TrustedHost>> = current.asStateFlow()

    fun find(deviceId: String?): TrustedHost? = deviceId?.let { id -> current.value.firstOrNull { it.deviceId == id } }

    fun isTrusted(deviceId: String?): Boolean = find(deviceId) != null

    /** Adds the PC, or replaces the entry with the same ID. */
    fun add(host: TrustedHost) {
        require(DeviceIds.isValid(host.deviceId)) { "Not a device ID." }
        mutate { list -> list.filterNot { it.deviceId == host.deviceId } + host }
    }

    /** Forgets the PC. False if it was not trusted. */
    fun remove(deviceId: String): Boolean {
        var removed = false
        mutate { list -> list.filterNot { it.deviceId == deviceId }.also { removed = it.size != list.size } }
        return removed
    }

    private fun mutate(change: (List<TrustedHost>) -> List<TrustedHost>) {
        synchronized(lock) {
            val next = change(current.value)
            if (next == current.value) return
            file.parentFile?.mkdirs()
            val temporary = File(file.path + ".tmp")
            temporary.writeText(serialise(next))
            if (!temporary.renameTo(file)) {
                // renameTo does not replace on every file system.
                file.delete()
                if (!temporary.renameTo(file)) throw IOException("Could not save the list of paired PCs.")
            }
            current.value = next
        }
    }

    private fun load(): List<TrustedHost> {
        if (!file.isFile) return emptyList()
        return try {
            val devices = JSONObject(file.readText()).getJSONArray("devices")
            (0 until devices.length()).mapNotNull { index ->
                val entry = devices.getJSONObject(index)
                val id = entry.optString("device_id")
                if (!DeviceIds.isValid(id)) null else TrustedHost(id, entry.optString("name", "PC"), entry.optLong("paired_at_ms"))
            }
        } catch (exception: JSONException) {
            file.renameTo(File(file.path + ".unreadable"))
            log("the list of paired PCs could not be read; it starts empty")
            emptyList()
        } catch (exception: IOException) {
            log("the list of paired PCs could not be read; it starts empty")
            emptyList()
        }
    }

    private fun serialise(hosts: List<TrustedHost>): String {
        val devices = JSONArray()
        hosts.forEach { devices.put(JSONObject().put("device_id", it.deviceId).put("name", it.name).put("paired_at_ms", it.pairedAtMillis)) }
        return JSONObject().put("version", 1).put("devices", devices).toString(2)
    }
}
