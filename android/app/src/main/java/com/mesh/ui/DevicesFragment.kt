package com.mesh.ui

import android.os.Bundle
import android.view.View
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.ListView
import android.widget.TextView
import androidx.fragment.app.Fragment
import com.mesh.R

/**
 * The Devices tab: discovered-device list, Connect button, mDNS error banner and pairing
 * status line. Purely the view layer of sub-project 1's device screen — the state behind it
 * (discovered devices, selection, status, discovery itself, the accept loop and the pairing
 * flows) lives in [MainActivity], so it survives switching to the Clipboard tab and back
 * (tab switches replace this fragment). The activity pushes changes through the `render*`
 * methods; each one is a no-op while this fragment has no view.
 */
class DevicesFragment : Fragment(R.layout.fragment_devices) {
    private var listView: ListView? = null
    private var connectButton: Button? = null
    private var statusText: TextView? = null
    private var errorBanner: View? = null
    private lateinit var adapter: ArrayAdapter<String>

    private val mainActivity: MainActivity get() = requireActivity() as MainActivity

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        val list = view.findViewById<ListView>(R.id.deviceListView)
        listView = list
        connectButton = view.findViewById(R.id.connectButton)
        statusText = view.findViewById(R.id.statusText)
        errorBanner = view.findViewById(R.id.mdnsErrorBanner)

        adapter = ArrayAdapter(requireContext(), android.R.layout.simple_list_item_single_choice, mutableListOf())
        list.adapter = adapter
        list.setOnItemClickListener { _, _, position, _ ->
            mainActivity.selectedUuid = mainActivity.devices.getOrNull(position)?.uuid
        }
        view.findViewById<Button>(R.id.refreshButton).setOnClickListener { mainActivity.startDiscovery() }
        connectButton?.setOnClickListener { mainActivity.connectToSelected() }

        renderDeviceList()
        renderStatus()
        renderErrorBanner()
        renderConnectEnabled()
    }

    override fun onDestroyView() {
        super.onDestroyView()
        listView = null
        connectButton = null
        statusText = null
        errorBanner = null
    }

    fun renderDeviceList() {
        val list = listView ?: return
        val devices = mainActivity.devices
        adapter.clear()
        adapter.addAll(devices.map { it.name })
        val selected = devices.indexOfFirst { it.uuid == mainActivity.selectedUuid }
        list.clearChoices()
        if (selected >= 0) list.setItemChecked(selected, true)
    }

    fun renderStatus() {
        statusText?.text = mainActivity.statusMessage
    }

    fun renderErrorBanner() {
        errorBanner?.visibility = if (mainActivity.mdnsErrorVisible) View.VISIBLE else View.GONE
    }

    fun renderConnectEnabled() {
        connectButton?.isEnabled = mainActivity.connectEnabled
    }
}
