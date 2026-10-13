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
    val port: Int
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
 * `FAILURE_ALREADY_ACTIVE`. When multiple services are discovered in quick succession
 * (multiple peers on the network), firing `resolveService` once per `onServiceFound`
 * without serializing would silently drop resolutions. This class queues resolve
 * requests and only issues the next one once the previous one's listener has fired.
 */
class MeshDiscovery(context: Context) {
    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private val serviceType = "_meshlink._tcp."
    private var registrationListener: NsdManager.RegistrationListener? = null
    private var discoveryListener: NsdManager.DiscoveryListener? = null

    private val resolveQueue = ArrayDeque<NsdServiceInfo>()
    private var resolveInFlight = false

    fun advertise(uuid: String, name: String, platform: String, port: Int) {
        val serviceInfo = NsdServiceInfo().apply {
            serviceName = name
            this.serviceType = this@MeshDiscovery.serviceType
            this.port = port
            setAttribute("id", uuid)
            setAttribute("name", name)
            setAttribute("platform", platform)
        }

        val listener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(info: NsdServiceInfo) {}
            override fun onRegistrationFailed(info: NsdServiceInfo, errorCode: Int) {}
            override fun onServiceUnregistered(info: NsdServiceInfo) {}
            override fun onUnregistrationFailed(info: NsdServiceInfo, errorCode: Int) {}
        }
        registrationListener = listener
        nsdManager.registerService(serviceInfo, NsdManager.PROTOCOL_DNS_SD, listener)
    }

    fun startBrowsing(onFound: (DiscoveredDevice) -> Unit) {
        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}

            override fun onServiceFound(service: NsdServiceInfo) {
                enqueueResolve(service, onFound)
            }

            override fun onServiceLost(service: NsdServiceInfo) {}
        }
        discoveryListener = listener
        nsdManager.discoverServices(serviceType, NsdManager.PROTOCOL_DNS_SD, listener)
    }

    private fun enqueueResolve(service: NsdServiceInfo, onFound: (DiscoveredDevice) -> Unit) {
        resolveQueue.add(service)
        resolveNext(onFound)
    }

    @Suppress("DEPRECATION")
    private fun resolveNext(onFound: (DiscoveredDevice) -> Unit) {
        if (resolveInFlight) return
        val next = resolveQueue.poll() ?: return
        resolveInFlight = true

        nsdManager.resolveService(next, object : NsdManager.ResolveListener {
            override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                resolveInFlight = false
                resolveNext(onFound)
            }

            override fun onServiceResolved(info: NsdServiceInfo) {
                resolveInFlight = false
                val uuid = info.attributes["id"]?.toString(Charsets.UTF_8)
                val name = info.attributes["name"]?.toString(Charsets.UTF_8) ?: uuid
                val platform = info.attributes["platform"]?.toString(Charsets.UTF_8) ?: "unknown"
                val host = info.host?.hostAddress
                if (uuid != null && host != null) {
                    onFound(DiscoveredDevice(uuid, name ?: uuid, platform, host, info.port))
                }
                resolveNext(onFound)
            }
        })
    }

    @Suppress("DEPRECATION")
    fun stop() {
        registrationListener?.let { nsdManager.unregisterService(it) }
        discoveryListener?.let { nsdManager.stopServiceDiscovery(it) }
        registrationListener = null
        discoveryListener = null
        resolveQueue.clear()
        resolveInFlight = false
    }
}
