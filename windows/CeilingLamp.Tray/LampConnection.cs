using System.Security.Cryptography;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace CeilingLamp;

public enum LinkState { Idle, Connecting, Connected, Error }

/// <summary>
/// Holds the Bluetooth link to the light. Commands are queued and coalesced per channel (fast slider
/// moves only send the newest value), the link is opened on demand and released after a quiet period
/// so a phone can use the light too.
/// </summary>
public sealed class LampConnection : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gatt = new(1, 1);
    private readonly object _queueLock = new();
    private readonly Dictionary<string, (int Dest, byte[] Command)> _pending = new();
    private readonly List<string> _order = new();
    private readonly System.Threading.Timer _timer;

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _cmd;
    private byte[]? _key;
    private bool _pumping;
    private DateTime _lastActivity = DateTime.UtcNow;
    private Task<bool>? _connecting;

    public LinkState State { get; private set; } = LinkState.Idle;
    public string StatusText { get; private set; } = "Not connected";
    public event Action? StatusChanged;
    public event Action<string>? Log;

    public LampConnection(AppSettings settings)
    {
        _settings = settings;
        _timer = new System.Threading.Timer(_ => Tick(), null, 5000, 5000);
    }

    private bool IsConnected => _key != null && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    private void SetState(LinkState s, string text)
    {
        State = s; StatusText = text;
        StatusChanged?.Invoke();
    }

    private void Write(string msg) => Log?.Invoke($"{DateTime.Now:HH:mm:ss} {msg}");

    // ---------- public API ----------

    public void Send(string channel, int dest, byte[] command)
    {
        lock (_queueLock)
        {
            if (!_pending.ContainsKey(channel)) _order.Add(channel);
            _pending[channel] = (dest, command);
            if (_pumping) return;
            _pumping = true;
        }
        _ = Task.Run(PumpAsync);
    }

    public Task<bool> ConnectAsync()
    {
        lock (_queueLock)
        {
            if (IsConnected) return Task.FromResult(true);
            return _connecting ??= ConnectCoreAsync().ContinueWith(t => { lock (_queueLock) _connecting = null; return t.Result; });
        }
    }

    public void Release(string reason = "Ready, connects when you use it")
    {
        _key = null; _cmd = null;
        // Every WinRT Bluetooth object must be closed, or Windows keeps the service locked and the next
        // connection fails with AccessDenied.
        try { _service?.Dispose(); } catch { }
        try { if (_session != null) { _session.MaintainConnection = false; _session.Dispose(); } } catch { }
        try { if (_device != null) { _device.ConnectionStatusChanged -= OnConnectionChanged; _device.Dispose(); } } catch { }
        var hadDevice = _device != null;
        _service = null; _session = null; _device = null;
        if (hadDevice) { GC.Collect(); GC.WaitForPendingFinalizers(); } // releases handles still held by WinRT wrappers
        SetState(LinkState.Idle, reason);
    }

    /// <summary>Lists nearby Bluetooth LE devices for a few seconds (to find the light's address).</summary>
    public static async Task<List<(string Mac, string Name, short Rssi)>> ScanAsync(TimeSpan duration)
    {
        var found = new Dictionary<ulong, (string Mac, string Name, short Rssi)>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            lock (found)
            {
                var name = e.Advertisement.LocalName;
                if (found.TryGetValue(e.BluetoothAddress, out var old) && string.IsNullOrEmpty(name)) name = old.Name;
                found[e.BluetoothAddress] = (MacToString(e.BluetoothAddress), name ?? "", e.RawSignalStrengthInDBm);
            }
        };
        watcher.Start();
        await Task.Delay(duration);
        watcher.Stop();
        lock (found) return found.Values.OrderByDescending(x => x.Rssi).ToList();
    }

    // ---------- internals ----------

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                string channel; (int Dest, byte[] Command) item;
                lock (_queueLock)
                {
                    if (_order.Count == 0) { _pumping = false; return; }
                    channel = _order[0]; _order.RemoveAt(0);
                    item = _pending[channel]; _pending.Remove(channel);
                }
                if (!await ConnectAsync())
                {
                    lock (_queueLock) { _pending.Clear(); _order.Clear(); _pumping = false; }
                    return;
                }
                _lastActivity = DateTime.UtcNow;
                if (!await WriteAsync(Protocol.MakeCommandPacket(_key!, item.Dest, item.Command)))
                {
                    Write("write failed, reconnecting");
                    Release("Reconnecting…");
                    if (await ConnectAsync()) await WriteAsync(Protocol.MakeCommandPacket(_key!, item.Dest, item.Command));
                }
                await Task.Delay(45);
            }
        }
        catch (Exception ex)
        {
            Write("send error: " + ex.Message);
            lock (_queueLock) { _pending.Clear(); _order.Clear(); _pumping = false; }
        }
    }

    private async Task<bool> WriteAsync(byte[] packet)
    {
        var c = _cmd;
        if (c == null) return false;
        await _gatt.WaitAsync();
        try
        {
            var status = await c.WriteValueAsync(ToBuffer(packet), GattWriteOption.WriteWithoutResponse);
            return status == GattCommunicationStatus.Success;
        }
        catch (Exception ex) { Write("write: " + ex.Message); return false; }
        finally { _gatt.Release(); }
    }

    private sealed class AccessDeniedException(string message) : Exception(message);

    private async Task<bool> ConnectCoreAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await ConnectOnceAsync(retryable: attempt == 1);
            if (result != null) return result.Value;
            Write("starting over with a fresh connection");
            await Task.Delay(1500);
        }
    }

    /// <summary>Returns true/false when done, or null when a fresh second attempt is worth it.</summary>
    private async Task<bool?> ConnectOnceAsync(bool retryable)
    {
        if (!_settings.IsConfigured) { SetState(LinkState.Error, "Open settings to set up the light"); return false; }
        SetState(LinkState.Connecting, "Connecting…");
        try
        {
            Release("Connecting…");
            SetState(LinkState.Connecting, "Connecting…");
            var addr = ParseMac(_settings.Mac);

            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
            if (device == null)
            {
                Write("light not cached by Windows yet, scanning");
                await WaitForAdvertisementAsync(addr, TimeSpan.FromSeconds(8));
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
            }
            if (device == null) throw new InvalidOperationException("Light not found. Is it powered and in range?");
            _device = device;
            device.ConnectionStatusChanged += OnConnectionChanged;

            try
            {
                _session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
                _session.MaintainConnection = true;
            }
            catch (Exception ex) { Write("session: " + ex.Message); }

            var access = await device.RequestAccessAsync();
            if (access != DeviceAccessStatus.Allowed) throw new InvalidOperationException($"Windows denied access ({access}).");

            GattDeviceServicesResult? services = null;
            for (var i = 1; i <= 3; i++)
            {
                services = await device.GetGattServicesForUuidAsync(Protocol.Service, BluetoothCacheMode.Uncached);
                if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 0) break;
                await Task.Delay(800);
            }
            if (services == null || services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new InvalidOperationException($"Light service not found ({services?.Status}). Close the EGLO app and the website on other devices.");

            _service = services.Services[0];
            foreach (var extra in services.Services.Skip(1)) extra.Dispose();

            GattCharacteristicsResult? chars = null;
            for (var i = 1; i <= 3; i++)
            {
                // After a reconnect Windows sometimes refuses an uncached read for a moment; the cached list is fine then.
                chars = await _service.GetCharacteristicsAsync(i == 1 ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached);
                if (chars.Status == GattCommunicationStatus.Success && chars.Characteristics.Count > 0) break;
                Write($"reading the light failed ({chars.Status}), retrying");
                await Task.Delay(700 * i);
            }
            if (chars == null || chars.Status != GattCommunicationStatus.Success)
                throw new AccessDeniedException($"Could not read the light ({chars?.Status}).");
            var pair = chars.Characteristics.FirstOrDefault(c => c.Uuid == Protocol.PairChar);
            var cmd = chars.Characteristics.FirstOrDefault(c => c.Uuid == Protocol.CommandChar);
            var status = chars.Characteristics.FirstOrDefault(c => c.Uuid == Protocol.StatusChar);
            if (pair == null || cmd == null) throw new InvalidOperationException("This does not look like an EGLO connect.z light.");

            await _gatt.WaitAsync();
            try
            {
                var rnd = RandomNumberGenerator.GetBytes(8);
                var w = await pair.WriteValueAsync(ToBuffer(Protocol.MakePairPacket(_settings.MeshName, _settings.MeshPassword, rnd)), GattWriteOption.WriteWithResponse);
                if (w != GattCommunicationStatus.Success) throw new InvalidOperationException($"Login write failed ({w}).");
                var r = await pair.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (r.Status != GattCommunicationStatus.Success) throw new InvalidOperationException($"Login read failed ({r.Status}).");
                var reply = FromBuffer(r.Value);
                Write("login reply " + Protocol.Hex(reply));
                if (reply.Length > 0 && reply[0] == 0x0E) throw new InvalidOperationException("The light rejected the mesh name or password.");
                if (reply.Length < 9 || reply[0] != 0x0D) throw new InvalidOperationException("Unexpected login reply.");
                _key = Protocol.MakeSessionKey(_settings.MeshName, _settings.MeshPassword, rnd, reply[1..9]);
                _cmd = cmd;
                if (status != null)
                {
                    try { await status.WriteValueAsync(ToBuffer(new byte[] { 1 }), GattWriteOption.WriteWithResponse); } catch { }
                }
            }
            finally { _gatt.Release(); }

            _lastActivity = DateTime.UtcNow;
            SetState(LinkState.Connected, "Connected");
            return true;
        }
        catch (AccessDeniedException ex) when (retryable)
        {
            Write(ex.Message);
            Release("Reconnecting…");
            return null;
        }
        catch (Exception ex)
        {
            Write("connect failed: " + ex.Message);
            Release("Not connected");
            SetState(LinkState.Error, ex.Message);
            return false;
        }
    }

    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && _key != null)
        {
            Write("light disconnected");
            Release("Disconnected, reconnects when you use it");
        }
    }

    private void Tick()
    {
        try
        {
            if (!IsConnected) return;
            var after = _settings.ReleaseAfterSeconds;
            bool busy; lock (_queueLock) busy = _pumping;
            if (busy) return;
            if (after > 0 && DateTime.UtcNow - _lastActivity >= TimeSpan.FromSeconds(after))
            {
                Write("released the light");
                Release();
                return;
            }
            _ = WriteAsync(Protocol.MakeStatusPoll(_key!)); // keep-alive
        }
        catch { }
    }

    private static async Task WaitForAdvertisementAsync(ulong addr, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) => { if (e.BluetoothAddress == addr) tcs.TrySetResult(true); };
        try { watcher.Start(); } catch { return; }
        await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        try { watcher.Stop(); } catch { }
    }

    private static IBuffer ToBuffer(byte[] b) => CryptographicBuffer.CreateFromByteArray(b);
    private static byte[] FromBuffer(IBuffer buf) { CryptographicBuffer.CopyToByteArray(buf, out var b); return b ?? Array.Empty<byte>(); }

    public static ulong ParseMac(string mac)
    {
        var hex = mac.Replace(":", "").Replace("-", "").Trim();
        if (hex.Length != 12) throw new InvalidOperationException("The light's Bluetooth address is missing or invalid.");
        return Convert.ToUInt64(hex, 16);
    }

    public static string MacToString(ulong a) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    public void Dispose()
    {
        _timer.Dispose();
        Release();
    }
}
