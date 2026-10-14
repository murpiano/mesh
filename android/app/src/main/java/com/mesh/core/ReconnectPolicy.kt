package com.mesh.core

/**
 * Who dials, and when, after a [MeshSession] to a trusted peer drops. Mirrors the Windows
 * orchestration in `MainWindow.RunMeshReconnectLoopAsync` exactly, because the two sides must
 * agree on it: if both dial at once each can reject the other's attempt as "busy" forever.
 *
 * Symmetry break: only the side whose device UUID sorts lower dials straight away, on
 * [DIALER_BACKOFF_MS]. The other side waits for that incoming connection on its accept loop and
 * only dials itself as a liveness fallback, after [PASSIVE_GRACE_MS] and then every
 * [PASSIVE_INTERVAL_MS] (deliberately not a multiple of the dialer's 30 s cap, so the two
 * schedules drift apart instead of running in lockstep).
 */
object ReconnectPolicy {
    val DIALER_BACKOFF_MS = longArrayOf(1_000, 2_000, 4_000, 8_000, 16_000, 30_000)
    const val PASSIVE_GRACE_MS = 120_000L
    const val PASSIVE_INTERVAL_MS = 45_000L

    /**
     * True if this device is the one that dials. Must give the exact complement of what the
     * peer computes, so it compares the UUID strings exactly as they travel over the wire in
     * the pairing identity exchange (no case or format normalisation — Windows does none either):
     * Kotlin's [String.compareTo] is an ordinal UTF-16 code-unit comparison, the same as .NET's
     * `string.CompareOrdinal` used on Windows. Both platforms generate the canonical lowercase,
     * hyphenated 36-char form (`java.util.UUID.toString()` / `Guid.ToString()`).
     */
    fun isDialer(selfUuid: String, peerUuid: String): Boolean = selfUuid.compareTo(peerUuid) < 0

    /** Delay before reconnect attempt number [attempt] (0-based). */
    fun delayBeforeAttemptMs(isDialer: Boolean, attempt: Int): Long =
        if (isDialer) DIALER_BACKOFF_MS[minOf(attempt, DIALER_BACKOFF_MS.size - 1)]
        else if (attempt == 0) PASSIVE_GRACE_MS
        else PASSIVE_INTERVAL_MS
}
