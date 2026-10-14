package com.mesh.ui

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.fragment.app.Fragment
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.mesh.R
import com.mesh.core.ClipboardEntry
import com.mesh.core.ClipboardHistoryDao

/**
 * The Clipboard tab: session status line plus the clipboard history (newest first).
 *
 * View layer only. The clipboard watcher, the history writes (local copies and clips received
 * from the peer) and the [com.mesh.core.MeshSession] are owned by [MainActivity] for its whole
 * lifetime, so nothing is missed or dropped while the Devices tab is showing (tab switches
 * replace this fragment). The activity pushes changes through [onHistoryEntryAdded] and
 * [renderSessionStatus], always on the main thread.
 */
class ClipboardFragment : Fragment(R.layout.fragment_clipboard) {
    private var statusText: TextView? = null
    private lateinit var adapter: ClipboardHistoryAdapter

    // Entries added before the initial load completes are already part of what it returns
    // (MainActivity serialises the load behind earlier writes), so they are not prepended.
    private var historyLoaded = false

    private val mainActivity: MainActivity get() = requireActivity() as MainActivity

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        statusText = view.findViewById(R.id.sessionStatusText)
        val recyclerView = view.findViewById<RecyclerView>(R.id.clipboardHistoryRecyclerView)
        recyclerView.layoutManager = LinearLayoutManager(requireContext())
        adapter = ClipboardHistoryAdapter(mutableListOf()) { entry -> mainActivity.copyTextToClipboard(entry.text) }
        recyclerView.adapter = adapter

        renderSessionStatus()
        mainActivity.loadClipboardHistory { entries ->
            if (this@ClipboardFragment.view != null) { // view not destroyed meanwhile
                adapter.replaceAll(entries)
                historyLoaded = true
            }
        }
    }

    override fun onDestroyView() {
        super.onDestroyView()
        statusText = null
        historyLoaded = false
    }

    fun onHistoryEntryAdded(entry: ClipboardEntry) {
        if (view == null || !historyLoaded) return
        adapter.prepend(entry)
    }

    fun renderSessionStatus() {
        statusText?.text = mainActivity.sessionStatus
    }
}

class ClipboardHistoryAdapter(
    private val items: MutableList<ClipboardEntry>,
    private val onCopyClicked: (ClipboardEntry) -> Unit
) : RecyclerView.Adapter<ClipboardHistoryAdapter.ViewHolder>() {

    class ViewHolder(view: View) : RecyclerView.ViewHolder(view) {
        val sourceText: TextView = view.findViewById(android.R.id.text1)
        val bodyText: TextView = view.findViewById(android.R.id.text2)
    }

    fun replaceAll(entries: List<ClipboardEntry>) {
        items.clear()
        items.addAll(entries)
        notifyDataSetChanged()
    }

    /** Adds [entry] at the top, dropping the oldest rows past the store's cap so the list matches the DB. */
    fun prepend(entry: ClipboardEntry) {
        items.add(0, entry)
        notifyItemInserted(0)
        while (items.size > ClipboardHistoryDao.CAP) {
            val last = items.size - 1
            items.removeAt(last)
            notifyItemRemoved(last)
        }
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
        val view = LayoutInflater.from(parent.context)
            .inflate(android.R.layout.two_line_list_item, parent, false)
        return ViewHolder(view).apply {
            // Entries can be up to 1 MiB of text: show a preview, the tap copies all of it.
            bodyText.maxLines = 3
            bodyText.ellipsize = android.text.TextUtils.TruncateAt.END
        }
    }

    override fun onBindViewHolder(holder: ViewHolder, position: Int) {
        val entry = items[position]
        holder.sourceText.text = entry.source
        holder.bodyText.text = entry.text
        holder.itemView.setOnClickListener { onCopyClicked(entry) }
    }

    override fun getItemCount() = items.size
}
