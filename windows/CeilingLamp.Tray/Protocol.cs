using System.Security.Cryptography;
using System.Text;

namespace CeilingLamp;

/// <summary>
/// EGLO connect.z / AwoX "Connect-Z" Bluetooth protocol. Same logic as protocol.js on the website.
/// Login + session key: Telink/AwoX mesh scheme (AES-128-ECB on byte-reversed key and value).
/// Commands: 17-byte frame = flag || AES(sessionKey, [crc8][len][dest BE16][zigbee command...]).
/// Based on github.com/Leiaz/python-awox-mesh-light and github.com/BastiAKA/awoxble2mqtt-api.
/// </summary>
public static class Protocol
{
    public static readonly Guid Service = new("00010203-0405-0607-0809-0a0b0c0d1910");
    public static readonly Guid StatusChar = new("00010203-0405-0607-0809-0a0b0c0d1911");
    public static readonly Guid CommandChar = new("00010203-0405-0607-0809-0a0b0c0d1912");
    public static readonly Guid PairChar = new("00010203-0405-0607-0809-0a0b0c0d1914");

    /// <summary>AwoX "encrypt": AES-ECB over reversed key and value (both zero-padded to 16), result reversed.</summary>
    public static byte[] Encrypt(byte[] key, byte[] value)
    {
        var k = Pad16(key); var v = Pad16(value);
        Array.Reverse(k); Array.Reverse(v);
        using var aes = Aes.Create();
        aes.Key = k;
        var outBuf = aes.EncryptEcb(v, PaddingMode.None);
        Array.Reverse(outBuf);
        return outBuf;
    }

    private static byte[] Pad16(byte[] b)
    {
        var o = new byte[16];
        Array.Copy(b, o, Math.Min(16, b.Length));
        return o;
    }

    private static byte[] XorCredentials(string name, string password)
    {
        var n = Encoding.UTF8.GetBytes(name);
        var p = Encoding.UTF8.GetBytes(password);
        if (n.Length > 16 || p.Length > 16) throw new ArgumentException("Mesh name and password can be at most 16 characters.");
        var a = Pad16(n); var b = Pad16(p); var o = new byte[16];
        for (var i = 0; i < 16; i++) o[i] = (byte)(a[i] ^ b[i]);
        return o;
    }

    /// <summary>17-byte login packet: 0x0C | random(8) | enc(name^pass, random)[0..8].</summary>
    public static byte[] MakePairPacket(string name, string password, byte[] random8)
    {
        var enc = Encrypt(Pad16(random8[..8]), XorCredentials(name, password));
        var pkt = new byte[17];
        pkt[0] = 0x0C;
        Array.Copy(random8, 0, pkt, 1, 8);
        Array.Copy(enc, 0, pkt, 9, 8);
        return pkt;
    }

    public static byte[] MakeSessionKey(string name, string password, byte[] random8, byte[] reply8)
    {
        var r = new byte[16];
        Array.Copy(random8, 0, r, 0, 8);
        Array.Copy(reply8, 0, r, 8, 8);
        return Encrypt(XorCredentials(name, password), r);
    }

    /// <summary>CRC-8, poly 0x36, init 0, reflected in and out.</summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        static int Refl(int x) { var r = 0; for (var i = 0; i < 8; i++) if ((x & (1 << i)) != 0) r |= 1 << (7 - i); return r; }
        var crc = 0;
        foreach (var b in data)
        {
            crc ^= Refl(b);
            for (var i = 0; i < 8; i++) crc = ((crc & 0x80) != 0 ? (crc << 1) ^ 0x36 : crc << 1) & 0xFF;
        }
        return (byte)Refl(crc);
    }

    public static byte[] MakeCommandPacket(byte[] sessionKey, int destId, byte[] command, byte flag = 0x00)
    {
        var len = 2 + command.Length;
        if (len > 14) throw new ArgumentException("Command too long");
        var block = new byte[16];
        block[1] = (byte)len;
        block[2] = (byte)((destId >> 8) & 0xFF);
        block[3] = (byte)(destId & 0xFF);
        Array.Copy(command, 0, block, 4, command.Length);
        block[0] = Crc8(block.AsSpan(1, 1 + len));
        var pkt = new byte[17];
        pkt[0] = flag;
        Array.Copy(Encrypt(sessionKey, block), 0, pkt, 1, 16);
        return pkt;
    }

    /// <summary>The official app's status request; also used as a keep-alive.</summary>
    public static byte[] MakeStatusPoll(byte[] sessionKey) => MakeCommandPacket(sessionKey, 0x0AFF, new byte[] { 0xFF }, 0x02);

    // ---- Zigbee cluster commands: [endpoint][cluster LE16][command id][payload] ----
    private static readonly byte[] Transition = { 0x02, 0x00 }; // 0.2 s, like the official app

    public static byte[] On(int ep) => new byte[] { (byte)ep, 0x06, 0x00, 0x01 };
    public static byte[] Off(int ep) => new byte[] { (byte)ep, 0x06, 0x00, 0x00 };

    /// <summary>Move to level (with on/off), brightness 1..100 %.</summary>
    public static byte[] Level(int ep, int percent)
    {
        var lvl = (byte)Math.Clamp((int)Math.Round(percent * 254 / 100.0), 1, 254);
        return new byte[] { (byte)ep, 0x08, 0x00, 0x04, lvl, Transition[0], Transition[1], 0x00, 0x00 };
    }

    /// <summary>Move to colour temperature in Kelvin (sent as mireds, u16 LE).</summary>
    public static byte[] ColorTemp(int ep, int kelvin)
    {
        var m = Math.Clamp((int)Math.Round(1_000_000.0 / kelvin), 153, 500);
        return new byte[] { (byte)ep, 0x00, 0x03, 0x0A, (byte)(m & 0xFF), (byte)(m >> 8), Transition[0], Transition[1] };
    }

    /// <summary>Move to hue (0..359°) and saturation (0..100 %).</summary>
    public static byte[] HueSat(int ep, int hueDeg, int satPercent)
    {
        var h = (byte)(Math.Round(((hueDeg % 360) + 360) % 360 / 360.0 * 254) % 255);
        var s = (byte)Math.Clamp((int)Math.Round(satPercent * 2.54), 0, 254);
        return new byte[] { (byte)ep, 0x00, 0x03, 0x06, h, s, Transition[0], Transition[1] };
    }

    public static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
