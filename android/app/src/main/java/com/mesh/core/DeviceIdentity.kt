package com.mesh.core

import android.content.Context
import android.content.SharedPreferences
import java.util.UUID

private const val DEVICE_PREFS = "mesh_device_identity"
private const val KEY_DEVICE_UUID = "device_uuid"

/**
 * Stable per-install device UUID. Peers key their trust records by this value, so it must
 * survive restarts and Activity recreation — generated once, persisted in a dedicated
 * SharedPreferences file, read thereafter.
 */
fun getOrCreateDeviceUuid(context: Context): String =
    getOrCreateDeviceUuid(context.getSharedPreferences(DEVICE_PREFS, Context.MODE_PRIVATE))

private val uuidLock = Any()

fun getOrCreateDeviceUuid(prefs: SharedPreferences): String = synchronized(uuidLock) {
    val existing = prefs.getString(KEY_DEVICE_UUID, null)
    if (existing != null && runCatching { UUID.fromString(existing) }.isSuccess) {
        return existing
    }
    val uuid = UUID.randomUUID().toString()
    prefs.edit().putString(KEY_DEVICE_UUID, uuid).commit()
    uuid
}
