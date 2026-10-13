package com.mesh.core

import java.security.MessageDigest

object PinDerivation {
    fun derive(fingerprintA: String, fingerprintB: String): String {
        val ordered = listOf(fingerprintA, fingerprintB).sorted()
        val combined = (ordered[0] + ordered[1]).toByteArray(Charsets.UTF_8)
        val hash = MessageDigest.getInstance("SHA-256").digest(combined)
        val num = (((hash[0].toInt() and 0xFF) shl 16) or
                   ((hash[1].toInt() and 0xFF) shl 8) or
                   (hash[2].toInt() and 0xFF)) % 1_000_000
        return num.toString().padStart(6, '0')
    }
}
