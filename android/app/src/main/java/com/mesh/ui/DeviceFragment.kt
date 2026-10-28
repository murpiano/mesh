package com.mesh.ui

import android.os.Bundle
import android.view.View
import android.widget.TextView
import androidx.fragment.app.Fragment
import com.mesh.R

/**
 * One device's page (raw/design/2.jpg): header ("Mesh" + device name + settings), a back arrow
 * under "Mesh" with the connection-status word centered under the device name, and the
 * chat/explorer section tabs with their own content below. Opened by tapping a paired row on
 * [DevicesFragment] (see [MainActivity.openDevicePage]); the back arrow (or the system Back
 * button, see MainActivity.onBackPressedDispatcher) returns to it. Only one device page is ever
 * open at a time — this is plain navigation, not browser-style tabs.
 */
class DeviceFragment : Fragment(R.layout.fragment_device) {
    companion object {
        private const val ARG_PEER_UUID = "peerUuid"
        private const val ARG_PEER_NAME = "peerName"

        fun newInstance(peerUuid: String, peerName: String): DeviceFragment = DeviceFragment().apply {
            arguments = Bundle().apply {
                putString(ARG_PEER_UUID, peerUuid)
                putString(ARG_PEER_NAME, peerName)
            }
        }
    }

    val peerUuid: String get() = requireArguments().getString(ARG_PEER_UUID)!!
    private val peerName: String get() = requireArguments().getString(ARG_PEER_NAME)!!

    private var statusStripe: View? = null
    private var statusLabel: TextView? = null
    private var chatTab: View? = null
    private var explorerTab: View? = null
    private var chatLabel: TextView? = null
    private var explorerLabel: TextView? = null

    private val mainActivity: MainActivity get() = requireActivity() as MainActivity

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        view.findViewById<TextView>(R.id.deviceNameText).text = peerName
        view.findViewById<View>(R.id.backButton).setOnClickListener { mainActivity.openDevicesPage() }
        view.findViewById<View>(R.id.meshTitle).setOnClickListener { mainActivity.openDevicesPage() }

        statusStripe = view.findViewById(R.id.connectionStatusStripe)
        statusLabel = view.findViewById(R.id.connectionStatusLabel)
        chatTab = view.findViewById(R.id.chatSectionTab)
        explorerTab = view.findViewById(R.id.explorerSectionTab)
        chatLabel = view.findViewById(R.id.chatSectionLabel)
        explorerLabel = view.findViewById(R.id.explorerSectionLabel)

        chatTab?.setOnClickListener { selectSection(Section.CHAT) }
        explorerTab?.setOnClickListener { selectSection(Section.EXPLORER) }

        selectSection(Section.CHAT)
        refreshConnectionState()
    }

    override fun onDestroyView() {
        super.onDestroyView()
        statusStripe = null
        statusLabel = null
        chatTab = null
        explorerTab = null
        chatLabel = null
        explorerLabel = null
    }

    private enum class Section { CHAT, EXPLORER }

    private fun selectSection(section: Section) {
        val fragment = if (section == Section.CHAT) ChatFragment.newInstance(peerUuid) else ExplorerFragment()
        childFragmentManager.beginTransaction().replace(R.id.deviceChildContainer, fragment).commit()

        // isActivated, not a background colour: tab_selector carries both the open tab's colour
        // and the ripple, and a colour set here would hide them.
        chatTab?.isActivated = section == Section.CHAT
        explorerTab?.isActivated = section == Section.EXPLORER
        chatLabel?.setTextColor(sectionColor(section == Section.CHAT))
        explorerLabel?.setTextColor(sectionColor(section == Section.EXPLORER))
    }

    private fun sectionColor(active: Boolean) =
        requireContext().getColor(if (active) R.color.text_primary else R.color.text_secondary)

    /** The chat section's own fragment, if it's the one currently showing — used by
     * [MainActivity.notifyChatChanged] to live-refresh whichever chat is on screen. */
    fun chatChild(): ChatFragment? = childFragmentManager.findFragmentById(R.id.deviceChildContainer) as? ChatFragment

    /** Updates the status stripe color and, if the chat section is showing, its Send-enabled
     * state. Called whenever connection state could have changed (mirrors the Windows design's
     * RefreshTabConnectionState, see MainActivity.rebuildDeviceRows). */
    fun refreshConnectionState() {
        val status = mainActivity.connectionStatusFor(peerUuid)
        val color = requireContext().getColor(status.colorRes)
        statusStripe?.setBackgroundColor(color)
        statusLabel?.text = status.label
        statusLabel?.setTextColor(color)
        chatChild()?.refreshConnectionState()
    }
}
