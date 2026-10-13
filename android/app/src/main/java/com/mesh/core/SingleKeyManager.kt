package com.mesh.core

import java.net.Socket
import java.security.Principal
import java.security.PrivateKey
import java.security.cert.X509Certificate
import javax.net.ssl.SSLEngine
import javax.net.ssl.X509ExtendedKeyManager

/**
 * Serves exactly one identity (certificate + private key) to the TLS stack.
 *
 * Replaces the previous approach of copying the key into an in-memory PKCS12 KeyStore and
 * feeding that to a KeyManagerFactory: an AndroidKeyStore private key is non-exportable
 * (`getEncoded()` returns null), so `PKCS12.setKeyEntry(...)` cannot serialize it. Handing the
 * key object straight to the TLS provider via a custom [X509ExtendedKeyManager] is the
 * documented pattern for hardware-backed keys (it is how `KeyChain` client certificates are
 * used): Conscrypt then delegates every private-key operation back to the AndroidKeyStore
 * provider. It also avoids `KeyManagerFactory.init(androidKeyStore)` picking an arbitrary
 * alias when the AndroidKeyStore holds more than one entry (e.g. in instrumented tests).
 *
 * Works unchanged with ordinary software keys (JVM unit tests load PKCS12 fixtures).
 */
internal class SingleKeyManager(
    private val certificate: X509Certificate,
    private val privateKey: PrivateKey
) : X509ExtendedKeyManager() {
    private val alias = "mesh"

    // keyType is e.g. "RSA", "EC", "RSASSA-PSS". Our key is a plain rsaEncryption key, which
    // TLS 1.2 RSA suites and TLS 1.3 rsa_pss_rsae_* / rsa_pkcs1_* schemes all request as "RSA".
    private fun matches(keyType: String?) = keyType == null || keyType.equals(privateKey.algorithm, ignoreCase = true)

    override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?) =
        if (matches(keyType)) arrayOf(alias) else null

    override fun chooseClientAlias(keyTypes: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?) =
        if (keyTypes == null || keyTypes.any { matches(it) }) alias else null

    override fun chooseEngineClientAlias(keyTypes: Array<out String>?, issuers: Array<out Principal>?, engine: SSLEngine?) =
        chooseClientAlias(keyTypes, issuers, null)

    override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?) =
        if (matches(keyType)) arrayOf(alias) else null

    override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?) =
        if (matches(keyType)) alias else null

    override fun chooseEngineServerAlias(keyType: String?, issuers: Array<out Principal>?, engine: SSLEngine?) =
        chooseServerAlias(keyType, issuers, null)

    override fun getCertificateChain(alias: String?): Array<X509Certificate>? =
        if (alias == this.alias) arrayOf(certificate) else null

    override fun getPrivateKey(alias: String?): PrivateKey? =
        if (alias == this.alias) privateKey else null
}
