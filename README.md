# Joycon2PC

Use **Joy-Con 2** (Switch 2 / NS2) as a virtual Xbox 360 controller on Windows — via Bluetooth LE and [ViGEmBus](https://github.com/nefarius/ViGEmBus).

Connects both Joy-Con 2 (L) and (R), merges them into one virtual gamepad. No USB dongle, no drivers beyond ViGEmBus.

> **Legal note:** This is a clean-room interoperability project (no Nintendo code, no ROMs).
> Reverse-engineering for interoperability is permitted under DMCA §1201(f) (US) and EU Directive 2009/24/EC Art. 6.
> The same legal basis applies to BetterJoy, JoyCon-Driver, and similar open-source projects.

---

## Requirements

| Requirement | Notes |
|---|---|
| Windows 11 (22H2+) | Bluetooth LE WinRT APIs required |
| [ViGEmBus driver](https://github.com/nefarius/ViGEmBus/releases/latest) | Install before running |
| .NET 9 Runtime | Or build from source |
| Joy-Con 2 (L) + (R) | Paired in Windows Bluetooth Settings |

## Quick Start

1. Install ViGEmBus
2. Pair both Joy-Con 2 controllers via **Windows Bluetooth Settings** (they appear as `Joy-Con 2 (L)` and `Joy-Con 2 (R)`)
3. Run `Joycon2PC.App.exe`
4. Click **Start** — both controllers connect automatically
5. Open a game and configure the virtual Xbox controller as normal

---

## What Works ✅

- [x] Dual Joy-Con 2 (L + R) connect simultaneously over BLE
- [x] All face buttons: A, B, X, Y
- [x] Shoulder buttons: L, R, ZL (trigger), ZR (trigger)
- [x] D-Pad (Up, Down, Left, Right)
- [x] Menu buttons: +, -, Home, Capture
- [x] Left stick + Right stick (12-bit precision, factory calibration)
- [x] Stick click (L3, R3)
- [x] C button (new Joy-Con 2 R exclusive)
- [x] Grip buttons (GripLeft, GripRight — new on Joy-Con 2)
- [x] SL / SR buttons (per-side rail buttons — bits parsed)
- [x] L/R auto-detection from Windows device name (`Joy-Con 2 (L)` / `Joy-Con 2 (R)`)
- [x] Player LED set after connect (L = P1, R = P2, stops pairing blink)
- [x] Reconnect button (handles silent BLE disconnects from Windows)
- [x] Virtual Xbox 360 controller output via ViGEmBus

---

## What's Not Done / Known Limitations ❌

### Input
- [ ] **Single Joy-Con mode** — using one Joy-Con alone in sideways orientation (rotated button mapping not implemented)
- [ ] **SL / SR forwarding** — bits are parsed but not mapped to any Xbox button (no standard Xbox equivalent; could map to LB/RB override)
- [ ] **Grip buttons forwarding** — parsed correctly but not forwarded to ViGEm (no Xbox equivalent; could map to a custom profile)

### Hardware Features
- [ ] **Rumble / haptics** — `SubcommandBuilder.BuildNS2Rumble()` exists but is never called; no XInput → Joy-Con rumble feedback loop
- [ ] **IMU / gyroscope / accelerometer** — report bytes beyond [15] are not parsed; no mouse-emulation or motion control mode
- [ ] **IR camera** — Joy-Con 2 R has an infrared camera; protocol not reversed; not planned short-term
- [ ] **Gyro mouse cursor mode** — Joy-Con 2 supports gyro pointer on Switch 2; not implemented
- [ ] **NFC** — not implemented

### Software
- [ ] **Release binary / installer** — no compiled `.exe` published yet; must build from source
- [ ] **Single-device fallback** — if only one Joy-Con connects, the merged stick output may be incorrect (sentinel detection may assign both L and R to the same device)
- [ ] **Dead BLEAK/Shiny code path** — the `#if BLEAK` reflection-based scanner block in `BLEScanner.cs` is unused dead code; should be removed
- [ ] **Auto-reconnect on sleep/resume** — Windows BLE stack drops connections on sleep; Reconnect button is a manual workaround
- [ ] **System tray / background mode** — app window must stay open; no minimise-to-tray
- [ ] **Virtual DS4 / DualSense output** — only Xbox 360 (XInput) output; no PlayStation controller emulation
- [ ] **Per-game button remapping UI** — no built-in remapping; use Steam Input or reWASD for custom layouts
- [ ] **Stick dead-zone / calibration UI** — hardcoded factory values (centre = 1998, range 746–3249); no per-controller calibration
- [ ] **Capture button** — parsed but not forwarded (no Xbox equivalent)
- [ ] **NS2 Pro Controller / other accessories** — device filter only tested with Joy-Con 2; untested with NS2 Pro Controller or other peripherals

### Protocol Unknowns
- [ ] **Report bytes [16..62]** — purpose unknown; likely battery level, IMU data, or connection quality
- [ ] **Vibration command format** — the exact NS2 BLE rumble payload format is not confirmed; current implementation is a guess based on JC1 protocol

---

## Building from Source

```bash
# Prerequisites: .NET 9 SDK, ViGEmBus installed
git clone https://github.com/your-username/joycon2pc
cd joycon2pc
dotnet build src/Joycon2PC.App/Joycon2PC.App.csproj -c Release
# Output: src/Joycon2PC.App/bin/Release/net9.0-windows10.0.22621.0/
```

### Publish single-file EXE (self-contained)

```bash
dotnet publish src/Joycon2PC.App/Joycon2PC.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish/
```

---

## Project Structure

```
src/
  Joycon2PC.App/          # WinForms UI + BLE scanner (InTheHand.BluetoothLE)
    Bluetooth/
      BLEScanner.cs       # Scan, connect, SPI init, notifications
    MainForm.cs           # UI, state merge, ViGEm dispatch
  Joycon2PC.Core/         # Protocol
    JoyconParser.cs       # Report byte parsing
    JoyconButton.cs       # SW2Button enum (26 buttons)
    SubcommandBuilder.cs  # Output command builders (LED, rumble, SPI)
    SubcommandManager.cs  # Reliable send queue
  Joycon2PC.ViGEm/        # ViGEm bridge
    ViGEmBridge.cs        # JoyconState → Xbox 360 axis/button mapping
```

---

## Protocol Reference

- BLE service UUID: `ab7de9be-89fe-49ad-828f-118f09df7fd0`
- Input (notify): `...fd2` — 63-byte reports at ~60 Hz
- Output (write without response): `...fd1`
- Report layout: `[0]` rolling counter · `[4..7]` 32-bit buttons LE · `[10..15]` 12-bit sticks × 2
- SPI calibration init must be sent immediately after subscribing to notifications, or the controller sends stub reports with buttons always zero

Based on reverse-engineering work by [Nohzockt/Switch2-Controllers](https://github.com/Nohzockt/Switch2-Controllers).

---

## Dependencies

| Package | License |
|---|---|
| [InTheHand.BluetoothLE](https://github.com/inthehand/32feet) | MIT |
| [Nefarius.ViGEm.Client](https://github.com/nefarius/ViGEm.NET) | MIT |
| [ViGEmBus](https://github.com/nefarius/ViGEmBus) | BSD 3-Clause |

---

## License

MIT — see [LICENSE](LICENSE)

```powershell
dotnet restore
dotnet build src\Joycon2PC.App\Joycon2PC.App.csproj
```

Next steps:
- Implement BLE scanner using Windows.Devices.Bluetooth.* APIs
- Implement Joy‑Con protocol parsing (see linux `hid-nintendo.c`)
- Integrate ViGEmClient for virtual controller output
