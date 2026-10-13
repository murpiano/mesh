package com.mesh.core

import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test

class MeshDiscoveryTest {
    @Test
    fun advertise_isFoundByBrowse_onLocalNetwork() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val found = CompletableDeferred<DiscoveredDevice>()

        val browser = MeshDiscovery(context)
        browser.startBrowsing(onFound = { device ->
            if (device.uuid == "test-uuid-1" && !found.isCompleted) found.complete(device)
        })

        val advertiser = MeshDiscovery(context)
        advertiser.advertise("test-uuid-1", "TestPhone", "android", 5555)

        val result = withTimeoutOrNull(10_000) { found.await() }
        browser.stop()
        advertiser.stop()

        assertNotNull(result)
        assertEquals("TestPhone", result?.name)
        assertEquals("android", result?.platform)
    }
}
