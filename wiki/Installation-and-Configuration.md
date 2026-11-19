# Installation and Configuration

## Prerequisites

- **OS:** Windows 10 or Windows 11 (64-bit)
- **Runtime / SDK:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Game:** Elite Dangerous (Live / Odyssey / Horizons) with access to the standard Saved Games and Options folders

## Building & Running from Source

```powershell
git clone https://github.com/AlonsoLP/HUDnopy.git
cd HUDnopy
dotnet run --project HUDnopy.csproj
```

To build without launching:

```powershell
dotnet build HUDnopy.csproj -c Release
```

## Runtime Files & Locations

| File / Folder | Location | Purpose |
|---|---|---|
| `config.json` | Next to `HUDnopy.exe` | User settings, thresholds, paths, and integration flags (excluded from Git). |
| `web-certificate.dat` | Next to `HUDnopy.exe` | DPAPI-encrypted self-signed HTTPS certificate for the Web HUD. |
| `frontier_capi.dat` | `%LocalAppData%\HUDnopy\` | DPAPI-encrypted Frontier cAPI OAuth tokens. |
| `Piper\` | `%LocalAppData%\HUDnopy\Piper\` | Portable Piper neural TTS engine and downloaded ONNX voice models. |
| `Spansh\` | `%LocalAppData%\HUDnopy\Spansh\` | Local disk cache for Spansh system intelligence lookups. |

## Game Paths & Profiles

Open **Settings → Game, Files & Controls**:

- **Journal Folder:** Defaults to `%UserProfile%\Saved Games\Frontier Developments\Elite Dangerous`.
- **Bindings Folder:** Defaults to `%LocalAppData%\Frontier Developments\Elite Dangerous\Options\Bindings`.
- **Active `.binds` Profile:** Select any detected `.binds` file; changes apply immediately. Use **Reload** if you edited bindings in-game.

## Appearance, Mini-HUD & Header Modules

Open **Settings → Appearance & Window**:

- **Accent Theme:** Choose a preset or enter a custom hex accent color; the theme propagates dynamically to both the desktop UI and the Web HUD.
- **Navigation Position & Font Scaling:** Place the navigation bar on any edge, optionally hide menu text labels, and scale desktop typography with `Ctrl + Mouse Wheel` or the slider.
- **Header Modules:** Choose which telemetry widgets appear in the top header bar (mirrored in the Web HUD).
- **Mini-HUD (`📌`):** Toggle a compact, translucent, always-on-top overlay for single-monitor flying.
- **System Tray:** Optionally start minimized to the Windows notification area.

## Alerts & Custom Sound Libraries

Open **Settings → Alerts**:

- **Core Thresholds:** Configure low/critical fuel percentages, critical hull threshold, and mission expiry warning/critical hours.
- **Main Menu Protection:** HUDnopy automatically suppresses false fuel/shield alerts while Elite Dangerous is on the main menu or loading screen (`Flags == 0 && Flags2 == 0`).
- **Custom Sound Folders:** Link one or more local folders containing audio files (`WAV`, `MP3`, `AIFF`, and Windows-decoded `M4A`/`AAC`/`WMA`/`FLAC`) and click **Refresh Sounds**. Unlinking a folder preserves your files on disk and marks missing assignments cleanly.
- **Speech Queue & Rate:** Choose between `tactical`, `immediate`, and `sequential` speech queues, and adjust speech speed from `-5` (slowest) to `+5` (fastest) — unified across both Windows SAPI and Piper Neural TTS.
