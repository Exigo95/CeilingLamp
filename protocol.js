/*
 * EGLO connect.z / AwoX "Connect-Z" Bluetooth protocol.
 *
 * Login + session key: Telink/AwoX mesh scheme (AES-128-ECB on byte-reversed key and value).
 * Commands: 17-byte frame = flag || AES(sessionKey, [crc8][len][dest BE16][zigbee command...]).
 * The command bytes are a Zigbee cluster command tunnelled over Bluetooth:
 *   [endpoint][cluster LE16][command id][payload...]
 * Dual-zone EGLO ceiling lights: endpoint 1 = white (front), endpoint 3 = RGB (back).
 *
 * Protocol knowledge from github.com/Leiaz/python-awox-mesh-light,
 * github.com/BastiAKA/awoxble2mqtt-api and zigbee-herdsman-converters (EGLO definitions).
 */
(function (root) {
  'use strict';

  // ---------- AES-128 (encrypt + decrypt, single block) ----------
  const SBOX = new Uint8Array(256), INV = new Uint8Array(256);
  (function initSbox() {
    let p = 1, q = 1;
    do {
      p = p ^ ((p << 1) & 0xff) ^ (p & 0x80 ? 0x1b : 0);
      q ^= q << 1; q ^= q << 2; q ^= q << 4; q &= 0xff;
      if (q & 0x80) q ^= 0x09;
      const x = q ^ ((q << 1 | q >> 7) & 0xff) ^ ((q << 2 | q >> 6) & 0xff) ^
        ((q << 3 | q >> 5) & 0xff) ^ ((q << 4 | q >> 4) & 0xff) ^ 0x63;
      SBOX[p] = x; INV[x] = p;
    } while (p !== 1);
    SBOX[0] = 0x63; INV[0x63] = 0;
  })();
  const xt = (a) => ((a << 1) ^ (a & 0x80 ? 0x1b : 0)) & 0xff;
  const mul = (a, b) => { let r = 0; while (b) { if (b & 1) r ^= a; a = xt(a); b >>= 1; } return r; };

  function expandKey(key) {
    const w = new Uint8Array(176); w.set(key);
    let rcon = 1;
    for (let i = 16; i < 176; i += 4) {
      let t = w.slice(i - 4, i);
      if (i % 16 === 0) {
        t = new Uint8Array([SBOX[t[1]] ^ rcon, SBOX[t[2]], SBOX[t[3]], SBOX[t[0]]]);
        rcon = xt(rcon);
      }
      for (let j = 0; j < 4; j++) w[i + j] = w[i - 16 + j] ^ t[j];
    }
    return w;
  }

  function aesEncryptBlock(key, input) {
    const w = expandKey(key), s = Uint8Array.from(input);
    for (let i = 0; i < 16; i++) s[i] ^= w[i];
    for (let r = 1; r <= 10; r++) {
      for (let i = 0; i < 16; i++) s[i] = SBOX[s[i]];
      const t = Uint8Array.from(s);
      for (let c = 0; c < 4; c++) for (let row = 0; row < 4; row++) s[c * 4 + row] = t[((c + row) % 4) * 4 + row];
      if (r < 10) for (let c = 0; c < 4; c++) {
        const a = s.slice(c * 4, c * 4 + 4);
        s[c * 4] = xt(a[0]) ^ xt(a[1]) ^ a[1] ^ a[2] ^ a[3];
        s[c * 4 + 1] = a[0] ^ xt(a[1]) ^ xt(a[2]) ^ a[2] ^ a[3];
        s[c * 4 + 2] = a[0] ^ a[1] ^ xt(a[2]) ^ xt(a[3]) ^ a[3];
        s[c * 4 + 3] = xt(a[0]) ^ a[0] ^ a[1] ^ a[2] ^ xt(a[3]);
      }
      for (let i = 0; i < 16; i++) s[i] ^= w[r * 16 + i];
    }
    return s;
  }

  function aesDecryptBlock(key, input) {
    const w = expandKey(key), s = Uint8Array.from(input);
    for (let i = 0; i < 16; i++) s[i] ^= w[160 + i];
    for (let r = 9; r >= 0; r--) {
      const t = Uint8Array.from(s);
      for (let c = 0; c < 4; c++) for (let row = 0; row < 4; row++) s[((c + row) % 4) * 4 + row] = t[c * 4 + row];
      for (let i = 0; i < 16; i++) s[i] = INV[s[i]];
      for (let i = 0; i < 16; i++) s[i] ^= w[r * 16 + i];
      if (r > 0) for (let c = 0; c < 4; c++) {
        const a = s.slice(c * 4, c * 4 + 4);
        s[c * 4] = mul(a[0], 14) ^ mul(a[1], 11) ^ mul(a[2], 13) ^ mul(a[3], 9);
        s[c * 4 + 1] = mul(a[0], 9) ^ mul(a[1], 14) ^ mul(a[2], 11) ^ mul(a[3], 13);
        s[c * 4 + 2] = mul(a[0], 13) ^ mul(a[1], 9) ^ mul(a[2], 14) ^ mul(a[3], 11);
        s[c * 4 + 3] = mul(a[0], 11) ^ mul(a[1], 13) ^ mul(a[2], 9) ^ mul(a[3], 14);
      }
    }
    return s;
  }

  // ---------- AwoX primitives ----------
  const pad16 = (b) => { const o = new Uint8Array(16); o.set(b.slice(0, 16)); return o; };
  const rev = (b) => Uint8Array.from(b).reverse();

  /** AwoX "encrypt": AES-ECB over reversed key and value, result reversed. */
  function encrypt(key, value) { return rev(aesEncryptBlock(rev(pad16(key)), rev(pad16(value)))); }
  function decrypt(key, value) { return rev(aesDecryptBlock(rev(pad16(key)), rev(pad16(value)))); }

  function xorCredentials(name, password) {
    const enc = new TextEncoder();
    const n = enc.encode(name), p = enc.encode(password);
    if (n.length > 16 || p.length > 16) throw new Error('Mesh name and password can be at most 16 characters.');
    const a = pad16(n), b = pad16(p), out = new Uint8Array(16);
    for (let i = 0; i < 16; i++) out[i] = a[i] ^ b[i];
    return out;
  }

  /** 17-byte login packet for the pair characteristic: 0x0C | random(8) | enc(name^pass, random)[0..8]. */
  function makePairPacket(name, password, random8) {
    const enc = encrypt(pad16(random8), xorCredentials(name, password));
    const pkt = new Uint8Array(17);
    pkt[0] = 0x0c; pkt.set(random8.slice(0, 8), 1); pkt.set(enc.slice(0, 8), 9);
    return pkt;
  }

  function makeSessionKey(name, password, random8, reply8) {
    const r = new Uint8Array(16); r.set(random8.slice(0, 8)); r.set(reply8.slice(0, 8), 8);
    return encrypt(xorCredentials(name, password), r);
  }

  /** CRC-8, poly 0x36, init 0, reflected in and out. */
  function crc8(data) {
    const refl = (x) => { let r = 0; for (let i = 0; i < 8; i++) if (x & (1 << i)) r |= 1 << (7 - i); return r; };
    let crc = 0;
    for (const b of data) {
      crc ^= refl(b);
      for (let i = 0; i < 8; i++) crc = (crc & 0x80 ? (crc << 1) ^ 0x36 : crc << 1) & 0xff;
    }
    return refl(crc);
  }

  /** 17-byte Connect-Z command frame. */
  function makeCommandPacket(sessionKey, destId, command, flag = 0x00) {
    const len = 2 + command.length;
    if (len > 14) throw new Error('Command too long');
    const block = new Uint8Array(16);
    block[1] = len; block[2] = (destId >> 8) & 0xff; block[3] = destId & 0xff;
    block.set(command, 4);
    block[0] = crc8(block.slice(1, 2 + len));
    const pkt = new Uint8Array(17);
    pkt[0] = flag; pkt.set(encrypt(sessionKey, block), 1);
    return pkt;
  }

  /** The app's status request: every node in the mesh answers with a frame naming its mesh id. */
  const makeStatusPoll = (sessionKey) => makeCommandPacket(sessionKey, 0x0aff, [0xff], 0x02);

  /** Status frame: flag(0x80/0x82) | enc16 | src mesh id (2) | 0x60. Returns {meshId, block} or null. */
  function parseStatus(sessionKey, frame) {
    if (!frame || frame.length < 17 || !(frame[0] & 0x80)) return null;
    const block = decrypt(sessionKey, frame.slice(1, 17));
    const len = block[1];
    const valid = len >= 2 && len <= 14 && block[0] === crc8(block.slice(1, 2 + len));
    const meshId = frame.length >= 19 ? (frame[17] << 8) | frame[18] : null;
    return { meshId, block, valid };
  }

  // ---------- Zigbee commands (endpoint, cluster LE, command id, payload) ----------
  const TRANSITION = [0x02, 0x00]; // 0.2 s, same as the official app
  const zcl = {
    on: (ep) => [ep, 0x06, 0x00, 0x01],
    off: (ep) => [ep, 0x06, 0x00, 0x00],
    /** Move to level with on/off: level 1..254 (also switches the endpoint on). */
    level: (ep, level) => [ep, 0x08, 0x00, 0x04, clamp(level, 1, 254), ...TRANSITION, 0x00, 0x00],
    /** Move to colour temperature, mireds as u16 LE (153 = 6500 K cold, 370 = 2700 K warm). */
    colorTemp: (ep, mireds) => { const m = clamp(Math.round(mireds), 153, 500); return [ep, 0x00, 0x03, 0x0a, m & 0xff, m >> 8, ...TRANSITION]; },
    /** Move to hue and saturation, both 0..254. */
    hueSat: (ep, hue, sat) => [ep, 0x00, 0x03, 0x06, hue & 0xff, clamp(sat, 0, 254), ...TRANSITION],
  };

  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, Math.round(v))); }
  const percentToLevel = (pct) => clamp(pct * 254 / 100, 1, 254);
  const kelvinToMireds = (k) => 1e6 / k;

  /** RGB (0-255) → Zigbee hue/sat (0-254) plus HSV value 0-1 (for brightness). */
  function rgbToHueSat(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
    let h = 0;
    if (d > 0) {
      if (max === r) h = ((g - b) / d) % 6; else if (max === g) h = (b - r) / d + 2; else h = (r - g) / d + 4;
      h *= 60; if (h < 0) h += 360;
    }
    const s = max <= 0 ? 0 : d / max;
    return { hue: Math.round(h / 360 * 254) % 255, sat: clamp(s * 254, 0, 254), value: max };
  }

  const hex = (b) => Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');

  root.EgloProtocol = {
    SERVICE: '00010203-0405-0607-0809-0a0b0c0d1910',
    STATUS_CHAR: '00010203-0405-0607-0809-0a0b0c0d1911',
    COMMAND_CHAR: '00010203-0405-0607-0809-0a0b0c0d1912',
    PAIR_CHAR: '00010203-0405-0607-0809-0a0b0c0d1914',
    encrypt, decrypt, makePairPacket, makeSessionKey, crc8, makeCommandPacket, makeStatusPoll,
    parseStatus, zcl, percentToLevel, kelvinToMireds, rgbToHueSat, hex,
  };
})(typeof window !== 'undefined' ? window : globalThis);
