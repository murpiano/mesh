package com.mesh.ui

import android.app.AlertDialog
import android.os.Bundle
import android.view.GestureDetector
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import androidx.core.content.ContextCompat
import androidx.fragment.app.Fragment
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.mesh.R
import com.mesh.core.ChatMessage
import kotlinx.coroutines.launch

/**
 * One device's chat section, shown inside its [DeviceFragment] page (raw/design/2.jpg). Always
 * shows that peer's persisted history regardless of connection state; the Send row is only
 * enabled while this peer is the one single device currently connected (see
 * [MainActivity.isActivePeer]) — the active connection is chosen on the Devices tab's popup.
 */
class ChatFragment : Fragment(R.layout.fragment_chat) {
    companion object {
        private const val ARG_PEER_UUID = "peerUuid"

        fun newInstance(peerUuid: String): ChatFragment = ChatFragment().apply {
            arguments = Bundle().apply { putString(ARG_PEER_UUID, peerUuid) }
        }
    }

    val peerUuid: String get() = requireArguments().getString(ARG_PEER_UUID)!!

    private var recyclerView: RecyclerView? = null
    private var hintText: TextView? = null
    private var inputEditText: EditText? = null
    private var sendButton: TextView? = null
    private lateinit var adapter: ChatMessageAdapter

    private val mainActivity: MainActivity get() = requireActivity() as MainActivity

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        val recycler = view.findViewById<RecyclerView>(R.id.chatMessageRecyclerView)
        recycler.layoutManager = LinearLayoutManager(requireContext())
        adapter = ChatMessageAdapter(
            mutableListOf(),
            selfUuid = mainActivity.selfDeviceUuid,
            onCopy = { mainActivity.copyTextToClipboard(it.text) },
            onEdit = { message -> showEditDialog(message) },
            onDelete = { message -> confirmAndDelete(message) })
        recycler.adapter = adapter
        recyclerView = recycler
        // A plain tap anywhere in the chat (another bubble, empty space, or the same bubble)
        // should end any active text selection first, like a normal chat app — Android doesn't
        // do this on its own for a textIsSelectable TextView inside a RecyclerView, since the
        // new touch can land on a different view entirely. Runs before the touch's own click
        // handling (false = don't consume), so a tap that lands on another bubble still opens
        // its menu afterward.
        //
        // clearFocus() alone just hides the selection handles — the Editor's selection range
        // survives underneath, and if the view is refocused later (e.g. after a popup dismisses)
        // the old selection visibly reappears. Toggling textIsSelectable off/on recreates the
        // Editor from scratch, actually discarding it.
        recycler.setOnTouchListener { _, event ->
            if (event.actionMasked == MotionEvent.ACTION_DOWN) {
                for (i in 0 until recycler.childCount) {
                    val bubble = recycler.getChildAt(i)?.findViewById<TextView>(R.id.bubbleText)
                    if (bubble?.hasSelection() == true) {
                        bubble.setTextIsSelectable(false)
                        bubble.setTextIsSelectable(true)
                    }
                }
                // A tap beside the bubbles belongs to no message: it only clears what is open.
                if (!isOnBubble(recycler, event.x, event.y)) DarkPopup.dismissCurrent()
            }
            false
        }

        hintText = view.findViewById(R.id.notConnectedHint)
        val input = view.findViewById<EditText>(R.id.chatInputEditText)
        inputEditText = input
        val send = view.findViewById<TextView>(R.id.sendButton)
        sendButton = send
        send.setOnClickListener {
            val text = input.text.toString()
            if (text.isNotEmpty()) {
                input.text.clear()
                mainActivity.sendChatMessage(peerUuid, text)
            }
        }

        // Attachments aren't implemented yet (no wire protocol for binary transfer) — this is a
        // visual stand-in for raw/design/2.jpg's "+" menu, not a working send.
        view.findViewById<View>(R.id.attachButton).setOnClickListener {
            DarkPopup.show(it, listOf("Image", "File")) {
                Toast.makeText(requireContext(), "Not supported yet", Toast.LENGTH_SHORT).show()
            }
        }

        reloadFromDb()
        refreshConnectionState()
    }

    override fun onDestroyView() {
        super.onDestroyView()
        // A message's popup is a separate overlay window — navigating away from this screen
        // (e.g. back to Devices) destroys this fragment's view but wouldn't otherwise touch it.
        DarkPopup.dismissCurrent()
        recyclerView = null
        hintText = null
        inputEditText = null
        sendButton = null
    }

    /** Reloads from the DB and re-renders. Called on first show and whenever this peer's chat
     * changes (a message sent/received/edited/deleted) while this section is the visible one —
     * see [MainActivity.notifyChatChanged]. */
    fun reloadFromDb() {
        lifecycleScope.launch {
            val messages = mainActivity.loadChatHistory(peerUuid)
            adapter.replaceAll(messages)
            if (adapter.itemCount > 0) recyclerView?.scrollToPosition(adapter.itemCount - 1)
        }
    }

    /** Enables Send only while [peerUuid] is the one currently-connected peer. Called on first
     * show and whenever connection state could have changed (mirrors Windows'
     * RefreshTabConnectionState). */
    fun refreshConnectionState() {
        val active = mainActivity.isActivePeer(peerUuid)
        inputEditText?.isEnabled = active
        sendButton?.isEnabled = active
        sendButton?.alpha = if (active) 1f else 0.4f
        hintText?.visibility = if (active) View.GONE else View.VISIBLE
    }

    private fun showEditDialog(message: ChatMessage) {
        val input = EditText(requireContext()).apply { setText(message.text); setSelection(text.length) }
        AlertDialog.Builder(requireContext())
            .setTitle("Edit message")
            .setView(input)
            .setPositiveButton("Save") { _, _ ->
                val newText = input.text.toString()
                if (newText.isNotEmpty() && newText != message.text) {
                    mainActivity.editChatMessage(peerUuid, message.msgId, newText)
                }
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun confirmAndDelete(message: ChatMessage) {
        lifecycleScope.launch {
            val confirmed = PinConfirmDialog.show(
                requireContext(), "Delete message", "Delete this message?", "Delete", "Cancel")
            if (confirmed) mainActivity.deleteChatMessage(peerUuid, message.msgId)
        }
    }

    /** True if ([x], [y]) in [recycler]'s own coordinates lands on a message bubble rather than
     * on the empty part of its row. A row spans the full width, the bubble does not. */
    private fun isOnBubble(recycler: RecyclerView, x: Float, y: Float): Boolean {
        val row = recycler.findChildViewUnder(x, y) ?: return false
        val bubble = row.findViewById<TextView>(R.id.bubbleText) ?: return false
        val left = row.left + bubble.left
        val top = row.top + bubble.top
        return x >= left && x <= left + bubble.width && y >= top && y <= top + bubble.height
    }
}

/**
 * Two view types (sent/received) so each bubble can use its own layout/background instead of
 * one shared row with conditional styling — see item_chat_bubble_sent.xml/_received.xml. A tap
 * opens the shared dark popup (Copy, +Edit/Delete for an own message); holding the text uses
 * Android's native selection/Copy (the bubble TextView is `textIsSelectable`).
 */
class ChatMessageAdapter(
    private val items: MutableList<ChatMessage>,
    private val selfUuid: String?,
    private val onCopy: (ChatMessage) -> Unit,
    private val onEdit: (ChatMessage) -> Unit,
    private val onDelete: (ChatMessage) -> Unit
) : RecyclerView.Adapter<ChatMessageAdapter.ViewHolder>() {

    private companion object {
        const val VIEW_TYPE_SENT = 0
        const val VIEW_TYPE_RECEIVED = 1
    }

    /** msgId of the message whose popup is currently open, if any — that bubble is tinted
     * lighter (see onBindViewHolder) until the popup is dismissed. */
    private var selectedMsgId: String? = null

    /**
     * A single tap anywhere on the row (the bubble text itself, or the empty space beside it —
     * the whole row spans the screen width, see item_chat_bubble_*.xml) opens the Copy/Edit/
     * Delete popup, right where the tap landed, on the FIRST tap.
     *
     * itemView (the empty space beside the bubble) uses a plain click listener — it's not
     * textIsSelectable, so that's enough. bubbleText is textIsSelectable (for native long-press
     * selection), and a plain click listener on a selectable TextView inside a RecyclerView row
     * needs a first tap just to focus the row before a second tap registers as a click — so
     * instead a [GestureDetector] feeds every touch and fires on `onSingleTapUp` (completes on
     * the first tap's ACTION_UP, unlike `onSingleTapConfirmed` which waits out a possible double
     * tap). Its listener always returns false, so the event still reaches bubbleText's own
     * onTouchEvent afterward and native selection/long-press keeps working untouched.
     */
    class ViewHolder(view: View) : RecyclerView.ViewHolder(view) {
        val bubbleText: TextView = view.findViewById(R.id.bubbleText)
        private var onTap: (() -> Unit)? = null

        private val bubbleGestureListener = object : GestureDetector.SimpleOnGestureListener() {
            override fun onSingleTapUp(e: MotionEvent): Boolean {
                onTap?.invoke()
                return false
            }
        }
        private val bubbleDetector = GestureDetector(view.context, bubbleGestureListener)

        init {
            // Only the bubble opens the menu. The empty space beside it is background, and a tap
            // there clears the open popup and any selection (see the recycler's touch listener).
            bubbleText.setOnTouchListener { _, event -> bubbleDetector.onTouchEvent(event); false }
        }

        fun bind(onTap: () -> Unit) {
            this.onTap = onTap
        }
    }

    fun replaceAll(messages: List<ChatMessage>) {
        items.clear()
        items.addAll(messages)
        notifyDataSetChanged()
    }

    override fun getItemViewType(position: Int) =
        if (items[position].senderUuid == selfUuid) VIEW_TYPE_SENT else VIEW_TYPE_RECEIVED

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
        val layoutId = if (viewType == VIEW_TYPE_SENT) R.layout.item_chat_bubble_sent else R.layout.item_chat_bubble_received
        val view = LayoutInflater.from(parent.context).inflate(layoutId, parent, false)
        return ViewHolder(view)
    }

    override fun onBindViewHolder(holder: ViewHolder, position: Int) {
        val message = items[position]
        val isOwn = message.senderUuid == selfUuid
        holder.bubbleText.text = message.displayText
        val baseColorRes = if (isOwn) R.color.bg_bubble_self else R.color.bg_bubble_peer
        val colorRes = if (message.msgId == selectedMsgId) R.color.bg_selected_active else baseColorRes
        holder.bubbleText.background?.mutate()
            ?.setTint(ContextCompat.getColor(holder.bubbleText.context, colorRes))
        holder.bind { showBubbleMenu(holder.itemView, message, isOwn) }
    }

    override fun getItemCount() = items.size

    private fun indexOfMsgId(msgId: String?) = items.indexOfFirst { it.msgId == msgId }

    private fun setSelected(msgId: String?) {
        val previous = selectedMsgId
        selectedMsgId = msgId
        indexOfMsgId(previous).takeIf { it >= 0 }?.let { notifyItemChanged(it) }
        indexOfMsgId(msgId).takeIf { it >= 0 }?.let { notifyItemChanged(it) }
    }

    private fun showBubbleMenu(anchor: View, message: ChatMessage, isOwn: Boolean) {
        setSelected(message.msgId)
        val items = if (isOwn) listOf("Copy", "Edit", "Delete") else listOf("Copy")
        DarkPopup.show(anchor, items, onDismiss = { setSelected(null) }) { action ->
            when (action) {
                "Copy" -> onCopy(message)
                "Edit" -> onEdit(message)
                "Delete" -> onDelete(message)
            }
        }
    }
}
