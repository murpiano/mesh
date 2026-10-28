package com.mesh.ui

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.BaseAdapter
import android.widget.Button
import android.widget.ListView
import android.widget.TextView
import androidx.fragment.app.Fragment
import androidx.lifecycle.lifecycleScope
import com.mesh.R
import com.mesh.core.BlockedDevice
import kotlinx.coroutines.launch

/**
 * The Settings screen behind the gear (desktop: the SettingsPage in MainWindow.xaml). So far it
 * holds one thing: the blacklist, and a Remove next to each device that lets it back in.
 */
class SettingsFragment : Fragment(R.layout.fragment_settings) {
    private var listView: ListView? = null
    private var emptyText: View? = null
    private lateinit var adapter: BlockedRowAdapter

    private val mainActivity: MainActivity get() = requireActivity() as MainActivity

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        val list = view.findViewById<ListView>(R.id.blockedListView)
        listView = list
        emptyText = view.findViewById(R.id.noBlockedDevicesText)

        adapter = BlockedRowAdapter(mainActivity)
        list.adapter = adapter
        view.findViewById<View>(R.id.meshTitle).setOnClickListener { mainActivity.openDevicesPage() }

        renderBlockedList()
    }

    override fun onDestroyView() {
        super.onDestroyView()
        listView = null
        emptyText = null
    }

    fun renderBlockedList() {
        val activity = activity as? MainActivity ?: return
        activity.lifecycleScope.launch {
            val blocked = activity.blockedDevices()
            adapter.rows = blocked
            adapter.notifyDataSetChanged()
            emptyText?.visibility = if (blocked.isEmpty()) View.VISIBLE else View.GONE
        }
    }

    private class BlockedRowAdapter(private val mainActivity: MainActivity) : BaseAdapter() {
        var rows: List<BlockedDevice> = emptyList()

        override fun getCount() = rows.size
        override fun getItem(position: Int) = rows[position]
        override fun getItemId(position: Int) = position.toLong()

        override fun getView(position: Int, recycled: View?, parent: ViewGroup): View {
            val view = recycled ?: LayoutInflater.from(parent.context)
                .inflate(R.layout.item_blocked_row, parent, false)
            val row = rows[position]
            view.findViewById<TextView>(R.id.blockedNameText).text = row.name
            view.findViewById<Button>(R.id.unblockButton).setOnClickListener {
                mainActivity.unblockDevice(row.uuid)
            }
            return view
        }
    }
}
