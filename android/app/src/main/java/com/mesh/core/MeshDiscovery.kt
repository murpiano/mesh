package com.mesh.core

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import java.util.ArrayDeque

data class DiscoveredDevice(
    val uuid: String,
    val name: String,
    val platform: String,
    val ipAddress: String,
    val port: Int,
    /** True when the device announced that it can take a screen cast. Builds older than
     * casting leave the TXT key out, so they are never offered it — their read loop would
     * throw on the new message tags. */
    val supportsCast: Boolean = false
)

/**
 * NSD-based (mDNS/DNS-SD) advertise and browse for Mesh devices on the local network.
 *
 * Note on `resolveService`: it is deprecated as of API 34 in favor of
 * `NsdManager.registerServiceInfoCallback`, but that replacement is only available on
 * API 34+. This app's minSdk is 26, so `resolveService` remains the correct choice to
 * support the full range of supported devices; the deprecation warning is suppressed
 * deliberately rather than worked around.
 *
 * Note on concurrency: `NsdManager.resolveService` only allows one outstanding resolve
 * per `NsdManager` instance at a time — a second call while one is pending fails with
 * `FAILURE_ALREADY_ACTIVE`. This class queues resolve requests and only issues the next one
 * once the previous one's listener has fired. NSD callbacks arrive on a binder thread, so the
 * queue and name->uuid map are guarded by [lock].
 *
 * Errors (registration / discovery start failures, or NsdManager throwing) are reported via
 * the `onError` callbacks so the UI can show "mDNS unreachable — check firewall settings".
 * [startBrowsing] may be called again as a manual refresh.
 */
class MeshDiscovery(context: Context) {
    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private val serviceType = "_meshlink._tcp."
    private val lock = Any()
    @Volatile private var registrationListener: NsdManager.RegistrationListener? = null
    @Volatile private var discoveryListener: NsdManager.DiscoveryListener? = null

    private val resolveQueue = ArrayDeque<NsdServiceInfo>()
    private var resolveInFlight = false
    private val serviceNameToUuid = mutableMapOf<String, String>()

    /** Idempotent while a registration is active; call again after a failure to retry. */
    fun advertise(uuid: String, name: String, platform: String, port: Int, onError: (String) -> Unit = {}) {
        if (registrationListener != null) return
        val serviceInfo = NsdServiceInfo().apply {
            serviceName = name
            this.serviceType = this@MeshDiscovery.serviceType
            this.port = port
            setAttribute("id", uuid)
            setAttribute("name", name)
            setAttribute("platform", platform)
            setAttribute(CAST_ATTRIBUTE, "1")
        }

        val listener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(info: NsdServiceInfo) {}
            override fun onRegistrationFailed(info: NsdServiceInfo, errorCode: Int) {
                registrationListener = null // allow a retry
                onError("mDNS registration failed (error $errorCode)")
            }
            override fun onServiceUnregistered(info: NsdServiceInfo) {}
            override fun onUnregistrationFailed(info: NsdServiceInfo, errorCode: Int) {}
        }
        registrationListener = listener
        try {
            nsdManager.registerService(serviceInfo, NsdManager.PROTOCOL_DNS_SD, listener)
        } catch (e: Exception) {
            registrationListener = null
            onError("mDNS registration failed: ${e.message}")
        }
    }

    /**
     * Start browsing, or restart it (manual refresh): any previous discovery session is
     * stopped and a fresh one started, which makes NSD re-query the network.
     */
    fun startBrowsing(
        onFound: (DiscoveredDevice) -> Unit,
        onLost: (String) -> Unit = {},
        onError: (String) -> Unit = {}
    ) {
        stopBrowsing()
        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                if (discoveryListener === this) discoveryListener = null
                onError("mDNS discovery failed (error $errorCode)")
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}

            override fun onServiceFound(service: NsdServiceInfo) {
                enqueueResolve(service, onFound)
            }

            override fun onServiceLost(service: NsdServiceInfo) {
                // The lost event only carries the service name; map it back to the uuid
                // learned when that service was resolved.
                val uuid = synchronized(lock) { serviceNameToUuid.remove(service.serviceName) }
                if (uuid != null) onLost(uuid)
            }
        }
        discoveryListener = listener
        try {
            nsdManager.discoverServices(serviceType, NsdManager.PROTOCOL_DNS_SD, listener)
        } catch (e: Exception) {
            discoveryListener = null
            onError("mDNS discovery failed: ${e.message}")
        }
    }

    private fun enqueueResolve(service: NsdServiceInfo, onFound: (DiscoveredDevice) -> Unit) {
        synchronized(lock) { resolveQueue.add(service) }
        resolveNext(onFound)
    }

    @Suppress("DEPRECATION")
    private fun resolveNext(onFound: (DiscoveredDevice) -> Unit) {
        val next = synchronized(lock) {
            if (resolveInFlight) return
            val polled = resolveQueue.poll() ?: return
            resolveInFlight = true
            polled
        }

        try {
            nsdManager.resolveService(next, object : NsdManager.ResolveListener {
                override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                    synchronized(lock) { resolveInFlight = false }
                    resolveNext(onFound)
                }

                override fun onServiceResolved(info: NsdServiceInfo) {
                    val uuid = info.attributes["id"]?.toString(Charsets.UTF_8)
                    val name = info.attributes["name"]?.toString(Charsets.UTF_8) ?: uuid
                    val platform = info.attributes["platform"]?.toString(Charsets.UTF_8) ?: "unknown"
                    val supportsCast =
                        info.attributes[CAST_ATTRIBUTE]?.toString(Charsets.UTF_8) == "1"
                    val host = info.host?.hostAddress
                    synchronized(lock) {
                        resolveInFlight = false
                        if (uuid != null) serviceNameToUuid[info.serviceName] = uuid
                    }
                    if (uuid != null && host != null) {
                        onFound(DiscoveredDevice(uuid, name ?: uuid, platform, host, info.port, supportsCast))
                    }
                    resolveNext(onFound)
                }
            })
        } catch (e: Exception) {
            synchronized(lock) { resolveInFlight = false }
        }
    }

    private fun stopBrowsing() {
        discoveryListener?.let { runCatching { nsdManager.stopServiceDiscovery(it) } }
        discoveryListener = null
        synchronized(lock) {
            resolveQueue.clear()
            resolveInFlight = false
        }
    }

    fun stop() {
        registrationListener?.let { runCatching { nsdManager.unregisterService(it) } }
        registrationListener = null
        stopBrowsing()
    }

    companion object {
        /** The TXT key that says this build understands the cast messages. */
        const val CAST_ATTRIBUTE = "cast"
    }
}
