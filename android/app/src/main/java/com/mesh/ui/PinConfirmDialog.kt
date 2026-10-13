package com.mesh.ui

import android.app.AlertDialog
import android.content.Context

object PinConfirmDialog {
    fun show(context: Context, pin: String, onResult: (Boolean) -> Unit) {
        AlertDialog.Builder(context)
            .setTitle("Confirm PIN")
            .setMessage("Does this PIN match the other device?\n\n$pin")
            .setPositiveButton("Match") { _, _ -> onResult(true) }
            .setNegativeButton("Not matching") { _, _ -> onResult(false) }
            .setCancelable(false)
            .show()
    }
}
