package com.mesh.core

import android.content.Context
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Test
import java.util.UUID

class DeviceIdentityInstrumentedTest {
    @Test
    fun getOrCreateDeviceUuid_returnsSameUuid_onSecondCall() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val first = getOrCreateDeviceUuid(context)
        val second = getOrCreateDeviceUuid(context)
        UUID.fromString(first)
        assertEquals(first, second)
    }

    @Test
    fun getOrCreateDeviceUuid_readsExistingPersistedValue() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val prefs = context.getSharedPreferences("mesh_device_identity_test", Context.MODE_PRIVATE)
        val existing = "3f2504e0-4f89-11d3-9a0c-0305e82c3301"
        prefs.edit().putString("device_uuid", existing).commit()
        try {
            assertEquals(existing, getOrCreateDeviceUuid(prefs))
        } finally {
            prefs.edit().clear().commit()
        }
    }
}
