package com.mesh.ui

import android.os.Bundle
import android.view.View
import android.widget.Button
import android.widget.CompoundButton
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.SeekBar
import android.widget.Switch
import android.widget.TextView
import androidx.fragment.app.Fragment
import com.mesh.R
import com.mesh.core.CastMonitor
import com.mesh.core.CastPlacement
import com.mesh.core.CastPlacementMode
import com.mesh.core.CastSettings

/**
 * The cast section of a device's page: one button that starts or stops casting this phone's
 * screen to that device, and, while a cast runs, the three settings either device can change —
 * quality, sound, and where the other device shows the picture.
 *
 * Every control writes the whole settings record through [MainActivity.updateCastSettings],
 * which sends it to the other device. A record arriving from there lands here through
 * [render] instead, so an incoming change is never echoed back.
 */
class CastControlsFragment : Fragment(R.layout.fragment_cast_controls) {
    companion object {
        private const val ARG_PEER_UUID = "peerUuid"

        fun newInstance(peerUuid: String): CastControlsFragment = CastControlsFragment().apply {
            arguments = Bundle().apply { putString(ARG_PEER_UUID, peerUuid) }
        }
    }

    private val peerUuid: String get() = requireArguments().getString(ARG_PEER_UUID)!!
    private val mainActivity: MainActivity get() = requireActivity() as MainActivity

    private var stateText: TextView? = null
    private var toggleButton: Button? = null
    private var settingsGroup: LinearLayout? = null
    private var qualityLabel: TextView? = null
    private var qualitySlider: SeekBar? = null
    private var audioSwitch: Switch? = null
    private var placementGroup: RadioGroup? = null

    // Set while a record from the other device is being rendered, so the listeners below do not
    // mistake it for the user moving a control and send it straight back.
    private var rendering = false

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        stateText = view.findViewById(R.id.castStateText)
        toggleButton = view.findViewById(R.id.castToggleButton)
        settingsGroup = view.findViewById(R.id.castSettingsGroup)
        qualityLabel = view.findViewById(R.id.castQualityLabel)
        qualitySlider = view.findViewById(R.id.castQualitySlider)
        audioSwitch = view.findViewById(R.id.castAudioSwitch)
        placementGroup = view.findViewById(R.id.castPlacementGroup)

        toggleButton?.setOnClickListener {
            if (mainActivity.isCasting(peerUuid)) mainActivity.stopCast() else mainActivity.startCast(peerUuid)
        }

        qualitySlider?.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(bar: SeekBar, value: Int, fromUser: Boolean) {
                qualityLabel?.text = qualityText(value)
                if (fromUser && !rendering) current()?.let { send(it.copy(quality = value)) }
            }

            override fun onStartTrackingTouch(bar: SeekBar) {}
            override fun onStopTrackingTouch(bar: SeekBar) {}
        })

        audioSwitch?.setOnCheckedChangeListener { _: CompoundButton, checked: Boolean ->
            if (!rendering) current()?.let { send(it.copy(audio = checked)) }
        }

        render()
    }

    override fun onDestroyView() {
        super.onDestroyView()
        stateText = null
        toggleButton = null
        settingsGroup = null
        qualityLabel = null
        qualitySlider = null
        audioSwitch = null
        placementGroup = null
    }

    /** Redraws every control from the settings record both devices last agreed on. Called when
     * a cast starts or stops and whenever a change arrives from the other device. */
    fun render() {
        if (stateText == null) return
        val casting = mainActivity.isCasting(peerUuid)
        val settings = current()

        stateText?.text = when {
            !casting -> "This phone's screen can be shown on ${mainActivity.peerDisplayName()}."
            settings == null -> "Starting the cast..."
            else -> "This phone's screen is on ${mainActivity.peerDisplayName()}."
        }
        toggleButton?.text = if (casting) "Stop casting" else "Cast this screen"
        settingsGroup?.visibility = if (casting && settings != null) View.VISIBLE else View.GONE
        if (settings == null) return

        rendering = true
        qualitySlider?.progress = settings.quality
        qualityLabel?.text = qualityText(settings.quality)
        audioSwitch?.isChecked = settings.audio
        renderPlacement(settings)
        rendering = false
    }

    /** One choice per place the other device can put the window: a window, each of its monitors
     * full screen, and all of them at once. The list comes from the other device — it is the
     * authority on its own monitors. */
    private fun renderPlacement(settings: CastSettings) {
        val group = placementGroup ?: return
        group.setOnCheckedChangeListener(null)
        group.removeAllViews()

        val choices = buildList {
            add("A window" to settings.placement.copy(mode = CastPlacementMode.WINDOW))
            for (monitor in settings.monitors) {
                add(
                    "${monitor.name}, full screen" to settings.placement.copy(
                        mode = CastPlacementMode.FULLSCREEN,
                        monitorId = monitor.id))
            }
            if (settings.monitors.size > 1) {
                add("Across every monitor" to settings.placement.copy(mode = CastPlacementMode.ALL_MONITORS))
            }
        }

        choices.forEachIndexed { index, (label, placement) ->
            val button = RadioButton(requireContext()).apply {
                id = index + 1
                text = label
                setTextColor(requireContext().getColor(R.color.text_primary))
                isChecked = matches(placement, settings.placement, settings.monitors)
            }
            group.addView(button)
        }

        group.setOnCheckedChangeListener { _, checkedId ->
            if (rendering) return@setOnCheckedChangeListener
            val placement = choices.getOrNull(checkedId - 1)?.second ?: return@setOnCheckedChangeListener
            current()?.let { send(it.copy(placement = placement)) }
        }
    }

    /** A choice is the current one when its mode matches and, for a full-screen choice, its
     * monitor does. */
    private fun matches(choice: CastPlacement, current: CastPlacement, monitors: List<CastMonitor>): Boolean {
        if (choice.mode != current.mode) return false
        if (choice.mode != CastPlacementMode.FULLSCREEN) return true
        if (monitors.none { it.id == current.monitorId }) return monitors.firstOrNull()?.id == choice.monitorId
        return choice.monitorId == current.monitorId
    }

    private fun qualityText(quality: Int) = "Quality $quality — 0 is smooth, 100 is sharp"

    private fun current(): CastSettings? =
        if (mainActivity.isCasting(peerUuid)) mainActivity.castSettings else null

    private fun send(settings: CastSettings) = mainActivity.updateCastSettings(settings)
}
