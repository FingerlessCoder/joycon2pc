# Joycon2PC

Scaffold for Joy‑Con v2 Windows feeder.

Goals:
- Connect to Joy‑Con v2 over Bluetooth LE (HOGP/GATT)
- Parse Joy‑Con reports (buttons, axes, IMU)
- Expose an XInput virtual controller via ViGEm

Build:

```powershell
dotnet restore
dotnet build src\Joycon2PC.App\Joycon2PC.App.csproj
```

Next steps:
- Implement BLE scanner using Windows.Devices.Bluetooth.* APIs
- Implement Joy‑Con protocol parsing (see linux `hid-nintendo.c`)
- Integrate ViGEmClient for virtual controller output
