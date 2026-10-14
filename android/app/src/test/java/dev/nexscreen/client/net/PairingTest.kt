package dev.nexscreen.client.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class PairingTest {
    @Test
    fun proofMatchesHostVector() {
        // Mismo vector que PairingTests.cs (host) y fake_client.py.
        assertEquals(
            "d0de3459577ccda1a0a23d616cc7427506893c97c5f4f8e38074832597cbe4af",
            Pairing.proof("123456", "aa11", "bb22"),
        )
    }

    @Test
    fun proofDependsOnPinAndPeers() {
        val proof = Pairing.proof("123456", "aa11", "bb22")
        assertNotEquals(proof, Pairing.proof("123457", "aa11", "bb22"))
        assertNotEquals(proof, Pairing.proof("123456", "aa12", "bb22"))
        assertNotEquals(proof, Pairing.proof("123456", "aa11", "bb23"))
    }

    @Test
    fun fingerprintIsLowercaseSha256() {
        assertEquals(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Pairing.fingerprint("abc".toByteArray()),
        )
    }
}
