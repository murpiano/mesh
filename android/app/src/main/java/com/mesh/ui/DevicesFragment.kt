package com.mesh.ui

import android.os.Bundle
import android.view.Gravity
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.BaseAdapter
import android.widget.Button
import android.widget.FrameLayout
import android.widget.ListView
import android.widget.TextView
import androidx.fragment.app.Fragment
import com.mesh.R

/**
 * The Devices tab: unified device list (paired + discovered), mDNS error banner and status
 * line. Mirrors raw/design/1.jpg: a status dot (filled green = online, hollow gray ring =
 * offline), the device's name, and its pairing state in the "Состояние" column. A row is tapped
 * to open that device's page ([MainActivity.openDevicePage], raw/design/2.jpg); long-pressed (or
 * right-clicked) for a dark popup with Connect/Disconnect/Unpair depending on its state — there
 * are no per-row buttons any more. Purely the view layer; the state behind it lives in
 * [MainActivity], so it survives navigating to a device page and back.
 */
class DevicesFragment : Fragment(R.layout.fragment_devices) {
    private var listView: ListView? = null
    private var errorBanner: View? = null
    private lateinit var adapter: DeviceRowAdapter

    private val mainActivity: MainActivity get() = requireActivity() as MainActivity

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        val list = view.findViewById<ListView>(R.id.deviceListView)
        listView = list
        errorBanner = view.findViewById(R.id.mdnsErrorBanner)

        adapter = DeviceRowAdapter(mainActivity)
        list.adapter = adapter
        view.findViewById<Button>(R.id.refreshButton).setOnClickListener { mainActivity.startDiscovery() }
        view.findViewById<View>(R.id.meshTitle).setOnClickListener { mainActivity.openDevicesPage() }
        view.findViewById<View>(R.id.settingsButton).setOnClickListener { mainActivity.openSettingsPage() }

        // The "Status" column (header + every row's dot) is exactly as wide as the word
        // "Devices" in the (now centered) card title above it, so the header never wraps and the
        // whole first column lines up under that word. Measured from the title's PAINT, not its
        // View width — the title View itself spans the whole card now that it's centered.
        val devicesTitle = view.findViewById<TextView>(R.id.devicesTitle)
        val statusHeaderColumn = view.findViewById<TextView>(R.id.statusHeaderColumn)
        val width = devicesTitle.paint.measureText(devicesTitle.text.toString()).toInt()
        if (width > 0) {
            statusHeaderColumn.layoutParams = statusHeaderColumn.layoutParams.apply { this.width = width }
            adapter.columnWidthPx = width
        }

        // The State column is as wide as the longest word it can hold, so every row's switch
        // sits exactly under the one above it instead of shifting with the word beside it.
        val sample = LayoutInflater.from(view.context).inflate(R.layout.item_device_row, list, false)
        val stateWidth = sample.findViewById<TextView>(R.id.stateText).paint.measureText("Connecting").toInt()
        if (stateWidth > 0) {
            adapter.stateWidthPx = stateWidth
            val stateHeader = view.findViewById<TextView>(R.id.stateHeaderColumn)
            stateHeader.layoutParams = stateHeader.layoutParams.apply { this.width = stateWidth }
        }

        renderDeviceList()
        renderErrorBanner()
    }

    override fun onDestroyView() {
        super.onDestroyView()
        // See ChatFragment.onDestroyView — a popup is a separate overlay window and needs an
        // explicit dismiss when this screen's view goes away.
        DarkPopup.dismissCurrent()
        listView = null
        errorBanner = null
    }

    fun renderDeviceList() {
        adapter.rows = mainActivity.deviceRows
        adapter.notifyDataSetChanged()
    }

    /** No UI left to update — the status line was removed (the device table already shows
     * connection state); kept as a no-op so [MainActivity.setStatus]'s callers don't need their
     * own special-casing. */
    fun renderStatus() {}

    fun renderErrorBanner() {
        errorBanner?.visibility = if (mainActivity.mdnsErrorVisible) View.VISIBLE else View.GONE
    }

    private class DeviceRowAdapter(private val mainActivity: MainActivity) : BaseAdapter() {
        var rows: List<DeviceRow> = emptyList()
        /** Set once from [DevicesFragment.onViewCreated] to match the "Devices" title's width. */
        var columnWidthPx: Int = 0
        /** Set once from [DevicesFragment.onViewCreated]: the width of the word "Connecting", so
         * the State column never changes width and the switches stay in one line. */
        var stateWidthPx: Int = 0
        /** uuid of the row whose popup is currently open, if any — highlighted green like a
         * connected status dot/stripe until the popup is dismissed. */
        private var selectedUuid: String? = null

        override fun getCount() = rows.size
        override fun getItem(position: Int) = rows[position]
        override fun getItemId(position: Int) = position.toLong()

        override fun getView(position: Int, recycled: View?, parent: ViewGroup): View {
            val view = recycled ?: LayoutInflater.from(parent.context)
                .inflate(R.layout.item_device_row, parent, false)
            val row = rows[position]

            view.findViewById<TextView>(R.id.nameText).text = row.name
            val stateText = view.findViewById<TextView>(R.id.stateText)
            stateText.text = row.stateLabel
            if (stateWidthPx > 0) {
                stateText.layoutParams = stateText.layoutParams.apply { width = stateWidthPx }
            }
            view.findViewById<View>(R.id.statusDot).setBackgroundResource(
                if (row.isOnline) R.drawable.status_dot_online else R.drawable.status_dot_offline)
            if (columnWidthPx > 0) {
                val column = view.findViewById<FrameLayout>(R.id.statusColumn)
                column.layoutParams = column.layoutParams.apply { width = columnWidthPx }
            }
            // The 0/1 switch between the name and the state: flicked right and green while this
            // device is connecting or connected. Tapping it is the only thing that connects,
            // cancels a connection still being made, or disconnects from this screen; the rest
            // of the row opens the device.
            val toggle = view.findViewById<FrameLayout>(R.id.connectToggle)
            val mark = view.findViewById<TextView>(R.id.toggleMark)
            val switchOn = row.isSwitchOn
            toggle.setBackgroundResource(
                if (switchOn) R.drawable.toggle_track_on else R.drawable.toggle_track_off)
            mark.text = if (switchOn) "I" else "0"
            mark.layoutParams = (mark.layoutParams as FrameLayout.LayoutParams).apply {
                gravity = (if (switchOn) Gravity.END else Gravity.START) or Gravity.CENTER_VERTICAL
            }
            toggle.setOnClickListener {
                when {
                    // Flicked off while the connection is still being made: abort that attempt.
                    row.isConnecting -> mainActivity.cancelPendingConnect()
                    row.isConnectedNow -> mainActivity.disconnectMeshSession()
                    row.isPaired -> mainActivity.connectDevice(row.uuid)
                    else -> mainActivity.pairDevice(row.uuid)
                }
            }

            // isActivated, not a background colour: the row's drawable (row_selector) carries
            // both the highlight and the press ripple, and a colour set here would hide them.
            view.isActivated = row.uuid == selectedUuid

            // Tap opens the device's page; only meaningful once paired (an unpaired device has
            // no chat/history to show there — see the popup below for how to pair it).
            view.setOnClickListener { if (row.isPaired) mainActivity.openDevicePage(row.uuid, row.name) }

            // Long-press highlights this row and opens its popup, centered on screen (see
            // DarkPopup.show), until it's dismissed.
            view.setOnLongClickListener {
                selectedUuid = row.uuid
                notifyDataSetChanged()
                DarkPopup.show(
                    it, actionsFor(row),
                    onDismiss = { selectedUuid = null; notifyDataSetChanged() }
                ) { action -> handleAction(action, row) }
                true
            }

            return view
        }

        /** Connecting and disconnecting live on the row's switch now, so the popup is left with
         * what is permanent: drop the pairing, or block the device outright. A device that was
         * never paired has no pairing to drop. */
        private fun actionsFor(row: DeviceRow): List<String> =
            if (row.isPaired) listOf("Unpair", "Blacklist") else listOf("Blacklist")

        private fun handleAction(action: String, row: DeviceRow) {
            when (action) {
                "Unpair" -> mainActivity.unpairDevice(row.uuid)
                "Blacklist" -> mainActivity.blacklistDevice(row.uuid)
            }
        }
    }
}
