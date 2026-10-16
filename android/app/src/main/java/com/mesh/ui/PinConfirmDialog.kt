package com.mesh.ui

import android.app.AlertDialog
import android.content.Context
import android.text.InputType
import android.widget.EditText
import com.mesh.R
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume

/**
 * Two-button confirmation dialog as a suspend function, used for the PIN comparison and the
 * "Certificate changed — re-pair?" warning. Cancelling the calling coroutine (user-interaction
 * timeout, or the peer already rejected) dismisses the dialog. Must be called on Main. Every
 * dialog here uses [R.style.DarkAlertDialog] and colors its buttons by meaning (see
 * [DarkPopup.colorFor]) so it matches [DarkPopup]'s look.
 */
object PinConfirmDialog {
    private fun builder(context: Context) = AlertDialog.Builder(context, R.style.DarkAlertDialog)

    private fun colorButtons(dialog: AlertDialog, positive: String, negative: String) {
        dialog.getButton(AlertDialog.BUTTON_POSITIVE)?.setTextColor(context(dialog).getColor(DarkPopup.colorFor(positive)))
        dialog.getButton(AlertDialog.BUTTON_NEGATIVE)?.setTextColor(context(dialog).getColor(DarkPopup.colorFor(negative)))
    }

    private fun context(dialog: AlertDialog) = dialog.context

    suspend fun show(
        context: Context,
        title: String,
        message: String,
        positive: String,
        negative: String
    ): Boolean = suspendCancellableCoroutine { cont ->
        val dialog = builder(context)
            .setTitle(title)
            .setMessage(message)
            .setPositiveButton(positive) { _, _ -> if (cont.isActive) cont.resume(true) }
            .setNegativeButton(negative) { _, _ -> if (cont.isActive) cont.resume(false) }
            .setCancelable(false)
            .show()
        colorButtons(dialog, positive, negative)
        cont.invokeOnCancellation { dialog.dismiss() }
    }

    /**
     * Listener side: shows the PIN and asks permission. Allow does not dismiss the dialog — the
     * PIN stays visible (the user still needs to see what they just approved) and the Allow
     * button just relabels to a greyed-out "Connecting" status, since the protocol still has to
     * wait on the dialer's own answer. The Deny button stays live throughout: before Allow it
     * still means "decline" (resumes false); after Allow it relabels to "Cancel" and instead
     * calls [onCancelWhileConnecting], letting the user abort an attempt they already approved
     * but no longer want to wait on, without changing the already-resolved true answer. [onShown]
     * lets the caller keep a reference to dismiss the dialog once the attempt actually finishes
     * (there is no further callback into it once confirmPin returns).
     */
    suspend fun confirmPin(
        context: Context,
        peerName: String,
        pin: String,
        onShown: (AlertDialog) -> Unit = {},
        onCancelWhileConnecting: () -> Unit = {}
    ): Boolean = suspendCancellableCoroutine { cont ->
        var connecting = false
        val dialog = builder(context)
            .setTitle("Pairing request")
            .setMessage("$peerName wants to connect. Allow?\n\n$pin")
            .setPositiveButton("Allow", null)
            .setNegativeButton("Deny", null)
            .setCancelable(false)
            .create()
        dialog.setOnShowListener {
            val allowButton = dialog.getButton(AlertDialog.BUTTON_POSITIVE)
            val denyButton = dialog.getButton(AlertDialog.BUTTON_NEGATIVE)
            allowButton.setTextColor(context.getColor(DarkPopup.colorFor("Allow")))
            denyButton.setTextColor(context.getColor(DarkPopup.colorFor("Deny")))
            allowButton.setOnClickListener {
                connecting = true
                allowButton.isEnabled = false
                allowButton.text = "Connecting"
                denyButton.text = "Cancel"
                denyButton.setTextColor(context.getColor(DarkPopup.colorFor("Cancel")))
                if (cont.isActive) cont.resume(true)
            }
            denyButton.setOnClickListener {
                if (connecting) {
                    onCancelWhileConnecting()
                } else if (cont.isActive) {
                    cont.resume(false)
                }
                dialog.dismiss()
            }
        }
        dialog.show()
        onShown(dialog)
        cont.invokeOnCancellation { dialog.dismiss() }
    }

    suspend fun confirmCertificateChanged(context: Context, peerName: String): Boolean =
        show(context, "Certificate changed",
            "Certificate changed for $peerName — re-pair?\n\nThis device no longer presents the " +
                "certificate you paired with. Only continue if you know why (e.g. the app was reinstalled).",
            "Re-pair", "Cancel")

    /** Listener side, already-trusted peer, manual Connect only: lightweight "allow?" with no
     * PIN — trust is already established. */
    suspend fun confirmReconnect(context: Context, peerName: String): Boolean =
        show(context, "Connect?", "$peerName wants to connect. Allow?", "Allow", "Deny")

    /** Dialer side, first-time pairing only, and only after the listener has already approved:
     * replaces the "Connecting..." placeholder (see [showConnecting]) with a text-entry prompt
     * for the code shown on the listener's screen. Returns the typed digits, or null if
     * cancelled/declined. */
    suspend fun enterPin(context: Context, peerName: String): String? = suspendCancellableCoroutine { cont ->
        val input = EditText(context).apply { inputType = InputType.TYPE_CLASS_NUMBER }
        val dialog = builder(context)
            .setTitle("Enter code")
            .setMessage("Enter the code shown on $peerName:")
            .setView(input)
            .setPositiveButton("Submit") { _, _ -> if (cont.isActive) cont.resume(input.text.toString().trim()) }
            .setNegativeButton("Cancel") { _, _ -> if (cont.isActive) cont.resume(null) }
            .setCancelable(false)
            .show()
        colorButtons(dialog, "Submit", "Cancel")
        cont.invokeOnCancellation { dialog.dismiss() }
    }

    /** Dialer side, first-time pairing only: shown immediately on the Pair/Connect tap, before
     * any protocol step — the dialer never sees the PIN, only the listener does, so there is
     * nothing to show until the listener approves (see [enterPin]). Carries its own Cancel
     * button so the user can abort while still waiting on the listener's decision, not only once
     * [enterPin] replaces it with its own Submit/Cancel pair. Caller dismisses this (it's just a
     * placeholder) once the attempt finishes or [enterPin] replaces it. */
    fun showConnecting(context: Context, peerName: String, onCancel: () -> Unit): AlertDialog {
        val dialog = builder(context)
            .setTitle("Connecting")
            .setMessage("Connecting to $peerName...")
            .setNegativeButton("Cancel") { _, _ -> onCancel() }
            .setCancelable(false)
            .show()
        dialog.getButton(AlertDialog.BUTTON_NEGATIVE)?.setTextColor(context.getColor(DarkPopup.colorFor("Cancel")))
        return dialog
    }
}
