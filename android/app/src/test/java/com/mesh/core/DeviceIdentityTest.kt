package com.mesh.core

import android.content.SharedPreferences
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test
import java.util.UUID

/** Minimal in-memory SharedPreferences (strings only) for JVM unit tests. */
class FakePrefs(initial: Map<String, String> = emptyMap()) : SharedPreferences {
    val data = initial.toMutableMap()

    override fun getAll(): MutableMap<String, *> = data.toMutableMap()
    override fun getString(key: String?, defValue: String?): String? = data[key] ?: defValue
    override fun getStringSet(key: String?, defValues: MutableSet<String>?) = defValues
    override fun getInt(key: String?, defValue: Int) = defValue
    override fun getLong(key: String?, defValue: Long) = defValue
    override fun getFloat(key: String?, defValue: Float) = defValue
    override fun getBoolean(key: String?, defValue: Boolean) = defValue
    override fun contains(key: String?) = data.containsKey(key)
    override fun registerOnSharedPreferenceChangeListener(l: SharedPreferences.OnSharedPreferenceChangeListener?) {}
    override fun unregisterOnSharedPreferenceChangeListener(l: SharedPreferences.OnSharedPreferenceChangeListener?) {}

    override fun edit(): SharedPreferences.Editor = object : SharedPreferences.Editor {
        private val pending = mutableMapOf<String, String?>()
        override fun putString(key: String, value: String?) = apply { pending[key] = value }
        override fun putStringSet(key: String?, values: MutableSet<String>?) = this
        override fun putInt(key: String?, value: Int) = this
        override fun putLong(key: String?, value: Long) = this
        override fun putFloat(key: String?, value: Float) = this
        override fun putBoolean(key: String?, value: Boolean) = this
        override fun remove(key: String) = apply { pending[key] = null }
        override fun clear() = apply { data.clear() }
        override fun commit(): Boolean {
            pending.forEach { (k, v) -> if (v == null) data.remove(k) else data[k] = v }
            return true
        }
        override fun apply() { commit() }
    }
}

class DeviceIdentityTest {
    @Test
    fun `returns same uuid on second call`() {
        val prefs = FakePrefs()
        val first = getOrCreateDeviceUuid(prefs)
        val second = getOrCreateDeviceUuid(prefs)
        UUID.fromString(first) // valid
        assertEquals(first, second)
    }

    @Test
    fun `reads an existing persisted uuid`() {
        val existing = "3f2504e0-4f89-11d3-9a0c-0305e82c3301"
        val prefs = FakePrefs(mapOf("device_uuid" to existing))
        assertEquals(existing, getOrCreateDeviceUuid(prefs))
    }

    @Test
    fun `regenerates and persists when stored value is corrupt`() {
        val prefs = FakePrefs(mapOf("device_uuid" to "not-a-uuid"))
        val uuid = getOrCreateDeviceUuid(prefs)
        assertNotEquals("not-a-uuid", uuid)
        assertEquals(uuid, getOrCreateDeviceUuid(prefs))
    }
}
