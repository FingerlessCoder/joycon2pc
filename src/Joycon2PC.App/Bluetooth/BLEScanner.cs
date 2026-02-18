using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
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

#if BLEAK
    // BLE implementation using a Bleak-like/shiny runtime discovered at runtime via reflection.
    // This block is compiled only when the `BLEAK` symbol is defined. It intentionally
    // avoids a hard compile-time dependency so you can add the NuGet package later.

    private readonly Dictionary<string, object?> _bleClients = new(); // deviceId -> device object (runtime-specific)
    private readonly Dictionary<string, object?> _writableCharacteristics = new();
    private readonly Dictionary<string, Joycon2PC.Core.SubcommandManager> _subManagers = new();

    public event Action<byte[]>? RawReportReceived;

    public async Task ScanAsync()
    {
        Console.WriteLine("Scanning for Bluetooth LE devices (Bleak/Shiny runtime)...");

        // Try to detect a known runtime (Shiny, Bleak, BleakSharp, etc.) via loaded assemblies.
        // First attempt to force-load common BLE runtime assemblies so their types
        // are present in AppDomain.CurrentDomain.GetAssemblies(). This helps when
        // assemblies are referenced but not yet loaded.
        try
        {
            try { System.Reflection.Assembly.Load("Shiny.BluetoothLE"); } catch { }
            try { System.Reflection.Assembly.Load("Bleak"); } catch { }
            try { System.Reflection.Assembly.Load("BleakSharp"); } catch { }
        }
        catch { }

        Type? scannerType = null;

        // Try to resolve common runtime type names via Type.GetType (loads assembly if available)
        var candidateTypeNames = new[] {
            "Shiny.BluetoothLE.BleAdapter, Shiny.BluetoothLE",
            "Shiny.BluetoothLE.BleManager, Shiny.BluetoothLE",
            "Shiny.BluetoothLE.BleAdapter, Shiny",
            "Shiny.BluetoothLE.BleManager, Shiny",
            "Bleak.BleakScanner, Bleak",
            "Bleak.Scanner, Bleak",
            "BleakSharp.BleakScanner, BleakSharp"
        };
        foreach (var tn in candidateTypeNames)
        {
            try
            {
                var tt = Type.GetType(tn, false);
                if (tt != null) { scannerType = tt; break; }
            }
            catch { }
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                // common runtime type names
                var t = asm.GetType("Shiny.BluetoothLE.BleAdapter")
                    ?? asm.GetType("Shiny.BluetoothLE.BleManager")
                    ?? asm.GetType("Bleak.BleakScanner")
                    ?? asm.GetType("Bleak.Scanner")
                    ?? asm.GetType("BleakSharp.BleakScanner");
                if (t != null) { scannerType = t; break; }
            }
            catch { }
        }

        if (scannerType == null)
        {
            Console.WriteLine("No compatible BLE runtime type was found via quick lookup.");

            // If Shiny is referenced, enumerate its exported types to help implement
            // a concrete integration. This prints available types for exploration.
            try
            {
                var asm = System.Reflection.Assembly.Load("Shiny.BluetoothLE");
                if (asm != null)
                {
                    Console.WriteLine($"Shiny.BluetoothLE assembly loaded: {asm.FullName}");
                    Console.WriteLine("Public types in Shiny.BluetoothLE:");
                    foreach (var t in asm.GetExportedTypes())
                    {
                        Console.WriteLine($"  - {t.FullName}");
                    }
                    Console.WriteLine("Use these type names to implement a concrete Shiny integration.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not load Shiny.BluetoothLE assembly: {ex.Message}");
            }

            Console.WriteLine("No compatible BLE runtime detected. To enable BLE support:");
            Console.WriteLine("  1) Add a BLE NuGet (example for Shiny: dotnet add src/Joycon2PC.App package Shiny.BluetoothLE)");
            Console.WriteLine("  2) Build with -p:DefineConstants=BLEAK and run again.");
            return;
        }

        Console.WriteLine($"Detected BLE runtime type: {scannerType.FullName}");

        // If Shiny.Managed is available, attempt to use ManagedScan (higher-level API)
        try
        {
            Type? managedScanType = null;
            // try assembly-qualified name first
            managedScanType = Type.GetType("Shiny.BluetoothLE.Managed.ManagedScan, Shiny.BluetoothLE", false);
            if (managedScanType == null)
            {
                // search loaded assemblies
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = a.GetType("Shiny.BluetoothLE.Managed.ManagedScan");
                        if (t != null) { managedScanType = t; break; }
                    }
                    catch { }
                }
            }

            if (managedScanType != null)
            {
                Console.WriteLine("Attempting to use Shiny.Managed.ManagedScan for scanning...");
                object? managedScan = null;

                // Try bootstrapping Shiny services via IServiceCollection + ServiceCollectionExtensions
                try
                {
                    var services = new ServiceCollection();
                    var scExt = managedScanType.Assembly.GetType("Shiny.ServiceCollectionExtensions");
                    if (scExt != null)
                    {
                        foreach (var m in scExt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            var pars = m.GetParameters();
                            if (pars.Length == 1 && pars[0].ParameterType == typeof(IServiceCollection))
                            {
                                try
                                {
                                    var res = m.Invoke(null, new object[] { services });
                                    if (res is IServiceCollection) break;
                                }
                                catch { }
                            }
                        }
                    }

                    var provider = services.BuildServiceProvider();
                    // Try to resolve IManagedScan or IBleManager from the provider
                    var iManagedScanType = managedScanType.Assembly.GetType("Shiny.BluetoothLE.Managed.IManagedScan");
                    if (iManagedScanType != null)
                    {
                        var ms = provider.GetService(iManagedScanType);
                        if (ms != null) managedScan = ms;
                    }

                    if (managedScan == null)
                    {
                        var ibmType = managedScanType.Assembly.GetType("Shiny.BluetoothLE.IBleManager");
                        if (ibmType != null)
                        {
                            var mm = provider.GetService(ibmType);
                            if (mm != null)
                            {
                                // try to call ManagedExtensions to create a managed scan from manager
                                var mex = managedScanType.Assembly.GetType("Shiny.BluetoothLE.ManagedExtensions");
                                if (mex != null)
                                {
                                    foreach (var met in mex.GetMethods(BindingFlags.Public | BindingFlags.Static))
                                    {
                                        var pars = met.GetParameters();
                                        if (pars.Length == 1 && pars[0].ParameterType.IsAssignableFrom(ibmType))
                                        {
                                            try
                                            {
                                                var r = met.Invoke(null, new[] { mm });
                                                if (r != null)
                                                {
                                                    managedScan = r;
                                                    break;
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Shiny DI bootstrap failed: {ex.Message}");
                }

                // Fallback: try parameterless construction
                if (managedScan == null)
                {
                    try { managedScan = Activator.CreateInstance(managedScanType!); } catch { }
                }

                if (managedScan != null)
                {
                    // try a couple of ways to subscribe to found devices and start scanning
                    string[] eventNames = new[] { "DeviceDiscovered", "DeviceFound", "OnDeviceDiscovered", "ScanResult", "DeviceDiscoveredAsync", "Found" };
                    bool subscribed = false;
                    foreach (var en in eventNames)
                    {
                        var devEvent = managedScanType.GetEvent(en, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        if (devEvent == null) continue;

                        var handler = new Action<object, object>((s, dev) =>
                        {
                            try
                            {
                                var devType = dev.GetType();
                                string name = devType.GetProperty("Name")?.GetValue(dev)?.ToString() ?? string.Empty;
                                string id = devType.GetProperty("Id")?.GetValue(dev)?.ToString() ?? devType.GetProperty("Address")?.GetValue(dev)?.ToString() ?? "unknown";
                                Console.WriteLine($"ManagedScan found: {name} ({id})");
                                if (!string.IsNullOrEmpty(name) && (name.Contains("Joy-Con") || name.Contains("JoyCon") || name.Contains("Pro Controller")))
                                {
                                    TryCreateClientForDevice(id, dev);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"ManagedScan handler error: {ex.Message}");
                            }
                        });

                        try
                        {
                            var del = Delegate.CreateDelegate(devEvent.EventHandlerType!, handler.Target, handler.Method);
                            devEvent.AddEventHandler(managedScan, del);
                            subscribed = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to attach to {en}: {ex.Message}");
                        }
                    }

                    // If no event available, try to look for a 'Results' or 'ScanResults' property we can poll
                    if (!subscribed)
                    {
                        var prop = managedScanType.GetProperty("Results") ?? managedScanType.GetProperty("ScanResults") ?? managedScanType.GetProperty("ManagedScanResults");
                        if (prop != null)
                        {
                            try
                            {
                                var results = prop.GetValue(managedScan) as System.Collections.IEnumerable;
                                if (results != null)
                                {
                                    foreach (var r in results)
                                    {
                                        var rt = r.GetType();
                                        string name = rt.GetProperty("Name")?.GetValue(r)?.ToString() ?? string.Empty;
                                        string id = rt.GetProperty("Id")?.GetValue(r)?.ToString() ?? "unknown";
                                        Console.WriteLine($"ManagedScan result (existing): {name} ({id})");
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    // try to start the managed scan using any common method names
                    var start = managedScanType.GetMethod("Start", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                ?? managedScanType.GetMethod("StartAsync", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                ?? managedScanType.GetMethod("Scan", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                ?? managedScanType.GetMethod("ScanAsync", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                ?? managedScanType.GetMethod("Run", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (start != null)
                    {
                        try
                        {
                            var res = start.Invoke(managedScan, null);
                            if (res is Task mt) await mt.ConfigureAwait(false);
                            return;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"ManagedScan start invocation failed: {ex.Message}");
                        }
                    }

                    // If we couldn't subscribe or start, attempt to find a factory on ManagedExtensions
                    bool factoryTried = false;
                    try
                    {
                        var asm = managedScanType.Assembly;
                        // Look for ManagedExtensions helpers that create a managed scan
                        var mexType = asm.GetType("Shiny.BluetoothLE.ManagedExtensions") ?? asm.GetType("Shiny.BluetoothLE.Managed.ManagedExtensions");
                        if (mexType != null)
                        {
                            foreach (var m in mexType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
                            {
                                if (!typeof(object).IsAssignableFrom(m.ReturnType) && m.ReturnType.Name != "IManagedScan" && m.ReturnType.Name != "ManagedScan") continue;
                                // try methods that return IManagedScan/ManagedScan
                                try
                                {
                                    object? arg = null;
                                    var pars = m.GetParameters();
                                    if (pars.Length == 0)
                                    {
                                        var r = m.Invoke(null, null);
                                        if (r != null)
                                        {
                                            managedScan = r;
                                            factoryTried = true;
                                            break;
                                        }
                                    }
                                    else if (pars.Length == 1)
                                    {
                                        // if it needs IBleManager, try to create one
                                        var pType = pars[0].ParameterType;
                                        object? managerInst = null;
                                        try
                                        {
                                            // search assembly for a concrete type implementing the parameter type
                                            foreach (var t in asm.GetTypes())
                                            {
                                                if (!pType.IsAssignableFrom(t)) continue;
                                                try
                                                {
                                                    managerInst = Activator.CreateInstance(t);
                                                    if (managerInst != null) break;
                                                }
                                                catch { }
                                            }
                                        }
                                        catch { }

                                        try
                                        {
                                            var r = m.Invoke(null, new[] { managerInst });
                                            if (r != null)
                                            {
                                                managedScan = r;
                                                factoryTried = true;
                                                break;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            }
                        }
                        // If factory returned a managedScan, and it's usable, attempt to start/subscribe similar to above
                        if (factoryTried && managedScan != null)
                        {
                            var mst = managedScan.GetType();
                            var ev = mst.GetEvent("DeviceDiscovered") ?? mst.GetEvent("DeviceFound");
                            if (ev != null)
                            {
                                var handler = new Action<object, object>((s, dev) =>
                                {
                                    try
                                    {
                                        var devType = dev.GetType();
                                        string name = devType.GetProperty("Name")?.GetValue(dev)?.ToString() ?? string.Empty;
                                        string id = devType.GetProperty("Id")?.GetValue(dev)?.ToString() ?? "unknown";
                                        Console.WriteLine($"ManagedScan(factory) found: {name} ({id})");
                                        if (!string.IsNullOrEmpty(name) && (name.Contains("Joy-Con") || name.Contains("JoyCon") || name.Contains("Pro Controller")))
                                        {
                                            TryCreateClientForDevice(id, dev);
                                        }
                                    }
                                    catch { }
                                });
                                try
                                {
                                    var del = Delegate.CreateDelegate(ev.EventHandlerType!, handler.Target, handler.Method);
                                    ev.AddEventHandler(managedScan, del);
                                }
                                catch { }
                            }

                            var start2 = managedScan.GetType().GetMethod("Start") ?? managedScan.GetType().GetMethod("StartAsync") ?? managedScan.GetType().GetMethod("Scan") ?? managedScan.GetType().GetMethod("ScanAsync");
                            if (start2 != null)
                            {
                                try
                                {
                                    var r2 = start2.Invoke(managedScan, null);
                                    if (r2 is Task t2) await t2.ConfigureAwait(false);
                                    return;
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }

                    // If we couldn't subscribe or start, attempt to find an IBleManager implementation in the same assembly
                    try
                    {
                        var asm = managedScanType.Assembly;
                        var ibmType = asm.GetType("Shiny.BluetoothLE.IBleManager") ?? asm.GetType("Shiny.BluetoothLE.IBleManager, Shiny.BluetoothLE");
                        if (ibmType != null)
                        {
                            foreach (var t in asm.GetTypes())
                            {
                                if (!ibmType.IsAssignableFrom(t)) continue;
                                try
                                {
                                    var inst = Activator.CreateInstance(t);
                                    var scanM = t.GetMethod("Scan") ?? t.GetMethod("StartScan") ?? t.GetMethod("ScanAsync");
                                    if (scanM != null)
                                    {
                                        var r = scanM.Invoke(inst, null);
                                        if (r is Task tr) await tr.ConfigureAwait(false);
                                        return;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Shiny.Managed attempt failed: {ex.Message}");
        }

        // Attempt to instantiate a scanner/adapter if possible. Many runtimes expose
        // either static singletons or manager/adapter classes — be flexible.
        object? scanner = null;
        try
        {
            // Prefer parameterless constructor
            scanner = Activator.CreateInstance(scannerType);
        }
        catch { }

        // If we couldn't create an instance, look for a static 'Default' or 'Current' property
        if (scanner == null)
        {
            var prop = scannerType.GetProperty("Current") ?? scannerType.GetProperty("Default") ?? scannerType.GetProperty("Instance");
            scanner = prop?.GetValue(null);
        }

        if (scanner == null)
        {
            Console.WriteLine("Found BLE runtime type but could not create/locate an instance.");
            return;
        }

        // If the runtime exposes a device discovered event or a scan method that accepts a callback,
        // try subscribing using reflection to receive discovered device objects.
        var deviceFoundEvent = scanner.GetType().GetEvent("DeviceDiscovered") ?? scanner.GetType().GetEvent("DeviceFound") ?? scanner.GetType().GetEvent("DeviceFoundAsync");
        if (deviceFoundEvent != null)
        {
            var handler = new Action<object, object>((s, dev) =>
            {
                try
                {
                    var devType = dev.GetType();
                    string name = devType.GetProperty("Name")?.GetValue(dev)?.ToString() ?? string.Empty;
                    string id = devType.GetProperty("Id")?.GetValue(dev)?.ToString() ?? devType.GetProperty("Address")?.GetValue(dev)?.ToString() ?? "unknown";
                    Console.WriteLine($"Found device: {name} ({id})");

                    if (!string.IsNullOrEmpty(name) && (name.Contains("Joy-Con") || name.Contains("JoyCon") || name.Contains("Pro Controller")))
                    {
                        Console.WriteLine("  -> Possible Nintendo controller found.");
                        TryCreateClientForDevice(id, dev);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DeviceFound handler error: {ex.Message}");
                }
            });

            var handlerDelegate = Delegate.CreateDelegate(deviceFoundEvent.EventHandlerType!, handler.Target, handler.Method);
            deviceFoundEvent.AddEventHandler(scanner, handlerDelegate);
        }

        // Try to find a Scan/Start method and invoke it
        var startMethod = scanner.GetType().GetMethod("ScanAsync") ?? scanner.GetType().GetMethod("Start") ?? scanner.GetType().GetMethod("StartScanningAsync") ?? scanner.GetType().GetMethod("StartAsync");
        if (startMethod == null)
        {
            Console.WriteLine("BLE runtime detected but no Start/Scan method found on the adapter/manager.");
            return;
        }

        try
        {
            var res = startMethod.Invoke(scanner, null);
            if (res is Task t) await t.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start scanner: {ex.Message}");
        }
    }

    // Record a device object and attempt discovery of writable characteristic via reflection.
    private void TryCreateClientForDevice(string deviceId, object device)
    {
        lock (_bleClients)
        {
            if (!_bleClients.ContainsKey(deviceId))
                _bleClients[deviceId] = device;
        }

        // Discover writable characteristic in background
        _ = Task.Run(async () => await TryDiscoverWritableCharacteristicAsync(deviceId, device));
    }

    // Best-effort: reflect into the runtime-specific device object to find a writable characteristic
    // (HID report characteristic or any characteristic supporting Write/WriteWithoutResponse),
    // subscribe to notifications and create a SubcommandManager for reliable writes.
    private async Task TryDiscoverWritableCharacteristicAsync(string deviceId, object device)
    {
        try
        {
            var devType = device.GetType();

            // Look for method names commonly used in various runtimes
            var getServices = devType.GetMethod("GetGattServicesAsync") ?? devType.GetMethod("GetServicesAsync") ?? devType.GetMethod("GetServices");
            if (getServices == null)
            {
                // nothing we can do here
                return;
            }

            var svcObj = getServices.Invoke(device, null);
            if (svcObj is Task svcTask)
            {
                await svcTask.ConfigureAwait(false);
                // try to get Result property
                var resultProp = svcTask.GetType().GetProperty("Result");
                svcObj = resultProp?.GetValue(svcTask) ?? svcObj;
            }

            if (svcObj == null) return;

            // svcObj is an enumerable of service objects
            var services = svcObj as System.Collections.IEnumerable;
            if (services == null) return;

            foreach (var svc in services)
            {
                try
                {
                    var svcType = svc.GetType();
                    var getChars = svcType.GetMethod("GetCharacteristicsAsync") ?? svcType.GetMethod("GetCharacteristics") ?? svcType.GetMethod("GetAllCharacteristics");
                    if (getChars == null) continue;

                    var charsObj = getChars.Invoke(svc, null);
                    if (charsObj is Task charsTask)
                    {
                        await charsTask.ConfigureAwait(false);
                        var r = charsTask.GetType().GetProperty("Result")?.GetValue(charsTask);
                        charsObj = r ?? charsObj;
                    }

                    var characteristics = charsObj as System.Collections.IEnumerable;
                    if (characteristics == null) continue;

                    foreach (var ch in characteristics)
                    {
                        try
                        {
                            var chType = ch.GetType();
                            var uuid = chType.GetProperty("Uuid")?.GetValue(ch)?.ToString() ?? chType.GetProperty("UuidString")?.GetValue(ch)?.ToString() ?? string.Empty;
                            var props = chType.GetProperty("Properties")?.GetValue(ch);

                            // Heuristic: HID Report characteristic UUID contains "2a4d"
                            if (!string.IsNullOrEmpty(uuid) && uuid.ToLowerInvariant().Contains("2a4d"))
                            {
                                Console.WriteLine($"    -> Found HID report characteristic {uuid}");
                            }

                            // Subscribe to notifications if available
                            var valueChanged = chType.GetEvent("ValueChanged") ?? chType.GetEvent("NotificationReceived") ?? chType.GetEvent("ValueUpdated");
                            if (valueChanged != null)
                            {
                                var handler = new Action<object, object>((s, e) =>
                                {
                                    try
                                    {
                                        var evType = e.GetType();
                                        var data = evType.GetProperty("Value")?.GetValue(e) as byte[];
                                        if (data == null)
                                        {
                                            // some runtimes expose 'Data' or 'Buffer'
                                            data = evType.GetProperty("Data")?.GetValue(e) as byte[] ?? evType.GetProperty("Buffer")?.GetValue(e) as byte[];
                                        }

                                        if (data != null)
                                        {
                                            RawReportReceived?.Invoke(data);
                                            if (_subManagers.TryGetValue(deviceId, out var m)) m.ProcessIncomingReport(data);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Notification handler error: {ex.Message}");
                                    }
                                });

                                var del = Delegate.CreateDelegate(valueChanged.EventHandlerType!, handler.Target, handler.Method);
                                valueChanged.AddEventHandler(ch, del);

                                // Try to start notifications
                                var startMethod = chType.GetMethod("StartNotificationsAsync") ?? chType.GetMethod("StartNotifications") ?? chType.GetMethod("SubscribeAsync") ?? chType.GetMethod("EnableNotifications");
                                if (startMethod != null)
                                {
                                    var res = startMethod.Invoke(ch, null);
                                    if (res is Task t) await t.ConfigureAwait(false);
                                }
                            }

                            // Find writable characteristic
                            var writeMethod = chType.GetMethod("WriteValueAsync") ?? chType.GetMethod("WriteAsync") ?? chType.GetMethod("WriteCharacteristicAsync") ?? chType.GetMethod("WriteWithoutResponseAsync");
                            if (writeMethod != null)
                            {
                                Console.WriteLine($"    -> Found writable characteristic on device {deviceId} (uuid={uuid})");
                                _writableCharacteristics[deviceId] = ch;

                                // Create SubcommandManager that writes via reflection
                                _subManagers[deviceId] = new Joycon2PC.Core.SubcommandManager(async (payload, ct) =>
                                {
                                    try
                                    {
                                        var parms = writeMethod.GetParameters();
                                        object? invokeRes = null;
                                        if (parms.Length == 1)
                                            invokeRes = writeMethod.Invoke(ch, new object[] { payload });
                                        else if (parms.Length == 2)
                                            invokeRes = writeMethod.Invoke(ch, new object[] { payload, ct });

                                        if (invokeRes is Task wt) await wt.ConfigureAwait(false);
                                        return true;
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Characteristic write failed: {ex.Message}");
                                        return false;
                                    }
                                });

                                // Once we've found a writable characteristic, no need to examine more
                                return;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Characteristic discovery failed: {ex.Message}");
        }
    }

    public async Task<bool> SendSubcommandAsync(string deviceId, byte[] payload, CancellationToken? ct = null)
    {
        // If we have a discovered writable characteristic for this device, write using reflection
        if (_writableCharacteristics.TryGetValue(deviceId, out var ch) && ch != null)
        {
            try
            {
                var t = ch.GetType().GetMethod("WriteValueAsync") ?? ch.GetType().GetMethod("WriteAsync") ?? ch.GetType().GetMethod("WriteCharacteristicAsync") ?? ch.GetType().GetMethod("WriteWithoutResponseAsync");
                if (t == null) { Console.WriteLine("Characteristic does not expose a write method"); return false; }
                var parameters = t.GetParameters();
                object? invokeRes = null;
                if (parameters.Length == 1)
                    invokeRes = t.Invoke(ch, new object[] { payload });
                else if (parameters.Length == 2)
                    invokeRes = t.Invoke(ch, new object[] { payload, ct ?? CancellationToken.None });

                if (invokeRes is Task task) { await task.ConfigureAwait(false); }
                Console.WriteLine($"Wrote {payload.Length} bytes to {deviceId} (via characteristic)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Write failed: {ex.Message}");
                return false;
            }
        }

        // Fallback: maybe the device object itself exposes a Write method
        if (_bleClients.TryGetValue(deviceId, out var clientObj) && clientObj != null)
        {
            try
            {
                var t = clientObj.GetType().GetMethod("WriteCharacteristicAsync") ?? clientObj.GetType().GetMethod("WriteAsync") ?? clientObj.GetType().GetMethod("WriteValueAsync");
                if (t == null) { Console.WriteLine("Client does not expose a write method"); return false; }
                var parameters = t.GetParameters();
                object? invokeRes = null;
                if (parameters.Length == 1)
                    invokeRes = t.Invoke(clientObj, new object[] { payload });
                else if (parameters.Length == 2)
                    invokeRes = t.Invoke(clientObj, new object[] { payload, ct ?? CancellationToken.None });

                if (invokeRes is Task task) { await task.ConfigureAwait(false); }
                Console.WriteLine($"Wrote {payload.Length} bytes to {deviceId} (via client)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bleak/Shiny write failed: {ex.Message}");
                return false;
            }
        }

        Console.WriteLine($"No writable characteristic or client known for device {deviceId}");
        return false;
    }

#elif INTHEHAND
    // InTheHand implementation for Joy-Con 2 (Switch 2 / NS2) controllers.
    // Filters to the Nintendo custom BLE service and subscribes only to the
    // NS2 input characteristic. Also reads PnP ID for L/R identification.

    // ── NS2 GATT UUIDs (from Nohzockt/Switch2-Controllers) ────────────────
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

            // ── Step 1: already-paired devices (no advertising needed) ────
            // This finds Joy-Con 2 controllers that are already bonded to Windows.
            // They don't advertise after pairing, so ScanForDevicesAsync misses them.
            // ── Step 1: already-paired devices ────────────────────────
            // This finds both "Joy-Con 2 (L)" and "Joy-Con 2 (R)" that Windows already knows.
            // Connect ALL of them — do NOT bail early after the first one.
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
                Console.WriteLine($"Both controllers found from paired list — skipping active scan.");
                return;
            }

            // ── Step 2: active scan (for advertising / newly-pairing devices) ─
            Console.WriteLine(_writableCharacteristics.Count == 1
                ? "Found 1 controller — scanning for the other one…"
                : "No paired controllers found — starting active scan…");
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

            // ── Read PnP ID to identify L vs R ───────────────────────────
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

            // ── Subscribe to NS2 service ──────────────────────────────────
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
                Console.WriteLine("    ★ NS2 service found!");
                var chars = await svc.GetCharacteristicsAsync();

                // ── Pass 1: register OUTPUT (writable) first ──────────────
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
                        Console.WriteLine($"      ★ NS2 OUTPUT (writable) — registered in pass 1");
                        RegisterWritable(deviceId, ch);
                    }
                }

                // ── Pass 2: subscribe to INPUT and send SPI init ───────────
                foreach (var ch in chars)
                {
                    string chUuid = ch.Uuid.ToString().ToLowerInvariant();
                    Console.WriteLine($"      Char: {chUuid} Prop={ch.Properties}");

                    if (chUuid.Contains("7fd1"))
                        Console.WriteLine("      ★ NS2 OUTPUT (already registered in pass 1)");

                    if (chUuid.Contains("7fd2") &&
                        (ch.Properties.HasFlag(GattCharacteristicProperties.Notify) ||
                         ch.Properties.HasFlag(GattCharacteristicProperties.Indicate)))
                    {
                        Console.WriteLine("      ★ Subscribing to NS2 INPUT");
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
                        // after start_notify — without it the controller sends stub reports
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
                                Console.WriteLine($"      ✓ SPI calibration init sent to {deviceId[..Math.Min(8, deviceId.Length)]}");
                            }
                            else
                            {
                                Console.WriteLine("      ⚠ SPI init skipped — no writable char found (pass 1 missed OUTPUT)");
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
                Console.WriteLine("    ⚠ NS2 service not found — falling back to all characteristics");
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
