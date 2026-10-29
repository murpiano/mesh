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
    private var castTab: View? = null
    private var chatLabel: TextView? = null
    private var explorerLabel: TextView? = null
    private var castLabel: TextView? = null

    private val mainActivity: MainActivity get() = requireActivity() as MainActivity

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        view.findViewById<TextView>(R.id.deviceNameText).text = peerName
        view.findViewById<View>(R.id.backButton).setOnClickListener { mainActivity.openDevicesPage() }
        view.findViewById<View>(R.id.meshTitle).setOnClickListener { mainActivity.openDevicesPage() }
        view.findViewById<View>(R.id.settingsButton).setOnClickListener { mainActivity.openSettingsPage() }

        statusStripe = view.findViewById(R.id.connectionStatusStripe)
        statusLabel = view.findViewById(R.id.connectionStatusLabel)
        chatTab = view.findViewById(R.id.chatSectionTab)
        explorerTab = view.findViewById(R.id.explorerSectionTab)
        chatLabel = view.findViewById(R.id.chatSectionLabel)
        explorerLabel = view.findViewById(R.id.explorerSectionLabel)
        castTab = view.findViewById(R.id.castSectionTab)
        castLabel = view.findViewById(R.id.castSectionLabel)

        chatTab?.setOnClickListener { selectSection(Section.CHAT) }
        explorerTab?.setOnClickListener { selectSection(Section.EXPLORER) }
        castTab?.setOnClickListener { selectSection(Section.CAST) }

        selectSection(Section.CHAT)
        refreshConnectionState()
        refreshCastSection()
    }

    override fun onDestroyView() {
        super.onDestroyView()
        statusStripe = null
        statusLabel = null
        chatTab = null
        explorerTab = null
        chatLabel = null
        explorerLabel = null
        castTab = null
        castLabel = null
    }

    private enum class Section { CHAT, EXPLORER, CAST }

    private fun selectSection(section: Section) {
        val fragment = when (section) {
            Section.CHAT -> ChatFragment.newInstance(peerUuid)
            Section.EXPLORER -> ExplorerFragment()
            Section.CAST -> CastControlsFragment.newInstance(peerUuid)
        }
        childFragmentManager.beginTransaction().replace(R.id.deviceChildContainer, fragment).commit()

        // isActivated, not a background colour: tab_selector carries both the open tab's colour
        // and the ripple, and a colour set here would hide them.
        chatTab?.isActivated = section == Section.CHAT
        explorerTab?.isActivated = section == Section.EXPLORER
        castTab?.isActivated = section == Section.CAST
        chatLabel?.setTextColor(sectionColor(section == Section.CHAT))
        explorerLabel?.setTextColor(sectionColor(section == Section.EXPLORER))
        castLabel?.setTextColor(sectionColor(section == Section.CAST))
    }

    private fun sectionColor(active: Boolean) =
        requireContext().getColor(if (active) R.color.text_primary else R.color.text_secondary)

    /** The chat section's own fragment, if it's the one currently showing — used by
     * [MainActivity.notifyChatChanged] to live-refresh whichever chat is on screen. */
    /** The cast section's own fragment, if it is the one currently showing. */
    private fun castChild(): CastControlsFragment? =
        childFragmentManager.findFragmentById(R.id.deviceChildContainer) as? CastControlsFragment

    /**
     * Shows or hides the cast tab and redraws its controls. The tab is there only while this
     * device is connected and announced that it understands casting — an older build would
     * close the connection on the cast messages. A cast that is running keeps the tab even if
     * the row's state lags behind.
     */
    fun refreshCastSection() {
        val available = mainActivity.isCasting(peerUuid) ||
            (mainActivity.peerSupportsCast(peerUuid) && mainActivity.isActivePeer(peerUuid))
        castTab?.visibility = if (available) View.VISIBLE else View.GONE
        if (!available && castTab?.isActivated == true) selectSection(Section.CHAT)
        castChild()?.render()
    }

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
        refreshCastSection()
    }
}
