package de.ceilinglight

import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.spec.SecretKeySpec
import kotlin.math.roundToInt

/**
 * EGLO connect.z / AwoX "Connect-Z" Bluetooth protocol. Same logic as protocol.js (website) and Protocol.cs (Windows).
 * Login + session key: Telink/AwoX mesh scheme (AES-128-ECB on byte-reversed key and value).
 * Commands: 17-byte frame = flag || AES(sessionKey, [crc8][len][dest BE16][zigbee command...]).
 */
object Protocol {
    val SERVICE: UUID = UUID.fromString("00010203-0405-0607-0809-0a0b0c0d1910")
    val STATUS_CHAR: UUID = UUID.fromString("00010203-0405-0607-0809-0a0b0c0d1911")
    val COMMAND_CHAR: UUID = UUID.fromString("00010203-0405-0607-0809-0a0b0c0d1912")
    val PAIR_CHAR: UUID = UUID.fromString("00010203-0405-0607-0809-0a0b0c0d1914")

    private fun pad16(b: ByteArray): ByteArray = ByteArray(16).also { b.copyInto(it, 0, 0, minOf(16, b.size)) }

    /** AwoX "encrypt": AES-ECB over reversed key and value (zero-padded to 16), result reversed. */
    fun encrypt(key: ByteArray, value: ByteArray): ByteArray {
        val k = pad16(key).reversedArray()
        val v = pad16(value).reversedArray()
        val c = Cipher.getInstance("AES/ECB/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, SecretKeySpec(k, "AES"))
        return c.doFinal(v).reversedArray()
    }

    private fun xorCredentials(name: String, password: String): ByteArray {
        val n = name.toByteArray(Charsets.UTF_8)
        val p = password.toByteArray(Charsets.UTF_8)
        require(n.size <= 16 && p.size <= 16) { "Mesh name and password can be at most 16 characters." }
        val a = pad16(n); val b = pad16(p)
        return ByteArray(16) { i -> (a[i].toInt() xor b[i].toInt()).toByte() }
    }

    /** 17-byte login packet: 0x0C | random(8) | enc(name^pass, random)[0..8]. */
    fun makePairPacket(name: String, password: String, random8: ByteArray): ByteArray {
        val enc = encrypt(pad16(random8.copyOf(8)), xorCredentials(name, password))
        val pkt = ByteArray(17)
        pkt[0] = 0x0C
        random8.copyInto(pkt, 1, 0, 8)
        enc.copyInto(pkt, 9, 0, 8)
        return pkt
    }

    fun makeSessionKey(name: String, password: String, random8: ByteArray, reply8: ByteArray): ByteArray {
        val r = ByteArray(16)
        random8.copyInto(r, 0, 0, 8)
        reply8.copyInto(r, 8, 0, 8)
        return encrypt(xorCredentials(name, password), r)
    }

    /** CRC-8, poly 0x36, init 0, reflected in and out. */
    fun crc8(data: ByteArray, offset: Int, length: Int): Int {
        fun refl(x: Int): Int {
            var r = 0
            for (i in 0 until 8) if ((x and (1 shl i)) != 0) r = r or (1 shl (7 - i))
            return r
        }
        var crc = 0
        for (j in offset until offset + length) {
            crc = crc xor refl(data[j].toInt() and 0xFF)
            repeat(8) { crc = (if ((crc and 0x80) != 0) (crc shl 1) xor 0x36 else (crc shl 1)) and 0xFF }
        }
        return refl(crc)
    }

    fun makeCommandPacket(sessionKey: ByteArray, destId: Int, command: ByteArray, flag: Int = 0x00): ByteArray {
        val len = 2 + command.size
        require(len <= 14) { "Command too long" }
        val block = ByteArray(16)
        block[1] = len.toByte()
        block[2] = ((destId shr 8) and 0xFF).toByte()
        block[3] = (destId and 0xFF).toByte()
        command.copyInto(block, 4)
        block[0] = crc8(block, 1, 1 + len).toByte()
        val pkt = ByteArray(17)
        pkt[0] = flag.toByte()
        encrypt(sessionKey, block).copyInto(pkt, 1)
        return pkt
    }

    /** The official app's status request; also used as a keep-alive. */
    fun makeStatusPoll(sessionKey: ByteArray) = makeCommandPacket(sessionKey, 0x0AFF, byteArrayOf(0xFF.toByte()), 0x02)

    // ---- Zigbee cluster commands: [endpoint][cluster LE16][command id][payload] ----
    private fun bytes(vararg v: Int) = ByteArray(v.size) { v[it].toByte() }

    fun on(ep: Int) = bytes(ep, 0x06, 0x00, 0x01)
    fun off(ep: Int) = bytes(ep, 0x06, 0x00, 0x00)

    /** Move to level (with on/off), brightness 1..100 %. Transition 0.2 s like the official app. */
    fun level(ep: Int, percent: Int): ByteArray {
        val lvl = (percent * 254 / 100.0).roundToInt().coerceIn(1, 254)
        return bytes(ep, 0x08, 0x00, 0x04, lvl, 0x02, 0x00, 0x00, 0x00)
    }

    /** Move to colour temperature in Kelvin (sent as mireds, u16 LE). */
    fun colorTemp(ep: Int, kelvin: Int): ByteArray {
        val m = (1_000_000.0 / kelvin).roundToInt().coerceIn(153, 500)
        return bytes(ep, 0x00, 0x03, 0x0A, m and 0xFF, m shr 8, 0x02, 0x00)
    }

    /** Move to hue (0..359°) and saturation (0..100 %). */
    fun hueSat(ep: Int, hueDeg: Int, satPercent: Int): ByteArray {
        val h = (((hueDeg % 360) + 360) % 360 / 360.0 * 254).roundToInt() % 255
        val s = (satPercent * 2.54).roundToInt().coerceIn(0, 254)
        return bytes(ep, 0x00, 0x03, 0x06, h, s, 0x02, 0x00)
    }

    fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
}
