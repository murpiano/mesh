package com.mesh.core

import android.content.Context
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Temporary diagnostic log for the "Connect on an already-paired/Trusted device still asks for a
 * PIN" repro: appends timestamped lines to <app files dir>/debug.log (pull with
 * `adb exec-out run-as com.mesh.debug cat files/debug.log`; run-as works only on debug builds).
 * Mirrors Windows' Mesh.Core/DebugLog.cs.
 * Not for production telemetry — remove once that bug is found and fixed.
 */
object DebugLog {
    private var file: File? = null
    private val lock = Any()
    private val format = SimpleDateFormat("HH:mm:ss.SSS", Locale.US)

    fun init(context: Context) {
        file = File(context.filesDir, "debug.log")
    }

    fun write(line: String) {
        val target = file ?: return
        try {
            synchronized(lock) {
                target.appendText("${format.format(Date())} $line\n")
            }
        } catch (e: Exception) {
            // Diagnostics must never affect the thing they're diagnosing.
        }
    }
}
