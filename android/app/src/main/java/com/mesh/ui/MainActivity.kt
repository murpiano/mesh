package com.mesh.ui

import android.os.Build
import android.os.Bundle
import android.view.View
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.ListView
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.mesh.core.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.security.PrivateKey
import java.security.cert.X509Certificate

class MainActivity : AppCompatActivity(), PairingPrompt {
    private val devices = mutableListOf<DiscoveredDevice>()
    private lateinit var adapter: ArrayAdapter<String>
    private lateinit var listView: ListView
    private lateinit var connectButton: Button
    private lateinit var statusText: TextView
    private lateinit var errorBanner: View
    private lateinit var discovery: MeshDiscovery

    private var identity: Pair<X509Certificate, PrivateKey>? = null
    private var selfUuid: String? = null
    private val selfName: String = Build.MODEL
    private var listener: PairingListener? = null
    private var selectedUuid: String? = null

    // All pairing work runs on Main, so a plain flag keeps a double-tap or an incoming
    // connection from racing an attempt that is already in flight.
    private var pairingInFlight = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(com.mesh.R.layout.activity_main)

        listView = findViewById(com.mesh.R.id.deviceListView)
        connectButton = findViewById(com.mesh.R.id.connectButton)
        statusText = findViewById(com.mesh.R.id.statusText)
        errorBanner = findViewById(com.mesh.R.id.mdnsErrorBanner)
        adapter = ArrayAdapter(this, android.R.layout.simple_list_item_single_choice, mutableListOf())
        listView.adapter = adapter
        listView.setOnItemClickListener { _, _, position, _ -> selectedUuid = devices.getOrNull(position)?.uuid }

        discovery = MeshDiscovery(this)
        findViewById<Button>(com.mesh.R.id.refreshButton).setOnClickListener { startDiscovery() }
        connectButton.setOnClickListener {
            val target = devices.firstOrNull { it.uuid == selectedUuid } ?: return@setOnClickListener
            connectTo(target)
        }

        lifecycleScope.launch {
            statusText.text = "Preparing identity…"
            // Keystore RSA-2048 generation on first run can take seconds: keep it off Main.
            val (id, uuid, l) = try {
                withContext(Dispatchers.IO) {
                    val id = IdentityStore.loadOrCreate(this@MainActivity)
                    val uuid = getOrCreateDeviceUuid(this@MainActivity)
                    // Port 0: the OS picks a free ephemeral port; that is what we advertise.
                    Triple(id, uuid, PairingListener(id, 0))
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                statusText.text = "Startup failed: ${e.message}"
                return@launch
            }
            identity = id
            selfUuid = uuid
            listener = l
            statusText.text = ""
            connectButton.isEnabled = true
            startDiscovery()

            // Listen for incoming pairing for the Activity's lifetime: one connection at a
            // time, then back to listening. Handlers run on Main (prompts need it).
            l.runAcceptLoop(::handleIncoming) { e ->
                runOnUiThread { statusText.text = "Incoming connection failed: ${e.message}" }
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        listener?.close() // unblocks accept(); the loop exits
        discovery.stop()
    }

    private fun startDiscovery() {
        val uuid = selfUuid ?: return
        val port = listener?.port ?: return
        errorBanner.visibility = View.GONE
        val onError: (String) -> Unit = { msg ->
            runOnUiThread {
                errorBanner.visibility = View.VISIBLE
                statusText.text = msg
            }
        }
        discovery.advertise(uuid, selfName, "android", port, onError)
        discovery.startBrowsing(
            onFound = { device -> runOnUiThread { onDeviceFound(device) } },
            onLost = { lostUuid -> runOnUiThread { onDeviceLost(lostUuid) } },
            onError = onError)
    }

    private fun onDeviceFound(device: DiscoveredDevice) {
        if (device.uuid == selfUuid) return // our own announcement
        val index = devices.indexOfFirst { it.uuid == device.uuid }
        if (index >= 0) devices[index] = device else devices.add(device)
        refreshList()
    }

    private fun onDeviceLost(uuid: String) {
        if (devices.removeAll { it.uuid == uuid }) refreshList()
    }

    private fun refreshList() {
        adapter.clear()
        adapter.addAll(devices.map { it.name })
        val selected = devices.indexOfFirst { it.uuid == selectedUuid }
        listView.clearChoices()
        if (selected >= 0) listView.setItemChecked(selected, true) else selectedUuid = null
    }

    private suspend fun handleIncoming(session: PairingSession) {
        val uuid = selfUuid ?: return
        if (pairingInFlight) return // busy: the loop closes this session; peer sees a failure
        pairingInFlight = true
        connectButton.isEnabled = false
        try {
            val outcome = session.run(uuid, selfName, dao(), this)
            showOutcome(outcome, outcome.peerName ?: session.peerIp)
        } finally {
            pairingInFlight = false
            connectButton.isEnabled = true
        }
    }

    private fun connectTo(target: DiscoveredDevice) {
        val id = identity ?: return
        val uuid = selfUuid ?: return
        if (pairingInFlight) return
        pairingInFlight = true
        connectButton.isEnabled = false
        lifecycleScope.launch {
            try {
                val session = try {
                    PairingSession.connect(id, target.ipAddress, target.port)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    showOutcome(PairingOutcome(PairingResult.CONNECTION_FAILED, target.uuid, target.name,
                        e.message ?: e.javaClass.simpleName), target.name)
                    return@launch
                }
                session.use {
                    val outcome = it.run(uuid, selfName, dao(), this@MainActivity)
                    showOutcome(outcome, outcome.peerName ?: target.name)
                }
            } finally {
                pairingInFlight = false
                connectButton.isEnabled = true
            }
        }
    }

    private fun dao() = MeshDatabase.getInstance(this).pairedDeviceDao()

    /**
     * "Connection failed" = TCP/TLS/transport problem (incl. the 10 s timeout);
     * "Pairing failed" = connected, but the PIN was rejected/declined/timed out.
     */
    private fun showOutcome(outcome: PairingOutcome, peerName: String) {
        val text = when (outcome.result) {
            PairingResult.PAIRED -> "Paired with $peerName."
            PairingResult.RECONNECTED -> "Reconnected to $peerName (trusted)."
            PairingResult.CONNECTION_FAILED -> "Connection failed: ${outcome.detail}"
            PairingResult.PAIRING_FAILED -> "Pairing failed."
        }
        statusText.text = text
        Toast.makeText(this, text, Toast.LENGTH_LONG).show()
    }

    // PairingPrompt -------------------------------------------------------------------

    override suspend fun confirmCertificateChanged(peerName: String): Boolean =
        PinConfirmDialog.confirmCertificateChanged(this, peerName)

    override suspend fun confirmPin(peerName: String, pin: String): Boolean =
        PinConfirmDialog.confirmPin(this, peerName, pin)
}
