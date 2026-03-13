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
| Windows 11 (22H2+) recommended | Fully validated BLE behavior and target runtime environment |
| Windows 10 (22H2) possible but unverified | Build may work, but BLE pairing/reconnect/latency behavior is not guaranteed |
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
- [x] Rumble / haptics forwarded from XInput → physical Joy-Con 2 (game rumble triggers controller vibration)
- [x] Single Joy-Con mode — one Joy-Con alone maps its stick to the left-stick output automatically
- [x] Dark Fluent UI with GDI+-drawn Joy-Con 2 silhouette visualizer (live button/stick state)

---

## Roadmap / Dev Checklist

### 🔴 High priority
- [ ] **BLE connection latency & jitter** — measure P95 connect time, report interval std dev, and stick idle noise across all scenarios; fix bottlenecks (tracked: #12)
- [ ] **Reduce build size** — current self-contained EXE is ~190 MB (full .NET 9 runtime bundled); switch to framework-dependent publish → ~28 MB with no code changes needed; self-contained cannot be trimmed (WinForms limitation)
- [ ] **Auto-reconnect on sleep/resume** — Windows drops BLE on sleep; add `SystemEvents.PowerModeChanged` listener to trigger reconnect automatically
- [ ] **Single Joy-Con sideways mode** — rotated button mapping when used alone (SL/SR → LB/RB, D-pad becomes face buttons for Joy-Con L sideways)
- [ ] **SL / SR forwarding** — parsed but not sent to ViGEm; map to bumpers

### 🟡 Medium priority
- [ ] **Grip button forwarding** — GripLeft/GripRight not forwarded to Xbox; no standard equivalent — could use custom paddles mapping
- [ ] **Capture button** — no Xbox equivalent; could map to `Back`+`Guide` chord
- [ ] **Stick dead-zone / calibration UI** — hardcoded factory values (centre=1998, range 746–3249); add per-controller calibration
- [ ] **System tray / background mode** — minimize to tray while gaming
- [ ] **DS4 / DualSense output option** — add PS4 ViGEm target for games with better PlayStation support

### 🟢 Low priority / research
- [ ] **IMU / gyroscope** — parse bytes [16..62]; implement gyro-mouse mode for FPS aiming
- [ ] **Vibration command format** — current NS2 rumble payload is unconfirmed; needs testing with real game rumble patterns
- [ ] **NS2 Pro Controller** — extend device filter and test
- [ ] **Per-game button remapping UI** — profile system with save/load
- [ ] **Battery level display** — likely encoded in bytes [16..62]

---

### 🔧 Refactor / Repo cleanup
- [ ] **Split `BLEScanner.cs`** (~400 lines mixing scan + GATT + rumble) into `BLEScanner.cs` (scan only) + `BLEConnection.cs` (per-device GATT lifecycle)
- [ ] **Split `MainForm.cs`** (~1100 lines mixing UI + parsing + state) — extract `JoyconInputHandler.cs` for report parsing and state merge
- [ ] **Remove `SubcommandManager.cs` dependency** — NS2 uses `WriteWithoutResponse`; the ACK-based queue adds complexity for no benefit
- [ ] **Clean up `Joycon2PC.App.csproj`** — ~20 lines of stale comments about removed packages

---

### 📦 Build size

| Artifact | Extra flags | Size |
|---|---|---|
| Current (self-contained, untrimmed) | *(as-is)* | ~190 MB |
| Framework-dependent *(recommended — requires [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0))* | `--self-contained false` | ~28 MB |
| Trimming | not possible — WinForms blocks IL trimming | — |

**Recommendation:** Switch the default release to framework-dependent (~28 MB). Most Windows 11 gamers already have .NET 9. Keep self-contained as a fallback for those who don't.

---

## What's Not Done / Known Limitations ❌
- [ ] **Single Joy-Con sideways orientation** — rotated button layout (e.g. d-pad on right) for single-Joy-Con games not implemented
- [ ] **SL / SR forwarding** — bits are parsed but not mapped to any Xbox button (no standard Xbox equivalent; could map to LB/RB override)
- [ ] **Grip buttons forwarding** — parsed correctly but not forwarded to ViGEm (no Xbox equivalent; could map to a custom profile)

### Hardware Features
- [ ] **IMU / gyroscope / accelerometer** — report bytes beyond [15] are not parsed; no mouse-emulation or motion control mode
- [ ] **IR camera** — Joy-Con 2 R has an infrared camera; protocol not reversed; not planned short-term
- [ ] **Gyro mouse cursor mode** — Joy-Con 2 supports gyro pointer on Switch 2; not implemented
- [ ] **NFC** — not implemented

### Software
- [x] **Release binary** — published as GitHub Release (framework-dependent single-file EXE)
- [ ] **Installer** — no installer/MSIX yet; manual extract-and-run only
- [ ] **Single-device fallback** — if only one Joy-Con connects, the merged stick output may be incorrect (sentinel detection may assign both L and R to the same device)
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

### Publish EXE

**Framework-dependent (~28 MB, recommended — requires [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)):**
```bash
dotnet publish src/Joycon2PC.App/Joycon2PC.App.csproj ^
  -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -o publish/
```

**Self-contained (~190 MB, no .NET install needed):**
```bash
dotnet publish src/Joycon2PC.App/Joycon2PC.App.csproj ^
  -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
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
