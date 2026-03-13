# Project Guidelines

## Communication
- Always reply to the user in Simplified Chinese.
- Keep explanations easy to read: short sentences, clear steps, avoid unnecessary jargon.
- When listing commands, prefer Windows PowerShell style paths and examples.

## Build And Run
- Prerequisites:
  - Windows 11 22H2+
  - ViGEmBus installed
  - .NET 9 SDK/runtime available
- Restore/build solution:
  - `dotnet restore joycon2.sln`
  - `dotnet build joycon2.sln -c Debug`
- Run desktop app:
  - `dotnet run --project src/Joycon2PC.App/Joycon2PC.App.csproj`
- Release publish (framework-dependent preferred for smaller size):
  - `dotnet publish src/Joycon2PC.App/Joycon2PC.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/`

## Architecture
- `src/Joycon2PC.App`: WinForms UI and BLE device lifecycle (scan/connect/reconnect/notifications).
- `src/Joycon2PC.Core`: Joy-Con 2 protocol parsing and subcommand building.
- `src/Joycon2PC.ViGEm`: Mapping Joy-Con state to virtual Xbox 360 controller via ViGEm.
- Dependency direction: App -> Core, ViGEm; ViGEm -> Core.

## Project Conventions
- Treat this as a Windows-only desktop project; avoid proposing cross-platform runtime behavior unless explicitly requested.
- Keep protocol logic in Core and transport/UI logic in App; avoid mixing parser code into WinForms handlers.
- Preserve existing Joy-Con 2 report assumptions:
  - Input report size is 63 bytes.
  - Button bits are read from bytes `[4..7]`.
  - Sticks are packed 12-bit fields from bytes `[10..15]`.
- Respect current conditional compilation in BLE code (for example `INTHEHAND` related blocks) unless task requires changing it.

## Pitfalls
- Solution uses mixed target frameworks (`net9.0-windows10.0.22621.0` for App, `net7.0` for Core/ViGEm). Do not "normalize" framework versions unless requested.
- Bluetooth behavior depends on Windows BLE stack and real hardware state; avoid claiming reconnect or rumble fixes without validating flow.
- There is currently no automated test project in this repository. If behavior changes, prefer adding focused validation steps and explicit manual verification notes.

## Key Reference Files
- `README.md`
- `src/Joycon2PC.App/Bluetooth/BLEScanner.cs`
- `src/Joycon2PC.Core/JoyconParser.cs`
- `src/Joycon2PC.Core/JoyconButton.cs`
- `src/Joycon2PC.ViGEm/ViGEmBridge.cs`