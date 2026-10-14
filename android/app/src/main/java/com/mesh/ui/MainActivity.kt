package com.mesh.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.fragment.app.Fragment
import androidx.lifecycle.lifecycleScope
import com.google.android.material.tabs.TabLayout
import com.mesh.R
import com.mesh.core.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.security.PrivateKey
import java.security.cert.X509Certificate

/**
 * Tab host (Devices / Clipboard) and owner of everything cross-cutting: identity, discovery,
 * the incoming-pairing accept loop, the pairing flows, the persistent [MeshSession] to the
 * paired peer with its reconnect orchestration, the clipboard watcher and the clipboard history
 * writes. The fragments are view layers over this state (tab switches replace them), so nothing
 * here stops or is lost while a tab is hidden.
 *
 * Threading: everything below runs on Main unless stated otherwise. MeshSession callbacks arrive
 * on its IO threads and are marshalled onto Main (state changes) or into [historyOps] (clips).
 */
class MainActivity : AppCompatActivity(), PairingPrompt {
    companion object {
        /** Clipboard texts longer than this (1 MiB of chars) are neither stored nor pushed. */
        private const val MAX_CLIPBOARD_TEXT_LENGTH = 1_048_576
        private const val CLIPBOARD_SELF_WRITE_SUPPRESS_MS = 500L
    }

    private enum class ReconnectAttemptResult { RECONNECTED, FAILED, TRUST_LOST }

    // Devices-tab state (rendered by DevicesFragment) --------------------------------------

    internal val devices = mutableListOf<DiscoveredDevice>()
    internal var selectedUuid: String? = null
    internal var statusMessage: String = ""
        private set
    internal var mdnsErrorVisible = false
        private set
    internal val connectEnabled: Boolean get() = identityReady && !pairingInFlight

    private lateinit var discovery: MeshDiscovery
    private var identity: Pair<X509Certificate, PrivateKey>? = null
    private var selfUuid: String? = null
    private val selfName: String = Build.MODEL
    private var listener: PairingListener? = null
    private var identityReady = false

    // All pairing work (user Connect, incoming, background reconnect attempts) runs on Main, so
    // a plain flag keeps a double-tap or an incoming connection from racing an attempt that is
    // already in flight. It also serialises every attachMeshSession call.
    private var pairingInFlight = false

    // Mesh session (rendered by ClipboardFragment) -----------------------------------------

    // The persistent post-pairing channel to the (single) connected peer, plus what we need to
    // re-establish it.
    private var meshSession: MeshSession? = null
    private var meshPeerUuid: String? = null
    private var meshPeerName: String? = null
    private var meshPeerEndpoint: Pair<String, Int> = "" to 0
    // The session the (single) running reconnect loop is currently trying to replace; null when
    // no loop is running. Tracking the instance instead of a plain "in flight" flag lets a newer
    // session's disconnect retarget a loop that was started for an older, since-replaced one.
    private var meshReconnectTarget: MeshSession? = null
    // A dead session whose peer no longer passes the trust check: retrying can never succeed,
    // so it is left alone until the user re-pairs (which replaces it).
    private var meshTrustLostSession: MeshSession? = null

    internal var sessionStatus: String = "Not connected"
        private set

    // Clipboard ------------------------------------------------------------------------------

    private lateinit var clipboardWatcher: ClipboardWatcher

    private sealed class HistoryOp {
        class Add(val entry: ClipboardEntry, val pushToPeer: Boolean) : HistoryOp()
        class Load(val onLoaded: (List<ClipboardEntry>) -> Unit) : HistoryOp()
    }

    // Single consumer: history writes and loads are applied strictly in arrival order, which
    // keeps received clips in order and lets a load see exactly the writes queued before it.
    private val historyOps = Channel<HistoryOp>(Channel.UNLIMITED)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        discovery = MeshDiscovery(this)
        clipboardWatcher = ClipboardWatcher(this)
        clipboardWatcher.onTextCopied = ::onLocalTextCopied
        clipboardWatcher.start()
        lifecycleScope.launch { runHistoryConsumer() }

        val tabLayout = findViewById<TabLayout>(R.id.tabLayout)
        if (savedInstanceState == null) {
            showFragment(DevicesFragment())
        } else if (supportFragmentManager.findFragmentById(R.id.fragmentContainer) is ClipboardFragment) {
            tabLayout.getTabAt(1)?.select() // restored fragment: keep the tab strip in sync
        }
        tabLayout.addOnTabSelectedListener(object : TabLayout.OnTabSelectedListener {
            override fun onTabSelected(tab: TabLayout.Tab) {
                showFragment(if (tab.position == 0) DevicesFragment() else ClipboardFragment())
            }
            override fun onTabUnselected(tab: TabLayout.Tab) {}
            override fun onTabReselected(tab: TabLayout.Tab) {}
        })

        lifecycleScope.launch {
            setStatus("Preparing identity…")
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
                setStatus("Startup failed: ${e.message}")
                return@launch
            }
            identity = id
            selfUuid = uuid
            listener = l
            setStatus("")
            identityReady = true
            devicesFragment()?.renderConnectEnabled()
            startDiscovery()

            // Listen for incoming pairing for the Activity's lifetime: one connection at a
            // time, then back to listening. Handlers run on Main (prompts need it).
            l.runAcceptLoop(::handleIncoming) { e ->
                runOnUiThread { setStatus("Incoming connection failed: ${e.message}") }
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        clipboardWatcher.stop()
        listener?.close() // unblocks accept(); the loop exits
        discovery.stop()
        historyOps.close()
        meshSession?.let(::closeInBackground)
        meshSession = null
    }

    private fun showFragment(fragment: Fragment) {
        supportFragmentManager.beginTransaction()
            .replace(R.id.fragmentContainer, fragment)
            .commit()
    }

    private fun devicesFragment() =
        supportFragmentManager.findFragmentById(R.id.fragmentContainer) as? DevicesFragment

    private fun clipboardFragment() =
        supportFragmentManager.findFragmentById(R.id.fragmentContainer) as? ClipboardFragment

    private fun setStatus(text: String) {
        statusMessage = text
        devicesFragment()?.renderStatus()
    }

    private fun setSessionStatus(text: String) {
        sessionStatus = text
        clipboardFragment()?.renderSessionStatus()
    }

    private fun setPairingInFlight(value: Boolean) {
        pairingInFlight = value
        devicesFragment()?.renderConnectEnabled()
    }

    // Discovery ------------------------------------------------------------------------------

    internal fun startDiscovery() {
        val uuid = selfUuid ?: return
        val port = listener?.port ?: return
        setMdnsErrorVisible(false)
        val onError: (String) -> Unit = { msg ->
            runOnUiThread {
                setMdnsErrorVisible(true)
                setStatus(msg)
            }
        }
        discovery.advertise(uuid, selfName, "android", port, onError)
        discovery.startBrowsing(
            onFound = { device -> runOnUiThread { onDeviceFound(device) } },
            onLost = { lostUuid -> runOnUiThread { onDeviceLost(lostUuid) } },
            onError = onError)
    }

    private fun setMdnsErrorVisible(visible: Boolean) {
        mdnsErrorVisible = visible
        devicesFragment()?.renderErrorBanner()
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
        if (devices.none { it.uuid == selectedUuid }) selectedUuid = null
        devicesFragment()?.renderDeviceList()
    }

    /** The peer's listening endpoint as currently advertised over mDNS, if seen. */
    private fun resolvePeerEndpoint(peerUuid: String, fallback: Pair<String, Int>): Pair<String, Int> =
        devices.firstOrNull { it.uuid == peerUuid }?.let { it.ipAddress to it.port } ?: fallback

    // Pairing --------------------------------------------------------------------------------

    private suspend fun handleIncoming(session: PairingSession) {
        val uuid = selfUuid ?: return
        if (pairingInFlight) return // busy: the loop closes this session; peer sees a failure
        setPairingInFlight(true)
        try {
            val outcome = session.run(uuid, selfName, dao(), this)
            val peerName = outcome.peerName ?: session.peerIp
            // A trusted reconnect from the peer we already have a mesh session with is the
            // peer's background reconnect replacing that session: report it quietly, the same
            // way our own background reconnect does, instead of a toast on every network blip.
            val isBackgroundReconnect = outcome.result == PairingResult.RECONNECTED &&
                meshSession != null && meshPeerUuid == outcome.peerUuid
            if (isBackgroundReconnect) setStatus("Reconnected to $peerName (trusted).")
            else showOutcome(outcome, peerName)
            if (outcome.result == PairingResult.PAIRED || outcome.result == PairingResult.RECONNECTED) {
                val peerUuid = outcome.peerUuid!!
                // The accepted socket came from the peer's ephemeral port, so its listening
                // endpoint (needed to reconnect later) comes from mDNS when we have it.
                val endpoint = resolvePeerEndpoint(peerUuid, session.peerIp to 0)
                attachMeshSession(session, peerUuid, peerName, endpoint)
            }
        } finally {
            setPairingInFlight(false)
        }
    }

    internal fun connectToSelected() {
        val target = devices.firstOrNull { it.uuid == selectedUuid } ?: return
        connectTo(target)
    }

    private fun connectTo(target: DiscoveredDevice) {
        val id = identity ?: return
        val uuid = selfUuid ?: return
        if (pairingInFlight) return
        setPairingInFlight(true)
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
                // On success the transport is detached into a MeshSession, which makes use{}'s
                // close a no-op; on every other outcome it closes the socket as before.
                session.use {
                    val outcome = it.run(uuid, selfName, dao(), this@MainActivity)
                    val peerName = outcome.peerName ?: target.name
                    showOutcome(outcome, peerName)
                    if (outcome.result == PairingResult.PAIRED || outcome.result == PairingResult.RECONNECTED) {
                        attachMeshSession(it, outcome.peerUuid!!, peerName, target.ipAddress to target.port)
                    }
                }
            } finally {
                setPairingInFlight(false)
            }
        }
    }

    private fun dao() = MeshDatabase.getInstance(this).pairedDeviceDao()

    /**
     * "Connection failed" = TCP/TLS/transport problem (incl. the 10 s timeout);
     * "Pairing failed" = connected, but the PIN was rejected/declined/timed out.
     * Used for user-visible pairing results only; background reconnects just update status.
     */
    private fun showOutcome(outcome: PairingOutcome, peerName: String) {
        val text = when (outcome.result) {
            PairingResult.PAIRED -> "Paired with $peerName."
            PairingResult.RECONNECTED -> "Reconnected to $peerName (trusted)."
            PairingResult.CONNECTION_FAILED -> "Connection failed: ${outcome.detail}"
            PairingResult.PAIRING_FAILED -> "Pairing failed."
        }
        setStatus(text)
        Toast.makeText(this, text, Toast.LENGTH_LONG).show()
    }

    // Mesh session lifecycle -----------------------------------------------------------------

    internal fun currentMeshSession(): MeshSession? = meshSession

    /** Name of the peer the current mesh session is with (as reported in its last handshake). */
    internal fun peerDisplayName(): String = meshPeerName ?: meshPeerUuid ?: "Unknown device"

    /**
     * Takes over [session]'s authenticated transport as the new [MeshSession], replacing (and
     * closing) any previous one. Messages the old session queued while it was disconnected are
     * drained and re-sent over the new one when it is the same peer, so nothing copied during an
     * outage is lost. Callers hold [pairingInFlight], so two attaches never interleave.
     */
    private suspend fun attachMeshSession(
        session: PairingSession,
        peerUuid: String,
        peerName: String,
        peerEndpoint: Pair<String, Int>
    ) {
        val transport = session.detachTransport()
        val previous = meshSession
        val previousPeerUuid = meshPeerUuid

        // Handlers go into fromTransport itself so they are attached before its read loop
        // starts: a clip the peer sends right after reconnecting can never hit a missing handler.
        // The state handler only reads `current` once on Main, which is after this (Main) method
        // has assigned it below, before its first suspension point.
        lateinit var current: MeshSession
        current = MeshSession.fromTransport(transport, peerUuid, peerEndpoint.first, peerEndpoint.second,
            onMessageReceived = { message -> onMeshMessageReceived(message, peerName) },
            onStateChanged = { state ->
                // lifecycleScope: dropped once the Activity is destroyed.
                lifecycleScope.launch(Dispatchers.Main) { onMeshSessionStateChanged(current, state) }
            })
        meshSession = current
        meshPeerUuid = peerUuid
        meshPeerName = peerName
        meshPeerEndpoint = peerEndpoint
        meshTrustLostSession = null // only ever refers to a session this one has just replaced
        setSessionStatus("Connected to $peerName")

        if (previous == null) return
        // Close first so the old session cannot accept anything more, then drain (which waits
        // for any in-flight send on it to settle). Only carry clipboard data over to the same
        // peer: content copied while linked to one device must not leak to a different one.
        val pending = withContext(Dispatchers.IO) {
            previous.close()
            previous.drainPendingSends()
        }
        if (previousPeerUuid != peerUuid) return
        for (message in pending) {
            // MeshSession.send writes to the socket on the calling thread: keep it off Main.
            // If this session drops too, send() queues the message again, in order.
            withContext(Dispatchers.IO) { current.send(message) }
        }
    }

    private fun onMeshSessionStateChanged(session: MeshSession, state: MeshSessionState) {
        if (session !== meshSession) return // a replaced/closed session
        if (session === meshTrustLostSession) return // keep "Re-pairing required"
        val name = peerDisplayName()
        setSessionStatus(when (state) {
            MeshSessionState.CONNECTED -> "Connected to $name"
            MeshSessionState.RECONNECTING -> "Reconnecting to $name..."
            MeshSessionState.DISCONNECTED -> "Disconnected from $name"
        })
        if (state == MeshSessionState.DISCONNECTED) ensureMeshReconnect(session)
    }

    /**
     * Makes sure a reconnect loop is working on [session]. At most one loop runs; if one is
     * already running (possibly still asleep in backoff for an older, since-replaced session)
     * it is retargeted to this session and picks it up when it wakes.
     */
    private fun ensureMeshReconnect(session: MeshSession) {
        val loopRunning = meshReconnectTarget != null
        meshReconnectTarget = session
        if (!loopRunning) lifecycleScope.launch { runMeshReconnectLoop() }
    }

    /** A current session that is down and still worth reconnecting. */
    private fun needsMeshReconnect(session: MeshSession?): Boolean =
        session != null && session.state != MeshSessionState.CONNECTED && session !== meshTrustLostSession

    /**
     * UI-level reconnect orchestration (mirrors Windows' RunMeshReconnectLoopAsync).
     * [MeshSession] only probes TCP reachability; re-establishing a usable channel needs a fresh
     * TLS connection plus the trusted-reconnect handshake (identity + mutual trust, no PIN),
     * which needs our identity and paired-device store. Runs on Main, so every check-then-act
     * below is atomic with respect to the state handler and the accept loop.
     *
     * The loop keeps going until the current [meshSession] is healthy. Before every sleep and
     * after every wake/attempt it re-decides what to work on: a newer target set by
     * [ensureMeshReconnect], or — if its target was replaced (e.g. the peer reconnected to us) —
     * whatever session is current now, if that one is itself down. So a session that drops while
     * a loop for an older one is asleep is never left without a reconnect attempt.
     *
     * Only the side whose UUID sorts lower dials straight away (see [ReconnectPolicy]); a peer
     * whose trust check fails stops the loop for good.
     */
    private suspend fun runMeshReconnectLoop() {
        var target: MeshSession? = null
        var attempt = 0
        try {
            while (true) {
                if (target !== meshReconnectTarget) {
                    target = meshReconnectTarget // retargeted by a newer disconnect
                    attempt = 0
                }
                if (meshSession !== target) {
                    // Replaced while we were asleep or attempting. Adopt the replacement if it is
                    // down too; otherwise there is nothing left to do.
                    if (!needsMeshReconnect(meshSession)) return
                    target = meshSession
                    meshReconnectTarget = target
                    attempt = 0
                }
                if (!needsMeshReconnect(target)) return

                val self = selfUuid ?: return
                val peerUuid = meshPeerUuid ?: return
                val peerName = peerDisplayName()
                val isDialer = ReconnectPolicy.isDialer(self, peerUuid)
                val delayMs = ReconnectPolicy.delayBeforeAttemptMs(isDialer, attempt)
                attempt++
                delay(delayMs) // cancelled with lifecycleScope when the Activity is destroyed

                // Anything may have changed while asleep: re-decide before dialing.
                if (target !== meshReconnectTarget || meshSession !== target) continue
                if (pairingInFlight) continue // a user/incoming pairing owns the floor; retry later

                // Re-resolve every attempt: mDNS updates the peer's IP/port if it restarted or
                // moved networks; otherwise fall back to the last endpoint we connected through.
                val (ip, port) = resolvePeerEndpoint(peerUuid, meshPeerEndpoint)
                if (port <= 0) continue // listening port not known yet (incoming-only peer)

                setPairingInFlight(true)
                val result = try {
                    tryTrustedReconnect(peerUuid, peerName, ip, port)
                } finally {
                    setPairingInFlight(false)
                }

                if (result == ReconnectAttemptResult.TRUST_LOST && meshSession === target) {
                    // The peer answered as itself but the trust check failed (certificate changed,
                    // or either side removed the pairing). No amount of retrying fixes that, so
                    // this is the one background outcome worth interrupting the user for.
                    meshTrustLostSession = target
                    setSessionStatus("Re-pairing required with $peerName")
                    val text = "$peerName is no longer trusted (certificate changed or pairing removed). " +
                        "Select it and press Connect to pair again."
                    setStatus(text)
                    Toast.makeText(this, text, Toast.LENGTH_LONG).show()
                }
                // RECONNECTED / TRUST_LOST / FAILED: the checks at the top decide whether to stop.
            }
        } finally {
            meshReconnectTarget = null
        }
    }

    /**
     * One silent trusted-reconnect attempt: connect, exchange identities, and proceed only if
     * the peer is the expected one and both sides still trust each other. Never prompts the user
     * — a peer that is no longer trusted needs an explicit re-pair via the Connect button.
     * Returns [ReconnectAttemptResult.TRUST_LOST] only when the expected peer answered but trust
     * failed on either side (permanent); network errors, a busy peer or a different device on
     * that endpoint are [ReconnectAttemptResult.FAILED] (worth retrying).
     */
    private suspend fun tryTrustedReconnect(
        peerUuid: String,
        peerName: String,
        ip: String,
        port: Int
    ): ReconnectAttemptResult {
        val id = identity ?: return ReconnectAttemptResult.FAILED
        val uuid = selfUuid ?: return ReconnectAttemptResult.FAILED
        val session = try {
            PairingSession.connect(id, ip, port)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            return ReconnectAttemptResult.FAILED
        }

        session.use {
            try {
                it.exchangeIdentity(uuid, selfName)
                // Someone else now on that IP:port (e.g. DHCP reuse): not our peer, keep trying.
                if (it.peerUuid != peerUuid) return ReconnectAttemptResult.FAILED
                // Our peer, but its certificate no longer matches our record (or the record is gone).
                if (!it.isPeerTrusted(dao(), peerUuid)) return ReconnectAttemptResult.TRUST_LOST
                // exchangeTrust returns false only if the peer explicitly answered "not trusted";
                // a busy/closed peer throws instead and lands in the catch below.
                if (!it.exchangeTrust(localTrustsPeer = true)) return ReconnectAttemptResult.TRUST_LOST
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                return ReconnectAttemptResult.FAILED
            }

            // Same bookkeeping PairingSession.run does on a trusted reconnect.
            val name = it.peerName ?: peerName
            try {
                dao().findByUuid(peerUuid)?.let { record ->
                    dao().upsert(record.copy(name = name, lastIp = it.peerIp))
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                // Bookkeeping only; the channel is authenticated and usable regardless.
            }

            attachMeshSession(it, peerUuid, name, ip to port)
            setStatus("Reconnected to $name (trusted).")
            return ReconnectAttemptResult.RECONNECTED
        }
    }

    /** MeshSession.close() closes its TLS socket inline (network I/O): never do that on Main. */
    private fun closeInBackground(session: MeshSession) {
        Thread({ session.close() }, "mesh-session-close").apply { isDaemon = true }.start()
    }

    // Clipboard ------------------------------------------------------------------------------

    internal fun clipboardHistoryDao(): ClipboardHistoryDao =
        MeshDatabase.getInstance(this).clipboardHistoryDao()

    /** Delivers the stored history (newest first) on Main, after every write queued before it. */
    internal fun loadClipboardHistory(onLoaded: (List<ClipboardEntry>) -> Unit) {
        historyOps.trySend(HistoryOp.Load(onLoaded))
    }

    internal fun copyTextToClipboard(text: String) {
        // Our own write must not come back through the watcher and get re-pushed.
        clipboardWatcher.suppressNextChange(CLIPBOARD_SELF_WRITE_SUPPRESS_MS)
        getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("mesh", text))
    }

    private fun onLocalTextCopied(text: String) {
        // Called on Main by the ClipboardManager listener.
        if (text.length > MAX_CLIPBOARD_TEXT_LENGTH) return
        val entry = ClipboardEntry(text = text, source = "local", timestamp = System.currentTimeMillis())
        historyOps.trySend(HistoryOp.Add(entry, pushToPeer = true))
    }

    private fun onMeshMessageReceived(message: MeshMessage, peerName: String) {
        // Called on MeshSession's read loop (IO thread); the channel is thread-safe.
        if (message !is ClipboardPush) return
        if (message.text.length > MAX_CLIPBOARD_TEXT_LENGTH) return
        val entry = ClipboardEntry(text = message.text, source = peerName, timestamp = message.timestamp)
        historyOps.trySend(HistoryOp.Add(entry, pushToPeer = false))
    }

    private suspend fun runHistoryConsumer() {
        val historyDao = clipboardHistoryDao()
        for (op in historyOps) {
            when (op) {
                is HistoryOp.Load -> {
                    val entries = try {
                        historyDao.getAll()
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        setStatus("Could not load clipboard history: ${e.message}")
                        emptyList()
                    }
                    op.onLoaded(entries)
                }
                is HistoryOp.Add -> {
                    try {
                        historyDao.insertAndEvict(op.entry)
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        setStatus("Could not save clipboard history: ${e.message}")
                        continue
                    }
                    clipboardFragment()?.onHistoryEntryAdded(op.entry)
                    if (op.pushToPeer) {
                        // While disconnected this queues inside the session and is drained and
                        // re-sent by attachMeshSession on reconnect. send() does socket I/O.
                        val session = meshSession ?: continue
                        withContext(Dispatchers.IO) { session.send(ClipboardPush(op.entry.timestamp, op.entry.text)) }
                    }
                }
            }
        }
    }

    // PairingPrompt ---------------------------------------------------------------------------

    override suspend fun confirmCertificateChanged(peerName: String): Boolean =
        PinConfirmDialog.confirmCertificateChanged(this, peerName)

    override suspend fun confirmPin(peerName: String, pin: String): Boolean =
        PinConfirmDialog.confirmPin(this, peerName, pin)
}
