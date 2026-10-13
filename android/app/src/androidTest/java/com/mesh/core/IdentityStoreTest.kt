package com.mesh.core

import android.security.keystore.KeyInfo
import android.security.keystore.KeyProperties
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.security.KeyFactory

class IdentityStoreTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private val testAlias = "mesh_test_identity_store"

    @After
    fun cleanup() = IdentityStore.delete(testAlias)

    @Test
    fun loadOrCreate_returnsSameFingerprint_onSecondCall() {
        val (certFirst, _) = IdentityStore.loadOrCreate(context)
        val (certSecond, _) = IdentityStore.loadOrCreate(context)
        assertEquals(IdentityStore.fingerprint(certFirst), IdentityStore.fingerprint(certSecond))
    }

    @Test
    fun fingerprint_is64LowercaseHexChars() {
        val (cert, _) = IdentityStore.loadOrCreate(context)
        val fp = IdentityStore.fingerprint(cert)
        assertEquals(64, fp.length)
        assertTrue(fp == fp.lowercase())
    }

    @Test
    fun distinctAliases_giveDistinctIdentities() {
        val (main, _) = IdentityStore.loadOrCreate(context)
        val (other, _) = IdentityStore.loadOrCreate(context, testAlias)
        assertNotEquals(IdentityStore.fingerprint(main), IdentityStore.fingerprint(other))
    }

    @Test
    fun key_carriesAuthorizationsNeededForTls() {
        val (_, key) = IdentityStore.loadOrCreate(context, testAlias)
        val info = KeyFactory.getInstance(key.algorithm, "AndroidKeyStore").getKeySpec(key, KeyInfo::class.java)
        assertTrue(info.purposes and KeyProperties.PURPOSE_SIGN != 0)
        assertTrue(info.purposes and KeyProperties.PURPOSE_DECRYPT != 0)
        assertTrue(info.digests.contains(KeyProperties.DIGEST_NONE))
        assertTrue(info.encryptionPaddings.contains(KeyProperties.ENCRYPTION_PADDING_NONE))
    }
}
