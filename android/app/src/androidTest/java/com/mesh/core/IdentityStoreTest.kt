package com.mesh.core

import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class IdentityStoreTest {
    @Test
    fun loadOrCreate_returnsSameFingerprint_onSecondCall() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val (certFirst, _) = IdentityStore.loadOrCreate(context)
        val (certSecond, _) = IdentityStore.loadOrCreate(context)
        assertEquals(IdentityStore.fingerprint(certFirst), IdentityStore.fingerprint(certSecond))
    }

    @Test
    fun fingerprint_is64LowercaseHexChars() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val (cert, _) = IdentityStore.loadOrCreate(context)
        val fp = IdentityStore.fingerprint(cert)
        assertEquals(64, fp.length)
        assertTrue(fp == fp.lowercase())
    }
}
