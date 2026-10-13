package com.mesh.ui

import android.app.AlertDialog
import android.content.Context
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume

/**
 * Two-button confirmation dialog as a suspend function, used for the PIN comparison and the
 * "Certificate changed — re-pair?" warning. Cancelling the calling coroutine (user-interaction
 * timeout, or the peer already rejected) dismisses the dialog. Must be called on Main.
 */
object PinConfirmDialog {
    suspend fun show(
        context: Context,
        title: String,
        message: String,
        positive: String,
        negative: String
    ): Boolean = suspendCancellableCoroutine { cont ->
        val dialog = AlertDialog.Builder(context)
            .setTitle(title)
            .setMessage(message)
            .setPositiveButton(positive) { _, _ -> if (cont.isActive) cont.resume(true) }
            .setNegativeButton(negative) { _, _ -> if (cont.isActive) cont.resume(false) }
            .setCancelable(false)
            .show()
        cont.invokeOnCancellation { dialog.dismiss() }
    }

    suspend fun confirmPin(context: Context, peerName: String, pin: String): Boolean =
        show(context, "Confirm PIN", "Does this PIN match the one shown on $peerName?\n\n$pin",
            "Match", "Not matching")

    suspend fun confirmCertificateChanged(context: Context, peerName: String): Boolean =
        show(context, "Certificate changed",
            "Certificate changed for $peerName — re-pair?\n\nThis device no longer presents the " +
                "certificate you paired with. Only continue if you know why (e.g. the app was reinstalled).",
            "Re-pair", "Cancel")
}
