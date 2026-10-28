package com.mesh.ui

/**
 * One row in the unified device list: every paired device (whether or not it is currently
 * visible over mDNS) plus every currently-discovered-but-unpaired device. Mirrors Windows'
 * Mesh.App/DeviceRow.cs. Rebuilt wholesale in [MainActivity.rebuildDeviceRows] whenever anything
 * changes (mDNS, pairing, session state).
 */
data class DeviceRow(
    val uuid: String,
    val name: String,
    val isOnline: Boolean,
    val isPaired: Boolean,
    val isConnectedNow: Boolean,
    val isConnecting: Boolean,
    val ipAddress: String,
    val port: Int,
    val supportsCast: Boolean = false
) {
    /** The "Состояние" column text (see raw/design/1.jpg): empty when never paired, otherwise
     * whichever of Connecting/Connected/Trusted currently applies. */
    val stateLabel: String
        get() = when {
            !isPaired -> ""
            isConnecting -> "Connecting"
            isConnectedNow -> "Connected"
            else -> "Trusted"
        }

    /** The row's 0/1 switch: on from the moment a connection starts until it is gone. */
    val isSwitchOn: Boolean get() = isConnecting || isConnectedNow
}
