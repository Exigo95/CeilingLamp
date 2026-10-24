package de.ceilinglight

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.BluetoothStatusCodes
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.SystemClock
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.security.SecureRandom
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Holds the Bluetooth link to the light. Commands are queued and coalesced per channel (fast slider moves
 * only send the newest value). The link opens on demand and is released after a quiet period, so a PC or
 * another phone can use the light too.
 */
@SuppressLint("MissingPermission") // every entry point checks hasPermissions() first
class LampLink(private val ctx: Context, private val settings: () -> Settings) {
    enum class State { Idle, Connecting, Connected, Error }

    @Volatile var state = State.Idle
        private set
    @Volatile var statusText = "Not connected"
        private set
    val statusListeners = mutableListOf<() -> Unit>()
    val logListeners = mutableListOf<(String) -> Unit>()
    val log = ArrayDeque<String>()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val connectMutex = Mutex()
    private val opMutex = Mutex()
    private val queueLock = Any()
    private val pending = LinkedHashMap<String, Pair<Int, ByteArray>>()
    @Volatile private var pumping = false

    @Volatile private var gatt: BluetoothGatt? = null
    @Volatile private var cmdChar: BluetoothGattCharacteristic? = null
    @Volatile private var key: ByteArray? = null
    @Volatile private var lastActivity = SystemClock.elapsedRealtime()

    // completions for the GATT callback in flight
    @Volatile private var connectWait: CompletableDeferred<Boolean>? = null
    @Volatile private var servicesWait: CompletableDeferred<Boolean>? = null
    @Volatile private var opWait: CompletableDeferred<Any?>? = null

    val isConnected get() = key != null && gatt != null
    val isBusy get() = pumping

    init {
        scope.launch {
            while (isActive) {
                delay(5000)
                tick()
            }
        }
    }

    private fun setState(s: State, text: String) {
        state = s; statusText = text
        statusListeners.toList().forEach { it() }
    }

    private fun write(msg: String) {
        val line = SimpleDateFormat("HH:mm:ss", Locale.US).format(Date()) + " " + msg
        synchronized(log) { log.addLast(line); while (log.size > 200) log.removeFirst() }
        logListeners.toList().forEach { it(line) }
    }

    fun hasPermissions(): Boolean {
        val perms = if (Build.VERSION.SDK_INT >= 31) listOf(Manifest.permission.BLUETOOTH_CONNECT, Manifest.permission.BLUETOOTH_SCAN)
                    else listOf(Manifest.permission.ACCESS_FINE_LOCATION)
        return perms.all { ctx.checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED }
    }

    // ---------- public API ----------

    fun send(channel: String, dest: Int, command: ByteArray) {
        synchronized(queueLock) {
            pending.remove(channel) // re-insert keeps the newest value; order follows the first request
            pending[channel] = dest to command
            if (pumping) return
            pumping = true
        }
        scope.launch { pump() }
    }

    /** Waits until all queued commands are sent (used by the widget and tile). */
    suspend fun awaitDrained(timeoutMs: Long) {
        withTimeoutOrNull(timeoutMs) { while (pumping) delay(50) }
    }

    suspend fun connect(): Boolean = connectMutex.withLock {
        if (isConnected) return@withLock true
        connectCore()
    }

    fun connectInBackground() { scope.launch { connect() } }

    fun release(reason: String = "Ready, connects when you use it") {
        key = null; cmdChar = null
        val g = gatt; gatt = null
        try { g?.disconnect() } catch (_: Exception) {}
        try { g?.close() } catch (_: Exception) {}
        opWait?.complete(null)
        setState(State.Idle, reason)
    }

    /** Lists nearby Bluetooth LE devices for a few seconds (to find the light's address). */
    fun scanAsync(durationMs: Long): Deferred<List<Triple<String, String, Int>>> = scope.async {
        val found = LinkedHashMap<String, Triple<String, String, Int>>()
        val scanner = ctx.getSystemService(BluetoothManager::class.java)?.adapter?.bluetoothLeScanner
            ?: return@async emptyList()
        val cb = object : ScanCallback() {
            override fun onScanResult(callbackType: Int, result: ScanResult) {
                val name = result.scanRecord?.deviceName ?: ""
                synchronized(found) {
                    val old = found[result.device.address]
                    found[result.device.address] = Triple(result.device.address, if (name.isEmpty()) old?.second ?: "" else name, result.rssi)
                }
            }
        }
        scanner.startScan(null, ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build(), cb)
        delay(durationMs)
        try { scanner.stopScan(cb) } catch (_: Exception) {}
        synchronized(found) { found.values.sortedByDescending { it.third } }
    }

    // ---------- internals ----------

    private suspend fun pump() {
        try {
            while (true) {
                val item: Pair<Int, ByteArray>
                synchronized(queueLock) {
                    val first = pending.entries.firstOrNull() ?: run { pumping = false; return }
                    item = first.value
                    pending.remove(first.key)
                }
                if (!connect()) {
                    synchronized(queueLock) { pending.clear(); pumping = false }
                    return
                }
                lastActivity = SystemClock.elapsedRealtime()
                val k = key
                if (k == null || !writeChar(cmdChar, Protocol.makeCommandPacket(k, item.first, item.second), BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE)) {
                    write("write failed, reconnecting")
                    release("Reconnecting…")
                    if (connect()) key?.let { writeChar(cmdChar, Protocol.makeCommandPacket(it, item.first, item.second), BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE) }
                }
                delay(45)
            }
        } catch (e: Exception) {
            write("send error: ${e.message}")
            synchronized(queueLock) { pending.clear(); pumping = false }
        }
    }

    private suspend fun connectCore(): Boolean {
        val s = settings()
        if (!s.isConfigured) { setState(State.Error, "Open settings to set up the light"); return false }
        if (!hasPermissions()) { setState(State.Error, "Allow Bluetooth access for this app"); return false }
        for (attempt in 1..3) {
            setState(State.Connecting, if (attempt == 1) "Connecting…" else "Connecting… (try $attempt)")
            try {
                val adapter = ctx.getSystemService(BluetoothManager::class.java)?.adapter ?: throw IllegalStateException("This phone has no Bluetooth.")
                if (!adapter.isEnabled) throw IllegalStateException("Bluetooth is off.")
                val device = adapter.getRemoteDevice(s.mac.uppercase())

                connectWait = CompletableDeferred()
                val g = device.connectGatt(ctx, false, callback, BluetoothDevice.TRANSPORT_LE)
                gatt = g
                if (withTimeoutOrNull(10_000) { connectWait!!.await() } != true) throw IllegalStateException("The light didn't answer. Is it powered and in range?")

                servicesWait = CompletableDeferred()
                if (!g.discoverServices()) throw IllegalStateException("Could not read the light.")
                if (withTimeoutOrNull(10_000) { servicesWait!!.await() } != true) throw IllegalStateException("Could not read the light.")

                val svc = g.getService(Protocol.SERVICE) ?: throw IllegalStateException("This does not look like an EGLO connect.z light.")
                val pair = svc.getCharacteristic(Protocol.PAIR_CHAR) ?: throw IllegalStateException("Login characteristic missing.")
                val cmd = svc.getCharacteristic(Protocol.COMMAND_CHAR) ?: throw IllegalStateException("Command characteristic missing.")
                val status = svc.getCharacteristic(Protocol.STATUS_CHAR)

                val rnd = ByteArray(8).also { SecureRandom().nextBytes(it) }
                if (!writeChar(pair, Protocol.makePairPacket(s.meshName, s.meshPassword, rnd), BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT))
                    throw IllegalStateException("Login failed (write).")
                val reply = readChar(pair) ?: throw IllegalStateException("Login failed (read).")
                write("login reply " + Protocol.hex(reply))
                if (reply.isNotEmpty() && reply[0].toInt() == 0x0E) {
                    release("Not connected")
                    setState(State.Error, "The light rejected the mesh name or password.")
                    return false
                }
                if (reply.size < 9 || reply[0].toInt() != 0x0D) throw IllegalStateException("Unexpected login reply.")
                key = Protocol.makeSessionKey(s.meshName, s.meshPassword, rnd, reply.copyOfRange(1, 9))
                cmdChar = cmd
                if (status != null) writeChar(status, byteArrayOf(1), BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT)

                lastActivity = SystemClock.elapsedRealtime()
                setState(State.Connected, "Connected")
                return true
            } catch (e: Exception) {
                write("connect attempt $attempt failed: ${e.message}")
                release("Not connected")
                if (attempt == 3) { setState(State.Error, e.message ?: "Could not connect"); return false }
                delay(700L * attempt)
            }
        }
        return false
    }

    private suspend fun writeChar(c: BluetoothGattCharacteristic?, value: ByteArray, type: Int): Boolean = opMutex.withLock {
        val g = gatt ?: return@withLock false
        if (c == null) return@withLock false
        val wait = CompletableDeferred<Any?>(); opWait = wait
        val started = if (Build.VERSION.SDK_INT >= 33) {
            g.writeCharacteristic(c, value, type) == BluetoothStatusCodes.SUCCESS
        } else {
            @Suppress("DEPRECATION")
            run { c.writeType = type; c.value = value; g.writeCharacteristic(c) }
        }
        if (!started) return@withLock false
        val result = withTimeoutOrNull(2000) { wait.await() }
        result == BluetoothGatt.GATT_SUCCESS
    }

    private suspend fun readChar(c: BluetoothGattCharacteristic): ByteArray? = opMutex.withLock {
        val g = gatt ?: return@withLock null
        val wait = CompletableDeferred<Any?>(); opWait = wait
        if (!g.readCharacteristic(c)) return@withLock null
        withTimeoutOrNull(3000) { wait.await() } as? ByteArray
    }

    private fun tick() {
        try {
            if (!isConnected || pumping) return
            val after = settings().releaseAfterSeconds
            if (after > 0 && SystemClock.elapsedRealtime() - lastActivity >= after * 1000L) {
                write("released the light")
                release()
                return
            }
            val k = key ?: return
            scope.launch { writeChar(cmdChar, Protocol.makeStatusPoll(k), BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE) } // keep-alive
        } catch (_: Exception) {}
    }

    private val callback = object : BluetoothGattCallback() {
        override fun onConnectionStateChange(g: BluetoothGatt, status: Int, newState: Int) {
            if (newState == BluetoothProfile.STATE_CONNECTED && status == BluetoothGatt.GATT_SUCCESS) {
                connectWait?.complete(true)
            } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                connectWait?.complete(false)
                servicesWait?.complete(false)
                opWait?.complete(null)
                if (g == gatt && key != null) {
                    write("light disconnected")
                    release("Disconnected, reconnects when you use it")
                } else if (g != gatt) {
                    try { g.close() } catch (_: Exception) {}
                }
            }
        }

        override fun onServicesDiscovered(g: BluetoothGatt, status: Int) {
            servicesWait?.complete(status == BluetoothGatt.GATT_SUCCESS)
        }

        override fun onCharacteristicWrite(g: BluetoothGatt, c: BluetoothGattCharacteristic, status: Int) {
            opWait?.complete(status)
        }

        override fun onCharacteristicRead(g: BluetoothGatt, c: BluetoothGattCharacteristic, value: ByteArray, status: Int) {
            opWait?.complete(if (status == BluetoothGatt.GATT_SUCCESS) value else null)
        }

        @Deprecated("Used on Android 12 and older")
        override fun onCharacteristicRead(g: BluetoothGatt, c: BluetoothGattCharacteristic, status: Int) {
            if (Build.VERSION.SDK_INT < 33) {
                @Suppress("DEPRECATION")
                opWait?.complete(if (status == BluetoothGatt.GATT_SUCCESS) c.value else null)
            }
        }
    }
}
