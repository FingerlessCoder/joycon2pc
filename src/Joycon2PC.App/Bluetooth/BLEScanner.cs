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

    // â”€â”€ NS2 GATT UUIDs (from Nohzockt/Switch2-Controllers) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private const string NS2_SERVICE_UUID = "ab7de9be-89fe-49ad-828f-118f09df7fd0";
    private const string NS2_INPUT_UUID   = "ab7de9be-89fe-49ad-828f-118f09df7fd2"; // notify
    private const string NS2_OUTPUT_UUID  = "ab7de9be-89fe-49ad-828f-118f09df7fd1"; // write

    // NS2 Product IDs (from PnP ID characteristic)
    public const ushort PID_JOYCON_L = 0x6605;
    public const ushort PID_JOYCON_R = 0x6705;

    // Track device writable characteristic per device id
    private readonly Dictionary<string, GattCharacteristic?> _writableCharacteristics = new();
    // Subcommand managers per device id for reliable sends
    private readonly Dictionary<string, Joycon2PC.Core.SubcommandManager> _subManagers = new();
    // Product IDs per device id (from PnP ID characteristic)
    private readonly Dictionary<string, ushort> _deviceProductIds = new();
    // Device names per device id (e.g. "Joy-Con 2 (L)", "Joy-Con 2 (R)")
    private readonly Dictionary<string, string> _deviceNames = new();

    /// <summary>
    /// Invoked when raw notification reports arrive from a specific device.
    /// Parameters: (deviceId, rawBytes).
    /// </summary>
    public event Action<string, byte[]>? RawReportReceived;

    /// <summary>Get the product ID for a connected device (0 if unknown).</summary>
    public ushort GetProductId(string deviceId)
        => _deviceProductIds.TryGetValue(deviceId, out var pid) ? pid : (ushort)0;

    /// <summary>Get the Windows Bluetooth device name (e.g. "Joy-Con 2 (L)").</summary>
    public string GetDeviceName(string deviceId)
        => _deviceNames.TryGetValue(deviceId, out var n) ? n : string.Empty;

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Console.WriteLine("InTheHand NS2 scan: checking paired devices first, then active scan...");

            // â”€â”€ Step 1: already-paired devices (no advertising needed) â”€â”€â”€â”€
            // This finds Joy-Con 2 controllers that are already bonded to Windows.
            // They don't advertise after pairing, so ScanForDevicesAsync misses them.
            // â”€â”€ Step 1: already-paired devices â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // This finds both "Joy-Con 2 (L)" and "Joy-Con 2 (R)" that Windows already knows.
            // Connect ALL of them â€” do NOT bail early after the first one.
            var paired = await InTheHand.Bluetooth.Bluetooth.GetPairedDevicesAsync();
            Console.WriteLine($"[Paired] Found {paired.Count()} paired device(s)");
            foreach (var dev in paired)
            {
                string name = dev.Name ?? string.Empty;
                Console.WriteLine($"[Paired] Name='{name}' Id={dev.Id} Paired={dev.IsPaired}");
                if (IsNintendoDevice(name))
                    await ConnectDeviceAsync(dev, cancellationToken);
            }

            if (_writableCharacteristics.Count >= 2)
            {
                Console.WriteLine($"Both controllers found from paired list â€” skipping active scan.");
                return;
            }

            // â”€â”€ Step 2: active scan (for advertising / newly-pairing devices) â”€
            Console.WriteLine(_writableCharacteristics.Count == 1
                ? "Found 1 controller â€” scanning for the other oneâ€¦"
                : "No paired controllers found â€” starting active scanâ€¦");
            var scanOptions = new RequestDeviceOptions { AcceptAllDevices = true };
            var devices = await InTheHand.Bluetooth.Bluetooth.ScanForDevicesAsync(scanOptions, cancellationToken);
            foreach (var dev in devices)
            {
                string name = dev.Name ?? string.Empty;
                Console.WriteLine($"[Scan] Name='{name}' Id={dev.Id} Paired={dev.IsPaired}");
                if (IsNintendoDevice(name) && !_writableCharacteristics.ContainsKey(dev.Id))
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

    private async Task ConnectDeviceAsync(InTheHand.Bluetooth.BluetoothDevice dev, CancellationToken cancellationToken)
    {
        string name = dev.Name ?? string.Empty;
        _deviceNames[dev.Id] = name;   // store immediately so GetDeviceName works
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

            // â”€â”€ Read PnP ID to identify L vs R â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

            // â”€â”€ Subscribe to NS2 service â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            bool foundNS2 = false;
            foreach (var svc in services)
            {
                string svcUuid = svc.Uuid.ToString().ToLowerInvariant();
                Console.WriteLine($"    Service: {svcUuid}");

                if (!svcUuid.Contains(NS2_SERVICE_UUID[..8]))
                {
                    Console.WriteLine("      (not NS2, skipping)");
                    continue;
                }

                foundNS2 = true;
                Console.WriteLine("    â˜… NS2 service found!");
                var chars = await svc.GetCharacteristicsAsync();

                // â”€â”€ Pass 1: register OUTPUT (writable) first â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
                    if (chUuid.Contains("7fd1") &&
                        (ch.Properties.HasFlag(GattCharacteristicProperties.Write) ||
                         ch.Properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)))
                    {
                        Console.WriteLine($"      â˜… NS2 OUTPUT (writable) â€” registered in pass 1");
                        RegisterWritable(deviceId, ch);
                    }
                }

                // â”€â”€ Pass 2: subscribe to INPUT and send SPI init â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                foreach (var ch in chars)
                {
                    string chUuid = ch.Uuid.ToString().ToLowerInvariant();
                    Console.WriteLine($"      Char: {chUuid} Prop={ch.Properties}");

                    if (chUuid.Contains("7fd1"))
                        Console.WriteLine("      â˜… NS2 OUTPUT (already registered in pass 1)");

                    if (chUuid.Contains("7fd2") &&
                        (ch.Properties.HasFlag(GattCharacteristicProperties.Notify) ||
                         ch.Properties.HasFlag(GattCharacteristicProperties.Indicate)))
                    {
                        Console.WriteLine("      â˜… Subscribing to NS2 INPUT");
                        ch.CharacteristicValueChanged += (s, e) =>
                        {
                            if (e.Value == null) return;
                            try
                            {
                                RawReportReceived?.Invoke(deviceId, e.Value);
                                if (_subManagers.TryGetValue(deviceId, out var mgr))
                                    mgr.ProcessIncomingReport(e.Value);
                            }
                            catch { }
                        };
                        await ch.StartNotificationsAsync();

                        // Send SPI calibration read IMMEDIATELY after subscribing.
                        // The reference implementation (NS2-Connect.py) does this right
                        // after start_notify â€” without it the controller sends stub reports
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
                                await wch.WriteValueWithoutResponseAsync(spiRead);
                                Console.WriteLine($"      âœ“ SPI calibration init sent to {deviceId[..Math.Min(8, deviceId.Length)]}");
                            }
                            else
                            {
                                Console.WriteLine("      âš  SPI init skipped â€” no writable char found (pass 1 missed OUTPUT)");
                            }
                        }
                        catch (Exception spiEx)
                        {
                            Console.WriteLine($"      SPI init warning (non-fatal): {spiEx.Message}");
                        }
                    }
                }
            }

            if (!foundNS2)
            {
                Console.WriteLine("    âš  NS2 service not found â€” falling back to all characteristics");
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
                                try { RawReportReceived?.Invoke(deviceId, e.Value); } catch { }
                            };
                            try { await ch.StartNotificationsAsync(); } catch { }
                        }
                        if (!_writableCharacteristics.ContainsKey(deviceId) &&
                            (ch.Properties.HasFlag(GattCharacteristicProperties.Write) ||
                             ch.Properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)))
                        {
                            RegisterWritable(deviceId, ch);
                        }
                    }
                }
            }

            dev.GattServerDisconnected += (s, e) =>
            {
                Console.WriteLine($"Disconnected: {name}");
                lock (_writableCharacteristics)
                {
                    _writableCharacteristics.Remove(deviceId);
                    _subManagers.Remove(deviceId);
                    _deviceProductIds.Remove(deviceId);
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    GATT error for {name}: {ex.Message}");
        }
    }

    private void RegisterWritable(string deviceId, GattCharacteristic ch)
    {
        _writableCharacteristics[deviceId] = ch;
        _subManagers[deviceId] = new Joycon2PC.Core.SubcommandManager(async (payload, ct2) =>
        {
            try { await ch.WriteValueWithoutResponseAsync(payload); return true; }
            catch (Exception ex) { Console.WriteLine($"Write failed: {ex.Message}"); return false; }
        });
    }

    public async Task<bool> SendSubcommandAsync(string deviceId, byte[] payload, CancellationToken? ct = null)
    {
        if (!_writableCharacteristics.TryGetValue(deviceId, out var ch) || ch == null)
        {
            Console.WriteLine($"No writable characteristic known for device {deviceId}");
            return false;
        }

        try
        {
            await ch.WriteValueWithoutResponseAsync(payload);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Write failed to {deviceId}: {ex.Message}");
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
            var keys = new string[_writableCharacteristics.Count];
            _writableCharacteristics.Keys.CopyTo(keys, 0);
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
            Console.WriteLine($"DisconnectAll: clearing {_writableCharacteristics.Count} device(s)");
            _writableCharacteristics.Clear();
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
        // Skip zero-rumble OS polls — they produce 0x50 packets that confuse the controller
        // and cause ghost inputs (R-stick drift, phantom L/ZL presses).
        if (large == 0 && small == 0) return;

        // Convert 0-255 XInput motor power to 0.0-1.0 amplitude
        float amp = Math.Max(large, small) / 255f;
        var payload = Joycon2PC.Core.SubcommandBuilder.BuildNS2Rumble(amp > 0.01f);

        string[] ids;
        lock (_writableCharacteristics)
            ids = _writableCharacteristics.Keys.ToArray();

        foreach (var id in ids)
        {
            if (!_writableCharacteristics.TryGetValue(id, out var ch) || ch == null) continue;
            try { await ch.WriteValueWithoutResponseAsync(payload); }
            catch { /* non-fatal — controller may have disconnected */ }
        }
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
