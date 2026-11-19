# HUDnopy

[![Version](https://img.shields.io/badge/Version-0.1.0--alpha%20(Preview)-yellow.svg)](#hudnopy)
[![.NET 10](https://img.shields.io/badge/.NET-10.0--windows-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20WPF%20%2B%20Web%20HUD-0078D6)](#)
[![License: GPL-3.0-only](https://img.shields.io/badge/License-GPL--3.0--only-orange.svg)](LICENSE)
[![Languages](https://img.shields.io/badge/Languages-ES%20%7C%20EN%20%7C%20FR%20%7C%20DE%20%7C%20RU%20%7C%20PT-brightgreen)](#localization)

> [!WARNING]
> **Early Preview (`v0.1.0-alpha`) — Active Development:** HUDnopy is currently in an early testing phase and is **not yet a final stable release**. Features, configuration formats, UI layouts, and API integrations are still evolving and may contain bugs or breaking changes. Feedback and bug reports are very welcome.

**HUDnopy** is an open-source desktop telemetry HUD, responsive Web MFD, Fleet Carrier manager, Spansh route/trade assistant, and voice-enabled AI Copilot (COVAS) for **Elite Dangerous**.

Built with **.NET 10 / WPF** and an embedded **Kestrel HTTPS + WebSocket** server, HUDnopy runs locally on Windows alongside the game with zero cloud lock-in and full multi-device support for tablets and second monitors.

---

## Key Features

- **Live Telemetry & Mini-HUD**
  - Real-time parsing of `Status.json`, Journal logs, `Cargo.json`, `Market.json`, `Outfitting.json`, `Shipyard.json`, `NavRoute.json`, `ModulesInfo.json`, `ShipLocker.json`, `Backpack.json`, and `FCMaterials.json`.
  - Always-on-top translucent **Mini-HUD** overlay and system tray support.
- **Exploration & Exobiology**
  - FSS/DSS body scanner table with estimated **Universal Cartographics** payouts, **Exobiology** genus predictions, 3-sample genetic diversity distance tracking, and unsold value accounting.
- **Fleet Carrier & Tritium Calculator**
  - Full Fleet Carrier dashboard (location, fuel, space, finances, bartender market, and cargo hold via Frontier cAPI).
  - Mass-aware **Tritium calculator** accounting for live cargo, depot fuel mass, and step-by-step fuel burn.
- **Combat, Mining & Operations**
  - Live target telemetry card, combat session statistics, massacre mission stack tracker, asteroid prospector readout, BGS faction/Powerplay tracker, colonisation resource tracker, and historical Journal viewer with CSV/JSON export.
- **Spansh & GalNet Integration**
  - Built-in **Spansh** plotters for Neutron, Road to Riches, Exomastery, and Fleet Carrier routes (with CSV import and automatic next-jump clipboard copy), Trade routes, Pristine Mining Rings, Nearest Services, Colonisation Suppliers, and Ship/Module search with Li Yong-Rui (-15%) filtering.
  - Multi-language **GalNet News** reader with voice playback.
- **COVAS AI Copilot & Piper Neural TTS**
  - Supports **Local LLMs (Ollama / LM Studio)**, **Google Gemini**, and **OpenAI / Groq / OpenRouter** with dynamic model discovery, live ship/carrier/GalNet context injection, Push-to-Talk, **Hands-Free VAD**, and optional dynamic event commentary.
  - Built-in **Piper Neural TTS** (portable auto-download with 11 open-source voices across 6 languages) plus **Windows SAPI** fallback and unified speech-rate control.
- **Frontier cAPI & Community Sync**
  - Official **Frontier Companion API (cAPI)** via OAuth 2.0 + PKCE and **Windows DPAPI** encrypted token storage (`/profile`, `/fleetcarrier`, `/communitygoals`, `/market`, `/shipyard`).
  - Independent, opt-in sync for **EDDN** (14 schemas), **EDSM** (`api-journal-v1` + bidirectional comments/traffic/deaths), and **Inara** (batched with dirty tracking).
- **Secure Web HUD & `.binds` Control Deck**
  - Self-signed **HTTPS** Web MFD with QR-code pairing, session codes, VPN interface binding, and auto-context Ship/SRV/On-Foot/DSS/SLF control deck reading your Elite Dangerous `.binds` files.

---

## Requirements & Quick Start

### Prerequisites
- **Windows 10 / 11**
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**
- **Elite Dangerous** (PC — Live/Odyssey/Horizons)

### Build & Run
```powershell
git clone https://github.com/AlonsoLP/HUDnopy.git
cd HUDnopy
dotnet run --project HUDnopy.csproj
```

For a release build:
```powershell
dotnet build HUDnopy.csproj -c Release
```

---

## Project Structure

```text
HUDnopy/
├── Localization/          # Strongly-typed translation catalogs and parity validators
├── Models/                # Domain models (ShipState, Exploration, Operations, Combat/Mining, Fleet/Goals)
├── Resources/Languages/   # 6-language JSON packs (es-ES, en-GB, fr-FR, de-DE, ru-RU, pt-BR)
├── Services/
│   ├── Telemetry/         # EliteFileWatcherService partials (Core, Snapshots, Journal, Exploration)
│   └── *.cs               # Alerts, AI Copilot, Piper TTS, Frontier cAPI, Community Sync, Spansh, Web Server
├── UI/
│   ├── Controls/          # Reusable WPF controls and attached localization properties
│   └── MainWindow.*.cs    # Modular partial classes for MainWindow by functional domain
├── Web/                   # Single-page responsive HTTPS Web HUD (index.html)
├── docs/                  # Static GitHub Pages OAuth callback relay (callback.html)
└── wiki/                  # Full technical and user documentation (GitHub Wiki source)
```

---

## Localization

HUDnopy ships with **100% key-verified localization** across 6 languages:
- **Spanish** (`es-ES`)
- **English** (`en-GB`)
- **French** (`fr-FR`)
- **German** (`de-DE`)
- **Russian** (`ru-RU`)
- **Portuguese (Brazil)** (`pt-BR`)

---

## Documentation (Wiki)

Detailed setup guides and architectural documentation live in the [`wiki/`](wiki/Home.md) directory:

- **[Wiki Home](wiki/Home.md)** — Full documentation index
- **[Architecture](wiki/Architecture.md)** — Event bus, telemetry pipeline, threading, and security design
- **[Installation & Configuration](wiki/Installation-and-Configuration.md)** — Paths, alerts, custom sounds, themes, and Mini-HUD
- **[AI Copilot & Voice](wiki/AI-Copilot-and-Voice.md)** — Ollama/Gemini/OpenAI setup, Hands-Free VAD, and Piper Neural TTS
- **[Frontier cAPI & Community Sync](wiki/Frontier-cAPI-and-Community-Sync.md)** — OAuth 2.0 + PKCE, EDDN, EDSM, and Inara
- **[Web HUD & Security](wiki/Web-HUD-and-Security.md)** — HTTPS certificates, access modes, VPN binding, and QR pairing
- **[Spansh & Commander Tools](wiki/Spansh-and-Commander-Tools.md)** — Route plotters, trade, mining rings, and Fleet Carrier tools
- **[Keybindings & Control Deck](wiki/Keybindings-and-Control-Deck.md)** — `.binds` profiles, auto-context deck, and macros

---

## License & Disclaimer

HUDnopy is free software licensed under **GPL-3.0-only** — see [LICENSE](LICENSE) for full terms.

*Elite Dangerous* is a registered trademark of Frontier Developments plc. HUDnopy is an independent community project and is not affiliated with or endorsed by Frontier Developments.
