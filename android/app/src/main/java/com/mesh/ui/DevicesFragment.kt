package com.mesh.ui

import android.graphics.Color
import android.os.Bundle
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
            view.findViewById<TextView>(R.id.stateText).text = row.stateLabel
            view.findViewById<View>(R.id.statusDot).setBackgroundResource(
                if (row.isOnline) R.drawable.status_dot_online else R.drawable.status_dot_offline)
            if (columnWidthPx > 0) {
                val column = view.findViewById<FrameLayout>(R.id.statusColumn)
                column.layoutParams = column.layoutParams.apply { width = columnWidthPx }
            }
            view.setBackgroundColor(
                if (row.uuid == selectedUuid) view.context.getColor(R.color.bg_selected_active)
                else Color.TRANSPARENT)

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

        /** Mirrors the design spec: not paired → just Connect (which pairs); paired but not
         * connected → Connect + Unpair; connected → Disconnect + Unpair. */
        private fun actionsFor(row: DeviceRow): List<String> = when {
            !row.isPaired -> listOf("Connect")
            row.isConnectedNow -> listOf("Disconnect", "Unpair")
            else -> listOf("Connect", "Unpair")
        }

        private fun handleAction(action: String, row: DeviceRow) {
            when (action) {
                "Connect" -> if (row.isPaired) mainActivity.connectDevice(row.uuid) else mainActivity.pairDevice(row.uuid)
                "Disconnect" -> mainActivity.disconnectMeshSession()
                "Unpair" -> mainActivity.unpairDevice(row.uuid)
            }
        }
    }
}
