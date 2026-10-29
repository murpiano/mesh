package com.mesh.ui

import android.Manifest
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.util.DisplayMetrics
import android.widget.Toast
import androidx.activity.addCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.fragment.app.Fragment
import androidx.lifecycle.lifecycleScope
import com.mesh.BuildConfig
import com.mesh.R
import com.mesh.core.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import java.security.PrivateKey
import java.security.cert.X509Certificate
import java.util.UUID

/**
 * Tab host (Devices / Chat) and owner of everything cross-cutting: identity, discovery,
 * the incoming-pairing accept loop, the pairing flows, the persistent [MeshSession] to the
 * paired peer with its reconnect orchestration, and the chat history for that peer (sent
 * messages, received messages, and cross-device history sync on every attach).
 *
 * Threading: everything below runs on Main unless stated otherwise. MeshSession callbacks arrive
 * on its IO threads and are marshalled onto Main via lifecycleScope.launch(Dispatchers.Main).
 */
class MainActivity : AppCompatActivity(), PairingPrompt {
    companion object {
        /** Chat texts longer than this are neither stored nor sent. Shared with the desktop app
         * through MeshMessage, so both ends draw the line in the same place. */
        private const val MAX_CHAT_TEXT_LENGTH = MeshMessage.MAX_TEXT_CHARS
    }

    private enum class ReconnectAttemptResult { RECONNECTED, FAILED, TRUST_LOST }

    // Devices-tab state (rendered by DevicesFragment) --------------------------------------

    // mDNS-visible devices right now, keyed implicitly by uuid; merged with the paired-device
    // store in rebuildDeviceRows() to produce deviceRows. Not rendered directly any more.
    private val devices = mutableListOf<DiscoveredDevice>()
    internal var deviceRows: List<DeviceRow> = emptyList()
        private set
    internal var statusMessage: String = ""
        private set
    internal var mdnsErrorVisible = false
        private set

    // The device a pair/connect attempt is currently in flight for (shows "..." on its Connect
    // button); null when nothing is connecting. Dialer-only placeholder dialog, shown just for
    // first-time pairing (the dialer never sees the PIN, so there is nothing to show until the
    // listener approves — see PairingPrompt.enterPin below).
    private var connectingUuid: String? = null
    private var pendingConnectingDialog: AlertDialog? = null
    // Dialer side only: the outgoing PairingSession currently in flight, so a Cancel tap on the
    // "Connecting..." placeholder (before the listener has even answered) can abort it by
    // closing the socket, same mechanism as pendingIncomingSession on the listener side.
    private var pendingDialerSession: PairingSession? = null
    // Listener side only: the PIN confirm dialog for the incoming pairing attempt currently in
    // flight, kept open (showing "Connecting...") after Allow is tapped instead of dismissing
    // right away. Dismissed once handleIncoming's run() call actually finishes.
    private var pendingPinDialog: AlertDialog? = null
    // Listener side only: the incoming PairingSession currently in flight, so a Cancel tap on
    // the "Connecting..." PIN dialog (after the user already tapped Allow) can abort it by
    // closing the socket — PairingSession.run's own exception handling turns that into a
    // PAIRING_FAILED outcome instead of crashing.
    private var pendingIncomingSession: PairingSession? = null

    private lateinit var discovery: MeshDiscovery
    private var identity: Pair<X509Certificate, PrivateKey>? = null
    private var selfUuid: String? = null
    // A debug build is the separate "Mesh Dev" app; the suffix tells it apart on the other side.
    private val selfName: String = if (BuildConfig.DEBUG) "${Build.MODEL} (dev)" else Build.MODEL
    private var listener: PairingListener? = null

    // Notifications aren't required for the foreground service to work, only to show it — the
    // service starts regardless of whether this is granted.
    private val notificationPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) {}

    // All pairing work (user Connect, incoming, background reconnect attempts) runs on Main, so
    // a plain flag keeps a double-tap or an incoming connection from racing an attempt that is
    // already in flight. It also serialises every attachMeshSession call.
    private var pairingInFlight = false

    // Mesh session (rendered by ChatFragment) -----------------------------------------------

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
    // Set by disconnectMeshSession(); blocks the reconnect loop from restarting until the user
    // connects again (manually, or by accepting an incoming pairing). Cleared whenever a session
    // actually attaches, so a later real drop still reconnects normally.
    private var userDisconnected = false
    // When the current/most recent session was attached, so the reconnect loop can tell a quick
    // flap (reset nothing — keep escalating) from a session that actually ran for a while (reset
    // backoff — this is a fresh problem). See ReconnectPolicy.FLAP_RESET_THRESHOLD_MS.
    private var meshSessionAttachedAt: Long = 0L

    // Device page --------------------------------------------------------------------------

    /** A device's actual current link state, shown as [DeviceFragment]'s status stripe — not the
     * same as the "Состояние" column on Devices (that shows Connecting too; this doesn't, since
     * a device page is only reachable for an already-paired device). */
    internal enum class ConnectionStatus(val colorRes: Int, val label: String) {
        CONNECTED(R.color.conn_connected, "connected"),
        DISCONNECTED(R.color.conn_disconnected, "disconnect"),
        UNPAIRED(R.color.conn_unpaired, "unpaired"),
        OFFLINE(R.color.conn_offline, "offline")
    }

    internal fun connectionStatusFor(uuid: String): ConnectionStatus {
        val row = deviceRows.firstOrNull { it.uuid == uuid } ?: return ConnectionStatus.UNPAIRED
        return when {
            !row.isPaired -> ConnectionStatus.UNPAIRED
            row.isConnectedNow -> ConnectionStatus.CONNECTED
            row.isOnline -> ConnectionStatus.DISCONNECTED
            else -> ConnectionStatus.OFFLINE
        }
    }

    /** Opens a paired device's page (raw/design/2.jpg) — plain navigation, replacing whatever is
     * currently shown. Only one device page is ever open; see [openDevicesPage] to go back. */
    internal fun openDevicePage(uuid: String, name: String) = showFragment(DeviceFragment.newInstance(uuid, name))

    internal fun openDevicesPage() = showFragment(DevicesFragment())

    /** The gear: the Settings screen, which so far holds the blacklist. */
    internal fun openSettingsPage() = showFragment(SettingsFragment())

    // Chat -------------------------------------------------------------------------------------

    internal val selfDeviceUuid: String? get() = selfUuid

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        DebugLog.init(this)

        discovery = MeshDiscovery(this)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) !=
                PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }

        openDevicesPage()
        onBackPressedDispatcher.addCallback(this) {
            // A device page's own "back" target is the device list, not closing the app.
            if (deviceFragment() != null || settingsFragment() != null) openDevicesPage() else {
                isEnabled = false
                onBackPressedDispatcher.onBackPressed()
                isEnabled = true
            }
        }

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
            rebuildDeviceRows()
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
        stopCast("The phone closed Mesh.")
        listener?.close() // unblocks accept(); the loop exits
        discovery.stop()
        meshSession?.let(::closeInBackground)
        meshSession = null
        MeshConnectionService.stop(this)
    }

    private fun showFragment(fragment: Fragment) {
        supportFragmentManager.beginTransaction()
            .replace(R.id.fragmentContainer, fragment)
            .commit()
    }

    private fun devicesFragment() =
        supportFragmentManager.findFragmentById(R.id.fragmentContainer) as? DevicesFragment

    private fun deviceFragment() =
        supportFragmentManager.findFragmentById(R.id.fragmentContainer) as? DeviceFragment

    private fun settingsFragment() =
        supportFragmentManager.findFragmentById(R.id.fragmentContainer) as? SettingsFragment

    /** Reloads the currently-visible chat section if its device page is showing [peerUuid]. A
     * no-op otherwise — that page's chat section reloads fresh the next time it's shown. */
    private fun notifyChatChanged(peerUuid: String) {
        deviceFragment()?.let { if (it.peerUuid == peerUuid) it.chatChild()?.reloadFromDb() }
    }

    private fun setStatus(text: String) {
        statusMessage = text
        devicesFragment()?.renderStatus()
    }

    private fun setPairingInFlight(value: Boolean) {
        pairingInFlight = value
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
        rebuildDeviceRows()
    }

    private fun onDeviceLost(uuid: String) {
        if (devices.removeAll { it.uuid == uuid }) rebuildDeviceRows()
    }

    /**
     * Rebuilds [deviceRows] from the paired-device store (every paired device, online or not)
     * merged with [devices] (currently mDNS-visible, paired or not) plus current session state.
     * Mirrors Windows' MainWindow.xaml.cs RebuildDeviceRows. Called whenever any input changes:
     * discovery, pairing, session state.
     */
    private fun rebuildDeviceRows() {
        lifecycleScope.launch {
            val paired = try { dao().all() } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                emptyList()
            }
            val pairedByUuid = paired.associateBy { it.uuid }
            val uuids = mutableSetOf<String>()
            uuids.addAll(pairedByUuid.keys)
            uuids.addAll(devices.map { it.uuid })
            selfUuid?.let { uuids.remove(it) }
            // A blacklisted device is not on this screen at all — the Settings screen is where
            // it can be found and let back in.
            blockedDevices().forEach { uuids.remove(it.uuid) }

            val rows = uuids.map { uuid ->
                val discovered = devices.firstOrNull { it.uuid == uuid }
                val record = pairedByUuid[uuid]
                DeviceRow(
                    uuid = uuid,
                    name = record?.name ?: discovered?.name ?: uuid,
                    isOnline = discovered != null,
                    isPaired = record != null,
                    isConnectedNow = record != null && meshPeerUuid == uuid && meshSession != null,
                    isConnecting = connectingUuid == uuid,
                    ipAddress = discovered?.ipAddress ?: record?.lastIp ?: "",
                    port = discovered?.port ?: 0,
                    supportsCast = discovered?.supportsCast == true
                )
            }.sortedBy { it.name.lowercase() }

            deviceRows = rows
            devicesFragment()?.renderDeviceList()
            deviceFragment()?.refreshConnectionState()
        }
    }

    /** The peer's listening endpoint as currently advertised over mDNS, if seen. */
    private fun resolvePeerEndpoint(peerUuid: String, fallback: Pair<String, Int>): Pair<String, Int> =
        devices.firstOrNull { it.uuid == peerUuid }?.let { it.ipAddress to it.port } ?: fallback

    // Pairing --------------------------------------------------------------------------------

    private suspend fun handleIncoming(session: PairingSession) {
        val uuid = selfUuid ?: return
        if (pairingInFlight) return // busy: the loop closes this session; peer sees a failure
        setPairingInFlight(true)
        pendingIncomingSession = session
        try {
            val outcome = session.run(uuid, selfName, dao(), this, isBlocked = ::isDeviceBlocked)
            val peerName = outcome.peerName ?: session.peerIp
            // PairingSession.run only returns RECONNECTED after the peer's certificate already
            // matched our trust record, so it is always a background reconnect of an
            // already-paired device, never a new pairing. Report it quietly (status text only),
            // the same way our own outgoing background reconnect does, instead of a toast on
            // every network blip. (Previously this also required meshSession/meshPeerUuid to
            // already point at this peer, which missed the reconnect right after an app restart
            // — and any other case where that bookkeeping lagged behind reality — showing the
            // toast every single time the peer reconnected in a loop.)
            setStatus(when (outcome.result) {
                PairingResult.PAIRED -> "Paired with $peerName."
                PairingResult.RECONNECTED -> "Reconnected to $peerName (trusted)."
                PairingResult.CONNECTION_FAILED -> "Connection failed: ${outcome.detail}"
                PairingResult.PAIRING_FAILED -> "Pairing failed: ${outcome.detail}"
            })
            if (outcome.result == PairingResult.PAIRED || outcome.result == PairingResult.RECONNECTED) {
                val peerUuid = outcome.peerUuid!!
                // The accepted socket came from the peer's ephemeral port, so its listening
                // endpoint (needed to reconnect later) comes from mDNS when we have it.
                val endpoint = resolvePeerEndpoint(peerUuid, session.peerIp to 0)
                attachMeshSession(session, peerUuid, peerName, endpoint)
                // Only a first-time pairing auto-opens a tab — a silent background/manual
                // reconnect to an already-paired device must not pop UI on its own.
                if (outcome.result == PairingResult.PAIRED) openDevicePage(peerUuid, peerName)
            }
        } finally {
            setPairingInFlight(false)
            pendingIncomingSession = null
            pendingPinDialog?.dismiss()
            pendingPinDialog = null
            rebuildDeviceRows()
        }
    }

    /** Pair button on an unpaired row: first-time pairing, dialer side, asymmetric PIN entry. */
    /** Aborts an outgoing attempt still in flight (the row's switch flicked back off). Closing
     * the socket is what stops it: run() fails, and connectOrPair's finally clears the
     * Connecting state. Same mechanism as the Cancel button on the first-time pairing dialog. */
    internal fun cancelPendingConnect() {
        pendingDialerSession?.close()
    }

    internal fun pairDevice(uuid: String) = connectOrPair(uuid, isFirstTimePair = true)

    /** Connect button on an already-paired row: manual trusted reconnect, listener gets a
     * lightweight "allow?" prompt (see [confirmReconnect]), no PIN. */
    internal fun connectDevice(uuid: String) = connectOrPair(uuid, isFirstTimePair = false)

    /**
     * Shared by [pairDevice] and [connectDevice]: both are the dialer side of a manual,
     * human-initiated Connect (`isManualConnect = true`), which is what makes the already-paired
     * case prompt the listener at all — background reconnects never do. Mirrors Windows'
     * ConnectOrPairAsync.
     */
    private fun connectOrPair(uuid: String, isFirstTimePair: Boolean) {
        val id = identity ?: return
        val self = selfUuid ?: return
        if (pairingInFlight) return
        val row = deviceRows.firstOrNull { it.uuid == uuid } ?: return
        if (row.port <= 0) return // not currently reachable over mDNS
        setPairingInFlight(true)
        connectingUuid = uuid
        rebuildDeviceRows()
        lifecycleScope.launch {
            if (isFirstTimePair) {
                // Nothing to show yet: the dialer never sees the PIN until the listener
                // approves (see enterPin below), which replaces this placeholder.
                pendingConnectingDialog = PinConfirmDialog.showConnecting(this@MainActivity, row.name) {
                    pendingDialerSession?.close()
                }
            }
            try {
                val session = try {
                    PairingSession.connect(id, row.ipAddress, row.port)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    setStatus("Connection failed: ${e.message ?: e.javaClass.simpleName}")
                    return@launch
                }
                pendingDialerSession = session
                // On success the transport is detached into a MeshSession, which makes use{}'s
                // close a no-op; on every other outcome it closes the socket as before.
                session.use {
                    val outcome = it.run(self, selfName, dao(), this@MainActivity, isManualConnect = true,
                        isBlocked = ::isDeviceBlocked)
                    val peerName = outcome.peerName ?: row.name
                    // No success dialog by design — the row's own Connected/greyed state is the
                    // confirmation; only failures get a status-line message.
                    when (outcome.result) {
                        PairingResult.PAIRED, PairingResult.RECONNECTED -> {
                            attachMeshSession(it, outcome.peerUuid!!, peerName, row.ipAddress to row.port)
                            if (outcome.result == PairingResult.PAIRED) openDevicePage(outcome.peerUuid, peerName)
                        }
                        PairingResult.CONNECTION_FAILED -> setStatus("Connection failed: ${outcome.detail}")
                        PairingResult.PAIRING_FAILED -> setStatus("Pairing failed: ${outcome.detail}")
                    }
                }
            } finally {
                pendingConnectingDialog?.dismiss()
                pendingConnectingDialog = null
                pendingDialerSession = null
                connectingUuid = null
                setPairingInFlight(false)
                rebuildDeviceRows()
            }
        }
    }

    /** ✕ button on a paired row: removes the local trust record only — no network notification
     * to the peer (see PairedDeviceDao.deleteByUuid). Disconnects first if currently connected
     * to this device. */
    internal fun unpairDevice(uuid: String) {
        val row = deviceRows.firstOrNull { it.uuid == uuid } ?: return
        lifecycleScope.launch {
            val confirmed = PinConfirmDialog.show(
                this@MainActivity, "Unpair", "Remove pairing with ${row.name}?", "Unpair", "Cancel")
            if (!confirmed) return@launch
            if (meshPeerUuid == uuid) disconnectMeshSession()
            try {
                dao().deleteByUuid(uuid)
                chatMessageDao().deleteForPeer(uuid)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                // Best effort; the row simply stays paired if this failed.
            }
            // Nothing meaningful left to show for this device; back to the list, a re-pair
            // opens its page fresh.
            if (deviceFragment()?.peerUuid == uuid) openDevicesPage()
            rebuildDeviceRows()
        }
    }

    private fun dao() = MeshDatabase.getInstance(this).pairedDeviceDao()

    private fun blockedDao() = MeshDatabase.getInstance(this).blockedDeviceDao()

    /** Whether this device is blacklisted. Passed into every pairing attempt, incoming and
     * outgoing, so a blocked peer is hung up on as soon as it says who it is. */
    private suspend fun isDeviceBlocked(uuid: String): Boolean =
        try { blockedDao().isBlocked(uuid) } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            false
        }

    /** Blocks the device, after confirmation: the live session (or the attempt still being made)
     * ends, the pairing and the chat go the way [unpairDevice] takes them, and the device drops
     * off the Devices screen until it is let back in from Settings. */
    internal fun blacklistDevice(uuid: String) {
        val row = deviceRows.firstOrNull { it.uuid == uuid } ?: return
        lifecycleScope.launch {
            val confirmed = PinConfirmDialog.show(
                this@MainActivity, "Blacklist",
                "Block ${row.name}? It leaves the device list and its connections are refused.",
                "Block", "Cancel")
            if (!confirmed) return@launch
            if (row.isConnecting) cancelPendingConnect()
            if (meshPeerUuid == uuid) disconnectMeshSession()
            try {
                dao().deleteByUuid(uuid)
                chatMessageDao().deleteForPeer(uuid)
                blockedDao().upsert(BlockedDevice(uuid, row.name, System.currentTimeMillis()))
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                // Best effort; the device simply stays listed if this failed.
            }
            if (deviceFragment()?.peerUuid == uuid) openDevicesPage()
            rebuildDeviceRows()
            settingsFragment()?.renderBlockedList()
        }
    }

    /** Settings screen: the blacklist, newest name order, for [SettingsFragment]. */
    internal suspend fun blockedDevices(): List<BlockedDevice> =
        try { blockedDao().all() } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            emptyList()
        }

    /** Settings screen: lets a blocked device back in. It comes back as an ordinary unpaired
     * device — blocking dropped the pairing, so the two have to pair again. */
    internal fun unblockDevice(uuid: String) {
        lifecycleScope.launch {
            try { blockedDao().deleteByUuid(uuid) } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                return@launch
            }
            settingsFragment()?.renderBlockedList()
            rebuildDeviceRows()
        }
    }

    // Mesh session lifecycle -----------------------------------------------------------------

    internal fun currentMeshSession(): MeshSession? = meshSession

    /** Name of the peer the current mesh session is with (as reported in its last handshake). */
    internal fun peerDisplayName(): String = meshPeerName ?: meshPeerUuid ?: "Unknown device"

    /**
     * Closes the current mesh session on purpose and stops the reconnect loop. No further
     * background reconnect happens for this peer until the user reconnects (Connect, or an
     * incoming trusted-reconnect from the peer).
     */
    internal fun disconnectMeshSession() {
        val session = meshSession ?: return
        stopCast("The connection to the device ended.")
        userDisconnected = true
        meshSession = null
        meshReconnectTarget = null
        setStatus("Not connected")
        rebuildDeviceRows()
        MeshConnectionService.stop(this)
        lifecycleScope.launch {
            // Best-effort: tells the peer this is intentional, so its own reconnect loop stops
            // too instead of dialing back in later and silently undoing the disconnect. If this
            // never arrives (peer already gone), the peer's own idle timeout notices the drop
            // regardless.
            withContext(Dispatchers.IO) {
                try { withTimeout(2_000) { session.send(Goodbye) } } catch (e: Exception) { /* best effort */ }
                session.close() // cancels its loops without firing onStateChanged
            }
        }
    }

    /** The peer disconnected on purpose (sent [Goodbye]): stop trying to reconnect to them
     * until the user reconnects, same as our own disconnectMeshSession(). */
    private fun onPeerGoodbye(session: MeshSession) {
        if (session !== meshSession) return // a replaced/closed session
        stopCast("The connection to the device ended.", notifyPeer = false)
        userDisconnected = true
        meshSession = null
        meshReconnectTarget = null
        setStatus("Disconnected by $meshPeerName")
        rebuildDeviceRows()
        MeshConnectionService.stop(this)
        session.close()
    }

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
            onMessageReceived = { message -> onMeshMessageReceived(message, peerUuid, peerName, current) },
            onStateChanged = { state ->
                // lifecycleScope: dropped once the Activity is destroyed.
                lifecycleScope.launch(Dispatchers.Main) { onMeshSessionStateChanged(current, state) }
            })
        meshSession = current
        meshPeerUuid = peerUuid
        meshPeerName = peerName
        meshPeerEndpoint = peerEndpoint
        meshTrustLostSession = null // only ever refers to a session this one has just replaced
        userDisconnected = false // a live session now exists; a later drop should reconnect again
        meshSessionAttachedAt = System.currentTimeMillis()
        setStatus("Connected to $peerName")
        rebuildDeviceRows()
        // Keeps this process at foreground importance so backgrounding the app (MIUI and
        // other aggressive battery managers especially) doesn't freeze it or cut its network
        // access mid-session.
        MeshConnectionService.start(this, peerName)

        val syncEntries = loadChatHistory(peerUuid).map { ChatSyncEntry(it.msgId, it.senderUuid, it.timestamp, it.text) }
        withContext(Dispatchers.IO) {
            try { current.send(ChatHistorySync(syncEntries)) } catch (e: Exception) { /* best effort */ }
        }

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
        setStatus(when (state) {
            MeshSessionState.CONNECTED -> "Connected to $name"
            MeshSessionState.RECONNECTING -> "Reconnecting to $name..."
            MeshSessionState.DISCONNECTED -> "Disconnected from $name"
        })
        rebuildDeviceRows()
        if (state != MeshSessionState.CONNECTED) {
            // A cast rides on the control plane for its settings and its stop; without it there
            // is nothing to steer the cast with.
            stopCast("The connection to the device ended.", notifyPeer = false)
        }
        if (state == MeshSessionState.DISCONNECTED) ensureMeshReconnect(session)
    }

    /**
     * Makes sure a reconnect loop is working on [session]. At most one loop runs; if one is
     * already running (possibly still asleep in backoff for an older, since-replaced session)
     * it is retargeted to this session and picks it up when it wakes.
     */
    private fun ensureMeshReconnect(session: MeshSession) {
        if (userDisconnected) return // the user closed this on purpose; wait for them to reconnect
        val loopRunning = meshReconnectTarget != null
        meshReconnectTarget = session
        if (!loopRunning) lifecycleScope.launch { runMeshReconnectLoop() }
    }

    /** A current session that is down and still worth reconnecting. */
    private fun needsMeshReconnect(session: MeshSession?): Boolean =
        session != null && session.state != MeshSessionState.CONNECTED &&
            session !== meshTrustLostSession && !userDisconnected

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
                    // Only treat this as a fresh problem (reset backoff) if the session we're
                    // now chasing had actually been up for a while. A session that flapped —
                    // connected and died again within FLAP_RESET_THRESHOLD_MS — is the same
                    // ongoing episode; keep escalating, or a flaky link causes a 1 s-interval
                    // reconnect storm that never slows down.
                    if (System.currentTimeMillis() - meshSessionAttachedAt > ReconnectPolicy.FLAP_RESET_THRESHOLD_MS) {
                        attempt = 0
                    }
                }
                if (meshSession !== target) {
                    // Replaced while we were asleep or attempting. Adopt the replacement if it is
                    // down too; otherwise there is nothing left to do.
                    if (!needsMeshReconnect(meshSession)) return
                    target = meshSession
                    meshReconnectTarget = target
                    if (System.currentTimeMillis() - meshSessionAttachedAt > ReconnectPolicy.FLAP_RESET_THRESHOLD_MS) {
                        attempt = 0
                    }
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
                    val text = "$peerName is no longer trusted (certificate changed or pairing removed). " +
                        "Press Connect to pair again."
                    setStatus(text)
                    rebuildDeviceRows()
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
                // Keeps the wire protocol in sync with PairingSession.run, which reads this same
                // step right after exchangeIdentity. This path drives the protocol manually (it
                // never calls run()) because a background reconnect must stay completely silent
                // — false here means "not a manual Connect", so the listener never prompts.
                it.exchangeManualIntent(isManualConnect = false)
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

    // Chat -------------------------------------------------------------------------------------

    private fun chatMessageDao(): ChatMessageDao = MeshDatabase.getInstance(this).chatMessageDao()

    /** True while [peerUuid] is the one single peer currently connected — tabs are just views
     * (see the device-tabs design: only Connect on the Devices tab changes this). */
    internal fun isActivePeer(peerUuid: String): Boolean = meshPeerUuid == peerUuid && meshSession != null

    internal suspend fun loadChatHistory(peerUuid: String): List<ChatMessage> = try {
        chatMessageDao().getAllForPeer(peerUuid)
    } catch (e: CancellationException) {
        throw e
    } catch (e: Exception) {
        setStatus("Could not load chat history: ${e.message}")
        emptyList()
    }

    /** Send button: stores the message locally, re-renders, then pushes it to the peer. Only
     * called while [peerUuid] is active — [ChatFragment] disables Send otherwise. */
    internal fun sendChatMessage(peerUuid: String, text: String) {
        if (text.isEmpty()) return
        if (text.length > MAX_CHAT_TEXT_LENGTH) {
            Toast.makeText(this, "Message too long", Toast.LENGTH_LONG).show()
            return
        }
        val session = meshSession?.takeIf { meshPeerUuid == peerUuid } ?: return
        val selfId = selfUuid ?: return
        lifecycleScope.launch {
            val id = UUID.randomUUID().toString()
            val timestamp = System.currentTimeMillis()
            val message = ChatMessage(
                msgId = id, peerUuid = peerUuid, senderUuid = selfId, senderLabel = selfName,
                text = text, timestamp = timestamp)
            try {
                chatMessageDao().insertAndEvict(message)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                setStatus("Could not save chat message: ${e.message}")
                return@launch
            }
            notifyChatChanged(peerUuid)
            withContext(Dispatchers.IO) { session.send(ChatSend(id, timestamp, text)) }
        }
    }

    /** Edits one of our own already-sent messages. Always applied locally; also sent live to the
     * peer if they're the one currently connected (an edit made while that peer is offline never
     * gets retried/queued). */
    internal fun editChatMessage(peerUuid: String, msgId: String, newText: String) {
        if (newText.isEmpty() || newText.length > MAX_CHAT_TEXT_LENGTH) return
        lifecycleScope.launch {
            try {
                chatMessageDao().updateText(peerUuid, msgId, newText)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                setStatus("Could not edit chat message: ${e.message}")
                return@launch
            }
            notifyChatChanged(peerUuid)
            val session = meshSession?.takeIf { meshPeerUuid == peerUuid } ?: return@launch
            withContext(Dispatchers.IO) { session.send(ChatEdit(msgId, newText)) }
        }
    }

    /** Deletes one of our own already-sent messages. Same best-effort delivery caveat as
     * [editChatMessage]. */
    internal fun deleteChatMessage(peerUuid: String, msgId: String) {
        lifecycleScope.launch {
            try {
                chatMessageDao().deleteMessage(peerUuid, msgId)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                setStatus("Could not delete chat message: ${e.message}")
                return@launch
            }
            notifyChatChanged(peerUuid)
            val session = meshSession?.takeIf { meshPeerUuid == peerUuid } ?: return@launch
            withContext(Dispatchers.IO) { session.send(ChatDelete(msgId)) }
        }
    }

    internal fun copyTextToClipboard(text: String) {
        getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("mesh", text))
    }

    // Casting -------------------------------------------------------------------------------

    // One cast at a time, always ours to the connected peer: the phone is the sender, the PC
    // the receiver. Everything below is torn down together by stopCast().
    private var castId: String? = null
    private var castPeerUuid: String? = null
    private var castConsent: Intent? = null
    private var castReady: CastReady? = null
    private var castProjection: MediaProjection? = null
    private var castChannel: CastChannel? = null
    private var caster: ScreenCaster? = null

    /** The settings both devices last agreed on, or null while no cast runs. The controls on the
     * device page render from this. */
    internal var castSettings: CastSettings? = null
        private set

    private val castConsentLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            val data = result.data
            if (result.resultCode != RESULT_OK || data == null) {
                stopCast("The phone did not allow sharing its screen.")
                return@registerForActivityResult
            }
            castConsent = data
            beginCastIfReady()
        }

    // The playback-capture API counts as recording, so the sound needs this. A refusal only
    // costs the audio: the picture keeps going.
    private val castAudioPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) caster?.setAudioEnabled(true)
            else {
                Toast.makeText(this, "Mesh may not capture this phone's sound", Toast.LENGTH_LONG).show()
                castSettings?.let { applyCastSettings(it.copy(audio = false), fromPeer = false) }
            }
        }

    /** True when the peer announced in its mDNS record that it understands casting. An older
     * build would close the connection on the cast messages, so it is never offered. */
    internal fun peerSupportsCast(uuid: String): Boolean =
        deviceRows.firstOrNull { it.uuid == uuid }?.supportsCast == true

    /** True while our cast to [uuid] runs. */
    internal fun isCasting(uuid: String): Boolean = castPeerUuid == uuid && castId != null

    /**
     * Starts a cast of this phone's screen to the connected peer: tells it we want to cast,
     * asks the user for the system's capture consent, and waits for both answers before the
     * encoder and the media plane come up (see [beginCastIfReady]).
     */
    internal fun startCast(peerUuid: String) {
        if (castId != null) return
        val session = meshSession?.takeIf { meshPeerUuid == peerUuid } ?: return
        if (!peerSupportsCast(peerUuid)) return

        val id = UUID.randomUUID().toString()
        castId = id
        castPeerUuid = peerUuid
        castConsent = null
        castReady = null
        refreshCastControls()

        lifecycleScope.launch {
            withContext(Dispatchers.IO) { runCatching { session.send(CastStart(id)) } }
        }

        val manager = getSystemService(MediaProjectionManager::class.java)
        castConsentLauncher.launch(manager.createScreenCaptureIntent())
    }

    /** Stops our cast, tells the peer why, and releases the encoder, the media plane and the
     * foreground service. Safe to call when no cast runs. */
    internal fun stopCast(reason: String = "The phone stopped the cast.", notifyPeer: Boolean = true) {
        val id = castId ?: return
        val session = meshSession?.takeIf { meshPeerUuid == castPeerUuid }

        castId = null
        castPeerUuid = null
        castConsent = null
        castReady = null
        castSettings = null

        caster?.stop()
        caster = null
        castProjection = null // ScreenCaster.stop() already released it
        runCatching { castChannel?.close() }
        castChannel = null
        CastService.stop(this)
        refreshCastControls()

        if (notifyPeer && session != null) lifecycleScope.launch {
            withContext(Dispatchers.IO) { runCatching { session.send(CastStop(id, reason)) } }
        }
    }

    /**
     * A control on this phone moved: apply the whole record here and send it to the peer. The
     * record is replicated whole on every change from whichever side made it, so there is
     * nothing to merge.
     */
    internal fun updateCastSettings(settings: CastSettings) = applyCastSettings(settings, fromPeer = false)

    private fun applyCastSettings(settings: CastSettings, fromPeer: Boolean) {
        val id = castId ?: return
        val previous = castSettings
        castSettings = settings

        if (previous?.quality != settings.quality) {
            val metrics = screenMetrics()
            caster?.applyProfile(CastQuality.profileFor(settings.quality, metrics.widthPixels, metrics.heightPixels))
        }
        if (previous?.audio != settings.audio) applyCastAudio(settings.audio)

        refreshCastControls()

        if (!fromPeer) {
            val session = meshSession?.takeIf { meshPeerUuid == castPeerUuid } ?: return
            lifecycleScope.launch {
                withContext(Dispatchers.IO) { runCatching { session.send(CastSettingsUpdate(id, settings)) } }
            }
        }
    }

    private fun applyCastAudio(enabled: Boolean) {
        if (!enabled) {
            caster?.setAudioEnabled(false)
            return
        }
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO)
            == PackageManager.PERMISSION_GRANTED
        ) {
            caster?.setAudioEnabled(true)
        } else {
            castAudioPermissionLauncher.launch(Manifest.permission.RECORD_AUDIO)
        }
    }

    /** Both halves are in: the peer's media listener and the user's consent. Brings up the
     * foreground service (Android 14 and newer refuse a projection without one), the media
     * plane and the encoder. */
    private fun beginCastIfReady() {
        val id = castId ?: return
        val ready = castReady?.takeIf { it.castId == id } ?: return
        val consent = castConsent ?: return
        val identityPair = identity ?: return
        val peerUuid = castPeerUuid ?: return
        if (caster != null) return

        CastService.start(this, peerDisplayName())

        lifecycleScope.launch {
            val projection = try {
                getSystemService(MediaProjectionManager::class.java)
                    .getMediaProjection(RESULT_OK, consent)
            } catch (e: Exception) {
                stopCast("The phone could not start sharing its screen: ${e.message}")
                return@launch
            }
            if (projection == null) {
                stopCast("The phone could not start sharing its screen.")
                return@launch
            }
            castProjection = projection

            val endpoint = resolvePeerEndpoint(peerUuid, meshPeerEndpoint)
            val channel = try {
                CastChannel.connect(identityPair, endpoint.first, ready.port, ready.token)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                runCatching { projection.stop() }
                stopCast("The cast could not reach the other device: ${e.message}")
                return@launch
            }
            if (castId != id) { // stopped while the channel was coming up
                runCatching { channel.close() }
                runCatching { projection.stop() }
                return@launch
            }
            castChannel = channel

            val metrics = screenMetrics()
            val settings = castSettings ?: CastSettings(
                quality = 50, audio = false,
                placement = CastPlacement(CastPlacementMode.WINDOW, "", 0, 0, 0, 0),
                monitors = emptyList())
            castSettings = settings

            val screenCaster = ScreenCaster(projection, channel, lifecycleScope, metrics.densityDpi)
            screenCaster.onEnded = { reason ->
                runOnUiThread { stopCast(reason) }
            }
            caster = screenCaster
            screenCaster.start(CastQuality.profileFor(settings.quality, metrics.widthPixels, metrics.heightPixels))
            if (settings.audio) applyCastAudio(true)
            refreshCastControls()
        }
    }

    /** The whole screen is what a cast carries, so a rotation changes the encoder's dimensions.
     * The activity keeps running through it (see configChanges in the manifest). */
    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        val quality = castSettings?.quality ?: return
        val metrics = screenMetrics()
        caster?.applyScreenSize(quality, metrics.widthPixels, metrics.heightPixels)
    }

    @Suppress("DEPRECATION") // getRealMetrics is the one call that works from API 26 up
    private fun screenMetrics(): DisplayMetrics {
        val metrics = DisplayMetrics()
        windowManager.defaultDisplay.getRealMetrics(metrics)
        return metrics
    }

    private fun refreshCastControls() {
        deviceFragment()?.refreshCastSection()
    }

    private fun onCastReadyReceived(message: CastReady) {
        if (message.castId != castId) return
        castReady = message
        beginCastIfReady()
    }

    private fun onCastSettingsReceived(message: CastSettingsUpdate) {
        if (message.castId != castId) return
        applyCastSettings(message.settings, fromPeer = true)
    }

    private fun onCastStopReceived(message: CastStop) {
        if (message.castId != castId) return
        val reason = message.reason
        stopCast(reason, notifyPeer = false)
        if (reason.isNotEmpty()) setStatus(reason)
    }

    private fun onMeshMessageReceived(message: MeshMessage, peerUuid: String, peerName: String, session: MeshSession) {
        // Called on MeshSession's read loop (IO thread); lifecycleScope.launch marshals to Main.
        when (message) {
            is Goodbye -> lifecycleScope.launch(Dispatchers.Main) { onPeerGoodbye(session) }
            is ChatSend -> lifecycleScope.launch(Dispatchers.Main) { onChatSendReceived(message, peerUuid, peerName) }
            is ChatHistorySync -> lifecycleScope.launch(Dispatchers.Main) { onChatHistorySyncReceived(message, peerUuid) }
            is ChatEdit -> lifecycleScope.launch(Dispatchers.Main) { onChatEditReceived(message, peerUuid) }
            is ChatDelete -> lifecycleScope.launch(Dispatchers.Main) { onChatDeleteReceived(message, peerUuid) }
            is CastReady -> lifecycleScope.launch(Dispatchers.Main) { onCastReadyReceived(message) }
            is CastSettingsUpdate -> lifecycleScope.launch(Dispatchers.Main) { onCastSettingsReceived(message) }
            is CastStop -> lifecycleScope.launch(Dispatchers.Main) { onCastStopReceived(message) }
            else -> {}
        }
    }

    private suspend fun onChatSendReceived(message: ChatSend, peerUuid: String, peerName: String) {
        if (message.text.length > MAX_CHAT_TEXT_LENGTH) return
        val entry = ChatMessage(
            msgId = message.id, peerUuid = peerUuid, senderUuid = peerUuid, senderLabel = peerName,
            text = message.text, timestamp = message.timestamp)
        val inserted = try {
            chatMessageDao().insertAndEvict(entry)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            setStatus("Could not save chat message: ${e.message}")
            return
        }
        if (inserted) notifyChatChanged(peerUuid)
    }

    private suspend fun onChatHistorySyncReceived(message: ChatHistorySync, peerUuid: String) {
        var anyInserted = false
        val dao = chatMessageDao()
        for (entry in message.entries) {
            if (entry.text.length > MAX_CHAT_TEXT_LENGTH) continue
            val senderLabel = if (entry.senderUuid == peerUuid) peerDisplayName() else selfName
            val chatEntry = ChatMessage(
                msgId = entry.id, peerUuid = peerUuid, senderUuid = entry.senderUuid, senderLabel = senderLabel,
                text = entry.text, timestamp = entry.timestamp)
            try {
                if (dao.insertAndEvict(chatEntry)) anyInserted = true
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                setStatus("Could not save chat history: ${e.message}")
            }
        }
        if (anyInserted) notifyChatChanged(peerUuid)
    }

    private suspend fun onChatEditReceived(message: ChatEdit, peerUuid: String) {
        if (message.text.length > MAX_CHAT_TEXT_LENGTH) return
        try {
            chatMessageDao().updateText(peerUuid, message.id, message.text)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            setStatus("Could not save chat edit: ${e.message}")
            return
        }
        notifyChatChanged(peerUuid)
    }

    private suspend fun onChatDeleteReceived(message: ChatDelete, peerUuid: String) {
        try {
            chatMessageDao().deleteMessage(peerUuid, message.id)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            setStatus("Could not save chat delete: ${e.message}")
            return
        }
        notifyChatChanged(peerUuid)
    }

    // PairingPrompt ---------------------------------------------------------------------------

    override suspend fun confirmCertificateChanged(peerName: String): Boolean =
        PinConfirmDialog.confirmCertificateChanged(this, peerName)

    override suspend fun confirmPin(peerName: String, pin: String): Boolean =
        PinConfirmDialog.confirmPin(
            this, peerName, pin,
            onShown = { pendingPinDialog = it },
            onCancelWhileConnecting = { pendingIncomingSession?.close() })

    // Dialer side, first-time pairing only: the listener has just approved, so swap the
    // "Connecting..." placeholder for the real text-entry prompt.
    override suspend fun enterPin(peerName: String): String? {
        pendingConnectingDialog?.dismiss()
        pendingConnectingDialog = null
        return PinConfirmDialog.enterPin(this, peerName)
    }

    // Listener side, already-trusted peer, manual Connect only: lightweight "allow?" with no PIN.
    override suspend fun confirmReconnect(peerName: String): Boolean =
        PinConfirmDialog.confirmReconnect(this, peerName)
}
