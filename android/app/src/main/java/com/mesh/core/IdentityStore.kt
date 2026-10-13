package com.mesh.core

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.cert.X509Certificate
import java.util.Date
import javax.security.auth.x500.X500Principal

object IdentityStore {
    private const val KEYSTORE_ALIAS = "mesh_identity"

    fun loadOrCreate(context: Context): Pair<X509Certificate, PrivateKey> {
        val keyStore = KeyStore.getInstance("AndroidKeyStore")
        keyStore.load(null)

        if (keyStore.containsAlias(KEYSTORE_ALIAS)) {
            val cert = keyStore.getCertificate(KEYSTORE_ALIAS) as X509Certificate
            val privateKey = keyStore.getKey(KEYSTORE_ALIAS, null) as PrivateKey
            return Pair(cert, privateKey)
        }

        val generator = KeyPairGenerator.getInstance(
            KeyProperties.KEY_ALGORITHM_RSA, "AndroidKeyStore")
        val now = Date()
        val expiry = Date(now.time + 10L * 365 * 24 * 60 * 60 * 1000)

        val spec = KeyGenParameterSpec.Builder(
            KEYSTORE_ALIAS,
            KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY
        )
            .setKeySize(2048)
            .setDigests(KeyProperties.DIGEST_SHA256)
            .setSignaturePaddings(KeyProperties.SIGNATURE_PADDING_RSA_PKCS1)
            .setCertificateSubject(X500Principal("CN=MeshDevice"))
            .setCertificateSerialNumber(BigInteger.ONE)
            .setCertificateNotBefore(now)
            .setCertificateNotAfter(expiry)
            .build()

        generator.initialize(spec)
        generator.generateKeyPair()

        val cert = keyStore.getCertificate(KEYSTORE_ALIAS) as X509Certificate
        val privateKey = keyStore.getKey(KEYSTORE_ALIAS, null) as PrivateKey
        return Pair(cert, privateKey)
    }

    fun fingerprint(cert: X509Certificate): String {
        val hash = MessageDigest.getInstance("SHA-256").digest(cert.encoded)
        return hash.joinToString("") { "%02x".format(it) }
    }
}
