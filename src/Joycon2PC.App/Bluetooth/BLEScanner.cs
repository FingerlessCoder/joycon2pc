using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
#if INTHEHAND
using InTheHand.Bluetooth;
#endif

namespace Joycon2PC.App.Bluetooth
{
    public class BLEScanner
    {
        public BLEScanner()
        {
        }

#if INTHEHAND
    // InTheHand implementation for Joy-Con 2 (Switch 2 / NS2) controllers.
    // Filters to the Nintendo custom BLE service and subscribes only to the
    // NS2 input characteristic. Also reads PnP ID for L/R identification.

    // NS2 GATT UUIDs (from Nohzockt/Switch2-Controllers)
    private const string NS2_SERVICE_UUID = "ab7de9be-89fe-49ad-828f-118f09df7fd0";
    private const string NS2_INPUT_UUID   = "ab7de9be-89fe-49ad-828f-118f09df7fd2"; // notify
    private const string NS2_OUTPUT_UUID  = "649d4ac9-8eb7-4e6c-af44-1ea54fe5f005"; // write, matches joycon2cpp

    // NS2 Product IDs (from PnP ID characteristic)
    public const ushort PID_JOYCON_L = 0x6605;
    public const ushort PID_JOYCON_R = 0x6705;

    // Track device writable characteristic per device id
    private readonly Dictionary<string, GattCharacteristic?> _writableCharacteristics = new();
    // Track device ids that have active NS2 input notifications.
    private readonly HashSet<string> _subscribedInputDevices = new();
    // Subcommand managers per device id for reliable sends
    private readonly Dictionary<string, Joycon2PC.Core.SubcommandManager> _subManagers = new();
    // Product IDs per device id (from PnP ID characteristic)
    private readonly Dictionary<string, ushort> _deviceProductIds = new();
    // Device names per device id (e.g. "Joy-Con 2 (L)", "Joy-Con 2 (R)")
    private readonly Dictionary<string, string> _deviceNames = new();
    // BluetoothDevice objects for proper GATT disconnect on session reset.
    private readonly Dictionary<string, InTheHand.Bluetooth.BluetoothDevice> _bluetoothDevices = new();

    /// <summary>
    /// Outbound/internals diagnostic trace stream.
    /// Parameters: (deviceId, message).
    /// </summary>
    public event Action<string, string>? DiagnosticTrace;

    /// <summary>
    /// Invoked when raw notification reports arrive from a specific device.
    /// Parameters: (deviceId, rawBytes).
    /// </summary>
    public event Action<string, byte[]>? RawReportReceived;

    /// <summary>
    /// Fired immediately when a device finishes GATT subscription — before ScanAsync() returns.
    /// Parameters: (deviceId, deviceName). Subscribe to update UI status in real-time.
    /// </summary>
    public event Action<string, string>? DeviceConnected;

    /// <summary>Get the product ID for a connected device (0 if unknown).</summary>
    public ushort GetProductId(string deviceId)
        => _deviceProductIds.TryGetValue(deviceId, out var pid) ? pid : (ushort)0;

    /// <summary>Get the Windows Bluetooth device name (e.g. "Joy-Con 2 (L)").</summary>
    public string GetDeviceName(string deviceId)
        => _deviceNames.TryGetValue(deviceId, out var n) ? n : string.Empty;

    private static string ShortId(string deviceId)
        => deviceId.Length > 8 ? deviceId[..8] : deviceId;

    private static string Hex(byte[] data)
        => data == null || data.Length == 0 ? "<empty>" : BitConverter.ToString(data);

    private bool IsAlreadyConnected(string deviceId)
    {
        lock (_writableCharacteristics)
        {
            return _subscribedInputDevices.Contains(deviceId)
                || _bluetoothDevices.ContainsKey(deviceId)
                || _writableCharacteristics.ContainsKey(deviceId);
        }
    }

    private void Trace(string level, string deviceId, string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{ShortId(deviceId)}] [{level}] {message}";
        Console.WriteLine(line);
        try { DiagnosticTrace?.Invoke(deviceId, line); } catch { }
    }

    private void TraceWrite(string deviceId, string message)
        => Trace("WRITE", deviceId, message);

    private void TraceInfo(string deviceId, string message)
        => Trace("INFO", deviceId, message);

    private async Task<bool> StartNotificationsWithRetryAsync(
        string deviceId,
        GattCharacteristic characteristic,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await characteristic.StartNotificationsAsync();
                Console.WriteLine($"      Notify enabled [{ShortId(deviceId)}] attempt {attempt}/{maxAttempts}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      Notify enable failed [{ShortId(deviceId)}] attempt {attempt}/{maxAttempts}: {ex.Message}");
                if (attempt >= maxAttempts)
                    return false;

                try
                {
                    await Task.Delay(250 * attempt, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private void RemoveKnownDevice(string deviceId, string reason)
    {
        lock (_writableCharacteristics)
        {
            bool removed = _writableCharacteristics.Remove(deviceId);
            _subscribedInputDevices.Remove(deviceId);
            _subManagers.Remove(deviceId);
            _deviceProductIds.Remove(deviceId);
            _deviceNames.Remove(deviceId);
            _bluetoothDevices.Remove(deviceId);
            if (removed)
                TraceInfo(deviceId, $"Removed writable device: {reason}");
            else
                TraceInfo(deviceId, $"Removed notify-only device: {reason}");
        }
    }

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Console.WriteLine("InTheHand NS2 scan: checking paired devices first, then active scan...");

            // Step 1: already-paired devices (no advertising needed)
            // This finds Joy-Con 2 controllers that are already bonded to Windows.
            // They don't advertise after pairing, so ScanForDevicesAsync misses them.
            // Step 1: already-paired devices
            // This finds both "Joy-Con 2 (L)" and "Joy-Con 2 (R)" that Windows already knows.
            // Connect ALL of them - do NOT bail early after the first one.
            var paired = await InTheHand.Bluetooth.Bluetooth.GetPairedDevicesAsync();
            Console.WriteLine($"[Paired] Found {paired.Count()} paired device(s)");
            foreach (var dev in paired)
            {
                string name = dev.Name ?? string.Empty;
                Console.WriteLine($"[Paired] Name='{name}' Id={dev.Id} Paired={dev.IsPaired}");
                // Skip devices already connected on this scanner instance.
                // Calling ConnectDeviceAsync again on an already-connected device
                // would register duplicate CharacteristicValueChanged handlers,
                // causing every input report to fire twice.
                if (IsPotentialJoyconCandidate(name, dev.Id, dev.IsPaired) && !IsAlreadyConnected(dev.Id))
                    await ConnectDeviceAsync(dev, cancellationToken);
            }

            if (_writableCharacteristics.Count >= 2)
            {
                Console.WriteLine($"Both controllers found from paired list - skipping active scan.");
                return;
            }

            // Step 2: active scan (for advertising / newly-pairing devices)
            Console.WriteLine(_writableCharacteristics.Count == 1
                ? "Found 1 controller - scanning for the other one..."
                : "No paired controllers found - starting active scan...");
            var scanOptions = new RequestDeviceOptions { AcceptAllDevices = true };
            var devices = await InTheHand.Bluetooth.Bluetooth.ScanForDevicesAsync(scanOptions, cancellationToken);
            foreach (var dev in devices)
            {
                string name = dev.Name ?? string.Empty;
                Console.WriteLine($"[Scan] Name='{name}' Id={dev.Id} Paired={dev.IsPaired}");
                if (IsPotentialJoyconCandidate(name, dev.Id, dev.IsPaired) && !IsAlreadyConnected(dev.Id))
                    await ConnectDeviceAsync(dev, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("BLE scan stopped (timeout or cancellation).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BLE scan failed: {ex.Message}");
        }
    }

    private static bool IsNintendoDevice(string name)
        => !string.IsNullOrEmpty(name) &&
           (name.Contains("Joy-Con",       StringComparison.OrdinalIgnoreCase) ||
            name.Contains("JoyCon",        StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Nintendo",      StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Pro Controller",StringComparison.OrdinalIgnoreCase));

    private static bool IsPotentialJoyconCandidate(string name, string deviceId, bool isPaired)
    {
        if (IsNintendoDevice(name))
            return true;

        // Some Windows setups expose bonded Joy-Con 2 controllers with generic
        // names like "DeviceName" even though the underlying BLE device is valid.
        // In that case, allow paired BTHLE devices to be probed by service UUID.
        if (!isPaired)
            return false;

        bool genericName = string.IsNullOrWhiteSpace(name)
            || string.Equals(name, "DeviceName", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Bluetooth LE Device", StringComparison.OrdinalIgnoreCase);

        if (!genericName)
            return false;

        return deviceId.Contains("BTHLE", StringComparison.OrdinalIgnoreCase)
            || deviceId.Contains("Dev_", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ConnectDeviceAsync(InTheHand.Bluetooth.BluetoothDevice dev, CancellationToken cancellationToken)
    {
        string name = dev.Name ?? string.Empty;
        lock (_writableCharacteristics)
        {
            _deviceNames[dev.Id] = name;   // store immediately so GetDeviceName works
            _bluetoothDevices[dev.Id] = dev; // store for proper GATT dispose on DisconnectAll
        }
        Console.WriteLine($"  -> Connecting to '{name}' ({dev.Id})...");

        // Pair if not already paired
        if (!dev.IsPaired)
        {
            try
            {
                Console.WriteLine("    Pairing...");
                await dev.PairAsync();
                Console.WriteLine("    Paired.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Pair failed: {ex.Message}");
            }
        }

        try
        {
            await dev.Gatt.ConnectAsync();
            string deviceId = dev.Id;
            Console.WriteLine($"    GATT connected: {deviceId}");

            var services = await dev.Gatt.GetPrimaryServicesAsync();

            // Read PnP ID to identify L vs R
            try
            {
                foreach (var svc in services)
                {
                    if (!svc.Uuid.ToString().Contains("180a", StringComparison.OrdinalIgnoreCase)) continue;
                    var chars = await svc.GetCharacteristicsAsync();
                    foreach (var ch in chars)
                    {
                        if (!ch.Uuid.ToString().Contains("2a50", StringComparison.OrdinalIgnoreCase)) continue;
                        var pnpData = await ch.ReadValueAsync();
                        if (pnpData != null && pnpData.Length >= 5)
                        {
                            ushort pid = (ushort)(pnpData[3] | (pnpData[4] << 8));
                            _deviceProductIds[deviceId] = pid;
                            string side = pid == PID_JOYCON_L ? "Joy-Con L"
                                        : pid == PID_JOYCON_R ? "Joy-Con R"
                                        : $"Unknown PID=0x{pid:X4}";
                            Console.WriteLine($"    PnP ID: {side}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    PnP ID read failed (non-fatal): {ex.Message}");
            }

            // Subscribe to NS2 service
            bool foundNS2 = false;
            bool subscribedInput = false;
            foreach (var svc in services)
            {
                string svcUuid = svc.Uuid.ToString().ToLowerInvariant();
                Console.WriteLine($"    Service: {svcUuid}");

                if (!string.Equals(svcUuid, NS2_SERVICE_UUID, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("      (not NS2, skipping)");
                    continue;
                }

                foundNS2 = true;
                Console.WriteLine("    * NS2 service found!");
                var chars = await svc.GetCharacteristicsAsync();

                // Pass 1: register OUTPUT (writable) first
                // We must have the writable characteristic registered BEFORE
                // we subscribe to INPUT notifications, so the SPI calibration
                // init can be sent immediately after StartNotificationsAsync.
                //
                // IMPORTANT: All three NS2 UUIDs share the same first 8 chars
                // ("ab7de9be"), so we must match on the unique trailing part:
                //   INPUT  ends with "7fd2"
                //   OUTPUT ends with "7fd1"
                //   SERVICE ends with "7fd0"
                foreach (var ch in chars)
                {
                    string chUuid = ch.Uuid.ToString().ToLowerInvariant();
                    TraceInfo(deviceId, $"Characteristic discovered uuid={chUuid} prop={ch.Properties}");

                    if (string.Equals(chUuid, NS2_OUTPUT_UUID, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"      * NS2 OUTPUT (write command) - registered in pass 1");
                        TraceInfo(deviceId, $"NS2 OUTPUT discovered prop={ch.Properties}");
                        RegisterWritable(deviceId, ch);
                    }
                }

                // Pass 2: subscribe to INPUT and send SPI init
                foreach (var ch in chars)
                {
                    string chUuid = ch.Uuid.ToString().ToLowerInvariant();
                    Console.WriteLine($"      Char: {chUuid} Prop={ch.Properties}");

                    if (string.Equals(chUuid, NS2_OUTPUT_UUID, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine("      * NS2 OUTPUT (already registered in pass 1)");

                    if (string.Equals(chUuid, NS2_INPUT_UUID, StringComparison.OrdinalIgnoreCase) &&
                        (ch.Properties.HasFlag(GattCharacteristicProperties.Notify) ||
                         ch.Properties.HasFlag(GattCharacteristicProperties.Indicate)))
                    {
                        Console.WriteLine("      * Subscribing to NS2 INPUT");
                        ch.CharacteristicValueChanged += (s, e) =>
                        {
                            if (e.Value == null) return;
                            try
                            {
                                RawReportReceived?.Invoke(deviceId, e.Value);
                                if (_subManagers.TryGetValue(deviceId, out var mgr))
                                    mgr.ProcessIncomingReport(e.Value);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Raw report handler error [{ShortId(deviceId)}]: {ex.Message}");
                            }
                        };

                        bool notifyReady = await StartNotificationsWithRetryAsync(deviceId, ch, cancellationToken);
                        if (!notifyReady)
                        {
                            Console.WriteLine("      Warning: NS2 INPUT notification setup failed after retries.");
                            continue;
                        }

                        lock (_writableCharacteristics)
                            _subscribedInputDevices.Add(deviceId);
                        TraceInfo(deviceId, "NS2 INPUT notifications active");
                        subscribedInput = true;

                        // Send SPI calibration read IMMEDIATELY after subscribing.
                        // The reference implementation (NS2-Connect.py) does this right
                        // after start_notify - without it the controller sends stub reports
                        // with buttons always zero.
                        // OUTPUT char is already registered (pass 1) so this will succeed.
                        try
                        {
                            if (_writableCharacteristics.TryGetValue(deviceId, out var wch) && wch != null)
                            {
                                var spiRead = new byte[] {
                                    0x21, 0x01, 0x00, 0x10,
                                    0x00, 0x18, 0x00, 0x00,
                                    0xD0, 0x3D, 0x06, 0x00,
                                    0x00, 0x00, 0x00, 0x00
                                };
                                TraceWrite(deviceId, $"TX SPI-INIT len={spiRead.Length} hex={Hex(spiRead)}");
                                await wch.WriteValueWithoutResponseAsync(spiRead);
                                Console.WriteLine($"      OK: SPI calibration init sent to {deviceId[..Math.Min(8, deviceId.Length)]}");
                            }
                            else
                            {
                                Console.WriteLine("      WARN: SPI init skipped - no writable char found (pass 1 missed OUTPUT)");
                                TraceInfo(deviceId, "SPI init skipped: no writable characteristic registered");
                            }
                        }
                        catch (Exception spiEx)
                        {
                            Console.WriteLine($"      SPI init warning (non-fatal): {spiEx.Message}");
                            TraceInfo(deviceId, $"SPI init exception: {spiEx.Message}");
                        }
                    }
                }
            }

            if (!_writableCharacteristics.ContainsKey(deviceId))
            {
                TraceInfo(deviceId, "Exact NS2 output UUID not found; falling back to first writable characteristic if available");
                foreach (var svc in services)
                {
                    var chars = await svc.GetCharacteristicsAsync();
                    foreach (var ch in chars)
                    {
                        if (ch.Properties.HasFlag(GattCharacteristicProperties.Write) ||
                            ch.Properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
                        {
                            RegisterWritable(deviceId, ch);
                            TraceInfo(deviceId, $"Fallback writable selected uuid={ch.Uuid}");
                            goto writable_found;
                        }
                    }
                }
            }

        writable_found:
            // Notify listeners immediately — before ScanAsync() finishes its 30-second window.
            if (foundNS2 && subscribedInput)
                DeviceConnected?.Invoke(deviceId, name);
            else if (foundNS2 && !subscribedInput)
            {
                Console.WriteLine($"    Warning: NS2 service found but input notifications failed for {name} ({ShortId(deviceId)}).");
                RemoveKnownDevice(deviceId, "notification setup failed");
            }
            if (!foundNS2)
            {
                Console.WriteLine("    WARN: NS2 service not found - falling back to all characteristics");
                TraceInfo(deviceId, "NS2 service not found; fallback characteristic scan");
                foreach (var svc in services)
                {
                    var chars = await svc.GetCharacteristicsAsync();
                    foreach (var ch in chars)
                    {
                        if (ch.Properties.HasFlag(GattCharacteristicProperties.Notify) ||
                            ch.Properties.HasFlag(GattCharacteristicProperties.Indicate))
                        {
                            ch.CharacteristicValueChanged += (s, e) =>
                            {
                                if (e.Value == null) return;
                                try { RawReportReceived?.Invoke(deviceId, e.Value); }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Raw report handler error [{ShortId(deviceId)}]: {ex.Message}");
                                }
                            };
                            try
                            {
                                await StartNotificationsWithRetryAsync(deviceId, ch, cancellationToken);
                                lock (_writableCharacteristics)
                                    _subscribedInputDevices.Add(deviceId);
                                TraceInfo(deviceId, $"Fallback notify active on {ch.Uuid}");
                            }
                            catch { }
                        }
                        if (!_writableCharacteristics.ContainsKey(deviceId) &&
                            (ch.Properties.HasFlag(GattCharacteristicProperties.Write) ||
                             ch.Properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)))
                        {
                            RegisterWritable(deviceId, ch);
                            TraceInfo(deviceId, $"Fallback writable registered on {ch.Uuid}");
                        }
                    }
                }
            }

            dev.GattServerDisconnected += (s, e) =>
            {
                Console.WriteLine($"Disconnected: {name}");
                RemoveKnownDevice(deviceId, "GattServerDisconnected event");
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    GATT error for {name}: {ex.Message}");
        }
    }

    private void RegisterWritable(string deviceId, GattCharacteristic ch)
    {
        lock (_writableCharacteristics)
            _writableCharacteristics[deviceId] = ch;
        TraceInfo(deviceId, $"Writable characteristic registered prop={ch.Properties}");

        _subManagers[deviceId] = new Joycon2PC.Core.SubcommandManager(async (payload, ct2) =>
        {
            try
            {
                TraceWrite(deviceId, $"TX RELIABLE len={payload.Length} hex={Hex(payload)}");
                await ch.WriteValueWithoutResponseAsync(payload);
                TraceWrite(deviceId, "TX RELIABLE result=OK");
                return true;
            }
            catch (Exception ex)
            {
                TraceWrite(deviceId, $"TX RELIABLE result=FAIL err={ex.Message}");
                Console.WriteLine($"Write failed [{ShortId(deviceId)}]: {ex.Message}");
                RemoveKnownDevice(deviceId, "write failure in SubcommandManager");
                return false;
            }
        });
    }

    public async Task<bool> SendSubcommandAsync(string deviceId, byte[] payload, CancellationToken ct = default)
        => await SendSubcommandAsync(deviceId, payload, tag: null, ct);

    public async Task<bool> SendSubcommandAsync(string deviceId, byte[] payload, string? tag, CancellationToken ct = default)
    {
        GattCharacteristic? ch;
        lock (_writableCharacteristics)
            _writableCharacteristics.TryGetValue(deviceId, out ch);

        if (ch == null)
        {
            Console.WriteLine($"No writable characteristic known for device {deviceId}");
            TraceWrite(deviceId, "TX DIRECT skipped=no-writable-char");
            return false;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            string label = string.IsNullOrWhiteSpace(tag) ? "TX DIRECT" : $"TX DIRECT tag={tag}";
            TraceWrite(deviceId, $"{label} len={payload.Length} hex={Hex(payload)}");
            await ch.WriteValueWithoutResponseAsync(payload);
            TraceWrite(deviceId, $"{label} result=OK");
            return true;
        }
        catch (OperationCanceledException)
        {
            TraceWrite(deviceId, $"TX DIRECT result=CANCELLED tag={tag ?? "<none>"}");
            throw;
        }
        catch (Exception ex)
        {
            TraceWrite(deviceId, $"TX DIRECT result=FAIL tag={tag ?? "<none>"} err={ex.Message}");
            Console.WriteLine($"Write failed to {deviceId}: {ex.Message}");
            RemoveKnownDevice(deviceId, "write failure in SendSubcommandAsync");
            return false;
        }
    }

    public Task<byte[]?> SendReliableSubcommandAsync(string deviceId, byte subcommand, byte[] data, CancellationToken ct = default)
    {
        if (!_subManagers.TryGetValue(deviceId, out var mgr))
        {
            Console.WriteLine($"No SubcommandManager for device {deviceId}");
            return Task.FromResult<byte[]?>(null);
        }
        return mgr.SendSubcommandAsync(subcommand, data, ct);
    }

    public string[] GetKnownDeviceIds()
    {
        lock (_writableCharacteristics)
        {
            var keys = _subscribedInputDevices.ToArray();
            return keys;
        }
    }

    /// <summary>
    /// Force-clear all tracked devices. Needed because Windows BLE's
    /// GattServerDisconnected event is unreliable and often never fires,
    /// leaving stale device IDs in the dictionary forever.
    /// Call this before a manual reconnect to guarantee a clean slate.
    /// </summary>
    public void DisconnectAll()
    {
        lock (_writableCharacteristics)
        {
            Console.WriteLine($"DisconnectAll: closing {_bluetoothDevices.Count} GATT client(s)");
            foreach (var kvp in _bluetoothDevices)
            {
                try { (kvp.Value as IDisposable)?.Dispose(); }
                catch (Exception ex)
                {
                    Console.WriteLine($"DisconnectAll: GATT dispose error [{ShortId(kvp.Key)}]: {ex.Message}");
                }
            }
            _bluetoothDevices.Clear();
            _writableCharacteristics.Clear();
            _subscribedInputDevices.Clear();
            _subManagers.Clear();
            _deviceProductIds.Clear();
            _deviceNames.Clear();
        }
    }

    /// <summary>
    /// Send a rumble command to all connected Joy-Con 2 controllers.
    /// <paramref name="large"/> and <paramref name="small"/> are 0-255 (XInput motor values).
    /// </summary>
    public async Task SendRumbleAsync(byte large, byte small)
    {
        if (large == 0 && small == 0)
            return;

        string[] ids;
        lock (_writableCharacteristics)
            ids = _writableCharacteristics.Keys.ToArray();

        foreach (var id in ids)
            await SendRumbleAsync(id, large, small, tag: null, CancellationToken.None, allowOff: false);
    }

    public Task<bool> SendRumbleAsync(
        string deviceId,
        byte large,
        byte small,
        string? tag,
        CancellationToken ct = default,
        bool allowOff = false)
    {
        bool rumbleOn = Math.Max(large, small) > 2;
        if (!rumbleOn && !allowOff)
            return Task.FromResult(false);

        var payload = Joycon2PC.Core.SubcommandBuilder.BuildNS2Rumble(rumbleOn);
        string effectiveTag = string.IsNullOrWhiteSpace(tag)
            ? (rumbleOn ? "rumble-on" : "rumble-off")
            : tag;

        return SendSubcommandAsync(deviceId, payload, effectiveTag, ct);
    }
#else
        // Default placeholder implementation (compiled by default).
        public Task ScanAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("BLE scanning is disabled. To enable InTheHand scanner:");
            Console.WriteLine("  1) Add PackageReference to InTheHand.BluetoothLE (e.g. 4.0.16)");
            Console.WriteLine("  2) Define the compilation symbol INTHEHAND for Joycon2PC.App project");
            Console.WriteLine("     (add <DefineConstants>INTHEHAND</DefineConstants> or define in IDE)");
            Console.WriteLine("  3) Rebuild. The scanner will enumerate, attempt pairing, and subscribe to GATT notifications.");
            return Task.CompletedTask;
        }
#endif
    }
}
